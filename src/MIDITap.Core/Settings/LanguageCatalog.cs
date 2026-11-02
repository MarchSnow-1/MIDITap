// LanguageCatalog.cs — 语言清单与校验
// 语言由本文件里的硬编码清单决定，不再扫描 i18n 目录
// 发布包升级时并不会删除旧文件，目录里可能留着上一版的 <code>.json；只按清单走，多出来的文件不会被当成一门语言
// 放在 Core（而非 App）是为了能脱离 WinUI 单测：本类只依赖 System.Text.Json
//
// Language catalogue and validation
// The languages are the hard-coded list below; the i18n directory is no longer scanned
// An upgrade does not delete files the new package no longer ships, so a stale <code>.json can survive in the directory
// Following the list alone keeps such a file from becoming a language of its own
// Lives in Core so it can be unit-tested without WinUI; it only needs System.Text.Json

using System.Globalization;
using System.Text.Json;

namespace MIDITap.Core.Settings;

/// <summary>
/// 一门可用语言
/// <paramref name="Code"/> 为文件名（如 zh-Hans）
/// <paramref name="NativeName"/> 为母语名（如 简体中文）
///
/// An available language
/// <paramref name="Code"/> is the file name (e.g. zh-Hans)
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
    /// 支持的语言清单，顺序即界面显示顺序（默认语言在第一项）
    /// 母语名是各语言的自称，写在这里而不读文件，因为它不随界面语言变化
    /// 新增一门语言：往 i18n/ 放 <code>.json，并在这里加一项
    ///
    /// The supported languages, in the order the UI shows them (the default comes first)
    /// A native name is what the language calls itself, so it lives here rather than in the file: it does not change with the UI language
    /// To add one: drop <code>.json into i18n/ and add an entry here
    /// </summary>
    private static readonly LanguageInfo[] Catalog =
    [
        new(DefaultCode, "English"),
        new("zh-Hans", "简体中文"),
        new("zh-Hant", "繁體中文"),
    ];

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

    private static string FilePath(string baseDir, string code) => Path.Combine(DirectoryPath(baseDir), code + ".json");

    /// <summary>
    /// 清单里是否列了这一门语言（不看文件在不在）
    ///
    /// Whether the catalogue lists the language (regardless of the file being present)
    /// </summary>
    private static bool IsListed(string? code)
        => !string.IsNullOrEmpty(code) && Catalog.Any(l => string.Equals(l.Code, code, StringComparison.Ordinal));

    /// <summary>
    /// 枚举可用语言：清单里有、且文件确实存在
    /// 默认语言无论如何都返回，让"语言"界面不会是空的（空的界面会让用户以为功能坏了）
    /// 顺序固定为清单顺序，跨机器、跨次运行一致
    ///
    /// Enumerates the available languages: listed in the catalogue and present on disk
    /// The default one is always returned, so the language page is never empty (an empty page looks like a broken feature)
    /// The order is the catalogue order, identical across machines and runs
    /// </summary>
    public static IReadOnlyList<LanguageInfo> Discover(string baseDir)
    {
        var result = new List<LanguageInfo>(Catalog.Length) { Catalog[0] };
        for (var i = 1; i < Catalog.Length; i++)
        {
            if (File.Exists(FilePath(baseDir, Catalog[i].Code)))
            {
                result.Add(Catalog[i]);
            }
        }
        return result;
    }

    /// <summary>
    /// 该语言是否可用：清单里有它，且 i18n 下有对应的 json 文件
    /// 两者都要，缺文件时选它只会把键名当文案显示
    ///
    /// Whether the language is usable: listed in the catalogue with a matching json file under i18n
    /// Both are required, since picking a language whose file is missing would show raw key names
    /// </summary>
    public static bool IsSupported(string baseDir, string? code)
        => IsListed(code) && File.Exists(FilePath(baseDir, code!));

    /// <summary>
    /// 读某个语言文件里的一个字符串键
    /// 解析失败、键缺失或值非字符串时返回 null
    ///
    /// Reads one string key out of a language file
    /// A parse failure, a missing key or a non-string value yields null
    /// </summary>
    public static string? ReadString(string baseDir, string locale, string key)
        => ReadKeyFrom(FilePath(baseDir, locale), key);

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
            // 文件缺失或损坏：调用方按缺键处理
            //
            // A missing or corrupt file is handled by the caller as a missing key
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

        // 文化名用 '-'（zh-Hans-CN），语言代码同样用 '-'（zh-Hans），统一后再比较
        //
        // Culture names use '-' (zh-Hans-CN) and so do language codes (zh-Hans); both are normalised before comparison
        var normalized = culture.Replace('_', '-');

        var exact = available.FirstOrDefault(code => string.Equals(code, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // 简体/繁体：系统给出 zh-Hans / zh-Hant 或 zh-CN / zh-TW 时挑对应写法
        //
        // Simplified/Traditional: when the system reports zh-Hans / zh-Hant or zh-CN / zh-TW, the matching spelling is picked
        var language = normalized.Split('-')[0];
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase))
        {
            var wantsTraditional = normalized.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("TW", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("HK", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("MO", StringComparison.OrdinalIgnoreCase);
            var preferred = wantsTraditional ? "zh-Hant" : "zh-Hans";
            if (available.Contains(preferred))
            {
                return preferred;
            }
        }

        // 仅语言部分一致（如 ja_JP 可用而系统是 ja-JP-...）
        //
        // Language-only match (the system culture is ja-JP-... while only ja_JP is available)
        var byLanguage = available.FirstOrDefault(
            code => code.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith(language + "_", StringComparison.OrdinalIgnoreCase));
        return byLanguage ?? DefaultCode;
    }
}
