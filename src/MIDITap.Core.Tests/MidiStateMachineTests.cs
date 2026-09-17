// 键盘输出与广播变成可断言的动作列表，而不是打桩的 FFI 调用
//
// Key output and broadcasts become assertable action lists instead of stubbed FFI calls

using MIDITap.Core.Midi;
using Xunit;

namespace MIDITap.Core.Tests;

public class MidiStateMachineTests
{
    private static MidiNoteStateMachine Map(params (byte Note, ushort[] Keys)[] pairs)
    {
        var state = new MidiNoteStateMachine();
        foreach (var (note, keys) in pairs)
        {
            state.NoteMap[note] = keys;
        }
        return state;
    }

    private static List<MidiAction> NoteOn(MidiNoteStateMachine state, byte channel, byte note, byte velocity = 100)
        => state.HandleMessage([(byte)(0x90 | channel), note, velocity]);

    private static List<MidiAction> NoteOff(MidiNoteStateMachine state, byte channel, byte note)
        => state.HandleMessage([(byte)(0x80 | channel), note, 0]);

    private static List<ushort> Downs(IEnumerable<MidiAction> actions)
        => actions.OfType<MidiAction.KeyDown>().Select(a => a.VkCode).ToList();

    private static List<ushort> Ups(IEnumerable<MidiAction> actions)
        => actions.OfType<MidiAction.KeyUp>().Select(a => a.VkCode).ToList();

    private static List<BroadcastKind> Broadcasts(IEnumerable<MidiAction> actions)
        => actions.OfType<MidiAction.Broadcast>().Select(a => a.Kind).ToList();

    [Fact]
    public void Single_channel_note_on_then_off_presses_and_releases_once()
    {
        var state = Map((60, [0x41]));
        var on = NoteOn(state, 0, 60);
        Assert.Equal(new List<ushort> { 0x41 }, Downs(on));
        var off = NoteOff(state, 0, 60);
        Assert.Equal(new List<ushort> { 0x41 }, Ups(off));
        Assert.Empty(state.ActiveNoteChannels);
        Assert.Empty(state.ActiveVkCount);
        Assert.Empty(state.ActiveNoteBindings);
    }

    [Fact]
    public void Duplicate_note_on_same_channel_is_broadcast_and_ignored()
    {
        var state = Map((60, [0x41]));
        NoteOn(state, 0, 60);
        var dup = NoteOn(state, 0, 60);
        Assert.Contains(BroadcastKind.DuplicateOn, Broadcasts(dup));
        Assert.Empty(Downs(dup)); // 只按下一次 / Pressed only once
    }

    [Fact]
    public void Cross_channel_same_pitch_keeps_key_until_every_channel_releases()
    {
        var state = Map((60, [0x41]));
        var first = NoteOn(state, 0, 60);
        Assert.Equal(new List<ushort> { 0x41 }, Downs(first));
        NoteOn(state, 1, 60); // layered voice on channel 1: no second key press
        var r0 = NoteOff(state, 0, 60);
        Assert.Empty(Ups(r0)); // 此时**不得**抬起 / must NOT release yet
        var r1 = NoteOff(state, 1, 60);
        Assert.Equal(new List<ushort> { 0x41 }, Ups(r1));
        Assert.Empty(state.ActiveNoteChannels);
    }

    [Fact]
    public void Note_off_after_mapping_removed_still_releases_pressed_key()
    {
        var state = Map((60, [0x41]));
        NoteOn(state, 0, 60);
        state.NoteMap.Remove(60); // mapping removed / config hot-switched while held
        var r = NoteOff(state, 0, 60);
        // 卡键回归用例
        //
        // stuck-key regression
        Assert.Equal(new List<ushort> { 0x41 }, Ups(r));
        Assert.Empty(state.ActiveVkCount);
    }

    [Fact]
    public void Unexpected_note_off_is_broadcast_without_key_output()
    {
        var state = Map((60, [0x41]));
        var r = NoteOff(state, 0, 61); // 该音符从未按下 / Note never pressed
        Assert.Contains(BroadcastKind.UnexpectedOff, Broadcasts(r));
        Assert.Empty(Ups(r));
    }

    [Fact]
    public void Combo_keys_are_pressed_left_to_right_and_released_right_to_left()
    {
        var state = Map((60, [0x11, 0x42])); // ctrl+b
        var on = NoteOn(state, 0, 60);
        Assert.Equal(new List<ushort> { 0x11, 0x42 }, Downs(on));
        var off = NoteOff(state, 0, 60);
        Assert.Equal(new List<ushort> { 0x42, 0x11 }, Ups(off));
    }

    [Fact]
    public void Shared_key_across_two_held_notes_uses_reference_counting()
    {
        var state = Map((60, [0x41]), (61, [0x41]));
        var a = NoteOn(state, 0, 60); // 按下 a（计数 1） / Press a (count 1)
        Assert.Equal(new List<ushort> { 0x41 }, Downs(a));
        var b = NoteOn(state, 0, 61); // a 已按下（计数 2） / a already down (count 2)
        Assert.Empty(Downs(b));
        var oa = NoteOff(state, 0, 60); // 计数回到 1 / Count back to 1
        Assert.Empty(Ups(oa)); // 按键保持按下 / Key stays down
        var ob = NoteOff(state, 0, 61); // 计数归 0 / Count 0
        Assert.Equal(new List<ushort> { 0x41 }, Ups(ob));
    }

    [Fact]
    public void Velocity_zero_note_on_is_a_note_off()
    {
        var state = Map((60, [0x41]));
        NoteOn(state, 0, 60, 100);
        // 力度为 0 的 note-on
        //
        // Note-on with velocity 0
        var release = state.HandleMessage([0x90, 60, 0]);
        Assert.Equal(new List<ushort> { 0x41 }, Ups(release));
    }

    [Fact]
    public void Release_all_keys_uplifts_every_held_key_and_clears_state()
    {
        var state = Map((60, [0x41]), (61, [0x42]));
        NoteOn(state, 0, 60);
        NoteOn(state, 0, 61);
        Assert.Equal(2, state.ActiveVkCount.Count);
        var actions = state.ReleaseAllKeys();
        Assert.Equal(new List<ushort> { 0x41, 0x42 }, Ups(actions));
        Assert.Empty(state.ActiveVkCount);
        Assert.Empty(state.ActiveNoteBindings);
        Assert.Empty(state.ActiveNoteChannels);
    }

    [Fact]
    public void Non_note_messages_are_ignored()
    {
        var state = Map((60, [0x41]));
        // 主动感应（1 字节）
        //
        // Active sensing (1 byte)
        Assert.Empty(state.HandleMessage([0xFE]));
        // 控制变更
        //
        // Control change
        Assert.Empty(state.HandleMessage([0xB0, 0x07, 0x40]));
        Assert.Empty(state.HandleMessage([0x90, 60])); // 截断 / Truncated
    }

    [Fact]
    public void Note_on_broadcast_carries_velocity_and_key_label()
    {
        var state = Map((60, [0x11, 0x42]));
        var on = NoteOn(state, 0, 60, 88);
        var broadcast = on.OfType<MidiAction.Broadcast>().Single(b => b.Kind == BroadcastKind.NoteOn);
        Assert.Equal(88, broadcast.Velocity);
        Assert.Equal("ctrl+b", broadcast.Key);
    }

    // 页面按导航重建后要拿回“哪些音符正按着”，下面几条锁定快照的行为

    [Fact]
    public void Held_note_snapshot_lists_notes_ascending_with_their_labels()
    {
        var state = Map((60, [0x11, 0x42]), (62, [0x41]));
        NoteOn(state, 0, 62);
        NoteOn(state, 0, 60);
        var held = state.SnapshotHeldNotes();
        Assert.Equal(new List<byte> { 60, 62 }, held.Select(h => h.Note).ToList());
        Assert.Equal("ctrl+b", held[0].KeyLabel);
        Assert.Equal("a", held[1].KeyLabel);
    }

    [Fact]
    public void Held_note_snapshot_is_empty_before_a_press_and_after_a_release()
    {
        var state = Map((60, [0x41]));
        Assert.Empty(state.SnapshotHeldNotes());
        NoteOn(state, 0, 60);
        Assert.Single(state.SnapshotHeldNotes());
        NoteOff(state, 0, 60);
        Assert.Empty(state.SnapshotHeldNotes());
    }

    [Fact]
    public void Held_note_snapshot_keeps_a_note_still_held_on_another_channel()
    {
        var state = Map((60, [0x41]));
        NoteOn(state, 0, 60);
        NoteOn(state, 1, 60);
        NoteOff(state, 0, 60);
        var held = state.SnapshotHeldNotes();
        Assert.Equal(new List<byte> { 60 }, held.Select(h => h.Note).ToList());
    }

    [Fact]
    public void Held_note_snapshot_reports_an_unmapped_note_with_no_label()
    {
        var state = Map();
        NoteOn(state, 0, 64);
        var held = state.SnapshotHeldNotes();
        Assert.Equal(new List<byte> { 64 }, held.Select(h => h.Note).ToList());
        Assert.Null(held[0].KeyLabel);
    }

    [Fact]
    public void Held_note_snapshot_labels_from_the_binding_recorded_at_press_time()
    {
        var state = Map((60, [0x41]));
        NoteOn(state, 0, 60);
        // 按下之后换掉映射：快照仍用按下那一刻的绑定，与实际按住的键一致
        //
        // The mapping changes after the press: the snapshot keeps the binding recorded at press time, matching the key actually held
        state.NoteMap[60] = [0x42];
        Assert.Equal("a", state.SnapshotHeldNotes()[0].KeyLabel);
    }

    [Fact]
    public void Held_note_snapshot_is_cleared_by_release_all_keys()
    {
        var state = Map((60, [0x41]));
        NoteOn(state, 0, 60);
        state.ReleaseAllKeys();
        Assert.Empty(state.SnapshotHeldNotes());
    }
}
