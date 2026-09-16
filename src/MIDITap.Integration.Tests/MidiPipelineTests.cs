// MidiPipelineTests.cs — 全链路：真实 MIDI 字节 -> 状态机 -> 应执行的按键动作
//
// 与 Core.Tests 里的状态机单测**互补而非重复**
// 那边喂的是构造好的字节数组，验证逻辑
// 这边喂的是**真正从驱动收到的字节**
// 验证"驱动器送来的东西"与"状态机期望的东西"确实对得上
// 例如 2 字节通道消息、velocity 0 特例、通道掩码
// 任何一处不一致都会让单测全绿而真机失灵
// 这正是这一层要拦住的
//
// Full pipeline: real MIDI bytes -> state machine -> the key actions to perform
//
// Complements rather than duplicates the Core state-machine unit tests
// Those feed hand-constructed byte arrays to verify logic
// These feed bytes actually received from the driver
// That verifies the driver's output matches what the state machine expects
// For example 2-byte channel messages, the velocity-0 quirk, channel masking
// A mismatch there would leave unit tests fully green while the real device misbehaves
// That is exactly what this layer catches

using MIDITap.Core.Midi;
using Xunit;

namespace MIDITap.Integration.Tests;

public sealed class MidiPipelineTests
{
    private const ushort KeyA = 0x41;
    private const ushort KeyB = 0x42;
    private const ushort KeyCtrl = 0x11;

    /// <summary>
    /// 发送刺激、等待抵达、再把收到的**真实字节**喂给状态机，返回产生的动作
    ///
    /// Sends the stimulus, then waits for it to arrive
    /// It feeds the **real bytes** received into the state machine
    /// Then it returns the actions produced
    /// </summary>
    private static List<MidiAction> Stimulate(
        MidiLoopbackSession session,
        MidiNoteStateMachine machine,
        Action<MidiLoopbackSession> stimulus,
        int expectedMessages)
    {
        session.Clear();
        stimulus(session);
        Assert.True(session.WaitForCount(expectedMessages),
            $"expected {expectedMessages} messages, got {session.Snapshot().Count}");

        var actions = new List<MidiAction>();
        foreach (var message in session.Snapshot())
        {
            actions.AddRange(machine.HandleMessage(message));
        }
        return actions;
    }

    private static ushort[] KeyDowns(IEnumerable<MidiAction> actions)
        => [.. actions.OfType<MidiAction.KeyDown>().Select(a => a.VkCode)];

    private static ushort[] KeyUps(IEnumerable<MidiAction> actions)
        => [.. actions.OfType<MidiAction.KeyUp>().Select(a => a.VkCode)];

    private static BroadcastKind[] Kinds(IEnumerable<MidiAction> actions)
        => [.. actions.OfType<MidiAction.Broadcast>().Select(a => a.Kind)];

    [MidiFact]
    public void Real_note_on_then_off_produces_key_down_then_key_up()
    {
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        machine.NoteMap[60] = [KeyA];

        var down = Stimulate(session, machine, s => s.SendNoteOn(60, 100), 1);
        Assert.Equal([KeyA], KeyDowns(down));
        Assert.Empty(KeyUps(down));
        Assert.Equal([BroadcastKind.NoteOn], Kinds(down));

        var up = Stimulate(session, machine, s => s.SendNoteOff(60), 1);
        Assert.Empty(KeyDowns(up));
        Assert.Equal([KeyA], KeyUps(up));
        Assert.Equal([BroadcastKind.NoteOff], Kinds(up));
    }

    [MidiTheory]
    [InlineData(0)]    // 最低合法音符/ Lowest legal note
    [InlineData(127)]  // 最高合法音符/ Highest legal note
    public void Real_boundary_notes_drive_keys(byte note)
    {
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        machine.NoteMap[note] = [KeyA];

        var down = Stimulate(session, machine, s => s.SendNoteOn(note, 127), 1);
        Assert.Equal([KeyA], KeyDowns(down));

        var up = Stimulate(session, machine, s => s.SendNoteOff(note), 1);
        Assert.Equal([KeyA], KeyUps(up));
    }

    [MidiFact]
    public void Real_velocity_zero_note_on_releases_the_key()
    {
        // 真机上最常见的"松键"其实来自 0x90 + velocity 0（很多键盘不发 0x80）
        // 若状态机只认 0x80，用户会持续卡键 —— 这条测试针对的就是那个真实场景
        //
        // On real hardware the most common "key release" actually comes from 0x90 with velocity 0
        // Many keyboards never send 0x80
        // If the state machine accepted only 0x80, the user would be left with a stuck key
        // That real scenario is exactly what this test targets
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        machine.NoteMap[60] = [KeyA];

        var down = Stimulate(session, machine, s => s.SendNoteOn(60, 100), 1);
        Assert.Equal([KeyA], KeyDowns(down));

        var release = Stimulate(session, machine, s => s.SendNoteOn(60, 0), 1);
        Assert.Equal([KeyA], KeyUps(release));
        Assert.Empty(machine.ActiveVkCount);
    }

    [MidiFact]
    public void Real_non_note_messages_produce_no_actions()
    {
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        machine.NoteMap[60] = [KeyA];

        var actions = Stimulate(session, machine, s =>
        {
            s.SendRaw(0xB0, 7, 127);  // 控制变更（CC）/ CC
            s.SendRaw(0xE0, 0, 64);   // 弯音/ Pitch bend
            s.SendRaw(0xA0, 60, 70);  // 复音触后/ Poly aftertouch
            s.SendRaw(0xC0, 42, 0);   // 音色变更/ Program change
            s.SendRaw(0xD0, 100, 0);  // 通道触后/ Channel pressure
        }, 5);

        Assert.Empty(actions);
        Assert.Empty(machine.ActiveNoteChannels);
        Assert.Empty(machine.ActiveVkCount);
    }

    [MidiFact]
    public void Real_duplicate_note_on_same_channel_is_ignored()
    {
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        machine.NoteMap[60] = [KeyA];

        Stimulate(session, machine, s => s.SendNoteOn(60, 100), 1);
        var duplicate = Stimulate(session, machine, s => s.SendNoteOn(60, 100), 1);

        // 重复按下不能再按一次键（否则计数泄漏、抬起时会卡键）
        //
        // A duplicate press must not press the key a second time
        // Otherwise the count leaks and the key sticks on release
        Assert.Empty(KeyDowns(duplicate));
        Assert.Empty(KeyUps(duplicate));
        Assert.Equal([BroadcastKind.DuplicateOn], Kinds(duplicate));
        Assert.Equal(1, machine.ActiveVkCount[KeyA]);
    }

    [MidiFact]
    public void Real_layered_channels_hold_key_until_every_channel_releases()
    {
        // 同一音高在不同通道（分层音色）叠加：只有全部通道抬起才松键
        //
        // The same pitch layered on several channels (layered voices)
        // The key is released only once every channel has released
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        machine.NoteMap[60] = [KeyA];

        Stimulate(session, machine, s => s.SendNoteOn(60, 100, 0), 1);
        Stimulate(session, machine, s => s.SendNoteOn(60, 100, 1), 1);

        // 通道 0 抬起后按键仍被通道 1 按住 / After channel 0 releases the key is still held by channel 1
        var partial = Stimulate(session, machine, s => s.SendNoteOff(60, 0, 0), 1);
        Assert.Empty(KeyUps(partial));
        Assert.Equal(1, machine.ActiveVkCount[KeyA]);

        var final = Stimulate(session, machine, s => s.SendNoteOff(60, 0, 1), 1);
        Assert.Equal([KeyA], KeyUps(final));
        Assert.Empty(machine.ActiveVkCount);
    }

    [MidiFact]
    public void Real_note_off_for_never_pressed_note_is_reported_as_unexpected()
    {
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        machine.NoteMap[60] = [KeyA];

        var actions = Stimulate(session, machine, s => s.SendNoteOff(60), 1);

        Assert.Empty(KeyDowns(actions));
        Assert.Empty(KeyUps(actions));
        Assert.Equal([BroadcastKind.UnexpectedOff], Kinds(actions));
    }

    [MidiFact]
    public void Real_unmapped_note_broadcasts_without_key_output()
    {
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        machine.NoteMap[60] = [KeyA];   // 只有 60 有映射/ only 60 is mapped

        var on = Stimulate(session, machine, s => s.SendNoteOn(61, 100), 1);
        Assert.Empty(KeyDowns(on));
        var broadcast = Assert.Single(on.OfType<MidiAction.Broadcast>());
        Assert.Equal(BroadcastKind.NoteOn, broadcast.Kind);
        // 未映射时键位标签为 null（UI 显示 unbound），但事件仍要送达
        //
        // When unmapped the key label is null (the UI shows unbound)
        // The event must still be delivered
        Assert.Null(broadcast.Key);

        var off = Stimulate(session, machine, s => s.SendNoteOff(61), 1);
        Assert.Equal([BroadcastKind.NoteOff], Kinds(off));
    }

    [MidiFact]
    public void Real_combo_binding_presses_left_to_right_and_releases_in_reverse()
    {
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        machine.NoteMap[60] = [KeyCtrl, KeyA];

        var down = Stimulate(session, machine, s => s.SendNoteOn(60, 100), 1);
        Assert.Equal([KeyCtrl, KeyA], KeyDowns(down));

        var up = Stimulate(session, machine, s => s.SendNoteOff(60), 1);
        // 组合键必须**逆序**抬起，否则会先松开修饰键、导致目标键变成别的输入
        //
        // A combo must be released in **reverse** order
        // Otherwise the modifier comes up first and the target key turns into a different input
        Assert.Equal([KeyA, KeyCtrl], KeyUps(up));
    }

    [MidiFact]
    public void Real_all_128_notes_then_release_leaves_no_stuck_keys()
    {
        // 压力场景：128 个音符全按下再全抬起，最后必须一把不留
        // 卡键是这个应用最严重的故障（用户被持续注入按键），必须被固定住
        //
        // The stress scenario: press all 128 notes and then release them all
        // At the end nothing must be left over
        // A stuck key is the most serious failure this app can have
        // The user is continuously being injected with key presses
        // So it must be pinned down
        using var session = new MidiLoopbackSession();
        var machine = new MidiNoteStateMachine();
        for (var note = 0; note <= 127; note++)
        {
            machine.NoteMap[(byte)note] = [(byte)(0x41 + note % 26)];
        }

        session.Clear();
        for (var note = 0; note <= 127; note++)
        {
            session.SendNoteOn((byte)note, 100);
        }
        Assert.True(session.WaitForCount(128, 4000), "note-on burst lost messages");
        foreach (var message in session.Snapshot())
        {
            machine.HandleMessage(message);
        }

        session.Clear();
        for (var note = 0; note <= 127; note++)
        {
            session.SendNoteOff((byte)note);
        }
        Assert.True(session.WaitForCount(128, 4000), "note-off burst lost messages");
        foreach (var message in session.Snapshot())
        {
            machine.HandleMessage(message);
        }

        Assert.Empty(machine.ActiveVkCount);
        Assert.Empty(machine.ActiveNoteChannels);

        // 兜底通道（停止监听 / 换配置）也不应再有可抬起的键
        //
        // The safety-net path (stop listening / change configuration) must also have no keys to release
        Assert.Empty(machine.ReleaseAllKeys());
    }
}
