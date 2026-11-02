// BridgeProtocolTests.cs — IPC 帧读取、监听行解析与状态行分类
//
// 这三段逻辑对着一个本仓库无法控制的外部程序，帧错位的典型症状是"读出巨大的长度值"
// （上游文档第 10 节点名了这个错误），部分读取被当成整帧正是它的根源，
// 所以这里用每次只给 1 字节的流把那条路径钉住
//
// 解析类的用例按上游源码逐条对照写：i18n.go 的 enStrings、ipc.go 的字面量、
// wsclient.go 的短语，上游改文案时这些用例会先红，而不是让界面静默显示"未知状态"
//
// IPC frame reading, listen-line parsing and status classification
//
// All three pieces face an external program this repository does not control; the classic symptom of a
// desynchronised frame is reading a huge length value, the mistake section 10 of the upstream document
// calls out, with a partial read treated as a whole frame as its root cause — so the one-byte-at-a-time
// stream here pins that path down
//
// The parser cases are written entry by entry against the upstream source: enStrings in i18n.go, the
// literals in ipc.go and the phrases in wsclient.go. When the upstream rewords, these cases go red
// first instead of letting the UI show "unknown state" quietly

using System.Buffers.Binary;
using System.Text;
using MIDITap.Core.Bridge;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class BridgeIpcFrameReaderTests
{
    /// <summary>文档第 4 节的示例 MIDI 消息 / The example MIDI message from section 4 of the document</summary>
    private static readonly byte[] NoteOn = [0x90, 0x3C, 0x64];

    /// <summary>握手帧 body，字段顺序与上游实际发送一致 / The hello body, field order as the upstream sends it</summary>
    private static byte[] HelloBody()
        => Encoding.UTF8.GetBytes(
            "{\"type\":\"hello\",\"proto\":\"1.0.0\",\"client\":\"midibridge-client\",\"version\":\"9.9.9\"}");

    /// <summary>把 kind 与 body 包成完整帧 / Wraps a kind and body into a complete frame</summary>
    private static byte[] Frame(byte kind, byte[] body)
    {
        var frame = new byte[5 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(1 + body.Length));
        frame[4] = kind;
        body.CopyTo(frame, 5);
        return frame;
    }

    [Fact]
    public async Task Reads_the_handshake_then_the_midi_frames()
    {
        // 帧序与文档第 9 节的参考输出一致：先 hello，之后只有 MIDI
        //
        // The frame order matches the reference output in section 9: hello first, then only MIDI
        using var stream = new MemoryStream(
            [.. Frame(0x01, HelloBody()), .. Frame(0x02, NoteOn), .. Frame(0x02, [0x80, 0x3C, 0x00])]);
        var reader = new BridgeIpcFrameReader(stream);

        var hello = await reader.ReadAsync();
        Assert.True(hello.HasFrame);
        Assert.Equal(BridgeIpcFrameKind.ControlJson, hello.Kind);
        Assert.Equal(HelloBody(), hello.Body);
        Assert.True(reader.HandshakeConfirmed);
        Assert.Equal("1.0.0", reader.Proto);
        Assert.Equal("9.9.9", reader.UpstreamVersion);
        Assert.Equal("midibridge-client", reader.UpstreamClient);

        var noteOn = await reader.ReadAsync();
        Assert.Equal(BridgeIpcFrameKind.Midi, noteOn.Kind);
        Assert.Equal(NoteOn, noteOn.Body);

        var noteOff = await reader.ReadAsync();
        Assert.Equal(BridgeIpcFrameKind.Midi, noteOff.Kind);

        var closed = await reader.ReadAsync();
        Assert.Equal(BridgeIpcReadStatus.EndOfStream, closed.Status);
        Assert.True(closed.EndsConnection);
    }

    [Fact]
    public async Task Fills_a_frame_delivered_one_byte_at_a_time()
    {
        // 每次最多给 1 字节：把"部分读取当整帧"这条错误路径逼出来
        // 若实现没读满就返回，这里的 body 长度或内容会立刻对不上
        //
        // One byte per read at most, forcing out the path where a partial read is mistaken for a frame
        // Were the implementation to return before filling, the body length or content would not match
        using var stream = new ChunkedStream(
            [.. Frame(0x01, HelloBody()), .. Frame(0x02, NoteOn)], chunkSize: 1);
        var reader = new BridgeIpcFrameReader(stream);

        var hello = await reader.ReadAsync();
        Assert.Equal(BridgeIpcFrameKind.ControlJson, hello.Kind);
        Assert.Equal(HelloBody(), hello.Body);

        var noteOn = await reader.ReadAsync();
        Assert.Equal(BridgeIpcFrameKind.Midi, noteOn.Kind);
        Assert.Equal(NoteOn, noteOn.Body);
    }

    [Fact]
    public async Task Consumes_one_frame_per_call_when_frames_arrive_coalesced()
    {
        // 三帧一次到达：每次调用只交付一帧，剩余的留给下一次调用
        // 这是"粘包"的那条路径，靠 Stream 自己的读取位置而不是私有缓冲解决
        //
        // Three frames arriving together: each call delivers exactly one and leaves the rest for later
        // This is the coalescing path, resolved by the stream's own position rather than a private buffer
        var payload = Frame(0x02, NoteOn);
        using var stream = new MemoryStream(
            [.. Frame(0x01, HelloBody()), .. payload, .. payload, .. payload]);
        var reader = new BridgeIpcFrameReader(stream);

        Assert.Equal(BridgeIpcFrameKind.ControlJson, (await reader.ReadAsync()).Kind);

        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();
        var third = await reader.ReadAsync();

        Assert.All([first, second, third], frame => Assert.Equal(NoteOn, frame!.Body));
        Assert.Equal(BridgeIpcReadStatus.EndOfStream, (await reader.ReadAsync()).Status);
    }

    [Fact]
    public async Task Rejects_a_zero_length_header_and_consumes_nothing_more()
    {
        // 长度为 0 时没有 kind 字节可读，属于协议错误
        // 断言流位置停在头部之后：实现没有尝试继续读任何字节
        //
        // A zero length leaves no kind byte to read, so it is a protocol error
        // The stream position stays right after the header: the implementation read nothing further
        using var stream = new MemoryStream([0, 0, 0, 0, 0x01, 0x02, 0x03]);
        var reader = new BridgeIpcFrameReader(stream);

        var result = await reader.ReadAsync();

        Assert.Equal(BridgeIpcReadStatus.InvalidFrame, result.Status);
        Assert.True(result.EndsConnection);
        Assert.Contains("zero", result.Detail, StringComparison.Ordinal);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task Rejects_a_length_above_the_cap_without_allocating_from_it()
    {
        // 长度超过 1 MiB：畸形或串位的头。断言流位置同样是"只读了头"
        // 分配发生在校验之后，因此那两次校验拦下的值不会到达数组分配
        //
        // A length above 1 MiB is a malformed or desynchronised header. The position assertion again
        // shows only the header was read. Allocation happens after the checks, so a value caught by either
        // check does not reach the array allocation
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, BridgeIpcFrameReader.MaxFrameSize + 1);
        using var stream = new MemoryStream([.. header, 0xFF, 0xFF, 0xFF]);
        var reader = new BridgeIpcFrameReader(stream);

        var result = await reader.ReadAsync();

        Assert.Equal(BridgeIpcReadStatus.InvalidFrame, result.Status);
        Assert.Contains("exceeds max", result.Detail, StringComparison.Ordinal);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task Accepts_a_frame_exactly_at_the_cap()
    {
        // 上限本身合法：上游 encodeFrame 允许 length 恰好等于 1 MiB
        // 判据是"大于上限"，这里的 body 是 MaxFrameSize-1 字节，length 正好 1 MiB
        //
        // The cap itself is legal, since the upstream encoder allows a length of exactly 1 MiB
        // The test is "above the cap": this body is MaxFrameSize-1 bytes, so length lands on the cap
        var body = new byte[BridgeIpcFrameReader.MaxFrameSize - 1];
        body.AsSpan().Fill(0x7F);
        using var stream = new MemoryStream(
            [.. Frame(0x01, HelloBody()), .. Frame(0x02, body)]);
        var reader = new BridgeIpcFrameReader(stream);

        await reader.ReadAsync();
        var frame = await reader.ReadAsync();

        Assert.Equal(BridgeIpcReadStatus.Ok, frame.Status);
        Assert.Equal(BridgeIpcFrameReader.MaxFrameSize - 1, frame.Body!.Length);
    }

    [Fact]
    public async Task Skips_an_unknown_kind_and_returns_the_next_frame()
    {
        // kind 0x7F 是保留类型：跳过并继续读，计数留在 SkippedFrameCount 里
        // 文档第 6 节把新增帧类型列为兼容变更，旧消费者因此仍工作
        //
        // Kind 0x7F is reserved: skip it, keep reading, and count it in SkippedFrameCount
        // Section 6 lists new frame types as a compatible change, which is why an older consumer works on
        using var stream = new MemoryStream(
            [.. Frame(0x01, HelloBody()), .. Frame(0x7F, [0xDE, 0xAD]), .. Frame(0x02, NoteOn)]);
        var reader = new BridgeIpcFrameReader(stream);

        await reader.ReadAsync();
        var frame = await reader.ReadAsync();

        Assert.Equal(BridgeIpcFrameKind.Midi, frame.Kind);
        Assert.Equal(NoteOn, frame.Body);
        Assert.Equal(1, reader.SkippedFrameCount);
        Assert.Equal(0x7F, reader.LastSkippedKind);
    }

    [Fact]
    public async Task Skips_a_malformed_control_frame_after_the_handshake()
    {
        // 握手之后的控制帧必须仍是 JSON 对象，语法坏了就跳过
        // 非对象（数组）同样跳过：协议规定 body 是 JSON 对象
        //
        // A control frame after the handshake must still be a JSON object and is skipped when broken
        // A non-object payload is skipped too, since the protocol fixes the body as a JSON object
        using var stream = new MemoryStream(
        [
            .. Frame(0x01, HelloBody()),
            .. Frame(0x01, "not json"u8.ToArray()),
            .. Frame(0x01, "[1,2,3]"u8.ToArray()),
            .. Frame(0x02, NoteOn),
        ]);
        var reader = new BridgeIpcFrameReader(stream);

        await reader.ReadAsync();
        var frame = await reader.ReadAsync();

        Assert.Equal(BridgeIpcFrameKind.Midi, frame.Kind);
        Assert.Equal(2, reader.SkippedFrameCount);
    }

    [Fact]
    public async Task Returns_a_control_frame_after_the_handshake_to_the_caller()
    {
        // 合法的后续控制帧不跳过：交回调用方处理
        // 上游当前只在连接开始发控制帧，但协议没有禁止以后再发
        //
        // A valid later control frame is not skipped but handed back to the caller
        // The upstream sends control frames only at the start today, though the protocol does not forbid
        // sending them later
        using var stream = new MemoryStream(
            [.. Frame(0x01, HelloBody()), .. Frame(0x01, "{\"type\":\"ping\"}"u8.ToArray())]);
        var reader = new BridgeIpcFrameReader(stream);

        await reader.ReadAsync();
        var ping = await reader.ReadAsync();

        Assert.Equal(BridgeIpcFrameKind.ControlJson, ping.Kind);
        Assert.Equal(0, reader.SkippedFrameCount);
    }

    [Fact]
    public async Task Parses_the_handshake_regardless_of_key_order()
    {
        // 键顺序没有保证（文档第 9 节），所以按对象解析而不做文本匹配
        // 这里把 proto 放到最前：按文本找 "type":"hello" 的实现在这里会失败
        //
        // Key order carries no guarantee (section 9), so fields are read from the object, not matched in
        // text. proto comes first here: an implementation searching for "type":"hello" would fail
        var body = Encoding.UTF8.GetBytes(
            "{\"proto\":\"1.0.0\",\"version\":\"9.9.9\",\"client\":\"midibridge-client\",\"type\":\"hello\"}");
        using var stream = new MemoryStream(Frame(0x01, body));
        var reader = new BridgeIpcFrameReader(stream);

        var hello = await reader.ReadAsync();

        Assert.True(reader.HandshakeConfirmed);
        Assert.Equal("1.0.0", reader.Proto);
        Assert.Equal(BridgeIpcFrameKind.ControlJson, hello.Kind);
    }

    [Fact]
    public async Task Reports_a_protocol_mismatch_when_the_type_is_wrong()
    {
        // type 不是 hello：返回 ProtocolMismatch 而不抛异常，调用方按结果分类上报
        //
        // A type other than hello yields ProtocolMismatch instead of an exception, so the caller classifies
        // the result
        using var stream = new MemoryStream(
            Frame(0x01, Encoding.UTF8.GetBytes("{\"type\":\"welcome\",\"proto\":\"1.0.0\"}")));
        var reader = new BridgeIpcFrameReader(stream);

        var result = await reader.ReadAsync();

        Assert.Equal(BridgeIpcReadStatus.ProtocolMismatch, result.Status);
        Assert.True(result.EndsConnection);
        Assert.False(reader.HandshakeConfirmed);
    }

    [Fact]
    public async Task Reports_a_protocol_mismatch_when_the_major_version_is_not_one()
    {
        // proto 2.0.0 跨了主版本：文档第 6 节只承诺同主版本向下兼容
        // 解析失败的值同样不通过：确认不了主版本就走严格一侧
        //
        // proto 2.0.0 crosses the major version, and section 6 promises compatibility within one major only
        // An unparseable value fails as well: without parsing it the major cannot be confirmed, so the
        // strict branch runs
        var protoTwo = "{\"type\":\"hello\",\"proto\":\"2.0.0\"}"u8.ToArray();
        var protoBroken = "{\"type\":\"hello\",\"proto\":\"not-a-version\"}"u8.ToArray();
        using var stream = new MemoryStream([.. Frame(0x01, protoTwo), .. Frame(0x01, protoBroken)]);
        var reader = new BridgeIpcFrameReader(stream);

        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();

        Assert.Equal(BridgeIpcReadStatus.ProtocolMismatch, first.Status);
        Assert.Contains("2.0.0", first.Detail, StringComparison.Ordinal);
        Assert.Equal(BridgeIpcReadStatus.ProtocolMismatch, second.Status);
    }

    [Fact]
    public async Task Reports_a_protocol_mismatch_when_the_first_frame_is_not_control()
    {
        // 第一帧就是 MIDI：没有握手就不能确认协议版本，按不匹配处理而不是跳过
        //
        // A MIDI frame first: without the handshake the protocol version is unconfirmed, so this is a
        // mismatch rather than a skippable frame
        using var stream = new MemoryStream([.. Frame(0x02, NoteOn), .. Frame(0x01, HelloBody())]);
        var reader = new BridgeIpcFrameReader(stream);

        var result = await reader.ReadAsync();

        Assert.Equal(BridgeIpcReadStatus.ProtocolMismatch, result.Status);
    }

    [Fact]
    public async Task Reports_a_protocol_mismatch_when_the_handshake_lacks_fields()
    {
        // 缺 type 或缺 proto 的 hello 都无法确认契约，同样走不匹配
        //
        // A hello without type or without proto cannot confirm the contract and mismatches too
        var noType = "{\"proto\":\"1.0.0\"}"u8.ToArray();
        var noProto = "{\"type\":\"hello\"}"u8.ToArray();
        var notObject = "\"hello\""u8.ToArray();
        using var stream = new MemoryStream(
            [.. Frame(0x01, noType), .. Frame(0x01, noProto), .. Frame(0x01, notObject)]);
        var reader = new BridgeIpcFrameReader(stream);

        Assert.Equal(BridgeIpcReadStatus.ProtocolMismatch, (await reader.ReadAsync()).Status);
        Assert.Equal(BridgeIpcReadStatus.ProtocolMismatch, (await reader.ReadAsync()).Status);
        Assert.Equal(BridgeIpcReadStatus.ProtocolMismatch, (await reader.ReadAsync()).Status);
    }

    [Fact]
    public async Task Reports_the_end_of_the_stream_when_a_frame_is_cut_short()
    {
        // hello 完整、MIDI 帧只到一半：读不满承诺字节就是连接结束
        // 不会把半帧当作完整帧交出去，这正是上游文档第 10 节点名要防的情形
        //
        // A complete hello then half a MIDI frame: falling short of the promised bytes ends the connection
        // The fragment is not handed over as a frame, which is the case section 10 of the upstream
        // document names for consumers to guard against
        var whole = Frame(0x02, NoteOn);
        using var stream = new MemoryStream([.. Frame(0x01, HelloBody()), .. whole[..^1]]);
        var reader = new BridgeIpcFrameReader(stream);

        await reader.ReadAsync();
        var result = await reader.ReadAsync();

        Assert.Equal(BridgeIpcReadStatus.EndOfStream, result.Status);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task Treats_a_reset_socket_as_the_end_of_the_stream()
    {
        // 对端重置抛 IOException：收成 EndOfStream 结果，调用方的循环只看结果值
        //
        // A peer reset throws IOException, folded into an EndOfStream result so the caller's loop only
        // looks at the result value
        using var stream = new ResetStream();
        var reader = new BridgeIpcFrameReader(stream);

        var result = await reader.ReadAsync();

        Assert.Equal(BridgeIpcReadStatus.EndOfStream, result.Status);
        Assert.Contains("reset", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Never_writes_to_the_stream()
    {
        // 只读语义有两层保障：类型上没有 Write 成员（结构断言），运行时也不碰 Write（ChunkedStream 的 Write 会直接抛）
        // 这条流无鉴权，往里写字节没有协议意义，还可能被对端当成攻击面
        //
        // The read-only promise holds at two levels: no Write member on the type (the structural check), and no
        // Write at run time, since ChunkedStream throws on Write
        // The stream has no authentication, so writing to it has no protocol meaning and would only widen the
        // attack surface
        Assert.DoesNotContain(typeof(BridgeIpcFrameReader).GetMethods(), m => m.Name.Contains("Write"));

        using var stream = new ChunkedStream(Frame(0x01, HelloBody()), chunkSize: 3);
        var reader = new BridgeIpcFrameReader(stream);
        Assert.True((await reader.ReadAsync()).HasFrame);
    }

    /// <summary>每次 Read 最多给 chunkSize 字节的流，用来模拟 TCP 的分段到达 / A stream yielding at most chunkSize bytes per read, imitating TCP segmentation</summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length)
            {
                return 0;
            }
            var take = Math.Min(count, Math.Min(chunkSize, data.Length - _position));
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        // 写操作直接抛：上面的用例若真的往流里写了字节，会在这里炸出来
        //
        // A write throws, so any case above that does write to the stream fails loudly here
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("the IPC stream is read-only");

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>读操作抛 IOException 的流，模拟对端重置 / A stream whose reads throw IOException, standing in for a peer reset</summary>
    private sealed class ResetStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new IOException("connection reset by peer");

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class BridgeIpcAddressParserTests
{
    [Fact]
    public void Parses_the_line_from_the_upstream_document()
    {
        // 这一行原文取自上游文档第 2 节 / The line is quoted from section 2 of the upstream document
        var endpoint = BridgeIpcAddressParser.Parse(
            "[2026-01-01 12:00:00.000] info: IPC listening on 127.0.0.1:53124 (loopback only, read-only)");

        Assert.NotNull(endpoint);
        Assert.Equal("127.0.0.1", endpoint!.Host);
        Assert.Equal(53124, endpoint.Port);
    }

    [Fact]
    public void Parses_the_ipv6_loopback_form()
    {
        // 方括号剥掉，host 可以直接交给 TcpClient.Connect
        //
        // Brackets are stripped so the host goes straight to TcpClient.Connect
        var endpoint = BridgeIpcAddressParser.Parse(
            "[2026-01-01 12:00:00.000] info: IPC listening on [::1]:53124 (loopback only, read-only)");

        Assert.NotNull(endpoint);
        Assert.Equal("::1", endpoint!.Host);
        Assert.Equal(53124, endpoint.Port);
    }

    [Fact]
    public void Parses_the_ipv6_all_interfaces_form()
    {
        // 非回环绑定那一行：解析照常工作，安全提示是界面的职责
        //
        // The non-loopback line parses as usual; surfacing the warning is the UI's job
        var endpoint = BridgeIpcAddressParser.Parse(
            "[2026-01-01 12:00:00.000] warn: IPC listening on [::]:53124 (reachable from network, NOT authenticated, read-only)");

        Assert.NotNull(endpoint);
        Assert.Equal("::", endpoint!.Host);
        Assert.Equal(53124, endpoint.Port);
    }

    [Fact]
    public void Parses_the_bare_port_form_with_an_empty_host()
    {
        var endpoint = BridgeIpcAddressParser.Parse("IPC listening on :9003 (reachable from network)");

        Assert.NotNull(endpoint);
        Assert.Equal(string.Empty, endpoint!.Host);
        Assert.Equal(9003, endpoint.Port);
    }

    [Fact]
    public void Parses_a_host_name()
    {
        var endpoint = BridgeIpcAddressParser.Parse("IPC listening on localhost:9003 (loopback only, read-only)");

        Assert.NotNull(endpoint);
        Assert.Equal("localhost", endpoint!.Host);
        Assert.Equal(9003, endpoint.Port);
    }

    [Fact]
    public void Returns_null_for_lines_that_are_not_the_listen_line()
    {
        // stdout 里的其余行都是噪声：启动横幅、MIDI 日志、go-logger 的时间戳行
        //
        // Every other line on stdout is noise: the start-up banner, MIDI logs, go-logger timestamps
        Assert.Null(BridgeIpcAddressParser.Parse(null));
        Assert.Null(BridgeIpcAddressParser.Parse(string.Empty));
        Assert.Null(BridgeIpcAddressParser.Parse("[2026-01-01 12:00:00.000] info: MIDIBridge Client 9.9.9 starting..."));
        Assert.Null(BridgeIpcAddressParser.Parse("[2026-01-01 12:00:00.000] info: Connected to server"));
    }

    [Fact]
    public void Returns_null_when_the_line_has_no_usable_port()
    {
        // 三种无端口的情况：地址缺端口、端口还是配置里的 0、端口越界
        // 端口为 0 返回 null 是关键：那说明拿到的是 ":0" 而不是实际绑定的地址
        //
        // Three portless shapes: no port at all, the configured 0, and an out-of-range port
        // Port 0 returning null matters most, since it means the value is ":0" rather than a bound address
        Assert.Null(BridgeIpcAddressParser.Parse("IPC listening on 127.0.0.1 (loopback only, read-only)"));
        Assert.Null(BridgeIpcAddressParser.Parse("IPC listening on 127.0.0.1:0 (loopback only, read-only)"));
        Assert.Null(BridgeIpcAddressParser.Parse("IPC listening on 127.0.0.1:70000 (loopback only, read-only)"));
    }

    [Fact]
    public void Returns_null_for_a_bare_ipv6_address_without_brackets()
    {
        // 上游文档第 2 节写明 IPv6 的方括号不可省略
        // "::1:53124" 这种写法不在协议里，按失败处理，不做兼容猜测
        //
        // Section 2 of the upstream document states the brackets are not optional for IPv6
        // "::1:53124" is not part of the protocol, so it fails here rather than being guessed at
        Assert.Null(BridgeIpcAddressParser.Parse("IPC listening on ::1:53124 (loopback only, read-only)"));
    }

    [Fact]
    public void Parses_a_bare_address_for_callers_that_already_hold_one()
    {
        Assert.True(BridgeIpcAddressParser.TryParseAddress("[::1]:9003", out var host6, out var port6));
        Assert.Equal("::1", host6);
        Assert.Equal(9003, port6);

        Assert.True(BridgeIpcAddressParser.TryParseAddress("example.com:80", out var host, out var port));
        Assert.Equal("example.com", host);
        Assert.Equal(80, port);

        // 缺端口与带正号的写法都拒绝：int.TryParse 会接受 "+80"，这里不会
        //
        // A missing port and a plus-signed port are refused; int.TryParse would take "+80", this does not
        Assert.False(BridgeIpcAddressParser.TryParseAddress("127.0.0.1", out _, out _));
        Assert.False(BridgeIpcAddressParser.TryParseAddress("example.com:+80", out _, out _));
        Assert.False(BridgeIpcAddressParser.TryParseAddress(null, out _, out _));
    }
}

public sealed class BridgeStatusParserTests
{
    [Fact]
    public void Classifies_the_start_up_banner()
    {
        Assert.Equal(
            BridgeStatus.Starting,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] info: MIDIBridge Client 9.9.9 starting..."));
    }

    [Fact]
    public void Classifies_the_ipc_listen_line()
    {
        Assert.Equal(
            BridgeStatus.ListeningIpc,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] info: IPC listening on 127.0.0.1:53124 (loopback only, read-only)"));
    }

    [Fact]
    public void Classifies_the_connection_lifecycle_phrases()
    {
        // 逐条对应 i18n.go 的 enStrings 与 wsclient.go；地址与 reason 都用通用占位
        //
        // One case per entry in enStrings and wsclient.go, with placeholder addresses and reasons
        Assert.Equal(
            BridgeStatus.Connecting,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] info: Connecting to server: ws://example.com:9001"));
        Assert.Equal(
            BridgeStatus.Connected,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] info: Connected to server"));
        Assert.Equal(
            BridgeStatus.Authenticated,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] info: Authenticated, receiving MIDI signals"));
        Assert.Equal(
            BridgeStatus.AuthFailed,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] error: Authentication failed: bad password. Check password in data/config.json"));
        Assert.Equal(
            BridgeStatus.Kicked,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] warn: Kicked by server: Server shutting down"));
        Assert.Equal(
            BridgeStatus.Disconnected,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] warn: Disconnected from server (code: 1000)"));
        Assert.Equal(
            BridgeStatus.Reconnecting,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] info: Reconnecting in 3s (attempt 1)..."));
        Assert.Equal(
            BridgeStatus.MaxReconnects,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] error: Max reconnect attempts reached (5), stopping"));
    }

    [Fact]
    public void Classifies_the_exit_line()
    {
        // main.go 在正常退出与被踢后退出两条路径都打印 Goodbye.
        //
        // main.go prints Goodbye. both on a normal exit and after exiting from a kick
        Assert.Equal(BridgeStatus.Exited, BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] info: Goodbye."));
    }

    [Fact]
    public void Leaves_the_ipc_runtime_logs_unclassified()
    {
        // IPC 侧的运行时日志（丢弃告警等）不是连接状态转移，落 None
        // 想观察它们就走原始日志，不挤进这个枚举
        //
        // IPC runtime logs such as the drop warning are not connection state transitions and fall to None
        // Anyone who needs them reads the raw log instead of stretching this enum
        Assert.Equal(
            BridgeStatus.None,
            BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] warn: IPC consumer is slow, dropped 1 frame(s) so far"));
    }

    [Fact]
    public void Leaves_unknown_and_localised_lines_unclassified()
    {
        // 最后一行是中文文案：状态识别依赖上游以 --lang en 启动（BridgeArguments 已固定）
        // 语言传错时这些短语匹配不上，状态落到 None
        //
        // The last line is Chinese copy: state detection relies on the upstream starting with --lang en, which
        // BridgeArguments fixes
        // With another language these phrases do not match and the status falls to None
        Assert.Equal(BridgeStatus.None, BridgeStatusParser.Parse(null));
        Assert.Equal(BridgeStatus.None, BridgeStatusParser.Parse(string.Empty));
        Assert.Equal(BridgeStatus.None, BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] info: something else"));
        Assert.Equal(BridgeStatus.None, BridgeStatusParser.Parse("[2026-01-01 12:00:00.000] info: 已连接到服务端"));
    }
}
