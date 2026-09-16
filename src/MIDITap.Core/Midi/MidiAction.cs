// MidiAction.cs — 纯音符状态机产生的副作用
//
// 按键与广播事件不在这里直接执行，而是作为动作返回
// 这样状态机可以脱离 MIDI 设备与 Windows 输入栈做单测
// 返回的动作由调用方执行：发送按键、广播事件
//
// Side effects produced by the pure note state machine
// Keys and broadcast events are not sent from here
// They are returned as actions
// The state machine is therefore unit-testable without a MIDI device or the Windows input stack
// The caller applies them: sending keys, broadcasting events

namespace MIDITap.Core.Midi;

using System.Threading;

/// <summary>广播事件种类 / Broadcast event kinds</summary>
public enum BroadcastKind
{
    NoteOn,
    NoteOff,
    DuplicateOn,
    UnexpectedOff,
}

/// <summary>一次状态转移之后需要执行的副作用 / A side effect to perform after one state transition</summary>
public abstract record MidiAction
{
    /// <summary>按下虚拟键
    /// Send a key-down for the virtual-key code</summary>
    public sealed record KeyDown(ushort VkCode) : MidiAction;

    /// <summary>抬起虚拟键
    /// Send a key-up for the virtual-key code</summary>
    public sealed record KeyUp(ushort VkCode) : MidiAction;

    /// <summary>
    /// 向 UI 广播事件
    ///
    /// Broadcasts an event to the UI
    /// </summary>
    public sealed record Broadcast(BroadcastKind Kind, byte Note, byte Velocity = 0, string? Key = null) : MidiAction;

    /// <summary>
    /// 注入队列的清空标记：工作线程处理到它时表示此前所有按键动作已落地
    /// 此时置位等待者（Stop/退出路径用）
    ///
    /// A drain marker for the injection queue
    /// When the worker thread reaches it, every earlier key action has landed, so the waiter is signalled
    /// Used by Stop and the shutdown path
    /// </summary>
    public sealed record DrainMarker(System.Threading.ManualResetEventSlim Done) : MidiAction;
}
