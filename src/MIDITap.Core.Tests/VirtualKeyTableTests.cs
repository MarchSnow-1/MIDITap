// VK 表完整性测试：每个有文档的键名都要能解析成有效键码
// 键名唯一，别名一致
// 反查结果与正查相符
//
// VK table integrity tests: every documented key name resolves to a valid code
// Names are unique, aliases agree
// The reverse lookup matches forward lookups

using MIDITap.Core.Keys;
using Xunit;

namespace MIDITap.Core.Tests;

public class VirtualKeyTableTests
{
    [Fact]
    public void Every_documented_key_name_resolves()
    {
        var all = new List<string>();
        all.AddRange(Enumerable.Range('a', 26).Select(c => ((char)c).ToString()));
        all.AddRange(Enumerable.Range(0, 10).Select(d => d.ToString()));
        all.AddRange(Enumerable.Range(1, 24).Select(i => $"f{i}"));
        all.AddRange(new[]
        {
            "enter", "space", "tab", "backspace", "shift", "ctrl", "alt", "escape", "esc",
            "capslock", "pause", "up", "down", "left", "right", "home", "end", "pageup",
            "pagedown", "insert", "delete", "lshift", "rshift", "lctrl", "rctrl", "lalt",
            "ralt", "lwin", "rwin", "win", "num0", "num1", "num2", "num3", "num4", "num5",
            "num6", "num7", "num8", "num9", "numlock", "add", "subtract", "multiply",
            "divide", "decimal", "separator", "printscreen", "scrolllock", "apps", "mute",
            "volumedown", "volumeup", "nexttrack", "prevtrack", "stop", "playpause",
            "select", "print", "execute", "help", "sleep", "backquote", "minus", "equal",
            "lbracket", "rbracket", "backslash", "semicolon", "quote", "comma", "period",
            "slash",
        });

        foreach (var name in all)
        {
            Assert.True(VirtualKeyTable.TryGetCode(name) is not null, $"missing key: {name}");
        }
    }

    [Fact]
    public void Codes_fall_in_valid_ranges_and_names_are_unique()
    {
        var seen = new HashSet<string>();
        foreach (var (name, code) in VirtualKeyTable.Entries)
        {
            Assert.True(seen.Add(name), $"duplicate key name: {name}");
            Assert.True(code > 0, $"key {name} has code 0");
        }
    }

    [Fact]
    public void Canonical_letters_and_numbers()
    {
        Assert.Equal((ushort)0x41, VirtualKeyTable.TryGetCode("a"));
        Assert.Equal((ushort)0x5A, VirtualKeyTable.TryGetCode("z"));
        Assert.Equal((ushort)0x30, VirtualKeyTable.TryGetCode("0"));
        Assert.Equal((ushort)0x39, VirtualKeyTable.TryGetCode("9"));
        // 大写字母不在表中（仅接受小写） / Uppercase letters are not in the table (lowercase only)
        Assert.Null(VirtualKeyTable.TryGetCode("X"));
    }

    [Fact]
    public void Function_keys_f1_to_f24()
    {
        for (var i = 1; i <= 24; i++)
        {
            Assert.Equal((ushort)(0x70 + i - 1), VirtualKeyTable.TryGetCode($"f{i}"));
        }
    }

    [Fact]
    public void Alias_pairs_agree()
    {
        Assert.Equal(VirtualKeyTable.TryGetCode("escape"), VirtualKeyTable.TryGetCode("esc"));
        Assert.Equal((ushort)0x1B, VirtualKeyTable.TryGetCode("escape"));
        Assert.Equal(VirtualKeyTable.TryGetCode("lwin"), VirtualKeyTable.TryGetCode("win"));
        Assert.Equal((ushort)0x5B, VirtualKeyTable.TryGetCode("win"));
        Assert.Equal("escape", VirtualKeyTable.TryGetName(0x1B));
        Assert.Equal("lwin", VirtualKeyTable.TryGetName(0x5B));
    }

    [Fact]
    public void Reverse_lookup_matches_forward_for_canonical_names()
    {
        foreach (var (name, code) in VirtualKeyTable.Entries)
        {
            if (name is "esc" or "win")
            {
                continue; // 别名与规范名共享同一个键码 / aliases share the canonical's code
            }
            Assert.Equal(name, VirtualKeyTable.TryGetName(code));
        }
    }

    [Fact]
    public void Unknown_or_case_mismatched_names_return_none()
    {
        Assert.Null(VirtualKeyTable.TryGetCode("A"));
        Assert.Null(VirtualKeyTable.TryGetCode("arrowup"));
        Assert.Null(VirtualKeyTable.TryGetCode("control"));
        Assert.Null(VirtualKeyTable.TryGetCode(""));
        Assert.Null(VirtualKeyTable.TryGetName(0xFFFF));
    }
}
