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
    /// 判定交给 <see cref="LanguageCatalog"/>，清单只有它那一份，这里不重复维护
    ///
    /// Validity is delegated to LanguageCatalog, which owns the only copy of the list
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

    /// <summary>
    /// 已下载完成、等待重启的版本号存储键
    /// 记下它，用户关掉更新窗口、之后重新启动应用时，才能恢复到「重启并更新」而不是重新下载一遍
    ///
    /// Storage key for the version downloaded and awaiting a restart
    /// Recording it lets a relaunch resume at "restart and update" after the update window was closed,
    /// rather than downloading the whole package again
    /// </summary>
    public const string StagedUpdateStorageKey = "miditap_staged_update";

    /// <summary>
    /// 与上面那条记录配套的形态存储键，值是 portable 或 installer
    /// 两种形态要核对的凭据不同：便携看解压出来的必需文件，安装器看那个 setup.exe 还在不在
    /// 因此必须一起记下来，否则重启后无从判断该核对什么
    /// 旧版本只写了版本号，因此这条缺失时按便携路线处理（见 StagedUpdate）
    ///
    /// The storage key for the shape that goes with the record above; the value is portable or installer
    /// The two shapes check different evidence: the portable one looks for the extracted required files, the
    /// installer one only asks whether that setup.exe is still there
    /// Both therefore have to be recorded, or a relaunch cannot know what to check
    /// An older version wrote only the version, so a missing key means the portable route (see StagedUpdate)
    /// </summary>
    public const string StagedUpdateKindStorageKey = "miditap_staged_update_kind";

    private static string AutoCheckPath(string baseDir)
        => AppPaths.StorageFile(baseDir, AutoCheckUpdatesStorageKey);

    private static string UpdateProxyPath(string baseDir)
        => AppPaths.StorageFile(baseDir, UpdateProxyStorageKey);

    private static string IgnoredUpdatePath(string baseDir)
        => AppPaths.StorageFile(baseDir, IgnoredUpdateStorageKey);

    private static string StagedUpdatePath(string baseDir)
        => AppPaths.StorageFile(baseDir, StagedUpdateStorageKey);

    private static string StagedUpdateKindPath(string baseDir)
        => AppPaths.StorageFile(baseDir, StagedUpdateKindStorageKey);

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
    /// 读取已下载完成、等待重启的版本号，没有记录时返回空串
    /// 读失败同样按"没有记录"处理：重新下载一次即可，不该让程序起不来或卡在某个阶段
    ///
    /// Reads the version downloaded and awaiting a restart; empty when there is no record
    /// A read failure counts as "no record" as well: the user simply downloads again,
    /// which beats failing to start or getting stuck at a stage
    /// </summary>
    public static string GetStagedUpdate(string baseDir)
    {
        try
        {
            return File.ReadAllText(StagedUpdatePath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 读取那条记录配套的形态，没有记录时返回空串
    /// 读失败同样返回空串：调用方据此按便携路线处理，那是保守的一侧
    ///
    /// Reads the shape that goes with the record; empty when there is none
    /// A read failure is empty as well, so the caller takes the portable route, which is the cautious side
    /// </summary>
    public static string GetStagedUpdateKind(string baseDir)
    {
        try
        {
            return File.ReadAllText(StagedUpdateKindPath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 记录等待重启的版本号与形态，版本号为空串表示清除记录
    /// 两条一起写：只写其中一条会让重启后的核对依据与记录对不上
    ///
    /// Records the version and shape awaiting a restart; an empty version clears the record
    /// Both are written together: leaving one out would make the evidence a relaunch checks disagree with the record
    /// </summary>
    public static bool SaveStagedUpdate(string baseDir, string version, string kind)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(StagedUpdatePath(baseDir), version.Trim());
            File.WriteAllText(StagedUpdateKindPath(baseDir), kind.Trim());
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

    // ------------------------------------------------------------------ MIDIBridge 扩展 / MIDIBridge extension

    /// <summary>
    /// MIDIBridge 上游服务器地址的存储键
    /// 语法校验交给 BridgeSettings.Validate（那里是唯一规则源），这里只做读取与修剪
    ///
    /// Storage key for the MIDIBridge upstream server address
    /// Syntax validation is BridgeSettings.Validate's job (the single rule source); this only reads and trims
    /// </summary>
    public const string BridgeHostStorageKey = "miditap_bridge_host";

    /// <summary>MIDIBridge 上游服务器端口的存储键 / Storage key for the MIDIBridge upstream server port</summary>
    public const string BridgePortStorageKey = "miditap_bridge_port";

    /// <summary>
    /// MIDIBridge 认证密码的键名
    /// 文件仍在 .storage 下，与其它设置同目录；特别之处在于**本文件不提供它的读写方法**
    /// 实现归 Bridge/BridgeSecret.cs，方向是 Bridge 引用 Settings，Settings 不反向引用 Bridge
    /// 上面那几组普通 Save*/Get* 会写出明文且继承目录 ACL 的文件，密码不能那样存，所以实现单独放
    ///
    /// The key name for the MIDIBridge auth password
    /// The file still sits under .storage beside the other settings; what differs is that **this file offers no
    /// read/write method for it**. The implementation lives in Bridge/BridgeSecret.cs, with Bridge referring to
    /// Settings and Settings never referring back to Bridge
    /// The ordinary Save*/Get* pair above produces a plain-text file under the directory's inherited ACL, which is
    /// exactly what a password must not get, so its implementation sits separately
    /// </summary>
    public const string BridgePasswordStorageKey = "miditap_bridge_password";

    /// <summary>
    /// 服务器列表的存储键（JSON 数组）
    /// 列表与密码分开存：本文件里可以放用户起的名字与地址端口，不含任何密码
    /// 单组键 miditap_bridge_host/_port/_password 由 BridgeServerStore 迁移进列表
    ///
    /// Storage key for the server list (a JSON array)
    /// The list and the passwords are stored apart: this file holds user-given names, addresses and ports, and no
    /// password at all. The single-address keys above are migrated into the list by BridgeServerStore
    /// </summary>
    public const string BridgeServerListStorageKey = "miditap_bridge_servers";

    /// <summary>
    /// 当前选中服务器的存储键（存的是服务器 id）
    /// 与输入源键 miditap_bridge_source 并存：source 说明"声音从哪一路来"，本键说明"远程那一路连的是哪台服务器"
    ///
    /// Storage key for the selected server (the value is a server id)
    /// It sits beside the source key miditap_bridge_source: that one says which route the sound comes from, this one
    /// says which server the remote route connects to
    /// </summary>
    public const string BridgeSelectedServerStorageKey = "miditap_bridge_selected_server";

    /// <summary>
    /// 当前输入源（None / Physical / Bridge）的存储键
    /// 枚举值由 App 层的 MidiSource 定义（i18n 键归界面层），这里按字符串原样存取
    /// 合法性由调用方校验：读回来的字符串可能是旧版本或手工编辑留下的任意值
    ///
    /// Storage key for the current input source (None / Physical / Bridge)
    /// The enum is defined in the App layer (i18n keys belong to the UI), so this stores the string as given
    /// The caller validates it: a value read back may be anything left by an older version or a manual edit
    /// </summary>
    public const string BridgeSourceStorageKey = "miditap_bridge_source";

    /// <summary>
    /// 已安装上游程序的版本号存储键（形如 v1.3.1）
    /// 它记的是**上一次探测成功**的结果，不是权威来源：真正的版本仍以上游可执行文件为准，
    /// 探测拿不到时读这个值只用于展示，不用于"要不要更新"的判断（判断走 Core 的实时探测）
    /// 之所以还要落盘：每次刷新界面都起一次子进程探测代价太大，而"已装版本"也是协议漂移检测的输入
    ///
    /// Storage key for the installed upstream version (shaped like v1.3.1)
    /// It records the **last successful probe**, and it is not the authority: the real version still comes from the
    /// upstream executable, so when a probe fails this value is for display only and does not feed the update verdict
    /// (that uses Core's live probe)
    /// It is persisted because probing spawns a subprocess on every UI refresh, and because this value is also an
    /// input to protocol-drift detection
    /// </summary>
    public const string BridgeInstalledVersionStorageKey = "miditap_bridge_installed_version";

    /// <summary>
    /// 已安装上游程序的 IPC 协议版本存储键（形如 1.0.0）
    /// 与版本号分开存：两者回答的问题不同（"要不要升级"与"能不能对接"），
    /// 上游也可能只升版本号而协议不动，合在一个值里会让后者被前者带着走
    ///
    /// Storage key for the installed upstream's IPC protocol version (shaped like 1.0.0)
    /// Kept apart from the program version because the two answer different questions ("should it be upgraded" versus
    /// "can the two sides talk")
    /// The upstream may also raise its version without touching the protocol, and folding both into one value would
    /// let the former drag the latter along
    /// </summary>
    public const string BridgeInstalledProtocolStorageKey = "miditap_bridge_installed_protocol";

    /// <summary>
    /// 已安装上游程序的文件名存储键（形如 midibridge-client.exe）
    /// 安装时落地的文件名是确定的，运行时就该照它启动，而不是再去目录里挑
    /// 目录里被放进别的 exe 时，这个记录让启动目标唯一
    ///
    /// Storage key for the installed upstream's file name (midibridge-client.exe for example)
    /// The file name landed at install time is a known fact, so starting the program uses it rather than choosing among
    /// whatever the directory holds
    /// When other exe files are dropped in, this record keeps the start target unique
    /// </summary>
    public const string BridgeExecutableNameStorageKey = "miditap_bridge_exe_name";

    private static string BridgeHostPath(string baseDir)
        => AppPaths.StorageFile(baseDir, BridgeHostStorageKey);

    private static string BridgePortPath(string baseDir)
        => AppPaths.StorageFile(baseDir, BridgePortStorageKey);

    private static string BridgeSourcePath(string baseDir)
        => AppPaths.StorageFile(baseDir, BridgeSourceStorageKey);

    private static string BridgeInstalledVersionPath(string baseDir)
        => AppPaths.StorageFile(baseDir, BridgeInstalledVersionStorageKey);

    private static string BridgeInstalledProtocolPath(string baseDir)
        => AppPaths.StorageFile(baseDir, BridgeInstalledProtocolStorageKey);

    private static string BridgeExecutableNamePath(string baseDir)
        => AppPaths.StorageFile(baseDir, BridgeExecutableNameStorageKey);

    private static string BridgeSelectedServerPath(string baseDir)
        => AppPaths.StorageFile(baseDir, BridgeSelectedServerStorageKey);

    /// <summary>
    /// 读取当前选中的服务器 id；未设置或读取失败返回空串
    ///
    /// Reads the id of the currently selected server; empty when unset or unreadable
    /// </summary>
    public static string GetBridgeSelectedServer(string baseDir)
    {
        try
        {
            return File.ReadAllText(BridgeSelectedServerPath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 保存当前选中的服务器 id（空串表示清除选中）
    ///
    /// Saves the id of the currently selected server (an empty string clears the selection)
    /// </summary>
    public static bool SaveBridgeSelectedServer(string baseDir, string? id)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(BridgeSelectedServerPath(baseDir), (id ?? string.Empty).Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 删除旧的单组键（host/port）
    /// 迁移成功后调用：列表文件已经接管这两项，留着旧键会让用户改了列表却被旧值带回去
    /// 密码文件不在本方法里删：它归 Bridge 层管（方向 Settings 不反向引用 Bridge），见 BridgeSecret.ClearLegacy
    ///
    /// Deletes the old single-set keys (host/port)
    /// Called after a successful migration: the list file now owns these two values, and keeping the old keys would
    /// let a stale value win over an edit made in the list
    /// The password file is not deleted here: the Bridge layer owns it (Settings never refers back to Bridge), see
    /// BridgeSecret.ClearLegacy
    /// </summary>
    public static void DeleteLegacyBridgeConnectionKeys(string baseDir)
    {
        DeleteQuietly(BridgeHostPath(baseDir));
        DeleteQuietly(BridgePortPath(baseDir));
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 单个文件删不掉不改变迁移结论 / One undeletable file does not change the migration's outcome
        }
    }

    /// <summary>
    /// 读取 MIDIBridge 上游服务器地址；未设置或读取失败返回空串（未配置）
    ///
    /// Reads the MIDIBridge upstream server address; empty when unset or unreadable (not configured)
    /// </summary>
    public static string GetBridgeHost(string baseDir)
    {
        try
        {
            return File.ReadAllText(BridgeHostPath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 保存 MIDIBridge 上游服务器地址（空串表示清除）
    /// 这里不校验语法：界面层先用 BridgeSettings.Validate 拦下非法值，通过后原样落盘
    /// 校验规则只在 BridgeSettings 一处，避免读写两侧各判一遍、判得不一样
    ///
    /// Saves the MIDIBridge upstream server address (an empty string clears it)
    /// No syntax check happens here: the UI rejects invalid values via BridgeSettings.Validate first
    /// The rule lives in BridgeSettings alone, so read and write cannot drift apart
    /// </summary>
    public static bool SaveBridgeHost(string baseDir, string? host)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(BridgeHostPath(baseDir), (host ?? string.Empty).Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取 MIDIBridge 上游服务器端口
    /// 未设置、读取失败或内容不是 1..65535 内的整数时返回 null（未配置）
    /// 这里即做范围判定而不留给界面：端口本来就是纯数字，两处判断只会徒增不一致的可能
    ///
    /// Reads the MIDIBridge upstream server port
    /// Null when unset, unreadable, or outside 1..65535 (not configured)
    /// The range check happens here rather than in the UI: a port is nothing but a number, and two checks would
    /// only add one more way to disagree
    /// </summary>
    public static int? GetBridgePort(string baseDir)
    {
        try
        {
            var text = File.ReadAllText(BridgePortPath(baseDir)).Trim();
            return int.TryParse(text, out var port) && port is >= 1 and <= 65535 ? port : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 保存 MIDIBridge 上游服务器端口；不在 1..65535 范围内时拒绝保存并返回 false
    ///
    /// Saves the MIDIBridge upstream server port; out-of-range values (outside 1..65535) are refused with false
    /// </summary>
    public static bool SaveBridgePort(string baseDir, int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(BridgePortPath(baseDir), port.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取当前输入源；未设置或读取失败返回空串（调用方按默认源处理）
    ///
    /// Reads the current input source; empty when unset or unreadable (the caller applies its default source)
    /// </summary>
    public static string GetBridgeSource(string baseDir)
    {
        try
        {
            return File.ReadAllText(BridgeSourcePath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 保存当前输入源（空串表示回到默认的"跟随启动状态"）
    /// 只做修剪，合法性（是不是 None/Physical/Bridge 之一）由调用方校验 ——
    /// 枚举定义在 App 层，Core 若在这里硬编码那份清单，就会出现两份必须同步的清单
    ///
    /// Saves the current input source (an empty string returns to the default start-up state)
    /// Only trimming happens here; the caller validates legality (one of None/Physical/Bridge) —
    /// the enum lives in the App layer, and hard-coding that list here would create a second list to keep in sync
    /// </summary>
    public static bool SaveBridgeSource(string baseDir, string? source)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(BridgeSourcePath(baseDir), (source ?? string.Empty).Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取已装上游版本号；未设置或读取失败返回空串
    /// 空串的含义是"没有记录"，而不是"版本是空" —— 调用方据此决定要不要重新探测
    ///
    /// Reads the recorded installed upstream version; empty when unset or unreadable
    /// An empty string means "no record" rather than "the version is empty", so the caller can decide whether to
    /// probe again
    /// </summary>
    public static string GetBridgeInstalledVersion(string baseDir)
    {
        try
        {
            return File.ReadAllText(BridgeInstalledVersionPath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 记录已装上游版本号（空串表示清除记录）
    /// 只做修剪，不校验形态：版本号由上游自己给出
    ///
    /// Records the installed upstream version (an empty string clears the record)
    /// Only trimming happens here and the shape is not validated: the upstream supplies its own version string
    /// </summary>
    public static bool SaveBridgeInstalledVersion(string baseDir, string? version)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(BridgeInstalledVersionPath(baseDir), (version ?? string.Empty).Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取已装上游的 IPC 协议版本；未设置或读取失败返回空串（无记录）
    ///
    /// Reads the recorded installed upstream IPC protocol version; empty when unset or unreadable (no record)
    /// </summary>
    public static string GetBridgeInstalledProtocol(string baseDir)
    {
        try
        {
            return File.ReadAllText(BridgeInstalledProtocolPath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 记录已装上游的 IPC 协议版本（空串表示清除记录）
    /// 同样是只修剪不校验：主版本判定归 Core 的 BridgeCompatibilityPolicy，
    /// 本层若也判一遍就会出现两处规则，迟早判得不一样
    ///
    /// Records the installed upstream IPC protocol version (an empty string clears the record)
    /// Again trimming only, with no validation: the major-version verdict belongs to Core's BridgeCompatibilityPolicy,
    /// and a second rule here would eventually disagree with it
    /// </summary>
    public static bool SaveBridgeInstalledProtocol(string baseDir, string? protocol)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(BridgeInstalledProtocolPath(baseDir), (protocol ?? string.Empty).Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取已装上游程序的文件名；未设置或读取失败返回空串（无记录）
    /// 空串表示"没有记录"，调用方回落到约定名与目录枚举
    ///
    /// Reads the recorded installed upstream file name; empty when unset or unreadable (no record)
    /// An empty string means "no record", and the caller falls back to the conventional name and a directory scan
    /// </summary>
    public static string GetBridgeExecutableName(string baseDir)
    {
        try
        {
            return File.ReadAllText(BridgeExecutableNamePath(baseDir)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 记录已装上游程序的文件名（空串表示清除记录）
    /// 只修剪不校验：文件名是否可用由 BridgeLayout 判断，本层不重复那一套规则
    ///
    /// Records the installed upstream file name (an empty string clears the record)
    /// Trimming only, with no validation: whether a name is usable belongs to BridgeLayout, and this layer does not
    /// repeat that rule
    /// </summary>
    public static bool SaveBridgeExecutableName(string baseDir, string? fileName)
    {
        try
        {
            EnsureStorageDir(baseDir);
            File.WriteAllText(BridgeExecutableNamePath(baseDir), (fileName ?? string.Empty).Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
