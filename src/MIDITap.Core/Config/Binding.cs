// Binding.cs — 把一条键位规格（"a"、"ctrl+b"）解析成 VK 码
//
// 只有当规格长度大于 1 且含 '+' 时才当作组合键
// 未知键名与空片段一律拒绝
// 标签由规范化后的键名列表用 '+' 连接而成
//
// Parses one key-spec ("a", "ctrl+b") into VK codes
// A spec is a combo only when it is longer than one char and contains '+'
// Unknown names and empty parts are rejected
// The label is the normalized token list joined with '+'

namespace MIDITap.Core.Config;

public sealed record Binding(IReadOnlyList<ushort> VkCodes, string Label);

public static class BindingParser
{
    /// <summary>
    /// 解析单条键位配置
    /// 解析失败返回 null（silent 时不再打印警告）
    ///
    /// Parses one key-binding spec
    /// Returns null on failure (with silent, no warning is printed)
    /// </summary>
    public static Binding? ParseBinding(string? keySpec, string noteStr, bool silent = false)
    {
        if (keySpec is null || keySpec.Trim().Length == 0)
        {
            if (!silent)
            {
                Console.Error.WriteLine(
                    $"[miditap.config] Invalid key name for note \"{noteStr}\", expected non-empty string, skipping...");
            }
            return null;
        }

        var normalizedSpec = keySpec.Trim().ToLowerInvariant();
        var isCombo = normalizedSpec.Length > 1 && normalizedSpec.Contains('+');
        var tokens = isCombo
            ? normalizedSpec.Split('+').Select(part => part.Trim()).ToArray()
            : [normalizedSpec];

        if (tokens.Any(token => token.Length == 0))
        {
            if (!silent)
            {
                Console.Error.WriteLine($"[miditap.config] Invalid key combo \"{keySpec}\" for note \"{noteStr}\", skipping...");
            }
            return null;
        }

        var vkCodes = new List<ushort>(tokens.Length);
        foreach (var token in tokens)
        {
            var vk = Keys.VirtualKeyTable.TryGetCode(token);
            if (vk is null)
            {
                if (!silent)
                {
                    Console.Error.WriteLine($"[miditap.config] Can't find '{token}' in VK Code List, skipping note \"{noteStr}\"...");
                }
                return null;
            }
            vkCodes.Add(vk.Value);
        }

        return new Binding(vkCodes, string.Join("+", tokens));
    }
}
