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
        // 恢复上次会话选择的远程来源：上次选了 Bridge 且扩展可用时自动接上
        // 不等待：接不上时 Bridge 自己会发出失败，界面不该为它推迟启动序列的其余部分
        //
        // Restores the remote source chosen in the last session: Bridge, when picked last time and the extension is
        // usable, is re-attached
        // It is not awaited: a failed re-attachment raises its own event, and the rest of the startup sequence must
        // not wait on it
        _ = AppServices.Bridge.RestoreAsync();
        // 启动时的静默更新检查：开发构建不做（见 AppServices.UpdatesEnabled），其余默认开启，可在设置里关闭
        // 开发构建仍留一条 debug 说明原因
        // 否则日志里少了那行"开始检查"，看起来会像是检查被静默跳过了
        //
        // The silent startup update check
        // Development builds skip it (see AppServices.UpdatesEnabled)
        // The rest default to on and can be turned off in Settings
        // A development build still logs why
        // A missing "check started" line would otherwise look like the check was silently dropped
        // 更新相关的启动动作全部推迟到 OnRootLoaded
        // 更新窗口要有 XamlRoot 才能显示，而构造阶段它还是 null
        // 更新失败的结果也一并等在那里，见 OnRootLoaded
        //
        // Every update-related startup action waits for OnRootLoaded
        // The update window needs a XamlRoot to be shown, and it is still null during construction
        // A failed update's outcome waits there too; see OnRootLoaded
        RootGrid.Loaded += OnRootLoaded;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // 只做一次：元素重新进入可视树时 Loaded 会再次触发
        //
        // Once only: Loaded fires again whenever the element re-enters the visual tree
        RootGrid.Loaded -= OnRootLoaded;
        ReportUpdateApplyFailure();
        StartUpdateFlow();
    }

    /// <summary>
    /// 启动时的更新动作：先看有没有上次下载好却没重启的包，没有才去问 GitHub
    ///
    /// 为什么先看本地：那份包已经校验通过、解压就绪，再问一次 GitHub 只能得到同一版本或更新的版本
    /// 而两条路都会弹一次窗口，用户在同一次启动里就会看到两个
    /// 因此有可恢复的更新时不再发起网络检查
    ///
    /// The startup update actions: an already-downloaded package is checked first, and GitHub is asked only without one
    ///
    /// Why the local check comes first: that package has already passed verification and been extracted,
    /// so asking GitHub again could only return the same version or a newer one
    /// Both routes raise a window, and the user would see two of them in a single launch
    /// With a restorable update present, no network check is started
    /// </summary>
    private void StartUpdateFlow()
    {
        var staged = UpdateService.TryRestoreStaged();
        if (staged is not null)
        {
            AppServices.Log.Info(AppServices.I18n.T("log.updateStaged", ("version", staged)));
            // 其余字段填当前已知值即可：包已在磁盘上，下载地址与发布说明那时都用不上了
            //
            // The remaining fields take the currently known values: the package is on disk,
            // so the download address and the release notes are of no further use
            _ = UpdateNotifier.ShowAsync(
                new Core.Update.UpdateInfo(staged, AppServices.AppVersion, Core.Update.UpdateChecker.ReleasesUrl));
            return;
        }

        // 走到这里说明没有可恢复的暂存更新：记一条，"上次下完了为什么这次没提示"就有据可查
        //
        // Reaching this point means there was no restorable staged update
        // One line makes "I downloaded it last time, why no prompt now" answerable
        AppServices.Log.Debug(AppServices.I18n.T("log.debug.updateNoStaged"));

        if (!AppServices.UpdatesEnabled)
        {
            AppServices.Log.Debug(
                AppServices.I18n.T("log.debug.updateSkipped", ("version", AppServices.AppVersion)));
            return;
        }

        if (!AppStorage.GetAutoCheckUpdates(AppServices.BaseDir))
        {
            return;
        }

        // 记一条"开始检查"的 debug：更新失败时最需要知道的是"到底有没有真的发出去请求"
        //
        // Logs a debug "check started"
        // When an update fails, the first thing worth knowing is whether a request was actually made
        AppServices.Log.Debug(
            AppServices.I18n.T("log.debug.updateCheck", ("version", AppServices.AppVersion)));
        _ = AppServices.Backend.CheckForUpdatesAsync(AppServices.AppVersion);
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
        // 先收掉上游进程：它由本进程拉起，本进程退出后没人再管它
        // 顺序放在 Backend.Stop 之前：Stop 会抬起并 drain 按键，那时上游已经不会再送帧
        //
        // The upstream process is stopped first: this process started it, and nothing would manage it afterwards
        // It comes before Backend.Stop, which releases and drains the keys, by which time no more frames can arrive
        AppServices.Bridge.Stop();
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
        {
            log.Debug(AppServices.I18n.T("log.debug.ports", ("count", ports.Count.ToString())));
            // 端口**名字**也记一条："明明插着琴却看不到设备"这类反馈，只报个数是查不出来的
            // 数量与名字分成两条：名字可能很长，混在一条里会让计数难以扫读
            //
            // The port NAMES go to a line of their own
            // The report that the piano is plugged in yet no device appears cannot be diagnosed from a count alone
            // Count and names are kept apart because names can be long, and mixing them makes the count hard to scan
            log.Debug(AppServices.I18n.T(
                "log.debug.portNames",
                ("names", string.Join(", ", ports.Select(port => port.Name)))));
        });
        backend.ConfigList += configs => AppServices.RunOnUi(() =>
            log.Debug(AppServices.I18n.T("log.debug.configs", ("count", configs.Count.ToString()))));
        // 状态重放走专用事件，因此不会进活动日志（见 BackendService 的说明）
        // 但"启动时到底恢复了什么"是排查的关键一环，这里补一条 debug
        //
        // State replay uses its own event and therefore does not reach the activity log (see BackendService)
        // What was restored at startup is a key part of diagnosing, so a debug line is added here
        backend.ConfigStateRestored += info => AppServices.RunOnUi(() =>
            log.Debug(AppServices.I18n.T(
                "log.debug.configRestored",
                ("name", info.Name),
                ("count", info.NoteCount.ToString()))));
        backend.HeldNotesRestored += held => AppServices.RunOnUi(() =>
            log.Debug(AppServices.I18n.T("log.debug.heldRestored", ("count", held.Count.ToString()))));
        backend.UpdateAvailable += info => AppServices.RunOnUi(() =>
            log.Debug(AppServices.I18n.T(
                "log.debug.updateFound", ("latest", info.Latest), ("current", info.Current))));
        // 更新可用时弹窗，弹一次即可
        // 订阅在窗口上而不是某个页面上：页面按导航重建，页面级订阅会随之消失并可能重复
        //
        // The update dialog is raised here, once
        // The subscription lives on the window rather than on a page: pages are rebuilt on navigation,
        // so a page-level subscription both disappears and duplicates
        backend.UpdateAvailable += info => AppServices.RunOnUi(() =>
        {
            _ = UpdateNotifier.ShowAsync(info);
        });

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

        WireBridgeEvents();
    }

    /// <summary>
    /// MIDIBridge 扩展的事件 -> 日志与状态药丸
    /// 与后端事件分开：扩展是可选的，它订阅的是另一个服务
    ///
    /// 高频事件不弹浮窗：状态行可能随重连反复出现，弹成浮窗会刷屏并遮住界面
    /// 需要用户动手的（未安装、协议不匹配）走 warn + 一条浮窗
    ///
    /// The MIDIBridge extension's events -> the log and the status pill
    /// Kept apart from the backend events: the extension is optional and subscribes to a different service
    ///
    /// High-frequency events raise no toast: a status line can repeat on every reconnect, and toasting each one would
    /// flood the screen
    /// What does need the user (not installed, protocol mismatch) goes to warn plus one toast
    /// </summary>
    private void WireBridgeEvents()
    {
        var bridge = AppServices.Bridge;
        var log = AppServices.Log;

        bridge.StatusReceived += status => AppServices.RunOnUi(() =>
        {
            log.Info(AppServices.I18n.T("log.bridge.status", ("status", DescribeBridgeStatus(status))));
            SetMonitorState();
        });        bridge.StateChanged += state => AppServices.RunOnUi(() =>
        {
            // Streaming 已经由 StatusReceived 覆盖（转成 info）；这里只记其余的状态迁移，
            // 否则同一次连接会在日志里留下两行说的是同一件事
            //
            // Streaming is already covered by StatusReceived (as an info line); only the other transitions are
            // recorded here, or one connection would leave two log lines saying the same thing
            if (state != BridgeSessionState.Streaming)
            {
                log.Debug(AppServices.I18n.T("log.debug.bridgeState", ("state", state.ToString())));
            }
            SetMonitorState();
        });
        bridge.Failed += (failure, detail) => AppServices.RunOnUi(() =>
        {
            var text = DescribeBridgeFailure(failure, detail);
            // 这些失败意味着扩展这一路走不通了，用户需要知道，因此是 warn 而不是 debug
            // 浮窗只给其中一条：设置页那张卡片会常驻显示同一个结论，浮窗只是把"刚发生"告诉用户
            //
            // These failures mean the extension route cannot work, so the user needs to know: warn, not debug
            // Only one of them raises a toast: the settings card shows the same conclusion as a standing line, and
            // the toast only says it just happened
            log.Warn(text);
            ToastService.Show(text, severity: Core.Notifications.ToastSeverity.Warning);
        });

        ReportBridgeCompatibilityOnStartup();
    }

    /// <summary>
    /// 启动时按已装版本判一次门禁：损坏与协议不支持记 warn，其余记 debug
    /// 损坏额外浮窗一条：用户不进设置页就看不到那个状态，而这时扩展整条路都是断的
    ///
    /// 判定要起上游子进程，因此不等待它：启动序列不该为一个可选扩展的探测阻塞
    /// 探测走 EnsureInstalledMetadataAsync，整个进程只探一轮，设置页随后读的就是这一次的结果
    ///
    /// Judges the gate once at startup from the installed version: damaged and unsupported protocols go to warn,
    /// everything else to debug
    /// A damaged build raises a toast on top of that, because the user would not see the state without opening the
    /// settings page, while the whole extension route is broken at that point
    ///
    /// The verdict spawns the upstream, so it is not awaited: the startup sequence must not block on an optional
    /// extension's probe
    /// The probe goes through EnsureInstalledMetadataAsync, so the process probes once and the settings page later reads
    /// that one result
    /// </summary>
    private void ReportBridgeCompatibilityOnStartup()
    {
        // 未安装时没有任何可判的东西，也不该为它起子进程
        //
        // With nothing installed there is nothing to judge, and spawning a subprocess for it is pointless
        if (!Core.Bridge.BridgeLayout.IsInstalled(AppServices.BaseDir))
        {
            return;
        }
        _ = ReportAsync();

        async Task ReportAsync()
        {
            var bridge = AppServices.Bridge;
            try
            {
                await bridge.EnsureInstalledMetadataAsync();
            }
            catch
            {
                return;
            }
            AppServices.RunOnUi(() =>
            {
                var reason = bridge.BridgeUnusableReason;
                if (reason == BridgeUnusableReason.Damaged)
                {
                    // warn + 浮窗：这个可执行文件跑不起来，用户不主动进设置页就无从得知
                    //
                    // warn plus a toast: the executable does not run, and a user who never opens the settings page
                    // would have no way of knowing
                    var damaged = AppServices.I18n.T("settings.bridge.damaged");
                    AppServices.Log.Warn(damaged);
                    ToastService.Show(damaged, severity: Core.Notifications.ToastSeverity.Warning);
                }
                else if (reason == BridgeUnusableReason.ProtocolUnsupported)
                {
                    // warn：这一版 MIDIBridge 装上了也用不了，用户需要重新安装，属于要留下痕迹的故障
                    //
                    // warn: this MIDIBridge build is installed but unusable and a reinstall is needed, which is a
                    // fault worth recording
                    AppServices.Log.Warn(AppServices.I18n.T(
                        "log.bridge.protocolUnsupported",
                        ("protocol", bridge.InstalledProtocol ?? string.Empty)));
                }
                else
                {
                    // debug：其余情形（可用、未配置、协议未知）都不是故障，记一条便于比对两次运行
                    //
                    // debug: every other outcome (usable, not configured, protocol unknown) is not a fault, and one
                    // line makes two runs comparable
                    AppServices.Log.Debug(AppServices.I18n.T(
                        "log.debug.bridgeCompatibility",
                        ("reason", reason.ToString()),
                        ("protocol", bridge.InstalledProtocol ?? string.Empty)));
                }
            });
        }
    }

    /// <summary>
    /// 上游状态行 -> 本地化文案。分类本身在 Core（BridgeStatusParser），这里只负责翻译
    /// 键按约定拼出（bridge.status.&lt;枚举名&gt;）
    ///
    /// An upstream status line -> localised copy
    /// The classification itself lives in Core (BridgeStatusParser) and this only translates
    /// The keys are composed by convention (bridge.status.&lt;enum name&gt;)
    /// </summary>
    private static string DescribeBridgeStatus(Core.Bridge.BridgeStatus status)
    {
        // 文案里带 {name}：接上"这台服务器叫什么"，多服务器时用户才能对上日志说的是哪一台
        //
        // The copy carries {name}: it states which server this line is about, which is what a user with several
        // servers needs to follow the log
        return AppServices.I18n.T("bridge.status." + status, ("name", AppServices.Bridge.CurrentServerLabel));
    }

    /// <summary>
    /// 分类后的会话失败 -> 用户能照着做的一句话
    /// 文案不说"失败了"，而是说下一步该做什么
    ///
    /// A categorised session failure -> one sentence the user can act on
    /// The copy states what to do next rather than that something failed
    /// </summary>
    private static string DescribeBridgeFailure(BridgeFailure failure, string? detail)
    {
        Func<string, string> t = AppServices.I18n.T;
        return failure switch
        {
            BridgeFailure.NotConfigured => t("bridge.failed.notConfigured"),
            BridgeFailure.NotInstalled => t("bridge.failed.notInstalled"),
            BridgeFailure.ProtocolMismatch =>
                AppServices.I18n.T("bridge.failed.protocol", ("detail", detail ?? string.Empty)),
            BridgeFailure.StartFailed => AppServices.I18n.T("bridge.failed.start", ("detail", detail ?? string.Empty)),
            _ => AppServices.I18n.T("bridge.failed.other", ("detail", detail ?? string.Empty)),
        };
    }

    /// <summary>
    /// 刷新标题栏状态药丸
    /// 界面没有启停按钮，用户无法从按钮推断当前状态
    /// 因此药丸直接反映后台真实状态：监听中 = 正在收 MIDI 事件
    /// 文案刻意用"监听中/未监听"而不是"运行中/已停止"
    /// 没有启停按钮，用户关心的是"我的琴键现在会不会被转换"，而不是进程是否存活
    ///
    /// 药丸还要区分输入来自哪一路：远程源在用时写"监听中（远程）"
    /// 只说"监听中"会让拨着琴却毫无反应的用户无从判断是不是选错了源
    /// 只有真正在收远程帧时才写"（远程）"：IPC 还连着但上游没数据时，那一路其实没有输入
    ///
    /// Refreshes the title-bar status pill
    /// The UI has no Start/Stop button, so the pill is the only place the real state is visible
    /// "Listening" means MIDI events are actually being consumed
    ///
    /// Once the extension is in play the pill also says which route the input comes from: with the remote source in
    /// use it reads "Listening (remote)"
    /// A plain "Listening" would leave a user playing a piano that does nothing with no way to tell whether the
    /// wrong source is selected
    /// The "(remote)" suffix appears only while remote frames are actually arriving: an IPC connection that is up
    /// but carries no data does not mean that route has input
    /// </summary>
    private void SetMonitorState()
    {
        var source = AppServices.Backend.ActiveSource;
        var streaming = AppServices.Bridge.IsStreaming;
        var running = source == MidiSource.Bridge
            ? streaming
            : AppServices.Backend.IsRunning;
        var remote = source == MidiSource.Bridge && streaming;
        var key = !running
            ? "status.idle"
            : remote
                ? "status.runningBridge"
                : "status.running";
        StatusText.Text = AppServices.I18n.T(key);
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
