// MidiTransportTests.cs — 真实 MIDI 传输层：字节是否原样、顺序是否保持、边界是否越界
//
// 这一层验证的是**应用无法控制但依赖的部分**：驱动、虚拟端口与 NAudio 的封装
// 若这里不成立，上层状态机测得再全也没有意义（输入本身就是错的）
//
// Real MIDI transport: are bytes preserved, is order kept, do boundaries hold?
//
// This layer verifies the parts the app cannot control
// Those parts are the driver, the virtual port and NAudio's wrapper
// The app depends on all of them
// If this layer does not hold, testing the state machine above it proves little
// The reason is that the input itself would be wrong

using Xunit;

namespace MIDITap.Integration.Tests;

public sealed class MidiTransportTests
{
    [MidiFact]
    public void Note_on_and_note_off_round_trip_byte_exactly()
    {
        using var session = new MidiLoopbackSession();

        session.SendNoteOn(60, 100);
        session.SendNoteOff(60);
        Assert.True(session.WaitForCount(2), "expected two messages to arrive");

        var received = session.Snapshot();
        Assert.Equal([0x90, 0x3C, 0x64], received[0]);
        Assert.Equal([0x80, 0x3C, 0x00], received[1]);
    }

    [MidiFact]
    public void All_128_notes_round_trip_in_order()
    {
        // 边界：0 与 127 是 MIDI 音符的合法端点，必须都能传输且不丢
        //
        // Boundary: 0 and 127 are the legal endpoints of a MIDI note
        // Both must transfer without loss
        using var session = new MidiLoopbackSession();

        for (var note = 0; note <= 127; note++)
        {
            session.SendNoteOn((byte)note, 1);
        }

        Assert.True(session.WaitForCount(128, 4000), "expected all 128 note-ons");

        var received = session.Snapshot();
        Assert.Equal(128, received.Count);
        for (var note = 0; note <= 127; note++)
        {
            Assert.Equal([0x90, (byte)note, 0x01], received[note]);
        }
    }

    [MidiFact]
    public void Velocity_zero_note_on_is_transported_unchanged()
    {
        // MIDI 规范里 0x90 + velocity 0 等价于 note-off
        // 虚拟端口**不会**替我们改写它，因此上层必须自己处理这个特例
        // 这条测试把"责任在上层"固定下来
        //
        // Per the MIDI spec, 0x90 with velocity 0 means note-off
        // The transport does NOT rewrite it, so the layer above must handle the quirk itself
        // This test pins down where that responsibility lies
        using var session = new MidiLoopbackSession();

        session.SendRaw(0x90, 60, 0);
        Assert.True(session.WaitForCount(1));

        Assert.Equal([0x90, 0x3C, 0x00], session.Snapshot()[0]);
    }

    // 只用 [MidiTheory]（它派生自 Theory，InlineData 照常生效）
    // 再写一个 [Theory] 会被 xUnit 分析器判为重复特性（xUnit1002）
    //
    // Only [MidiTheory] is used (it derives from Theory, so InlineData still works)
    // Adding another [Theory] would be flagged by the xUnit analyser as a duplicate attribute (xUnit1002)
    [MidiTheory]
    [InlineData(0)]
    [InlineData(15)]
    public void Channel_boundaries_round_trip(byte channel)
    {
        // 通道是 4 位，0 与 15 是端点；越界编码不可能出现（被掩码），这里固定端点行为
        //
        // The channel is 4 bits, and 0 and 15 are the endpoints
        // An out-of-range encoding cannot occur (it is masked)
        // The endpoint behaviour is pinned down here
        using var session = new MidiLoopbackSession();

        session.SendNoteOn(64, 90, channel);
        Assert.True(session.WaitForCount(1));

        Assert.Equal([(byte)(0x90 | channel), 0x40, 0x5A], session.Snapshot()[0]);
    }

    [MidiFact]
    public void Non_note_channel_messages_round_trip()
    {
        // 控制变更 / 弯音 / 复音触后 / 音色变更 / 通道触后：应用必须收到并**忽略**它们
        // 传输层要保证它们确实抵达，否则"忽略"可能只是因为消息根本没到
        //
        // Control change / pitch bend / polyphonic aftertouch / program change / channel aftertouch
        // The app must receive and **ignore** them
        // The transport layer has to guarantee they really arrive
        // Otherwise "ignoring" might simply mean the message never got there
        using var session = new MidiLoopbackSession();

        session.SendRaw(0xB0, 7, 127);   // 控制变更：音量/ Control change: volume
        session.SendRaw(0xE0, 0, 64);    // 弯音/ Pitch bend
        session.SendRaw(0xA0, 60, 70);   // 复音触后/ Polyphonic aftertouch
        session.SendRaw(0xC0, 42, 0);    // 音色变更（2 字节消息）/ Program change (2-byte message)
        session.SendRaw(0xD0, 100, 0);   // 通道触后（2 字节消息）/ Channel pressure (2-byte message)

        Assert.True(session.WaitForCount(5, 3000), "expected all five messages");

        var received = session.Snapshot();
        Assert.Equal([0xB0, 0x07, 0x7F], received[0]);
        Assert.Equal([0xE0, 0x00, 0x40], received[1]);
        Assert.Equal([0xA0, 0x3C, 0x46], received[2]);
        // 2 字节消息：不应被读成多一个 0 的三字节（长度判定必须按状态字节，而非固定 3）
        //
        // A 2-byte message must not be read as three bytes with an extra 0
        // The length decision must come from the status byte, not from a fixed 3
        Assert.Equal([0xC0, 0x2A], received[3]);
        Assert.Equal([0xD0, 0x64], received[4]);
    }

    [MidiFact]
    public void Rapid_burst_preserves_order_and_count()
    {
        // 高速连发：验证不会因为缓冲/回调竞争而丢消息或乱序 —— 演奏时正是这种负载
        //
        // A rapid burst: verifies that messages are neither lost nor reordered by buffer/callback races
        // That is exactly the load seen while playing
        using var session = new MidiLoopbackSession();

        const int notes = 64;
        for (var i = 0; i < notes; i++)
        {
            session.SendNoteOn(40, (byte)(i + 1));
        }

        Assert.True(session.WaitForCount(notes, 4000), "burst lost messages");

        var received = session.Snapshot();
        for (var i = 0; i < notes; i++)
        {
            Assert.Equal([0x90, 40, (byte)(i + 1)], received[i]);
        }
    }
}
