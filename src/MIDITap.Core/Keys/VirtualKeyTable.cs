// VirtualKeyTable.cs — 虚拟键码表（纯数据，不依赖平台）
//
// 使配置中的键名解析到对应的 Windows 虚拟键码
// 条目顺序保留，因为反查对别名保留**首次出现**的键名
// 别名是 "escape" 之后的 "esc"、"lwin" 之后的 "win"
//
// Virtual-Key code table (pure, no platform dependencies)
// Config key names resolve to the matching Windows virtual-key codes
// Entry order is preserved because the reverse lookup keeps the FIRST occurrence for aliases
// The aliases are "esc" after "escape", and "win" after "lwin"

namespace MIDITap.Core.Keys;

public static class VirtualKeyTable
{
    /// <summary>
    /// （键名, Windows 虚拟键码）对，顺序保留，反查时取首次出现的键名
    ///
    /// (key name, Windows virtual-key code) pairs, order preserved so the reverse lookup keeps the first name
    /// </summary>
    public static readonly IReadOnlyList<(string Name, ushort Code)> Entries =
    [
        // 字母键 / Letters
        ("a", 0x41), ("b", 0x42), ("c", 0x43), ("d", 0x44), ("e", 0x45),
        ("f", 0x46), ("g", 0x47), ("h", 0x48), ("i", 0x49), ("j", 0x4A),
        ("k", 0x4B), ("l", 0x4C), ("m", 0x4D), ("n", 0x4E), ("o", 0x4F),
        ("p", 0x50), ("q", 0x51), ("r", 0x52), ("s", 0x53), ("t", 0x54),
        ("u", 0x55), ("v", 0x56), ("w", 0x57), ("x", 0x58), ("y", 0x59),
        ("z", 0x5A),
        // 数字键（主键盘行）/ Numbers (main row)
        ("0", 0x30), ("1", 0x31), ("2", 0x32), ("3", 0x33), ("4", 0x34),
        ("5", 0x35), ("6", 0x36), ("7", 0x37), ("8", 0x38), ("9", 0x39),
        // 功能键 / Function keys
        ("f1", 0x70), ("f2", 0x71), ("f3", 0x72), ("f4", 0x73), ("f5", 0x74),
        ("f6", 0x75), ("f7", 0x76), ("f8", 0x77), ("f9", 0x78), ("f10", 0x79),
        ("f11", 0x7A), ("f12", 0x7B), ("f13", 0x7C), ("f14", 0x7D), ("f15", 0x7E),
        ("f16", 0x7F), ("f17", 0x80), ("f18", 0x81), ("f19", 0x82), ("f20", 0x83),
        ("f21", 0x84), ("f22", 0x85), ("f23", 0x86), ("f24", 0x87),
        // 控制键 / Control
        ("backspace", 0x08), ("tab", 0x09), ("enter", 0x0D), ("shift", 0x10),
        ("ctrl", 0x11), ("alt", 0x12), ("pause", 0x13), ("capslock", 0x14),
        ("escape", 0x1B), ("esc", 0x1B), ("space", 0x20),
        // 导航键 / Navigation
        ("pageup", 0x21), ("pagedown", 0x22), ("end", 0x23), ("home", 0x24),
        ("left", 0x25), ("up", 0x26), ("right", 0x27), ("down", 0x28),
        ("insert", 0x2D), ("delete", 0x2E),
        // 左右修饰键 / Left & right modifiers
        ("lshift", 0xA0), ("rshift", 0xA1),
        ("lctrl", 0xA2), ("rctrl", 0xA3),
        ("lalt", 0xA4), ("ralt", 0xA5),
        ("lwin", 0x5B), ("rwin", 0x5C), ("win", 0x5B),
        // 数字小键盘 / Numpad
        ("num0", 0x60), ("num1", 0x61), ("num2", 0x62), ("num3", 0x63), ("num4", 0x64),
        ("num5", 0x65), ("num6", 0x66), ("num7", 0x67), ("num8", 0x68), ("num9", 0x69),
        ("multiply", 0x6A), ("add", 0x6B), ("separator", 0x6C), ("subtract", 0x6D),
        ("decimal", 0x6E), ("divide", 0x6F), ("numlock", 0x90),
        // 美式标点 / US punctuation
        ("semicolon", 0xBA), ("equal", 0xBB), ("comma", 0xBC), ("minus", 0xBD),
        ("period", 0xBE), ("slash", 0xBF), ("backquote", 0xC0), ("lbracket", 0xDB),
        ("backslash", 0xDC), ("rbracket", 0xDD), ("quote", 0xDE),
        // 系统键 / System
        ("printscreen", 0x2C), ("scrolllock", 0x91), ("apps", 0x5D),
        // 媒体键 / Media
        ("mute", 0xAD), ("volumedown", 0xAE), ("volumeup", 0xAF),
        ("nexttrack", 0xB0), ("prevtrack", 0xB1), ("stop", 0xB2), ("playpause", 0xB3),
        // 其他 / Other
        ("select", 0x29), ("print", 0x2A), ("execute", 0x2B), ("help", 0x2F),
        ("sleep", 0x5F),
    ];

    private static readonly Dictionary<string, ushort> CodeByName =
        Entries.ToDictionary(e => e.Name, e => e.Code, StringComparer.Ordinal);

    // 多个键名共用一个键码时保留首次出现的名称
    //
    // The first name wins when several names alias to the same code, keeping log labels stable
    private static readonly Dictionary<ushort, string> NameByCode = BuildReverse();

    private static Dictionary<ushort, string> BuildReverse()
    {
        var map = new Dictionary<ushort, string>();
        foreach (var (name, code) in Entries)
        {
            if (!map.ContainsKey(code))
            {
                map[code] = name;
            }
        }
        return map;
    }

    /// <summary>
    /// 把配置里的键名（小写、规范词表）解析为 VK 码
    ///
    /// Resolves a config key name (lowercase, canonical vocabulary) to a VK code
    /// </summary>
    public static ushort? TryGetCode(string name)
        => CodeByName.TryGetValue(name, out var code) ? code : null;

    /// <summary>
    /// 反查，按"首次出现者优先"的语义
    ///
    /// Reverse lookup with first-occurrence-wins semantics
    /// </summary>
    public static string? TryGetName(ushort code)
        => NameByCode.TryGetValue(code, out var name) ? name : null;

    /// <summary>
    /// 键位标签：键名或 "0xNN" 十六进制回退
    ///
    /// Key label: name or "0xNN" hex fallback
    /// </summary>
    public static string LabelFor(ushort code)
        => TryGetName(code) ?? $"0x{code:X}";
}
