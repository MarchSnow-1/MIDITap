// NoteNames.cs — 把 MIDI 音符号转成显示名（C-1 .. G9），音符盘、映射列表和编辑器都用它
//
// NoteNames.cs — MIDI note number to display name (C-1 .. G9)
// Used by the note board, the mapping list and the editor

namespace MIDITap.App.Helpers;

public static class NoteNames
{
    private static readonly string[] Names =
    [
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B",
    ];

    /// <summary>0 -> "C-1", 21 -> "A0", 60 -> "C4", 108 -> "C8"</summary>
    public static string Name(byte note)
    {
        var name = Names[note % 12];
        var octave = note / 12 - 1;
        return $"{name}{octave}";
    }
}
