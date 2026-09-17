// MidiLoopbackSession.cs — 虚拟 MIDI 回环会话：向输出端口写、从同名输入端口读回
//
// 有意混用两套 API
//   * **接收**走 NAudio
//     这是被测试应用实际使用的栈（BackendService 用 NAudio.Midi）
//     因此它才是需要被验证的那条路
//   * **发送**走原始 WinMM P/Invoke
//     测试刺激必须精确到字节，不能经过 NAudio 对消息的再解释
//     否则"发送端规范化"可能掩盖被测代码的解析问题
//
// Virtual MIDI loopback session: writes to the output port
// Reads back from the input port of the same name
//
// Two APIs are mixed on purpose
// Reception goes through NAudio, because that is the stack the app really uses
// BackendService uses NAudio.Midi
// The stimulus uses raw WinMM P/Invoke instead
// That writes the exact bytes with no intermediary reinterpreting them
// Otherwise sender-side normalisation could mask a parsing bug in the code under test

using System.Runtime.InteropServices;
using NAudio.Midi;

namespace MIDITap.Integration.Tests;

public sealed class MidiLoopbackSession : IDisposable
{
    private const uint MidiIoStatusNoError = 0;

    [DllImport("winmm.dll", EntryPoint = "midiOutOpen")]
    private static extern uint MidiOutOpen(out IntPtr handle, uint deviceId, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll", EntryPoint = "midiOutShortMsg")]
    private static extern uint MidiOutShortMsg(IntPtr handle, uint message);

    [DllImport("winmm.dll", EntryPoint = "midiOutClose")]
    private static extern uint MidiOutClose(IntPtr handle);

    private readonly MidiIn _input;
    private readonly IntPtr _output;
    private readonly List<byte[]> _received = [];
    // 用 object 而非 System.Threading.Lock：本次只做框架升级，不改锁的实现语义
    //
    // An object rather than System.Threading.Lock: this change only moves the target framework, leaving lock semantics untouched
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _signal = new(false);

    public MidiLoopbackSession()
    {
        if (!MidiTestPort.IsAvailable)
        {
            throw new InvalidOperationException(MidiTestPort.SkipReason);
        }

        var rc = MidiOutOpen(out _output, (uint)MidiTestPort.OutputIndex, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != MidiIoStatusNoError)
        {
            throw new InvalidOperationException($"midiOutOpen failed with MMSYSERR {rc}");
        }

        // NAudio 的回调在 WinMM 线程上触发，因此接收端必须自行加锁（不能用 UI 线程假设）
        //
        // NAudio callbacks fire on the WinMM thread, so the receive side must lock itself
        // A UI-thread assumption is not available
        _input = new MidiIn(MidiTestPort.InputIndex);
        _input.MessageReceived += OnMessageReceived;
        _input.Start();

        WaitUntilReady();
    }

    /// <summary>
    /// 等待回环真正可用
    /// **必须先握手再开始测试**：WinMM 的 MIDI 输入在 Start() 之后需要一小段时间才真正生效
    /// 这期间发送的消息会被**静默丢弃**（不报错、也不排队）
    /// 用固定 Thread.Sleep 掩盖它会让测试时快时慢，所以这里发探针并确认收到为止
    ///
    /// Waits until the loopback is genuinely usable
    /// A handshake is required before any test
    /// A WinMM MIDI input is not live the instant Start() returns
    /// Messages sent in that window are silently dropped (no error, no queue)
    /// Papering over it with a fixed Thread.Sleep would make the suite flaky by construction
    /// So a probe is sent and confirmed instead
    /// </summary>
    private void WaitUntilReady(int timeoutMs = 5000, int probeTimeoutMs = 250)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Clear();
            // 探针用"控制变更 0 -> 0"：对任何可能的监听者都没有副作用，且是 3 字节通道消息（与测试主体同类，能证明的正是我们要用的那条通路）
            //
            // The probe uses a control change 0 -> 0
            // It has no side effect on any conceivable listener
            // It is also a 3-byte channel message, the same class as the test body
            // That is exactly the path that needs proving
            SendRaw(0xB0, 0x00, 0x00);
            if (WaitForCount(1, probeTimeoutMs))
            {
                // 丢掉探针，保证测试看到的第一条消息一定是它自己发的
                //
                // Discard the probe so the first message a test sees is guaranteed to be its own
                Clear();
                return;
            }
        }

        throw new InvalidOperationException(
            $"MIDI loopback '{MidiTestPort.Name}' opened but never delivered a probe message " +
            $"within {timeoutMs}ms. Check that the port is not being held exclusively by " +
            "another application (e.g. a running MIDITap instance).");
    }

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        var bytes = MessageBytes.FromRawMessage((uint)e.RawMessage);
        lock (_gate)
        {
            _received.Add(bytes);
        }
        _signal.Set();
    }

    /// <summary>
    /// 已接收消息的快照（拷贝，避免调用方持锁）
    ///
    /// Snapshot of the received messages (a copy, so the caller holds no lock)
    /// </summary>
    public List<byte[]> Snapshot()
    {
        lock (_gate)
        {
            return [.. _received];
        }
    }

    /// <summary>清空已接收缓冲，便于分段断言 / Clears the receive buffer so assertions can be staged</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _received.Clear();
        }
        _signal.Reset();
    }

    /// <summary>
    /// 等待至少 <paramref name="count"/> 条消息到达；超时返回 false
    ///
    /// Waits for at least <paramref name="count"/> messages; returns false on timeout
    /// </summary>
    public bool WaitForCount(int count, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_received.Count >= count)
                {
                    return true;
                }
            }
            _signal.Wait(25);
        }
        lock (_gate)
        {
            return _received.Count >= count;
        }
    }

    /// <summary>
    /// 发送一条三字节通道消息（原始字节，不经任何再解释）
    ///
    /// Sends one 3-byte channel message as raw bytes, with no reinterpretation
    /// </summary>
    public void SendRaw(byte status, byte data1, byte data2)
    {
        // WinMM 的短消息按小端打包：status | data1<<8 | data2<<16
        //
        // WinMM packs short messages little-endian: status | data1<<8 | data2<<16
        var packed = (uint)(status | (data1 << 8) | (data2 << 16));
        var rc = MidiOutShortMsg(_output, packed);
        if (rc != MidiIoStatusNoError)
        {
            throw new InvalidOperationException($"midiOutShortMsg failed with MMSYSERR {rc}");
        }
    }

    public void SendNoteOn(byte note, byte velocity = 100, byte channel = 0)
        => SendRaw((byte)(0x90 | (channel & 0x0F)), note, velocity);

    public void SendNoteOff(byte note, byte velocity = 0, byte channel = 0)
        => SendRaw((byte)(0x80 | (channel & 0x0F)), note, velocity);

    public void Dispose()
    {
        try
        {
            _input.Stop();
            _input.MessageReceived -= OnMessageReceived;
            _input.Dispose();
        }
        catch
        {
            // 关闭失败不应掩盖测试本身的失败 / A failure to close must not mask a failure of the test itself
        }
        if (_output != IntPtr.Zero)
        {
            _ = MidiOutClose(_output);
        }
        _signal.Dispose();
    }
}

/// <summary>
/// 把 NAudio 的打包消息还原为字节，用于精确比对
///
/// Turns NAudio's packed message back into bytes for exact comparison
/// </summary>
public static class MessageBytes
{
    /// <summary>
    /// 按状态字节推断消息长度。通道消息为 2 或 3 字节；系统消息长度各异
    /// 测试只发送通道消息，但仍完整处理，避免边界断言依赖"永远是 3 字节"的假设
    ///
    /// Infers the message length from the status byte
    /// Channel messages are 2 or 3 bytes, and system messages vary
    /// Tests only send channel messages, but everything is handled
    /// That way boundary assertions do not rest on the assumption that a message is always 3 bytes
    /// </summary>
    public static byte[] FromRawMessage(uint raw)
        => ToBytes(raw, LengthForStatus((byte)(raw & 0xFF)));

    /// <summary>
    /// 按 MIDI 规范给出长度：1/2/3 字节，或 0 表示长度不定（SysEx）
    ///
    /// Gives the length per the MIDI spec: 1/2/3 bytes, or 0 for indeterminate length (SysEx)
    /// </summary>
    public static int LengthForStatus(byte status)
    {
        if (status < 0x80)
        {
            // 数据字节不是合法状态；退化为 3 字节以便断言失败时能看到原始值
            //
            // A data byte is not a valid status; it degrades to 3 bytes
            // That way a failing assertion still shows the raw value
            return 3;
        }
        var high = (byte)(status & 0xF0);
        return high switch
        {
            0x80 or 0x90 or 0xA0 or 0xB0 or 0xE0 => 3,
            // Program change 与 channel pressure 只有 1 个数据字节
            //
            // Program change and channel pressure have only 1 data byte
            0xC0 or 0xD0 => 2,
            _ => status switch
            {
                0xF1 or 0xF3 => 2,
                0xF2 => 3,
                0xF6 or 0xF8 or 0xF9 or 0xFA or 0xFB or 0xFC or 0xFD or 0xFE or 0xFF => 1,
                // 0xF0 SysEx：长度不定，无法从单条短消息推断
                //
                // 0xF0 SysEx: the length is indeterminate
                // It cannot be inferred from a single short message
                _ => 0,
            },
        };
    }

    public static byte[] ToBytes(uint raw, int length)
    {
        // 长度为 0（SysEx）时给一个保守的最小表示，调用方通常不会走到这里
        //
        // For length 0 (SysEx) this is a conservative minimal representation
        // Callers normally never get here
        var count = length is > 0 and <= 4 ? length : 3;
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
        {
            bytes[i] = (byte)((raw >> (8 * i)) & 0xFF);
        }
        return bytes;
    }

    /// <summary>
    /// 便于断言失败时阅读：0x90 3C 64 这样的十六进制串
    ///
    /// Easy to read when an assertion fails: a hex string such as 0x90 3C 64
    /// </summary>
    public static string Describe(IEnumerable<byte> message)
        => string.Join(' ', message.Select(b => b.ToString("X2")));
}
