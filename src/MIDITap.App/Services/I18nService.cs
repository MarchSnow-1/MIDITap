// I18nService.cs — JSON 字典式多语言
// en_US 为回退底包，{var} 插值，运行中可切换，页面订阅 LanguageChanged 重新绑定文本
//
// 语言清单不硬编码，改由 i18n/*.json **动态发现**（LanguageCatalog）
// 新增语言只需丢一个 json 文件，代码与界面都不用动
//
// I18nService.cs — dictionary-based localisation loaded from JSON files
// en_US is the fallback base pack; {var} interpolation
// The language is switchable at runtime, and pages re-bind their text on LanguageChanged
// The language list is not hard-coded; it is **discovered** from i18n/*.json (LanguageCatalog)
// Adding a language means dropping in one json file, and neither the code nor the UI has to change
//

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using MIDITap.Core.Settings;

namespace MIDITap.App.Services;

public sealed class I18nService : INotifyPropertyChanged
{
    private readonly string _baseDir;
    private Dictionary<string, string> _strings = new();

    public event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前语言代码（如 zh_CN）</summary>
    /// <remarks>The current language code (e.g. zh_CN)</remarks>
    public string Current { get; private set; } = LanguageCatalog.DefaultCode;

    /// <summary>当前语言的母语名（用于界面显示）</summary>
    /// <remarks>The native name of the current language, as shown in the UI</remarks>
    public string CurrentNativeName { get; private set; } = LanguageCatalog.DefaultCode;

    /// <summary>可用语言清单（含母语名），首次访问时枚举并缓存</summary>
    /// <remarks>The available languages including their native names; enumerated and cached on first access</remarks>
    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    public I18nService(string baseDir)
    {
        _baseDir = baseDir;
        AvailableLanguages = LanguageCatalog.Discover(baseDir);
        var saved = Core.Settings.AppStorage.GetLocale(baseDir);
        Load(saved ?? PreferredLocale());
    }

    /// <summary>
    /// 翻译单个键；缺失时回退 en_US，再缺失返回键名本身
    /// 返回键名而不是空串：界面上直接暴露 "settings.theme" 比留白更容易发现问题
    ///
    /// Translates a single key; a missing key falls back to en_US, and a key missing there too yields the key name itself
    /// The key name is returned instead of an empty string
    /// Showing "settings.theme" in the UI makes the problem easier to notice than leaving the space blank
    /// </summary>
    public string T(string key)
        => _strings.TryGetValue(key, out var value) ? value : key;

    /// <summary>
    /// 键是否存在。用于「有就挂上」这类**可选**文案（例如某个级别额外的一句提示）
    /// T 在缺键时会回退成键名，所以它的返回值不能拿来判断"有没有"
    ///
    /// Whether the key exists, for **optional** copy that is attached only when present (the extra hint for one level, say)
    /// T falls back to the key name itself, so its return value cannot answer that
    /// </summary>
    public bool Has(string key) => _strings.ContainsKey(key);

    /// <summary>带 {var} 插值的翻译</summary>
    /// <remarks>Translation with {var} interpolation</remarks>
    public string T(string key, IReadOnlyDictionary<string, string?> vars)
    {
        var text = T(key);
        foreach (var (name, value) in vars)
        {
            text = text.Replace("{" + name + "}", value);
        }
        return text;
    }

    public string T(string key, params (string Name, string? Value)[] vars)
    {
        var dict = new Dictionary<string, string?>(vars.Length);
        foreach (var (name, value) in vars)
        {
            dict[name] = value;
        }
        return T(key, dict);
    }

    /// <summary>
    /// 切换语言并持久化。语言不可用时不做任何改动（返回 false）
    ///
    /// Switches the language and persists it. A language that is not available changes nothing (returns false)
    /// </summary>
    public bool SetLanguage(string locale)
    {
        if (locale == Current)
        {
            return true;
        }
        if (!Core.Settings.AppStorage.SaveLocale(_baseDir, locale))
        {
            return false;
        }
        Load(locale);
        LanguageChanged?.Invoke();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        return true;
    }

    private void Load(string locale)
    {
        // 语言文件不存在时退回默认语言，避免把界面切成一片键名
        //
        // A missing language file falls back to the default language, so the UI does not turn into a wall of key names
        if (!LanguageCatalog.IsSupported(_baseDir, locale))
        {
            locale = LanguageCatalog.DefaultCode;
        }

        // en_US 作为回退底包，再叠加所选语言的条目
        //
        // en_US serves as the fallback base pack, and the selected language's entries are layered on top of it
        var merged = LoadPack(LanguageCatalog.DefaultCode);
        if (!string.Equals(locale, LanguageCatalog.DefaultCode, StringComparison.Ordinal))
        {
            foreach (var (key, value) in LoadPack(locale))
            {
                merged[key] = value;
            }
        }
        _strings = merged;
        Current = locale;
        CurrentNativeName = AvailableLanguages.FirstOrDefault(l => l.Code == locale)?.NativeName ?? locale;
    }

    private Dictionary<string, string> LoadPack(string locale)
    {
        var path = Path.Combine(_baseDir, LanguageCatalog.DirectoryName, locale + ".json");
        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is null ? new Dictionary<string, string>() : new Dictionary<string, string>(parsed);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine($"[miditap.i18n] Failed to load language pack '{locale}': {err.Message}");
            return new Dictionary<string, string>();
        }
    }

    // 按系统 UI 文化自动选择最接近的可用语言
    // 匹配顺序：完全一致 -> 仅语言部分一致 -> 中文再按简繁偏好 -> 默认
    //
    // The closest AVAILABLE language to the system UI culture is chosen
    // Order: exact match, then language-only match, then Simplified/Traditional preference for Chinese, default
    private string PreferredLocale()
    {
        var available = AvailableLanguages.Select(l => l.Code).ToList();
        var culture = CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrEmpty(culture))
        {
            return LanguageCatalog.DefaultCode;
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
        return byLanguage ?? LanguageCatalog.DefaultCode;
    }
}
