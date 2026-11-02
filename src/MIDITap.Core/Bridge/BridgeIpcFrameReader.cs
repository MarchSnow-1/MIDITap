// BridgeIpcFrameReader.cs — 读取上游 midibridge-client 本地 IPC 的帧
// 实现依据上游文档 docs/PROTOCOL_zh-CN.md 第 3 与第 4 节，以及它自己的 src/ipc.go
//
// 帧格式：[uint32 LE length][uint8 kind][body...]
// length 统计的是 kind 加 body 的字节数，**不含**这 4 字节自身
// 因此读完 length 后还要再读 length 个字节，其中第 1 个是 kind，剩下 length-1 个是 body
//
// 这个读取循环要处理的三个边界：
//   1) 部分读取：NetworkStream.Read 只给当前可用的字节，
//      因此按承诺字节数循环读满，读不满就当作连接结束
//   2) 粘包：一次 Read 可能返回若干完整帧加半帧，因此每次调用只消费一帧，剩余字节留在流里
//      剩下的字节由下一次调用从帧边界处接着读（这里不自己缓冲，交给 Stream 的读取语义）
//   3) 畸形长度：length 为 0 或超过 1 MiB 时立刻返回协议错误，且**不按该值分配内存**
//      否则一个 4 字节的头就能让进程去申请几 GB
//
// kind 的分派按文档第 4 节：0x01 控制 JSON、0x02 MIDI，其余是保留类型
// 未知 kind 跳过并继续读下一帧，而不是当致命错误关闭连接，因此上游新增帧类型时本消费者仍然工作
// （文档第 6 节把新增帧类型列为兼容变更）
// 被跳过的帧计数在 SkippedFrameCount 里，界面想知道流是否在跑偏就读这个值
//
// 握手帧（连接上的第一帧）单独校验：必须是 0x01 且 JSON 对象里 type=="hello"、proto 主版本为 1
// 不匹配返回 ProtocolMismatch 结果而不抛异常：调用方按结果分类上报，和 UpdateFailure 的做法一致
// 校验用 System.Text.Json 按对象取字段，不做文本匹配 —— 文档第 9 节明确 JSON 键顺序没有保证
// client 字段只记录不参与判定：协议兼容性由 proto 决定，换一个实现名不影响帧布局
// proto 解析交给 Core 已引用的 Semver 库，不自己按点号切字符串
//
// 本类只做读取：类型上没有任何写方法，因此不会向这条 socket 发出一个字节
// 这一点与文档第 5 节"只读"的约定相符
//
// Reads the frames of the local IPC stream served by the upstream midibridge-client
// The basis is docs/PROTOCOL_zh-CN.md sections 3 and 4 in that repository, and its own src/ipc.go
//
// Frame layout: [uint32 LE length][uint8 kind][body...]
// length counts the kind byte plus the body, and does NOT include those 4 bytes themselves
// So after reading length there are exactly length more bytes to read, the first being kind
// and the remaining length-1 being the body
//
// Three boundaries this read loop handles:
//   1) Partial reads: NetworkStream.Read returns only what is available right now
//      The promised byte count is therefore filled in a loop, and a shortfall is treated as the end of the stream
//   2) Coalesced frames: one Read can return several whole frames plus a fragment
//      Each call consumes exactly one frame and leaves the rest in the stream for the next call
//      No private buffer is kept, since the Stream's own read semantics already do that job
//   3) A malformed length: zero or above 1 MiB returns a protocol error immediately, and memory is NOT
//      allocated from that value — otherwise a 4-byte header could make the process ask for gigabytes
//
// Kind dispatch follows section 4: 0x01 control JSON, 0x02 MIDI, anything else is reserved
// An unknown kind is skipped and reading continues, rather than treated as fatal and closing the connection,
// so this consumer keeps working when the upstream adds a frame type, which section 6 lists as a compatible change
// Skipped frames are counted in SkippedFrameCount for anyone who wants to know whether the stream is drifting
//
// The handshake frame, the first on a connection, is checked separately: kind 0x01, a JSON object whose
// type is "hello" and whose proto has major version 1
// A mismatch returns a ProtocolMismatch result instead of throwing, so the caller classifies it the way
// the update flow classifies an UpdateFailure
// Fields are taken with System.Text.Json as an object, not by matching text: section 9 states that JSON
// key order carries no guarantee
// The client field is recorded but not enforced, since compatibility is decided by proto and a different
// implementation name does not change the frame layout
// Parsing proto uses the Semver library Core already references rather than splitting the string on dots
//
// This class only reads: the type exposes no write method, so not one byte goes back to the socket
// That matches the read-only promise in section 5

using System.Buffers.Binary;
using System.Text.Json;
using Semver;

namespace MIDITap.Core.Bridge;

/// <summary>
/// 帧类型。数值与上游协议一致，保留 0 给"未识别"这个内部含义
///
/// Frame kinds. The values match the upstream protocol, and 0 stays for the internal "unrecognized" case
/// </summary>
public enum BridgeIpcFrameKind
{
    /// <summary>未识别或本次没有帧 / Unrecognized, or no frame from this call</summary>
    Unknown = 0,

    /// <summary>0x01 控制帧，body 是 UTF-8 JSON 对象 / 0x01 control frame whose body is a UTF-8 JSON object</summary>
    ControlJson,

    /// <summary>0x02 数据帧，body 是一条完整的原始 MIDI 消息 / 0x02 data frame whose body is one complete raw MIDI message</summary>
    Midi,
}

/// <summary>
/// 一次读取的结局。分成四类，因为界面与日志要区分"正常断开"与"流不可信"
/// 前三类中只有 Ok 携带帧数据
///
/// How one read ended. The classes stay distinct because the UI and the log must tell a normal
/// disconnect apart from a stream that can no longer be trusted. Only Ok carries frame data
/// </summary>
public enum BridgeIpcReadStatus
{
    /// <summary>读到一帧 / A frame was read</summary>
    Ok,

    /// <summary>对端关闭，或帧在承诺字节数之前截断 / The peer closed, or a frame was cut short before its promised bytes</summary>
    EndOfStream,

    /// <summary>长度不合法（0 或超过上限），流已不可信 / An invalid length (zero or above the cap): the stream can no longer be trusted</summary>
    InvalidFrame,

    /// <summary>握手帧不符合本消费者所说的协议版本 / The handshake frame does not speak the protocol this consumer implements</summary>
    ProtocolMismatch,
}

/// <summary>
/// 一次读取的结果
/// <paramref name="Body"/> 是 kind 之后的原始字节副本，调用方可以直接交给 MIDI 状态机
/// 之所以是副本：它必须在下一次读取之后仍然有效，否则界面持有的音符会引用到被复用的缓冲区
///
/// The result of one read. Body is the raw bytes after the kind byte, ready to hand to the MIDI
/// state machine. It is a copy because it has to stay valid across the next read, so a note the UI
/// is still holding cannot refer to a reused buffer
/// </summary>
/// <param name="Status">结局 / How the read ended</param>
/// <param name="Kind">帧类型 / Which kind of frame</param>
/// <param name="Body">载荷字节 / The payload bytes</param>
/// <param name="Detail">错误类结果的原因，只用于日志 / Why an error-class result happened, for the log only</param>
public sealed record BridgeIpcReadResult(
    BridgeIpcReadStatus Status,
    BridgeIpcFrameKind Kind = BridgeIpcFrameKind.Unknown,
    byte[]? Body = null,
    string? Detail = null)
{
    /// <summary>本次调用是否交出一帧 / Whether this call hands over a frame</summary>
    public bool HasFrame => Status == BridgeIpcReadStatus.Ok && Body is not null;

    /// <summary>
    /// 这个结局是否意味着连接该结束了
    /// 三类非 Ok 结局都返回 true：截断、畸形长度、协议不符之后都无法再按帧边界定位
    ///
    /// Whether the connection should end on this outcome
    /// All three non-Ok outcomes return true: after a truncation, an invalid length or a protocol
    /// mismatch the frame boundary can no longer be located
    /// </summary>
    public bool EndsConnection => Status != BridgeIpcReadStatus.Ok;
}

/// <summary>
/// 按长度前缀读取帧的消费者
/// 一个实例绑定一条流，内部只保留 4 字节头部缓冲，不缓存载荷
///
/// A consumer that reads frames by their length prefix. One instance belongs to one stream and keeps
/// only a 4-byte header buffer, while each payload is handed over as its own array
/// </summary>
public sealed class BridgeIpcFrameReader
{
    /// <summary>控制帧的 kind / The kind byte of a control frame</summary>
    public const byte KindControlJson = 0x01;

    /// <summary>MIDI 帧的 kind / The kind byte of a MIDI frame</summary>
    public const byte KindMidi = 0x02;

    /// <summary>
    /// 单帧长度上限，与上游 ipc.go 的 ipcMaxFrameSize 相同（1 MiB）
    /// 判据是"大于上限"，等于上限的帧仍被接受
    ///
    /// The per-frame length cap, matching ipcMaxFrameSize in the upstream ipc.go (1 MiB)
    /// The test is "above the cap", so a frame exactly at the cap is still accepted
    /// </summary>
    public const int MaxFrameSize = 1 << 20;

    /// <summary>
    /// 握手帧里期望的协议主版本
    /// 文档第 6 节：同主版本号内向下兼容，因此只比主版本，不比次版本与补丁号
    ///
    /// The protocol major version this consumer implements
    /// Section 6: releases within one major version are backward compatible, so only the major is compared
    /// </summary>
    public const int SupportedProtoMajor = 1;

    /// <summary>握手帧的 type 字段取值 / The value of the handshake frame's type field</summary>
    public const string HelloFrameType = "hello";

    private readonly Stream _stream;
    private readonly byte[] _header = new byte[4];
    private readonly byte[] _kindByte = new byte[1];

    private bool _handshakeConfirmed;

    /// <summary>握手是否已经通过；通过后 Proto 与 UpstreamVersion 才有值 / Whether the handshake passed; only then are Proto and UpstreamVersion set</summary>
    public bool HandshakeConfirmed => _handshakeConfirmed;

    /// <summary>握手帧报告的协议版本原值 / The protocol version the handshake reported, verbatim</summary>
    public string? Proto { get; private set; }

    /// <summary>握手帧报告的上游构建版本，仅用于展示 / The upstream build version from the handshake, for display only</summary>
    public string? UpstreamVersion { get; private set; }

    /// <summary>握手帧报告的实现名，仅用于展示与排查 / The implementation name from the handshake, for display and diagnosis</summary>
    public string? UpstreamClient { get; private set; }

    /// <summary>因未知 kind 或空载荷而被跳过的帧数 / How many frames were skipped for an unknown kind or an empty payload</summary>
    public int SkippedFrameCount { get; private set; }

    /// <summary>最近一次被跳过帧的 kind / The kind byte of the most recently skipped frame</summary>
    public byte LastSkippedKind { get; private set; }

    /// <summary>
    /// 绑定一条只读流；<paramref name="stream"/> 为空属于调用方的用法错误，直接抛出
    ///
    /// Binds one read-only stream. A null stream is a caller mistake and throws
    /// </summary>
    public BridgeIpcFrameReader(Stream stream)
        => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>
    /// 读取下一帧。未知 kind 与空载荷的帧在本方法内部跳掉并继续读，因此一次调用可能消费多帧
    /// 调用方每次拿到的都是有意义的一帧，或者一个结束连接的结局
    /// 取消信号原样抛出，不折成结果值：那是调用方主动停止，不是流出了问题
    ///
    /// Reads the next frame. Frames with an unknown kind or an empty payload are skipped inside this
    /// method and reading continues, so one call may consume several frames. The caller therefore gets
    /// either one meaningful frame or an outcome that ends the connection. Cancellation is rethrown
    /// rather than folded into a result value, since it means the caller stopped on purpose
    /// </summary>
    public async Task<BridgeIpcReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                if (!await FillAsync(_header, cancellationToken).ConfigureAwait(false))
                {
                    return new BridgeIpcReadResult(BridgeIpcReadStatus.EndOfStream);
                }

                // 长度统计 kind + body，不含头部自身；小端，见文档第 3 节
                //
                // The length counts kind plus body and not the header itself, in little-endian order (section 3)
                var length = BinaryPrimitives.ReadUInt32LittleEndian(_header);

                // 两个畸形值在进入分配之前拒掉：0 意味着后面没有 kind 字节，超过上限则可能是恶意或被串位的头
                // 此处提前返回，所以下面的 new byte[] 拿不到这些值
                //
                // Both malformed values are refused before any allocation: zero leaves no kind byte to read,
                // and anything above the cap may be malicious or a desynchronised header. The early return
                // below sits ahead of the array allocation, so that allocation is not reached with those values
                if (length == 0)
                {
                    return new BridgeIpcReadResult(
                        BridgeIpcReadStatus.InvalidFrame,
                        Detail: "invalid frame: zero length");
                }
                if (length > MaxFrameSize)
                {
                    return new BridgeIpcReadResult(
                        BridgeIpcReadStatus.InvalidFrame,
                        Detail: $"invalid frame: length {length} exceeds max {MaxFrameSize}");
                }

                if (!await FillAsync(_kindByte, cancellationToken).ConfigureAwait(false))
                {
                    return new BridgeIpcReadResult(
                        BridgeIpcReadStatus.EndOfStream,
                        Detail: "connection closed mid-frame");
                }
                var kind = _kindByte[0];

                // length-1 是 body 的字节数；length 至少为 1，所以下面不会得到负数
                //
                // length - 1 is the body size. length is at least 1 here, so no negative size follows
                var bodySize = (int)length - 1;
                byte[] body;
                if (bodySize > 0)
                {
                    var buffer = new byte[bodySize];
                    if (!await FillAsync(buffer, cancellationToken).ConfigureAwait(false))
                    {
                        return new BridgeIpcReadResult(
                            BridgeIpcReadStatus.EndOfStream,
                            Detail: "connection closed mid-frame");
                    }
                    body = buffer;
                }
                else
                {
                    body = [];
                }

                if (!_handshakeConfirmed)
                {
                    var mismatch = ValidateHello(kind, body);
                    if (mismatch is not null)
                    {
                        return mismatch;
                    }
                    return new BridgeIpcReadResult(BridgeIpcReadStatus.Ok, BridgeIpcFrameKind.ControlJson, body);
                }

                switch (kind)
                {
                    case KindControlJson:
                        // 握手之后仍有控制帧时交回调用方，但它不是 MIDI，空载荷或非法 JSON 都跳过
                        //
                        // A control frame after the handshake goes back to the caller, since it is not MIDI
                        // An empty payload or invalid JSON inside it is skipped
                        if (body.Length == 0 || !IsJsonObject(body))
                        {
                            Skip(kind);
                            continue;
                        }
                        return new BridgeIpcReadResult(BridgeIpcReadStatus.Ok, BridgeIpcFrameKind.ControlJson, body);

                    case KindMidi:
                        // 空 body 的 MIDI 帧没有任何可交付的字节，跳过而不交给状态机
                        //
                        // A MIDI frame with an empty body has no bytes to hand over, so it is skipped
                        // rather than passed to the state machine
                        if (body.Length == 0)
                        {
                            Skip(kind);
                            continue;
                        }
                        return new BridgeIpcReadResult(BridgeIpcReadStatus.Ok, BridgeIpcFrameKind.Midi, body);

                    default:
                        Skip(kind);
                        continue;
                }
            }
        }
        catch (Exception err) when (err is IOException or ObjectDisposedException)
        {
            // 对端重置（SocketException 派生自 IOException）与本地关闭流都归为连接结束
            // 这里不重新抛出，是为了让调用方的循环只看结果值
            //
            // A peer reset (SocketException derives from IOException) and closing the stream locally both
            // count as the connection ending. Neither is rethrown, so the caller's loop only looks at the result
            return new BridgeIpcReadResult(
                BridgeIpcReadStatus.EndOfStream,
                Detail: "read failed: " + err.Message);
        }
    }

    /// <summary>
    /// 循环读取直到填满 buffer；中途遇到对端关闭则返回 false
    /// 只在整个缓冲都读满时返回 true，这就是"必须读满承诺字节"的那一条
    ///
    /// Reads until the buffer is full and returns false if the peer closes partway through
    /// true is returned only when every byte of the buffer arrived, which is the "fill the promised
    /// bytes" requirement
    /// </summary>
    private async Task<bool> FillAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            var read = await _stream
                .ReadAsync(buffer.AsMemory(written, buffer.Length - written), cancellationToken)
                .ConfigureAwait(false);

            // read 为 0 表示对端已关闭：剩余字节不会再来
            //
            // A read of 0 means the peer closed: the remaining bytes will not arrive
            if (read <= 0)
            {
                return false;
            }
            written += read;
        }
        return true;
    }

    /// <summary>
    /// 校验连接上的第一帧。通过则记录 proto 等信息并返回 null，不通过返回 ProtocolMismatch
    /// 三种情况都算不通过：kind 不是控制帧、JSON 不是对象或缺 type/proto 字段、proto 主版本不是 1
    /// proto 无法解析时同样不通过：解析不了就无从确认它是第 1 版，走严格一侧
    /// 本消费者要在一条会被执行的流上按键，所以字段布局未知时停止，由调用方报"协议版本不符"
    ///
    /// Validates the first frame on a connection, returning null on success and ProtocolMismatch otherwise
    /// Three cases fail: the kind is not a control frame, the JSON is not an object or lacks type/proto,
    /// and the proto major version is not 1
    /// An unparseable proto fails too: without parsing it the major version cannot be confirmed, so the
    /// strict branch runs. This consumer drives key injection from the stream, so it stops when the field
    /// layout is unknown and lets the caller report a protocol mismatch
    /// </summary>
    private BridgeIpcReadResult? ValidateHello(byte kind, byte[] body)
    {
        if (kind != KindControlJson)
        {
            return Mismatch("the first frame is not a control frame (kind 0x"
                            + kind.ToString("X2") + ")");
        }

        using var document = ParseJson(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Mismatch("the handshake frame is not a JSON object");
        }

        var root = document.RootElement;
        if (!TryReadString(root, "type", out var type))
        {
            return Mismatch("the handshake frame has no type field");
        }
        if (type != HelloFrameType)
        {
            return Mismatch("unexpected handshake type: " + type);
        }

        // 实现名只记录不判定：换一个名字的实现只要帧布局不变就能工作
        //
        // The implementation name is recorded but not enforced: a differently named implementation
        // works as long as the frame layout is unchanged
        TryReadString(root, "client", out var client);
        TryReadString(root, "version", out var version);
        if (!TryReadString(root, "proto", out var proto) || string.IsNullOrWhiteSpace(proto))
        {
            return Mismatch("the handshake frame has no proto field");
        }

        if (!SemVersion.TryParse(proto, SemVersionStyles.Any, out var parsed)
            || parsed.Major != SupportedProtoMajor)
        {
            return Mismatch("unsupported protocol version: " + proto);
        }

        Proto = proto;
        UpstreamVersion = version;
        UpstreamClient = client;
        _handshakeConfirmed = true;
        return null;
    }

    /// <summary>把不通过的情形收敛成一个结果 / Turns a failed check into one result value</summary>
    private static BridgeIpcReadResult Mismatch(string detail)
        => new(BridgeIpcReadStatus.ProtocolMismatch, Detail: detail);

    /// <summary>记一次跳过 / Records one skipped frame</summary>
    private void Skip(byte kind)
    {
        SkippedFrameCount++;
        LastSkippedKind = kind;
    }

    /// <summary>
    /// 把载荷解析成 JSON 文档，语法不合法返回 null
    /// 解析失败不抛异常：本类的契约是用结果值表达问题
    ///
    /// Parses the payload into a JSON document, returning null when the syntax is invalid
    /// A parse failure does not throw, since this class expresses problems through result values
    /// </summary>
    private static JsonDocument? ParseJson(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (Exception err) when (err is JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>载荷是否为 JSON 对象 / Whether the payload is a JSON object</summary>
    private static bool IsJsonObject(byte[] body)
    {
        using var document = ParseJson(body);
        return document is not null && document.RootElement.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// 按键取字符串字段，不做文本匹配（文档第 9 节：键顺序没有保证）
    /// 值不是字符串时视为缺失，由调用方决定
    ///
    /// Reads one string field by key rather than matching text (section 9: key order carries no
    /// guarantee). A value that is not a string counts as absent, and the caller decides what to do
    /// </summary>
    private static bool TryReadString(JsonElement root, string key, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString();
        return value is not null;
    }
}
