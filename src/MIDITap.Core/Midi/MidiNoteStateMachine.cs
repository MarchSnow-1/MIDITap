// MidiNoteStateMachine.cs — 纯 MIDI 音符状态机
//
// 处理原始 MIDI 音符消息，并跟踪各音符与按键的按下状态
// 不依赖任何 I/O，因此可以脱离 MIDI 设备与 Windows 输入栈做单测
// 由调用方执行返回的动作（发送按键事件 / 广播界面事件）
//
// Pure MIDI note state machine
// Handles raw MIDI note messages and tracks which notes and keys are held
// Free of any I/O dependency, so it can be unit-tested without a MIDI device or the Windows input stack
// The caller applies the returned actions (send key events / broadcast UI events)

namespace MIDITap.Core.Midi;

public sealed class MidiNoteStateMachine
{
    /// <summary>音符编号 -> VK 码列表（当前活动配置的音符映射）
    /// Note -> mapped VK codes</summary>
    public Dictionary<byte, ushort[]> NoteMap { get; } = new();

    /// <summary>音符编号 -> 当前正按着该音符的 MIDI 通道集合
    /// Note -> channels currently holding it</summary>
    public Dictionary<byte, HashSet<byte>> ActiveNoteChannels { get; } = new();

    /// <summary>音符编号 -> 按下时刻记录的绑定（防止按键卡键）
    /// Note -> binding recorded at press time</summary>
    public Dictionary<byte, ushort[]> ActiveNoteBindings { get; } = new();

    /// <summary>VK 码 -> 当前按住该键的物理按压次数
    /// VK code -> physical press count</summary>
    public Dictionary<ushort, int> ActiveVkCount { get; } = new();

    /// <summary>
    /// 依据当前状态处理一条原始 MIDI 消息（通道消息）
    ///
    /// Process one raw MIDI message against the state
    /// </summary>
    public List<MidiAction> HandleMessage(ReadOnlySpan<byte> message)
    {
        if (message.Length < 3)
        {
            return [];
        }

        var statusByte = message[0];
        var status = (byte)(statusByte & 0xF0);
        var channel = (byte)(statusByte & 0x0F);
        var note = message[1];
        var velocity = message[2];

        var isNoteOn = status == 0x90 && velocity > 0;
        var isNoteOff = status == 0x80 || (status == 0x90 && velocity == 0);
        if (!isNoteOn && !isNoteOff)
        {
            return [];
        }

        NoteMap.TryGetValue(note, out var binding);
        var actions = new List<MidiAction>();

        if (isNoteOn)
        {
            if (!ActiveNoteChannels.TryGetValue(note, out var holding))
            {
                holding = new HashSet<byte>();
            }

            // 同一通道再次触发已按住的音符属于真正的重复
            //
            // Same channel re-triggering an already-held note is a real duplicate
            if (!holding.Add(channel))
            {
                actions.Add(new MidiAction.Broadcast(BroadcastKind.DuplicateOn, note));
                return actions;
            }
            ActiveNoteChannels[note] = holding;

            // 只有该音符的第一次物理按下才会按下映射的按键
            // 其他通道上的分层叠加音色不应再次触发它
            //
            // Only the first physical press presses the mapped key
            // A layered voice on another channel must not re-trigger it
            if (holding.Count == 1 && binding is not null)
            {
                foreach (var vkCode in binding)
                {
                    var count = ActiveVkCount.TryGetValue(vkCode, out var c) ? c : 0;
                    if (count == 0)
                    {
                        actions.Add(new MidiAction.KeyDown(vkCode));
                    }
                    ActiveVkCount[vkCode] = count + 1;
                }
                ActiveNoteBindings[note] = binding;
            }

            var keyLabel = LabelOf(binding);
            actions.Add(new MidiAction.Broadcast(BroadcastKind.NoteOn, note, velocity, keyLabel));
            return actions;
        }

        // --- 音符结束 / Note off -------------------------------------------
        if (!ActiveNoteChannels.TryGetValue(note, out var holdingOff) || !holdingOff.Contains(channel))
        {
            actions.Add(new MidiAction.Broadcast(BroadcastKind.UnexpectedOff, note));
            return actions;
        }

        holdingOff.Remove(channel);
        if (holdingOff.Count != 0)
        {
            // 仍被另一通道按住 —— 保持按键不抬起
            //
            // Still held by another channel — keep the key down
            return actions;
        }
        ActiveNoteChannels.Remove(note);

        // 已完全抬起
        // 使用按下时记录的绑定来抬起按键，而不是当前配置的绑定（避免按键卡键）
        //
        // Fully released
        // Release using the binding recorded at press time, NOT the current config binding (prevents stuck keys)
        ActiveNoteBindings.TryGetValue(note, out var pressBinding);
        ActiveNoteBindings.Remove(note);
        pressBinding ??= binding;
        if (pressBinding is not null)
        {
            for (var i = pressBinding.Length - 1; i >= 0; i--)
            {
                var vkCode = pressBinding[i];
                if (ActiveVkCount.TryGetValue(vkCode, out var count) && count > 0)
                {
                    var newCount = count - 1;
                    if (newCount == 0)
                    {
                        actions.Add(new MidiAction.KeyUp(vkCode));
                        ActiveVkCount.Remove(vkCode);
                    }
                    else
                    {
                        ActiveVkCount[vkCode] = newCount;
                    }
                }
            }
        }

        actions.Add(new MidiAction.Broadcast(BroadcastKind.NoteOff, note));
        return actions;
    }

    /// <summary>
    /// 当前仍被按住的音符快照，按音符编号升序
    /// 页面按导航重建，重建期间发出的按下事件它收不到，靠这份快照把“正按着”的样子补回来
    /// 调用方须持有保护本状态机的锁，本类自身不加锁
    ///
    /// A snapshot of the notes still held, ascending by note number
    /// A page is rebuilt on navigation and receives none of the presses made while it was gone, and this snapshot restores the state it missed
    /// The caller must hold the lock that guards this state machine, which takes no lock of its own
    /// </summary>
    public List<HeldNote> SnapshotHeldNotes()
    {
        var held = new List<HeldNote>();
        foreach (var (note, channels) in ActiveNoteChannels)
        {
            // 判据是“通道集合非空”，与 HandleMessage 的抬起判据一致
            //
            // The test is a non-empty channel set, matching the release test in HandleMessage
            if (channels.Count == 0)
            {
                continue;
            }
            ActiveNoteBindings.TryGetValue(note, out var binding);
            held.Add(new HeldNote(note, LabelOf(binding)));
        }
        held.Sort((a, b) => a.Note.CompareTo(b.Note));
        return held;
    }

    /// <summary>绑定转成显示标签，未绑定返回 null
    /// A binding as its display label, or null when unbound</summary>
    private static string? LabelOf(ushort[]? binding) =>
        binding is null ? null : string.Join("+", binding.Select(Keys.VirtualKeyTable.LabelFor));

    /// <summary>
    /// 抬起当前所有被按住的按键并清空跟踪状态
    /// 用于停止监听或（重新）应用配置时
    ///
    /// Release every held key and clear the tracking state
    /// Used when monitoring stops or a config is (re)applied
    /// </summary>
    public List<MidiAction> ReleaseAllKeys()
    {
        var actions = new List<MidiAction>();
        foreach (var (vkCode, count) in ActiveVkCount)
        {
            if (count > 0)
            {
                actions.Add(new MidiAction.KeyUp(vkCode));
            }
        }
        ActiveVkCount.Clear();
        ActiveNoteBindings.Clear();
        ActiveNoteChannels.Clear();
        return actions;
    }
}
