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

using System.Globalization;
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

    /// <summary>
    /// 语言文件里存放**默认配置文件名**的键（不带 .json）
    /// 首次启动生成的那份配置因此跟随界面语言：中文得到「默认配置」，英文得到「Default Config」
    /// 键缺失或值不可用时回退为 FallbackConfigStem，而不是让首次启动没有配置
    ///
    /// The key holding the **default config file name** inside a language file (without .json)
    /// The config created on first launch therefore follows the UI language: Chinese yields 默认配置, English yields Default Config
    /// A missing key or an unusable value falls back to FallbackConfigStem rather than leaving the first launch with no config
    /// </summary>
    public const string DefaultConfigStemKey = "config.defaultName";

    /// <summary>
    /// 读不到默认配置名时的兜底主干
    /// 取的是**默认语言**那一份，因此与 en_US 的 config.defaultName 一致
    /// 这一层只在所选语言与默认语言的键都读不出来时走到，此时跟随默认语言才有一致的名字
    ///
    /// The fallback stem when no default config name can be read
    /// It is the **default language's** value, so it matches en_US's config.defaultName
    /// This layer is reached only when neither the selected nor the default language yields the key, and following the default language is what keeps the name consistent
    /// </summary>
    public const string FallbackConfigStem = "Default Config";

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
    private static string? ReadNativeName(string path) => ReadKeyFrom(path, NativeNameKey);

    /// <summary>
    /// 读某个语言文件里的一个字符串键
    /// 解析失败、键缺失或值非字符串时返回 null
    ///
    /// Reads one string key out of a language file
    /// A parse failure, a missing key or a non-string value yields null
    /// </summary>
    public static string? ReadString(string baseDir, string locale, string key)
        => ReadKeyFrom(Path.Combine(DirectoryPath(baseDir), locale + ".json"), key);

    private static string? ReadKeyFrom(string path, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
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

    /// <summary>
    /// 默认配置文件名的主干（不带 .json），取自当前语言的 config.defaultName
    /// 该语言没有这个键时用默认语言的，两者都没有才回退 FallbackConfigStem
    /// 与界面文案一样先取所选语言、再退回 en_US，因此键漏译不会让首次启动没有配置
    ///
    /// The stem of the default config file name (without .json), taken from the current language's config.defaultName
    /// A language without that key uses the default language's value, and only if neither has one does it fall back to FallbackConfigStem
    /// It resolves the selected language first and then en_US, exactly as UI copy does, so a missing translation cannot leave the first launch without a config
    /// </summary>
    public static string DefaultConfigStem(string baseDir)
    {
        var locale = ResolveLocale(baseDir);
        var stem = ReadString(baseDir, locale, DefaultConfigStemKey);
        if (stem is null && !string.Equals(locale, DefaultCode, StringComparison.Ordinal))
        {
            stem = ReadString(baseDir, DefaultCode, DefaultConfigStemKey);
        }
        return stem ?? FallbackConfigStem;
    }

    /// <summary>
    /// 本次运行应当使用的语言：已保存的优先，其次按系统 UI 文化匹配最接近的一项
    /// 已保存的语言文件被删掉时不再强行使用它，而是重新按系统文化挑选
    /// 匹配顺序：完全一致 -> 中文按简繁偏好 -> 仅语言部分一致 -> 默认
    ///
    /// The language this run should use: a saved one wins, otherwise the closest match to the system UI culture
    /// A saved language whose file has been removed is no longer forced; the system culture is consulted again
    /// Order: exact match, then Simplified/Traditional preference for Chinese, then language-only match, then the default
    /// </summary>
    public static string ResolveLocale(string baseDir)
    {
        var saved = AppStorage.GetLocale(baseDir);
        if (!string.IsNullOrEmpty(saved) && IsSupported(baseDir, saved))
        {
            return saved;
        }

        var available = Discover(baseDir).Select(l => l.Code).ToList();
        var culture = CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrEmpty(culture))
        {
            return DefaultCode;
        }

        // 文化名用 '-'（zh-Hans-CN），语言代码用 '_'（zh_CN），统一后再比较
        //
        // Culture names use '-' (zh-Hans-CN) while language codes use '_' (zh_CN); the two are normalised before being compared
        var normalized = culture.Replace('-', '_');

        var exact = available.FirstOrDefault(code => string.Equals(code, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // 简体/繁体：系统给出 zh-Hans / zh-Hant 或 zh-CN / zh-TW 时挑对应写法
        //
        // Simplified/Traditional: when the system reports zh-Hans / zh-Hant or zh-CN / zh-TW, the matching spelling is picked
        var language = normalized.Split('_')[0];
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase))
        {
            var wantsTraditional = normalized.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("TW", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("HK", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("MO", StringComparison.OrdinalIgnoreCase);
            var preferred = wantsTraditional ? "zh_TW" : "zh_CN";
            if (available.Contains(preferred))
            {
                return preferred;
            }
        }

        // 仅语言部分一致（如 ja_JP 可用而系统是 ja-JP-...）
        //
        // Language-only match (the system culture is ja-JP-... while only ja_JP is available)
        var byLanguage = available.FirstOrDefault(
            code => code.StartsWith(language + "_", StringComparison.OrdinalIgnoreCase));
        return byLanguage ?? DefaultCode;
    }
}
