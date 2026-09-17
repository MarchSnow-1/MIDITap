// HeldNote.cs — 当前仍被按住的音符
//
// 页面按导航重建，重建后无从得知“哪些音符正按着”
// 状态机持有那份状态，这个类型就是它交给界面的形状
//
// HeldNote.cs — a note that is still held
//
// A page is rebuilt on navigation and cannot tell which notes are still held
// The state machine holds that state, and this type is the shape it hands to the UI

namespace MIDITap.Core.Midi;

/// <summary>一个仍被按住的音符：音符编号 + 键位标签（未绑定为 null）
/// One note still held: the note number plus its key label, null when unbound</summary>
public sealed record HeldNote(byte Note, string? KeyLabel);
