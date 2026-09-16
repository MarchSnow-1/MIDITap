// MidiStateMachineBoundaryTests.cs — 状态机的边界与异常输入
//
// 与 MidiStateMachineTests 的分工：那边覆盖**正常路径**（按下/抬起/重复/引用计数）
// 这里专门打边界与畸形输入
// 真实驱动会送来这些，且它们最容易让状态机悄悄失去同步
// 状态机失同步的后果是**卡键**，是这个应用最严重的故障
//
// Boundary and malformed-input cases for the note state machine
//
// MidiStateMachineTests covers the happy path
// This file attacks boundaries and malformed input
// Real drivers do produce these
// They are the most likely way for the state machine to silently desynchronise
// Its consequence is a stuck key, the worst failure this app has

using MIDITap.Core.Midi;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class MidiStateMachineBoundaryTests
{
    private const ushort KeyA = 0x41;
    private const ushort KeyB = 0x42;
    private const ushort KeyCtrl = 0x11;
    private const ushort KeyShift = 0x10;
    private const ushort KeyAlt = 0x12;

    private static MidiNoteStateMachine Machine(params (byte Note, ushort[] Keys)[] map)
    {
        var machine = new MidiNoteStateMachine();
        foreach (var (note, keys) in map)
        {
            machine.NoteMap[note] = keys;
        }
        return machine;
    }

    private static ushort[] Downs(IEnumerable<MidiAction> actions)
        => [.. actions.OfType<MidiAction.KeyDown>().Select(a => a.VkCode)];

    private static ushort[] Ups(IEnumerable<MidiAction> actions)
        => [.. actions.OfType<MidiAction.KeyUp>().Select(a => a.VkCode)];

    // ------------------------------------------------------------------ 畸形 / 过短消息 / Malformed / too-short messages

    [Fact]
    public void Empty_message_is_ignored()
    {
        var machine = Machine((60, [KeyA]));
        Assert.Empty(machine.HandleMessage([]));
        Assert.Empty(machine.ActiveNoteChannels);
        Assert.Empty(machine.ActiveVkCount);
    }

    // 用显式数组而不是 params：xUnit 的 InlineData 无法把散列实参绑定到 params 参数
    // 它会把 0x90 当成一个待匹配的实参而报参数不匹配
    //
    // Explicit arrays rather than params: xUnit's InlineData cannot bind loose arguments to a params parameter
    // It treats 0x90 as a single argument and fails to match
    [Theory]
    [InlineData(new byte[] { 0x90 })]           // 只有状态字节 / Status byte only
    [InlineData(new byte[] { 0x90, 60 })]       // 缺力度字节 / Missing velocity byte
    // note-off 状态字节
    //
    // note-off status byte
    [InlineData(new byte[] { 0x80 })]
    public void Messages_shorter_than_three_bytes_are_ignored(byte[] message)
    {
        // 真实驱动偶尔会送来截断的消息（尤其端口刚打开/关闭时）
        // 必须安全忽略，且**不得**改变状态
        // 否则后续的 note-off 会被判为"意外抬起"而漏掉按键释放
        //
        // Real drivers occasionally deliver truncated messages
        // That happens especially while a port is just being opened or closed
        // They must be ignored safely and must NOT change state
        // Otherwise a later note-off would be judged an "unexpected off" and the key release would be missed
        var machine = Machine((60, [KeyA]));

        Assert.Empty(machine.HandleMessage(message));
        Assert.Empty(machine.ActiveNoteChannels);
        Assert.Empty(machine.ActiveVkCount);
    }

    [Fact]
    public void Truncated_message_does_not_desynchronise_a_held_note()
    {
        // 边界：先正常按下，再插入一条截断消息，最后正常抬起 —— 按键必须仍能释放
        //
        // Boundary: press normally, then insert a truncated message, then release normally
        // The key must still be released
        var machine = Machine((60, [KeyA]));
        machine.HandleMessage([0x90, 60, 100]);

        machine.HandleMessage([0x90, 60]);   // 截断 / Truncated

        var up = machine.HandleMessage([0x80, 60, 0]);
        Assert.Equal([KeyA], Ups(up));
        Assert.Empty(machine.ActiveVkCount);
    }

    // ------------------------------------------------------------------ 状态字节端点与掩码 / Status-byte endpoints and masking

    [Theory]
    [InlineData(0x80, 0)]    // Note off, 最低通道 / Note off, lowest channel
    [InlineData(0x8F, 15)]   // Note off, 最高通道 / Note off, highest channel
    [InlineData(0x90, 0)]    // Note on,  最低通道 / Note on, lowest channel
    [InlineData(0x9F, 15)]   // Note on,  最高通道 / Note on, highest channel
    public void Channel_nibble_boundaries_are_accepted(byte status, byte channel)
    {
        // 通道是低 4 位；0 与 15 是端点，且状态字节高位必须被正确掩码
        //
        // The channel is the low 4 bits; 0 and 15 are the endpoints
        // The status byte's high bits must be masked off correctly
        var machine = Machine((60, [KeyA]));

        var down = machine.HandleMessage([status, 60, 100]);
        if (status >= 0x90)
        {
            Assert.Equal([KeyA], Downs(down));
            var up = machine.HandleMessage([(byte)(0x80 | channel), 60, 0]);
            Assert.Equal([KeyA], Ups(up));
        }
        else
        {
            // 对未按下的音符发 note-off：只广播"意外抬起"，不注入按键
            //
            // A note-off for a note that was never pressed only broadcasts an "unexpected off"
            // It injects no key
            Assert.Empty(Downs(down));
            Assert.Empty(Ups(down));
        }
    }

    [Fact]
    public void All_sixteen_channels_can_hold_the_same_note()
    {
        // 16 个通道全部按住同一音符：按键只按下一次，直到最后一个通道释放
        //
        // All 16 channels holding the same note: the key is pressed only once
        // It stays down until the last channel releases
        var machine = Machine((60, [KeyA]));
        for (byte channel = 0; channel < 16; channel++)
        {
            machine.HandleMessage([(byte)(0x90 | channel), 60, 100]);
        }

        Assert.Equal(1, machine.ActiveVkCount[KeyA]);
        Assert.Equal(16, machine.ActiveNoteChannels[60].Count);

        for (byte channel = 0; channel < 15; channel++)
        {
            var mid = machine.HandleMessage([(byte)(0x80 | channel), 60, 0]);
            Assert.Empty(Ups(mid));
        }
        var last = machine.HandleMessage([0x8F, 60, 0]);
        Assert.Equal([KeyA], Ups(last));
        Assert.Empty(machine.ActiveVkCount);
    }

    // ------------------------------------------------------------------ 力度 / Velocity

    [Theory]
    // 最小有效力度：是 note-on
    //
    // Smallest valid velocity: this is a note-on
    [InlineData(1)]
    [InlineData(127)]   // 最大力度 / Maximum velocity
    public void Non_zero_velocity_is_a_note_on(byte velocity)
    {
        var machine = Machine((60, [KeyA]));
        Assert.Equal([KeyA], Downs(machine.HandleMessage([0x90, 60, velocity])));
    }

    [Fact]
    public void Velocity_zero_on_note_off_status_still_releases()
    {
        // 0x80 即为 note-off，力度字节无意义（但驱动会填 0）
        // 这里用非零力度验证实现没有误判成"力度>0 所以是按下"
        //
        // 0x80 is note-off, so the velocity byte is meaningless (but drivers fill in 0)
        // This uses a non-zero velocity to verify the implementation does not misjudge the message
        // The wrong reading would be "velocity > 0, so it is a press"
        var machine = Machine((60, [KeyA]));
        machine.HandleMessage([0x90, 60, 100]);

        var up = machine.HandleMessage([0x80, 60, 64]);
        Assert.Equal([KeyA], Ups(up));
        Assert.Empty(machine.ActiveVkCount);
    }

    [Fact]
    public void Velocity_zero_on_note_on_status_is_a_note_off_across_channels()
    {
        // 通道 15 的 0x9F + 力度 0 同样是 note-off（不能只看状态字节高 4 位）
        //
        // 0x9F plus velocity 0 on channel 15 is likewise a note-off
        // The top 4 bits of the status byte must not be the only thing examined
        var machine = Machine((60, [KeyA]));
        machine.HandleMessage([0x9F, 60, 100]);

        var up = machine.HandleMessage([0x9F, 60, 0]);
        Assert.Equal([KeyA], Ups(up));
        Assert.Empty(machine.ActiveVkCount);
    }

    // ------------------------------------------------------------------ 音符编号端点 / Note-number endpoints

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    public void Note_number_endpoints_are_supported(byte note)
    {
        var machine = Machine((note, [KeyA]));
        Assert.Equal([KeyA], Downs(machine.HandleMessage([0x90, note, 100])));
        Assert.Equal([KeyA], Ups(machine.HandleMessage([0x80, note, 0])));
    }

    // ------------------------------------------------------------------ 非音符通道消息 / Non-note channel messages

    [Theory]
    [InlineData(new byte[] { 0xA0, 60, 64 })]   // 复音触后 / Polyphonic aftertouch
    [InlineData(new byte[] { 0xB0, 7, 127 })]   // 控制变更 / Control change
    [InlineData(new byte[] { 0xC0, 42, 0 })]    // 音色变更 / Program change
    [InlineData(new byte[] { 0xD0, 100, 0 })]   // 通道触后 / Channel aftertouch
    [InlineData(new byte[] { 0xE0, 0, 64 })]    // 弯音 / Pitch bend
    public void Other_channel_messages_produce_no_actions(byte[] message)
    {
        var machine = Machine((60, [KeyA]));
        Assert.Empty(machine.HandleMessage(message));
    }

    [Theory]
    [InlineData(0xF0)]   // SysEx 起始 / SysEx start
    [InlineData(0xF8)]   // 时序时钟 / Timing clock
    [InlineData(0xFE)]   // 主动感应 / Active sensing
    [InlineData(0xFF)]   // 系统复位 / System reset
    public void System_messages_are_ignored(byte status)
    {
        // 系统消息不携带通道
        // 若实现漏掉这个判断，0xF0 会被当成 channel 0 的 note-on 而注入按键
        // 0xF0 & 0xF0 == 0xF0，不等于 0x90，所以实际已被挡掉
        // 这条测试锁住该行为
        //
        // System messages carry no channel
        // Had the implementation missed this check, 0xF0 would be taken for a note-on on channel 0 and inject a key
        // In fact 0xF0 & 0xF0 == 0xF0, which is not 0x90, so it is already blocked
        // This test locks that behaviour down
        var machine = Machine((60, [KeyA]));
        Assert.Empty(machine.HandleMessage([status, 60, 100]));
        Assert.Empty(machine.ActiveNoteChannels);
    }

    // ------------------------------------------------------------------ 组合键：部分共享 / Combos: partially shared

    [Fact]
    public void Combo_bindings_sharing_one_key_use_reference_counting()
    {
        // 两个音符的组合键共享 Ctrl，但各自还有独立键
        //   note A -> Ctrl + Shift
        //   note B -> Ctrl + Alt
        // 释放 A 时 Ctrl 必须保持按下（B 仍需要），而 Shift 必须抬起
        //
        // Two notes' combos share Ctrl, but each has a key of its own
        //   note A -> Ctrl + Shift
        //   note B -> Ctrl + Alt
        // Releasing A must keep Ctrl down (B still needs it) while Shift must go up
        var machine = Machine((60, [KeyCtrl, KeyShift]), (61, [KeyCtrl, KeyAlt]));

        machine.HandleMessage([0x90, 60, 100]);
        machine.HandleMessage([0x90, 61, 100]);
        Assert.Equal(2, machine.ActiveVkCount[KeyCtrl]);
        Assert.Equal(1, machine.ActiveVkCount[KeyShift]);
        Assert.Equal(1, machine.ActiveVkCount[KeyAlt]);

        var releaseA = machine.HandleMessage([0x80, 60, 0]);
        Assert.Equal([KeyShift], Ups(releaseA));
        Assert.Empty(Downs(releaseA));
        // Ctrl 仍被 B 按住，不能抬起
        //
        // Ctrl is still held by B and must not be released
        Assert.Equal(1, machine.ActiveVkCount[KeyCtrl]);
        Assert.False(machine.ActiveVkCount.ContainsKey(KeyShift));

        var releaseB = machine.HandleMessage([0x80, 61, 0]);
        Assert.Equal([KeyAlt, KeyCtrl], Ups(releaseB));
        Assert.Empty(machine.ActiveVkCount);
    }

    [Fact]
    public void Single_key_binding_reused_by_many_notes_releases_only_at_zero()
    {
        var machine = Machine((60, [KeyA]), (61, [KeyA]), (62, [KeyA]));
        for (byte note = 60; note <= 62; note++)
        {
            machine.HandleMessage([0x90, note, 100]);
        }
        Assert.Equal(3, machine.ActiveVkCount[KeyA]);

        Assert.Empty(Ups(machine.HandleMessage([0x80, 60, 0])));
        Assert.Empty(Ups(machine.HandleMessage([0x80, 61, 0])));
        Assert.Equal([KeyA], Ups(machine.HandleMessage([0x80, 62, 0])));
        Assert.Empty(machine.ActiveVkCount);
    }

    // ------------------------------------------------------------------ 映射在按住期间变化 / Mapping changes while held

    [Fact]
    public void Remapping_while_held_does_not_leak_the_old_key()
    {
        // 按住期间用户改了映射（热更新配置）
        // 抬起时必须松开**按下时**的键，而不是新映射的键
        // 否则旧键永远卡住、新键被凭空虚按一次
        //
        // The user changed the mapping while the key was held (config hot reload)
        // Releasing must let go of the key held at PRESS time rather than the newly mapped key
        // Otherwise the old key sticks down forever and the new key is pressed once out of nowhere
        var machine = Machine((60, [KeyA]));
        machine.HandleMessage([0x90, 60, 100]);

        machine.NoteMap[60] = [KeyB];   // 热更新 / Hot reload

        var up = machine.HandleMessage([0x80, 60, 0]);
        Assert.Equal([KeyA], Ups(up));
        Assert.Empty(Downs(up));
        Assert.Empty(machine.ActiveVkCount);
    }

    [Fact]
    public void Removing_the_mapping_while_held_still_releases_the_key()
    {
        var machine = Machine((60, [KeyA]));
        machine.HandleMessage([0x90, 60, 100]);

        machine.NoteMap.Remove(60);

        var up = machine.HandleMessage([0x80, 60, 0]);
        Assert.Equal([KeyA], Ups(up));
        Assert.Empty(machine.ActiveVkCount);
    }

    // ------------------------------------------------------------------ 幂等与清理 / Idempotence and cleanup

    [Fact]
    public void Release_all_keys_is_idempotent()
    {
        // 停止监听、切换配置、设备拔出等路径都会调用它，可能连续触发
        // 第二次调用不得再产生按键抬起（重复发送 KeyUp 在某些程序里会被当成额外输入）
        //
        // Called on paths such as stopping monitoring, switching config or unplugging the device
        // It can fire repeatedly
        // The second call must produce no further key-ups
        // Sending a duplicate KeyUp counts as extra input in some programs
        var machine = Machine((60, [KeyA]), (61, [KeyCtrl, KeyShift]));
        machine.HandleMessage([0x90, 60, 100]);
        machine.HandleMessage([0x90, 61, 100]);

        var first = machine.ReleaseAllKeys();
        Assert.Equal(3, first.OfType<MidiAction.KeyUp>().Count());

        var second = machine.ReleaseAllKeys();
        Assert.Empty(second);
    }

    [Fact]
    public void Release_all_keys_on_fresh_machine_is_empty()
    {
        var machine = Machine((60, [KeyA]));
        Assert.Empty(machine.ReleaseAllKeys());
    }

    [Fact]
    public void Release_all_keys_clears_note_tracking_so_later_off_is_unexpected()
    {
        // ReleaseAllKeys 之后，同一音符的 note-off 必须被当成"意外抬起"，而不是去抬起一个已经不存在的键
        //
        // After ReleaseAllKeys the note-off for the same note must be treated as an "unexpected off"
        // rather than releasing a key that no longer exists
        var machine = Machine((60, [KeyA]));
        machine.HandleMessage([0x90, 60, 100]);
        machine.ReleaseAllKeys();

        var late = machine.HandleMessage([0x80, 60, 0]);
        Assert.Empty(Ups(late));
        Assert.Contains(late.OfType<MidiAction.Broadcast>(), b => b.Kind == BroadcastKind.UnexpectedOff);
        // 随后仍可正常重新按下 / Afterwards it can still be pressed again normally
        Assert.Equal([KeyA], Downs(machine.HandleMessage([0x90, 60, 100])));
    }

    [Fact]
    public void Note_off_for_unmapped_note_is_unexpected_without_output()
    {
        var machine = Machine();   // 空映射 / Empty mapping
        var actions = machine.HandleMessage([0x80, 60, 0]);

        Assert.Empty(Downs(actions));
        Assert.Empty(Ups(actions));
        Assert.Contains(actions.OfType<MidiAction.Broadcast>(), b => b.Kind == BroadcastKind.UnexpectedOff);
    }

    [Fact]
    public void Note_on_broadcast_reports_velocity_unchanged()
    {
        // 边界力度必须原样上报给 UI（实时预览显示的就是这个值）
        //
        // A boundary velocity must be reported to the UI unchanged
        // This is the value the live preview displays
        var machine = Machine((60, [KeyA]));
        var broadcast = machine.HandleMessage([0x90, 60, 127])
            .OfType<MidiAction.Broadcast>().Single();
        Assert.Equal(127, broadcast.Velocity);
        Assert.Equal(60, broadcast.Note);
    }
}
