// LanguageCatalog.cs — 语言清单的发现与校验
// 设计：语言由 i18n/*.json 文件**动态发现**，而不是在代码里维护一份硬编码清单
// 新增一门语言 = 往 i18n/ 丢一个 <code>.json（内含 lang.name 作为母语名），无需改代码
// 这样"多语言"是可扩展的，而不是每加一门语言都要动 C# 与 XAML
// 放在 Core（而非 App）是为了能脱离 WinUI 单测：本类只依赖 System.Text.Json
//
// Language catalogue discovery and validation
// Languages are DISCOVERED from the i18n/*.json files, not kept in a hard-coded list
// Adding a language means dropping <code>.json into i18n/ (with lang.name holding the native name)
// No code change is needed, so the feature is genuinely extensible
// Lives in Core so it can be unit-tested without WinUI; it only needs System.Text.Json

using System.Text.Json;

namespace MIDITap.Core.Settings;

/// <summary>
/// 一门可用语言
/// <paramref name="Code"/> 为文件名（如 zh_CN）
/// <paramref name="NativeName"/> 为母语名（如 简体中文）
///
/// An available language
/// <paramref name="Code"/> is the file name (e.g. zh_CN)
/// <paramref name="NativeName"/> is the native name (e.g. 简体中文)
/// </summary>
public sealed record LanguageInfo(string Code, string NativeName);

public static class LanguageCatalog
{
    /// <summary>i18n 文件所在目录名（exe 旁） / Directory name holding the i18n files (next to the exe)</summary>
    public const string DirectoryName = "i18n";

    /// <summary>默认与最终回退语言 / The default and final fallback language</summary>
    public const string DefaultCode = "en_US";

    /// <summary>
    /// 语言文件里存放母语名的键。没有该键时退回用文件名显示
    ///
    /// The key holding the native name inside a language file; without it the file name is displayed instead
    /// </summary>
    public const string NativeNameKey = "lang.name";

    private static string DirectoryPath(string baseDir) => Path.Combine(baseDir, DirectoryName);

    /// <summary>
    /// 枚举可用语言
    /// 目录不存在或没有可用文件时，至少返回默认语言一项 —— 让"语言"界面永远不会是空的（空的界面会让用户以为功能坏了）
    ///
    /// Enumerates available languages, always returning at least the default one
    /// That way the language page can never be empty (an empty page looks like a broken feature)
    /// </summary>
    public static IReadOnlyList<LanguageInfo> Discover(string baseDir)
    {
        var result = new List<LanguageInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var dir = DirectoryPath(baseDir);
            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
                {
                    var code = Path.GetFileNameWithoutExtension(path);
                    if (!seen.Add(code))
                    {
                        continue;
                    }
                    result.Add(new LanguageInfo(code, ReadNativeName(path) ?? code));
                }
            }
        }
        catch
        {
            // 目录不可读：退回到下面的兜底，不影响应用启动
            //
            // An unreadable directory falls back to the code below and does not affect app startup
        }

        // 默认语言必须存在，且固定排在最前（顺序稳定，界面不会因文件系统顺序而变）
        //
        // The default language must exist and always comes first, so the order is stable
        // The UI therefore does not depend on file-system order
        var existing = result.FirstOrDefault(l => l.Code == DefaultCode);
        result.RemoveAll(l => l.Code == DefaultCode);
        result.Insert(0, existing ?? new LanguageInfo(DefaultCode, DefaultCode));

        // 其余按语言代码排序，保证跨机器、跨次运行显示顺序一致
        //
        // The rest are sorted by language code, so the order is identical across machines and runs
        var rest = result.Skip(1).OrderBy(l => l.Code, StringComparer.Ordinal).ToList();
        return [result[0], .. rest];
    }

    /// <summary>
    /// 该语言是否可用（即 i18n 下存在对应的 json 文件）
    ///
    /// Whether the language is available, i.e. a matching json file exists under i18n
    /// </summary>
    public static bool IsSupported(string baseDir, string? code)
        => !string.IsNullOrEmpty(code) && File.Exists(Path.Combine(DirectoryPath(baseDir), code + ".json"));

    /// <summary>
    /// 读出语言文件里的 lang.name（母语名）
    /// 解析失败、键缺失或值非字符串时返回 null，由调用方回退到语言代码
    /// 语言名读不出来不该让整门语言不可用
    ///
    /// Reads lang.name (the native name) from a language file
    /// A parse failure, a missing key or a non-string value yields null
    /// The caller then falls back to the language code
    /// An unreadable name must not make the whole language unavailable
    /// </summary>
    private static string? ReadNativeName(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(NativeNameKey, out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                var text = name.GetString()?.Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }
        catch
        {
            // 文件损坏：该项仍会以文件名显示，语言本身依旧可选
            //
            // Even a corrupt file still shows up under its file name, and the language stays selectable
        }
        return null;
    }
}
