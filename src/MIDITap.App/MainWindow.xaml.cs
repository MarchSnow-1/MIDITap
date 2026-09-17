// MainWindow.cs — WinUI 3 外壳：侧边栏导航、自绘标题栏（带运行状态胶囊）
// 这里还承担中央事件路由，把后端的广播转成活动日志条目
//
// MainWindow.cs — WinUI 3 shell: side-bar navigation and a custom title bar (with a running-state pill)
// It also carries the central event routing that turns backend broadcasts into activity-log entries


using MIDITap.App.Services;
using MIDITap.Core.Config;
using MIDITap.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MIDITap.App.Views;
using Windows.Graphics;

namespace MIDITap.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultClientWidth = 1000;
    private const int DefaultClientHeight = 700;
    private const int MinClientWidth = 900;
    private const int MinClientHeight = 700;

    private bool _suppressNavSelection;

    public MainWindow()
    {
        AppServices.Initialize(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        InitializeComponent();
        // 句柄尽早登记：日志导出要用它初始化文件选择器（未打包应用必须绑定窗口）
        //
        // Register the handle early: the log export needs it to initialize the file picker
        // An unpackaged app must bind the picker to a window
        AppServices.AttachWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));

        // 主题必须在窗口显示前定下来，否则会先闪一下深色再切到用户选择的浅色
        // Load 读存储；Attach 立刻把模式写到根元素（默认跟随系统）
        //
        // The theme is decided before the window is shown
        // Otherwise a light-mode user would see a dark flash first
        // Load reads the stored mode, and Attach applies it to the root element immediately
        // The default mode follows the system
        ThemeService.Load(AppServices.BaseDir);
        ThemeService.Changed += RefreshThemeDependentVisuals;
        ThemeService.Attach(RootGrid);
        // 日志落盘开关同样在窗口显示前读取：若用户已开启，启动阶段的日志也要记下来
        // （启动问题恰恰是最常需要回看的一段）
        //
        // The log-to-file toggle is read before the window shows for the same reason
        // If the user enabled it, the startup sequence must be captured too
        // Startup is exactly the part most often worth re-reading
        LogPersistence.Load(AppServices.BaseDir);

        Title = "MIDITap";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        SetupWindow();
        SetupNavigation();
        WireBackendEvents();
        ApplyTexts();
        AppServices.I18n.LanguageChanged += OnLanguageChanged;
        Closed += OnWindowClosed;

        RefreshTexts();
        StartThemeWatcher();
        // 启动序列：确保配置目录 -> v1 迁移 -> 枚举设备/配置 -> 恢复上次配置 -> 静默检查更新
        //
        // Startup sequence: ensure the config directory -> v1 migration -> enumerate ports/configs
        // Then restore the last config -> silent update check
        ConfigLocator.EnsureConfigDir(AppServices.BaseDir);
        // v1 把显示名写在文件的 name 字段里，v2 改从文件名取
        // 在这里迁一次，老用户升级后界面上的名字与磁盘上的文件名立刻对上
        // 迁移发生在 LoadConfig 之前，因此随后恢复的 last_config 路径已经是新名字
        //
        // A v1 config kept its display name in a name field, while v2 reads it from the file name
        // Migrating here means an upgraded user sees the two agree right away
        // It runs before LoadConfig, so the last_config path restored afterwards already carries the new name
        ConfigEditor.MigrateV1NameField(AppServices.BaseDir);
        AppServices.Backend.ListPorts();
        AppServices.Backend.ListConfigs();
        AppServices.Backend.LoadConfig(null);
        AppServices.Log.Info(AppServices.I18n.T("log.connected"));
        // 启动环境摘要走 **debug** 级
        // 它对排查"启动就出问题"这类反馈几乎是必需的第一手信息，但对日常使用是噪音（每次启动都占一行）
        // 因此记录、默认不显示、导出时默认带上
        //
        // 前缀**不走 i18n**，固定英文：这一行的读者是排查问题的人，不是使用者
        // 界面语言换成中文时它若跟着变，同一份日志会因语言而异，比对两次运行时还要先查翻译
        // 字段名同理，一律英文小写
        //
        // The startup environment summary goes to the **debug** level
        // It is close to essential first-hand information when triaging "it broke on startup" reports
        // Yet it is noise for everyday use (one line per launch)
        // So it is recorded, hidden by default, and included in exports
        //
        // The prefix deliberately does **not** go through i18n and stays English: the reader of this line is whoever triages a report, not the end user
        // Were it to follow the UI language, one log would read differently per language and comparing two runs would start with a lookup
        // The field names follow the same rule and are lower-case English throughout
        AppServices.Log.Debug("Environment: " + LogEnvironment.Summary());
        // 设备热插拔监视：插入设备自动选择并启动监听，拔出自动停止/热切换
        // 同时开启配置文件热更新（外部编辑 config/*.json 自动生效）
        //
        // Device hot-plug watch: plugging a device in selects it and starts monitoring
        // Unplugging stops it or switches to another device
        // Config hot reload is enabled at the same time
        // Editing config/*.json externally then takes effect automatically
        AppServices.Backend.StartDeviceWatcher();
        // 启动时的静默更新检查：开发构建不做（见 AppServices.UpdatesEnabled），其余默认开启，可在设置里关闭
        // 开发构建仍留一条 debug 说明原因
        // 否则日志里少了那行"开始检查"，看起来会像是检查被静默跳过了
        //
        // The silent startup update check
        // Development builds skip it (see AppServices.UpdatesEnabled)
        // The rest default to on and can be turned off in Settings
        // A development build still logs why
        // A missing "check started" line would otherwise look like the check was silently dropped
        if (!AppServices.UpdatesEnabled)
        {
            AppServices.Log.Debug(
                AppServices.I18n.T("log.debug.updateSkipped", ("version", AppServices.AppVersion)));
        }
        else if (AppStorage.GetAutoCheckUpdates(AppServices.BaseDir))
        {
            // 记一条"开始检查"的 debug：更新失败时最需要知道的是"到底有没有真的发出去请求"
            //
            // Logs a debug "check started"
            // When an update fails, the first thing worth knowing is whether a request was actually made
            AppServices.Log.Debug(
                AppServices.I18n.T("log.debug.updateCheck", ("version", AppServices.AppVersion)));
            _ = AppServices.Backend.CheckForUpdatesAsync(AppServices.AppVersion);
        }

        // 更新失败的结果由 apply-update.ps1 经命令行带回（见 Core 的 UpdateApplyReport）
        // 等根元素加载完再提示：构造阶段抛出的浮窗会在窗口显示前就走完自己的计时，用户看不到
        //
        // A failed update's outcome arrives on the command line (see Core's UpdateApplyReport)
        // The toast waits for the root element to load
        // One raised during construction would run out its timer before the window is on screen
        RootGrid.Loaded += OnRootLoaded;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // 只做一次：元素重新进入可视树时 Loaded 会再次触发
        //
        // Once only: Loaded fires again whenever the element re-enters the visual tree
        RootGrid.Loaded -= OnRootLoaded;
        ReportUpdateApplyFailure();
    }

    /// <summary>
    /// 本次启动若是"更新失败后的重启"，把结果告诉用户（见 Core.Update.UpdateApplyReport）
    ///
    /// 辅助脚本是独立进程，重启应用是它唯一能说明失败原因的机会
    /// 错过它，用户只会看到应用莫名其妙地重开、版本却没变
    /// 除了浮窗，也写一条 error 进日志
    /// 更新出问题时，事后回看日志是主要手段
    ///
    /// If this launch follows a failed update, tell the user (see Core.Update.UpdateApplyReport)
    ///
    /// The helper is a separate process, and relaunching the app is its only chance to explain the failure
    /// Miss it and the user just sees the app reopen with the same version
    /// Besides the toast, an error line goes to the log
    /// When an update goes wrong, reading the log afterwards is the main recourse
    /// </summary>
    private void ReportUpdateApplyFailure()
    {
        if (Core.Update.UpdateApplyReport.Parse(Environment.GetCommandLineArgs()) is not { } exitCode)
        {
            return;
        }

        var text = AppServices.I18n.T(
            Core.Update.UpdateApplyReport.MessageKey(exitCode), ("code", exitCode.ToString()));
        AppServices.Log.Error(text);
        ToastService.Show(
            AppServices.I18n.T("update.failed"),
            text,
            Core.Notifications.ToastSeverity.Error);
    }

    // ------------------------------------------------------------------ contrast theme

    // 系统主题变化监视（浅色/深色切换、对比度模式开关、强调色变化）
    // XAML 里的 {ThemeResource} 会自己跟着变，但代码绘制的音符盘不会，需要手动重绘
    //
    // Watches system theme changes (light/dark switch, contrast mode, accent colour)
    // XAML {ThemeResource} values follow automatically, but the code-drawn note board does not follow them
    // It is repainted manually
    private Windows.UI.ViewManagement.UISettings? _uiSettings;
    private Microsoft.UI.System.ThemeSettings? _themeSettings;

    private void StartThemeWatcher()
    {
        // 两个来源都要订阅，它们覆盖的情况不同
        //   UISettings.ColorValuesChanged —— 系统浅色/深色切换与强调色变化
        //   ThemeSettings.Changed         —— 对比度模式开关与方案切换
        // 回调只做同一件事：重绘代码绘制部分（幂等，重复触发无害）
        //
        // Both sources are needed because they cover different changes
        // UISettings covers the system light/dark switch and accent changes
        // ThemeSettings covers contrast mode and contrast-scheme changes
        // Both callbacks do the same idempotent repaint
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        }
        catch
        {
            _uiSettings = null;
        }

        try
        {
            _themeSettings = Microsoft.UI.System.ThemeSettings.CreateForWindowId(AppWindow.Id);
            _themeSettings.Changed += OnThemeSettingsChanged;
        }
        catch
        {
            _themeSettings = null;
        }
    }

    private void OnThemeSettingsChanged(Microsoft.UI.System.ThemeSettings sender, object args)
        => RefreshThemeDependentVisuals();

    private void OnColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args)
        => RefreshThemeDependentVisuals();

    /// <summary>
    /// 主题变化后刷新**代码绘制**的部分：XAML 里的 {ThemeResource} 由框架自动重解析
    /// 但音符盘的颜色由代码挑选、必须让它重挑一次
    /// 状态胶囊用纯 XAML 主题画笔 + 可见性切换，因此这里只需重绘音符盘
    /// SetMonitorState 仍调用一次，用于同步状态文字与圆点
    ///
    /// Refreshes code-drawn parts after a theme change
    /// XAML {ThemeResource} values re-resolve on their own
    /// The note board picks its colours in code and has to be told to pick again
    /// </summary>
    private void RefreshThemeDependentVisuals() => AppServices.RunOnUi(() =>
    {
        // 标题栏按钮由系统绘制，不跟随元素主题，主题变化后必须重新指定颜色
        //
        // The caption buttons are drawn by the OS and do not follow the element theme
        // Their colours are therefore re-specified after a theme change
        ApplyTitleBarButtonColors();
        SetMonitorState();
        // 主页可能当前未显示（页面按导航重建），取到才重绘
        //
        // The home page may not be showing (pages are rebuilt by navigation)
        // It is therefore repainted only when it is present
        (ContentFrame.Content as HomePage)?.ApplyNoteBoardTheme();
    });

    // 退出前抬起所有被注入的按键并关闭 MIDI 连接，否则按住的键会在系统层面卡住
    //
    // Release every injected key and close the MIDI connection before exiting
    // Otherwise held keys stay stuck at the OS level
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        AppServices.I18n.LanguageChanged -= OnLanguageChanged;
        ThemeService.Changed -= RefreshThemeDependentVisuals;
        if (_themeSettings is not null)
        {
            _themeSettings.Changed -= OnThemeSettingsChanged;
            _themeSettings = null;
        }
        if (_uiSettings is not null)
        {
            _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
            _uiSettings = null;
        }
        AppServices.Backend.Stop();
        // 最后再关日志：这样 Backend.Stop() 的日志（含 release-all-keys）也能落盘
        //
        // Close the log last so Backend.Stop() output (including release-all-keys) lands too
        AppServices.Log.Info(AppServices.I18n.T("log.shuttingDown"));
        LogPersistence.Shutdown();
    }

    // ------------------------------------------------------------------ window

    /// <summary>AppWindow 没有 Center()，按主显示器工作区手动居中 / AppWindow has no Center(), so it is centered manually on the primary display work area</summary>
    private void CenterOnWorkArea()
    {
        var workArea = DisplayArea.Primary.WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2)));
    }

    /// <summary>
    /// 让标题栏的「最小化/最大化/关闭」按钮跟随应用当前主题
    ///
    /// Keeps the caption buttons in step with the app's current theme
    /// </summary>
    private void ApplyTitleBarButtonColors()
    {
        var titleBar = AppWindow.TitleBar;
        if (AppServices.IsLightTheme)
        {
            // 深色图标，用于浅色标题栏 / Dark icons, for a light title bar
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
            titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x33, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x55, 0x00, 0x00, 0x00);
            // 非活动窗口用更淡的灰，与系统行为一致 / Inactive windows use a fainter grey, matching the OS behaviour
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
        }
        else
        {
            // 浅色图标，用于深色标题栏 / Light icons, for a dark title bar
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
        }
        titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
        titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
    }

    private void SetupWindow()
    {
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsMaximizable = false; // 不推荐放大使用 / Maximising the window is not recommended
        presenter.IsResizable = true;
        ApplyTitleBarButtonColors();

        AppWindow.ResizeClient(new SizeInt32(DefaultClientWidth, DefaultClientHeight));
        CenterOnWorkArea();
        AppWindow.SetIcon(Path.Combine(AppServices.BaseDir, "Assets", "appIcon.ico"));

        // 最小尺寸交给系统的 WM_GETMINMAXINFO（见 WindowMinSize）
        //
        // Minimum size is delegated to the OS via WM_GETMINMAXINFO (see WindowMinSize)
        WindowMinSize.Apply(
            WinRT.Interop.WindowNative.GetWindowHandle(this), MinClientWidth, MinClientHeight);
    }

    private void SetupNavigation()
    {
        ContentFrame.Navigated += (_, _) => UpdateNavSelection();
        NavView.Loaded += (_, _) =>
        {
            // 选中项变化会触发 OnNavSelectionChanged 完成首次导航，无需再显式 Navigate
            //
            // A selection change runs OnNavSelectionChanged, which performs the first navigation
            // No explicit Navigate call is then needed
            NavView.SelectedItem = NavHome;
        };
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavSelection || args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }
        var pageType = tag switch
        {
            "home" => typeof(HomePage),
            "mappings" => typeof(MappingsPage),
            "log" => typeof(LogPage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };
        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
        }
    }

    private void UpdateNavSelection()
    {
        var tag = ContentFrame.CurrentSourcePageType?.Name switch
        {
            nameof(HomePage) => "home",
            nameof(MappingsPage) => "mappings",
            nameof(LogPage) => "log",
            nameof(SettingsPage) => "settings",
            _ => null,
        };
        if (tag is null)
        {
            return;
        }
        NavigationViewItem? target = tag switch
        {
            "home" => NavHome,
            "mappings" => NavMappings,
            "log" => NavLog,
            "settings" => NavSettings,
            _ => null,
        };
        if (target is not null && !ReferenceEquals(NavView.SelectedItem, target))
        {
            _suppressNavSelection = true;
            NavView.SelectedItem = target;
            _suppressNavSelection = false;
        }
    }

    // ------------------------------------------------------------------ backend events -> log

    private void WireBackendEvents()
    {
        var backend = AppServices.Backend;
        var log = AppServices.Log;

        // 设备与配置的枚举结果走 debug：每一次热插拔都会重新枚举，日常使用下这是噪音
        // 但"明明插着琴却看不到设备"这类反馈恰恰要从这里下手
        // 记录与显示必须分开
        //
        // Port and config enumeration goes to debug
        // Every hot-plug re-enumerates, which is noise day to day
        // Yet "the piano is plugged in but no device appears" is exactly the report that starts with these lines
        // That is why recording and display are separate
        backend.PortsListed += ports => AppServices.RunOnUi(() =>
            log.Debug(AppServices.I18n.T("log.debug.ports", ("count", ports.Count.ToString()))));
        backend.ConfigList += configs => AppServices.RunOnUi(() =>
            log.Debug(AppServices.I18n.T("log.debug.configs", ("count", configs.Count.ToString()))));
        backend.UpdateAvailable += info => AppServices.RunOnUi(() =>
            log.Debug(AppServices.I18n.T(
                "log.debug.updateFound", ("latest", info.Latest), ("current", info.Current))));

        // 映射增删的日志在这里本地化，与上面各事件保持一致
        //
        // They are localized here in step with the events above
        backend.MappingAdded += (note, key) => AppServices.RunOnUi(() =>
            log.Info(AppServices.I18n.T("log.mappingAdded", ("note", note), ("key", key))));
        backend.MappingDeleted += note => AppServices.RunOnUi(() =>
            log.Info(AppServices.I18n.T("log.mappingDeleted", ("note", note))));
        backend.Error += message => AppServices.RunOnUi(() =>
        {
            var text = AppServices.I18n.T("log.error", ("message", message));
            log.Error(text);
            // 错误同时以浮窗提示：出错时用户多半不在看日志页，只写进日志等于没提示
            //
            // Errors also raise a toast
            // When something fails the user is usually not looking at the log page
            // Writing only to the log is effectively silent
            ToastService.Show(text, severity: Core.Notifications.ToastSeverity.Error);
        });
        backend.Started += (port, portName) => AppServices.RunOnUi(() =>
        {
            log.Info(AppServices.I18n.T("log.monitorStarted", ("port", port.ToString()), ("portName", portName)));
            SetMonitorState();
        });
        backend.Stopped += () => AppServices.RunOnUi(() =>
        {
            log.Info(AppServices.I18n.T("log.monitorStopped"));
            SetMonitorState();
        });
        backend.ConfigLoaded += info => AppServices.RunOnUi(() =>
            log.Info(AppServices.I18n.T("log.configLoaded", ("name", info.Name), ("count", info.NoteCount.ToString()))));
        backend.ConfigRenamed += (_, name) => AppServices.RunOnUi(() =>
            log.Info(AppServices.I18n.T("log.configRenamed", ("name", name))));
        backend.ConfigCreated += filename => AppServices.RunOnUi(() =>
            log.Info(AppServices.I18n.T("log.configCreated", ("name", filename))));
        backend.NoteOn += (note, velocity, key) => AppServices.RunOnUi(() =>
            log.Info(AppServices.I18n.T("log.noteOn",
                ("note", note.ToString()),
                ("velocity", velocity.ToString()),
                ("key", key ?? AppServices.I18n.T("log.unbound")))));
        backend.NoteOff += note => AppServices.RunOnUi(() =>
            log.Info(AppServices.I18n.T("log.noteOff", ("note", note.ToString()))));
        // 这两条**刻意只进日志、不弹浮窗**：它们在演奏过程中可能每秒触发多次（重复按下、异常抬起）
        // 若做成浮窗会立刻刷屏并遮住界面
        // 级别为 warn 但属于"演奏噪音"而非"需要用户处理的问题"
        //
        // These two deliberately only log
        // They can fire many times per second while playing (repeated note-on, unmatched note-off)
        // Toasting them would flood the screen
        backend.DuplicateOn += note => AppServices.RunOnUi(() =>
            log.Warn(AppServices.I18n.T("log.duplicateOn", ("note", note.ToString()))));
        backend.UnexpectedOff += note => AppServices.RunOnUi(() =>
            log.Warn(AppServices.I18n.T("log.unexpectedOff", ("note", note.ToString()))));
        backend.NoteCaptured += note => AppServices.RunOnUi(() =>
            log.Info(AppServices.I18n.T("log.noteCaptured", ("note", note.ToString()))));
    }

    /// <summary>
    /// 刷新标题栏状态药丸
    /// 没有启停按钮，用户无法从按钮推断当前状态
    /// 因此药丸直接反映后台真实状态：监听中 = 正在收 MIDI 事件
    /// 文案刻意用"监听中/未监听"而不是"运行中/已停止"
    /// 没有启停按钮之后，用户关心的是"我的琴键现在会不会被转换"，而不是进程是否存活
    ///
    /// Refreshes the title-bar status pill
    /// With the Start/Stop button gone the pill is the only place the real state is visible
    /// "Listening" means MIDI events are actually being consumed
    /// </summary>
    private void SetMonitorState()
    {
        var running = AppServices.Backend.IsRunning;
        StatusText.Text = AppServices.I18n.T(running ? "status.running" : "status.idle");
        // 只切换可见性，不赋值画刷：两个圆点各自带 {ThemeResource}，颜色由框架按当前主题解析（含对比度分支）
        // 代码一旦直接赋值画刷，就会绕过主题解析、在主题切换后变成陈旧颜色
        //
        // Only the visibility is toggled and no brush is assigned
        // Each dot carries its own {ThemeResource}
        // The framework resolves the colour from the current theme (the contrast branch included)
        // Assigning a brush from code would bypass theme resolution
        // It would also leave a stale colour after a theme switch

        StatusDotRunning.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StatusDotIdle.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ i18n

    /// <summary>
    /// 语言切换：刷新界面文案，并清掉已显示的浮窗 —— 它们带着旧语言的文字，留着会出现混排
    ///
    /// On a language change: refresh texts and drop visible toasts
    /// Those toasts still carry the previous language
    /// They would otherwise mix two languages on screen
    /// </summary>
    private void OnLanguageChanged()
    {
        ToastService.Clear();
        RefreshTexts();
    }

    private void RefreshTexts() => AppServices.RunOnUi(ApplyTexts);

    private void ApplyTexts()
    {
        Func<string, string> t = AppServices.I18n.T;
        NavHome.Content = t("nav.home");
        NavMappings.Content = t("nav.mappings");
        NavLog.Content = t("nav.log");
        NavSettings.Content = t("nav.settings");
        SetMonitorState();
    }
}
