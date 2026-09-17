// AppStorage.cs — exe 旁边便携 `.storage/` 目录里的应用设置
// 界面语言存在 "miditap_locale" 键下
// 上次使用的映射配置存在 ".storage/last_config"
// 值都是纯 UTF-8 文本文件
//
// App preference storage in the portable `.storage/` folder next to the exe
// The UI locale goes under the "miditap_locale" key
// The last-used mapping config goes into ".storage/last_config"
// Values are plain UTF-8 text files

namespace MIDITap.Core.Settings;

public static class AppStorage
{
    /// <summary>界面语言的存储键名 / Storage key name for the UI locale</summary>
    public const string LocaleStorageKey = "miditap_locale";

    /// <summary>
    /// 语言是否可用
    /// 判定交给 <see cref="LanguageCatalog"/>（按 i18n/*.json 动态发现），而不是在这里维护一份硬编码清单 —— 那样每加一门语言都得改两个地方
    ///
    /// Validity is delegated to LanguageCatalog (dynamic discovery from i18n/*.json) rather than to a hard-coded list here
    /// Such a list would need editing for every new language
    /// </summary>
    public static bool IsSupportedLocale(string baseDir, string locale)
        => LanguageCatalog.IsSupported(baseDir, locale);

    /// <summary>
    /// 确保 .storage 目录存在
    /// 路径归 AppPaths 所有，这里只负责建目录
    /// 这样「设置值放在哪」只定义一次，本文件的每个 Save* 不必各自再拼一遍
    ///
    /// Ensures the .storage directory exists
    /// AppPaths owns the path and this only creates it
    /// So «where a setting value lives» is defined once instead of being re-assembled by every Save*
    /// </summary>
    private static void EnsureStorageDir(string baseDir)
        => Directory.CreateDirectory(AppPaths.StorageDir(baseDir));

    private static string LocalePath(string baseDir) => AppPaths.StorageFile(baseDir, LocaleStorageKey);

    /// <summary>
    /// 读取界面语言
    /// 未设置或非法时返回 null（由调用方决定默认值）
    /// 兼容 Neutralino 存储格式的 {"language":"zh_CN"} JSON 包装
    ///
    /// Reads the UI locale; null when unset or invalid, so the caller decides the default
    /// It is compatible with the Neutralino storage format (a {"language":"zh_CN"} JSON wrapper)
    /// </summary>
    public static string? GetLocale(string baseDir)
    {
        try
        {
            var value = File.ReadAllText(LocalePath(baseDir)).Trim();
            if (value.StartsWith('{'))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(value);
                    if (document.RootElement.TryGetProperty("language", out var language)
                        && language.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        value = language.GetString()?.Trim() ?? string.Empty;
                    }
                }
                catch
                {
                    return null;
                }
            }
            return IsSupportedLocale(baseDir, value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 保存界面语言
    /// 只接受可用语言（i18n 下有对应文件），避免任意字符串落盘
    ///
    /// Saves the UI locale
    /// Only an available language is accepted (a matching file exists under i18n)
    /// So no arbitrary string reaches disk
    /// </summary>
    public static bool SaveLocale(string baseDir, string locale)
    {
        if (!IsSupportedLocale(baseDir, locale))
        {
            return false;
        }
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(LocalePath(baseDir), locale);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 主题模式 / Theme mode

    /// <summary>
    /// 主题模式存储键
    ///
    /// Theme-mode storage key
    /// </summary>
    public const string ThemeStorageKey = "miditap_theme";

    /// <summary>跟随系统 / Follow the system</summary>
    public const string ThemeSystem = "system";
    /// <summary>强制浅色 / Force light</summary>
    public const string ThemeLight = "light";
    /// <summary>强制深色 / Force dark</summary>
    public const string ThemeDark = "dark";

    /// <summary>受支持的主题模式 / The supported theme modes</summary>
    public static readonly IReadOnlyList<string> SupportedThemes = [ThemeSystem, ThemeLight, ThemeDark];

    public static bool IsSupportedTheme(string theme) => SupportedThemes.Contains(theme);

    private static string ThemePath(string baseDir) => AppPaths.StorageFile(baseDir, ThemeStorageKey);

    /// <summary>
    /// 读取主题模式
    /// 未设置或非法时返回 null，由调用方决定默认值（应用默认跟随系统）
    ///
    /// Reads the theme mode
    /// It returns null when unset or invalid, so the caller decides the default
    /// The app defaults to following the system
    /// </summary>
    public static string? GetThemeMode(string baseDir)
    {
        try
        {
            var value = File.ReadAllText(ThemePath(baseDir)).Trim();
            return IsSupportedTheme(value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 保存主题模式
    /// 只接受受支持的值，避免任意字符串落盘
    ///
    /// Saves the theme mode; only a supported value is accepted, so no arbitrary string reaches disk
    /// </summary>
    public static bool SaveThemeMode(string baseDir, string theme)
    {
        if (!IsSupportedTheme(theme))
        {
            return false;
        }
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(ThemePath(baseDir), theme);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 日志落盘 / Log to file

    /// <summary>
    /// "是否把日志写入文件"的存储键
    ///
    /// Storage key for "write logs to file"
    /// </summary>
    public const string LogToFileStorageKey = "miditap_log_to_file";

    private static string LogToFilePath(string baseDir)
        => AppPaths.StorageFile(baseDir, LogToFileStorageKey);

    /// <summary>
    /// 是否把活动日志写入文件
    /// 默认 **关闭**：未经要求就往磁盘写属于多余副作用
    /// 且日志行可能含设备名与按键内容，应当由用户显式开启
    ///
    /// Whether to write the activity log to a file
    /// Defaults to OFF: writing to disk unasked is an unrequested side effect
    /// Log lines can also contain device names and key presses, so opting in should be explicit
    /// </summary>
    public static bool GetLogToFile(string baseDir)
    {
        try
        {
            return File.ReadAllText(LogToFilePath(baseDir)).Trim() == "1";
        }
        catch
        {
            // 未设置或不可读都视为关闭（与主题/语言一致：读取失败不改变默认行为）
            //
            // Unset and unreadable both count as off, consistent with theme and locale
            // A failed read does not change the default behaviour
            return false;
        }
    }

    /// <summary>保存"日志落盘"开关 / Saves the "write logs to file" switch</summary>
    public static bool SaveLogToFile(string baseDir, bool enabled)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(LogToFilePath(baseDir), enabled ? "1" : "0");
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 更新检查与代理 / Update check & proxy

    /// <summary>"启动时自动检查更新"的存储键 / Storage key for "check for updates at startup"</summary>
    public const string AutoCheckUpdatesStorageKey = "miditap_auto_check_updates";

    /// <summary>
    /// 更新检查使用的代理地址存储键。留空 = 跟随系统代理
    ///
    /// Storage key for the update proxy. Empty means follow the system proxy
    /// </summary>
    public const string UpdateProxyStorageKey = "miditap_update_proxy";

    /// <summary>
    /// 用户选了「忽略此版本」的版本号存储键
    /// 只影响**自动**弹出的更新窗口：从设置页手动检查仍然会显示
    ///
    /// Storage key for the version the user chose to ignore
    /// It affects only the dialog raised automatically; a manual check on the settings page still shows it
    /// </summary>
    public const string IgnoredUpdateStorageKey = "miditap_ignored_update";

    private static string AutoCheckPath(string baseDir)
        => AppPaths.StorageFile(baseDir, AutoCheckUpdatesStorageKey);

    private static string UpdateProxyPath(string baseDir)
        => AppPaths.StorageFile(baseDir, UpdateProxyStorageKey);

    private static string IgnoredUpdatePath(string baseDir)
        => AppPaths.StorageFile(baseDir, IgnoredUpdateStorageKey);


    /// <summary>
    /// 是否在启动时自动检查更新
    /// 默认 **开启**，这与本应用既有行为一致
    /// 开关只是让用户能关掉它
    /// **注意与 GetLogToFile 的方向相反**：那个默认关闭、读失败也回落到关闭
    /// 这里默认开启，因此读取失败必须回落到 true
    /// 否则一次读盘异常就会静默禁用自动更新检查
    ///
    /// Whether to check for updates at startup
    /// Defaults to ON, matching this app's existing behaviour
    /// The switch merely lets the user turn it off
    /// Note this is the OPPOSITE of GetLogToFile, which defaults to off and falls back to off
    /// Here a read failure must fall back to true
    /// One bad read would otherwise silently disable automatic update checks
    /// </summary>
    public static bool GetAutoCheckUpdates(string baseDir)
    {
        try
        {
            return File.ReadAllText(AutoCheckPath(baseDir)).Trim() != "0";
        }
        catch
        {
            return true;
        }
    }

    /// <summary>保存"启动时自动检查更新"开关 / Saves the "check for updates at startup" switch</summary>
    public static bool SaveAutoCheckUpdates(string baseDir, bool enabled)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(AutoCheckPath(baseDir), enabled ? "1" : "0");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取用户已忽略的版本号，没有记录时返回空串
    /// 读失败一律按"没有记录"处理：一个坏文件不该让更新提示从此消失
    ///
    /// Reads the version the user ignored; empty when there is no record
    /// A read failure counts as "no record": one bad file must not silence the update prompt for good
    /// </summary>
    public static string GetIgnoredUpdate(string baseDir)
    {
        try
        {
            return File.ReadAllText(IgnoredUpdatePath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>记录用户忽略的版本号，空串表示清除记录 / Records the ignored version; an empty string clears it</summary>
    public static bool SaveIgnoredUpdate(string baseDir, string version)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(IgnoredUpdatePath(baseDir), version.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// 读取更新代理地址
    /// 未设置返回空串（表示跟随系统代理）
    /// 这里只做长度与字符的基本清理
    /// 合法性交给 UpdateOptions.BuildProxy
    /// 校验规则只有一处，避免两边判断不一致
    ///
    /// Reads the update proxy; empty means follow the system proxy
    /// Only basic trimming happens here
    /// Validity is decided by UpdateOptions.BuildProxy
    /// So the rule lives in exactly one place
    /// </summary>
    public static string GetUpdateProxy(string baseDir)
    {
        try
        {
            return File.ReadAllText(UpdateProxyPath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 保存更新代理地址（空串表示跟随系统代理）
    ///
    /// Saves the update proxy address; an empty string means follow the system proxy
    /// </summary>
    public static bool SaveUpdateProxy(string baseDir, string? proxyUrl)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(UpdateProxyPath(baseDir), (proxyUrl ?? string.Empty).Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
