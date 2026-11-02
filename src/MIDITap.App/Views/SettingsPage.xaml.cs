// SettingsPage.cs — 全应用唯一的偏好页：外观（主题 + 语言）、活动日志、更新、关于
//
// SettingsPage.cs — the application's only preferences page
// That covers appearance (theme + language), activity log, updates and about


using MIDITap.App.Dialogs;
using MIDITap.App.Services;
using MIDITap.Core.Settings;
using MIDITap.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _checking;
    // 程序化同步选择时抑制 SelectionChanged，避免把读取当成用户操作又写回存储
    // Suppresses SelectionChanged while the selection is synced programmatically
    // So reading the value back is not taken for a user action and written to storage
    private bool _suppressThemeSelection;
    // 初值为 true：构造期间控件初始化可能触发 Toggled，那时还没读到真实偏好
    // 若放行就会把"默认关闭"写进存储、覆盖掉用户原先的设置
    //
    // Starts true: control initialisation during construction can raise Toggled before the real preference has been read
    // Letting it through would persist "off" over the user's setting
    private bool _suppressLogToggle = true;
    // 同理：构造期间控件初始化会触发 Toggled/TextChanged，那时还没读到真实设置
    // Likewise: control initialisation during construction raises Toggled/TextChanged before the real settings have been read
    private bool _suppressUpdateControls = true;
    // 语言下拉框：程序化同步选中项时抑制 SelectionChanged
    // The language drop-down: suppresses SelectionChanged while the selected item is synced programmatically
    private bool _suppressLanguageSelection;

    /// <summary>主题下拉框的一项：已本地化的显示文案 + 存储值
    /// One theme drop-down item: the localised label plus the stored value</summary>
    private sealed record ThemeOption(string Label, string Mode);

    /// <summary>当前的主题项（每次语言切换都会重建，见 BuildThemeChoices）
    /// The current theme items; rebuilt on every language switch (see BuildThemeChoices)</summary>
    private List<ThemeOption> _themeChoices = [];

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AppServices.I18n.LanguageChanged += RefreshTexts;
        BuildLanguageChoices();
        RefreshTexts();
        SyncThemeSelection();
        SyncLanguageSelection();
        SyncLogToggle();
        SyncUpdateControls();
        // 构造完成：此后事件才是真实用户操作
        // Construction is complete: from here on the events are genuine user actions
        _suppressLogToggle = false;
        _suppressUpdateControls = false;
        _suppressBridgeControls = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshTexts();
        // 状态行要跟着会话变化走：停在设置页的用户看得见的唯一反馈就是这一行
        // 订阅放在页面生命周期里而不是构造里，页面按导航重建时不会留下重复订阅
        //
        // The status line follows the session: it is the only feedback a user who stays on the settings page sees
        // The subscription lives in the page's lifetime rather than in the constructor, so a page rebuilt by
        // navigation leaves no duplicate behind
        AppServices.Bridge.StateChanged += OnBridgeStateChanged;
        RefreshBridgeStateText();
    }

    private void OnBridgeStateChanged(BridgeSessionState state)
        => AppServices.RunOnUi(RefreshBridgeStateText);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.I18n.LanguageChanged -= RefreshTexts;
        AppServices.Bridge.StateChanged -= OnBridgeStateChanged;
    }

    // ------------------------------------------------------------------ theme

    /// <summary>
    /// 重建主题项
    /// **每次语言切换都要调用**，因为文案是本地化字符串、必须重新生成
    ///
    /// 为什么是"重建数据源"而不是"改现有条目的文案"
    /// WinUI 的 ComboBox 在选中时会把条目的内容拷进 SelectionBoxItem
    /// 之后改条目本身**不会刷新那个框**
    /// 换 ItemsSource 会整块重建，选中框随之刷新，随后按存储值恢复选中项
    ///
    /// 条目来自 Core 的 SupportedThemes，文案键按约定拼出（settings.theme.&lt;模式&gt;），因此不必维护一张配对表
    ///
    /// Rebuilds the theme items
    /// **This has to be called on every language switch**
    /// The labels are localised strings and must be regenerated
    ///
    /// Why rebuild the data source instead of relabelling the existing items
    /// A WinUI ComboBox copies the item's content into SelectionBoxItem when it is selected
    /// Editing the item itself afterwards does not refresh that box
    /// Replacing the ItemsSource rebuilds the whole set, which refreshes the selection box
    /// The selection is then restored from the stored value
    ///
    /// The items come from SupportedThemes in Core and the label keys are composed by convention (settings.theme.&lt;mode&gt;)
    /// The naming rule stands in for a pairing table, so none has to be maintained
    /// </summary>
    private void BuildThemeChoices()
    {
        Func<string, string> t = AppServices.I18n.T;
        _themeChoices = Core.Settings.AppStorage.SupportedThemes
            .Select(mode => new ThemeOption(t("settings.theme." + mode), mode))
            .ToList();

        _suppressThemeSelection = true;
        try
        {
            ThemeBox.ItemsSource = _themeChoices;
            ThemeBox.DisplayMemberPath = nameof(ThemeOption.Label);
        }
        finally
        {
            _suppressThemeSelection = false;
        }

        SyncThemeSelection();
    }

    /// <summary>
    /// 把选择器同步到当前模式
    /// 程序化赋值会触发 SelectionChanged，用标志抑制
    /// 按存储值查找，而不是把"第几项 = 哪个模式"写在这里，即使顺序变了也不会错位
    ///
    /// Syncs the selector to the current mode
    /// Programmatic assignment raises SelectionChanged, hence the suppression flag
    /// The lookup goes through the stored values rather than hardcoding "item N is mode M"
    /// So a reordering cannot silently mismatch the two
    /// </summary>
    private void SyncThemeSelection()
    {
        _suppressThemeSelection = true;
        try
        {
            var index = 0;
            for (var i = 0; i < _themeChoices.Count; i++)
            {
                if (_themeChoices[i].Mode == ThemeService.Mode)
                {
                    index = i;
                    break;
                }
            }
            ThemeBox.SelectedIndex = index;
        }
        finally
        {
            _suppressThemeSelection = false;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeSelection)
        {
            return;
        }
        if (ThemeBox.SelectedItem is ThemeOption choice)
        {
            ThemeService.SetMode(AppServices.BaseDir, choice.Mode);
        }
    }

    // ------------------------------------------------------------------ 语言 / Language

    /// <summary>
    /// 语言项按 i18n 目录动态生成，显示**母语名**
    /// 用户看不懂当前界面语言时，仍能在列表里认出自己的语言
    ///
    /// Items come from the i18n folder and show each language's NATIVE name
    /// So a user who cannot read the current UI language can still recognise their own
    /// </summary>
    private void BuildLanguageChoices()
    {
        LanguageBox.ItemsSource = AppServices.I18n.AvailableLanguages;
        LanguageBox.DisplayMemberPath = nameof(Core.Settings.LanguageInfo.NativeName);
    }

    private void SyncLanguageSelection()
    {
        _suppressLanguageSelection = true;
        try
        {
            var current = AppServices.I18n.Current;
            for (var i = 0; i < LanguageBox.Items.Count; i++)
            {
                if (LanguageBox.Items[i] is Core.Settings.LanguageInfo info
                    && string.Equals(info.Code, current, StringComparison.Ordinal))
                {
                    LanguageBox.SelectedIndex = i;
                    return;
                }
            }
            LanguageBox.SelectedIndex = -1;
        }
        finally
        {
            _suppressLanguageSelection = false;
        }
    }

    private void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageSelection)
        {
            return;
        }
        if (LanguageBox.SelectedItem is Core.Settings.LanguageInfo info)
        {
            // 切换会触发 LanguageChanged -> RefreshTexts，本方法不需要再刷新界面
            // The switch raises LanguageChanged -> RefreshTexts, so this method does not need to refresh the UI itself
            AppServices.I18n.SetLanguage(info.Code);
        }
    }

    // ------------------------------------------------------------------ 活动日志落盘 / Writing the activity log to disk

    /// <summary>把开关同步到实际状态；程序化赋值会触发 Toggled，用标志抑制</summary>
    private void SyncLogToggle()
    {
        var previous = _suppressLogToggle;
        _suppressLogToggle = true;
        try
        {
            LogFileToggle.IsOn = LogPersistence.Enabled;
        }
        finally
        {
            _suppressLogToggle = previous;
        }
        UpdateLogDirButton();
    }

    /// <summary>日志目录按钮仅在开启时可用，因为关闭状态下点它只会打开一个空目录</summary>
    private void UpdateLogDirButton() => OpenLogDirBtn.IsEnabled = LogPersistence.Enabled;

    private void OnLogFileToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressLogToggle)
        {
            return;
        }

        var enabled = LogFileToggle.IsOn;
        if (!LogPersistence.SetEnabled(AppServices.BaseDir, enabled))
        {
            // 偏好写盘失败（只读介质等）：本次运行内开关仍然生效，但要如实告知，否则用户会以为"重启后依然开着"
            // Writing the preference to disk failed (read-only media, for example)
            // The toggle still applies for this run, but the user has to be told
            // Otherwise they would assume it is still on after a restart
            ToastService.Show(
                AppServices.I18n.T("settings.logToFile.saveFailed"),
                severity: Core.Notifications.ToastSeverity.Warning);
        }
        UpdateLogDirButton();
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e) => LogPersistence.OpenLogFolder(AppServices.BaseDir);

    /// <summary>
    /// 打开配置目录。后端会先确保目录存在，因此全新安装、一个配置都没有时点它也有效果
    ///
    /// Opens the config directory
    /// The back end ensures the directory exists first, so the button also works on a fresh install with no configs yet
    /// </summary>
    private void OnOpenConfigDir(object sender, RoutedEventArgs e) => AppServices.Backend.OpenConfigDir();

    // ------------------------------------------------------------------ 更新与代理 / Updates and proxy

    /// <summary>把开关与代理框同步到存储值。程序化赋值会触发事件，用标志抑制</summary>
    private void SyncUpdateControls()
    {
        var previous = _suppressUpdateControls;
        _suppressUpdateControls = true;
        try
        {
            AutoCheckToggle.IsOn = AppStorage.GetAutoCheckUpdates(AppServices.BaseDir);
            // 开关同理：开发构建里它没有可控制的对象
            // 三处（开关、代理框、检查更新按钮）行为一致，不必让使用者记住哪一个才会弹
            //
            // The switch likewise: in a development build it has nothing to control
            // All three controls (the switch, the proxy box and the check-for-updates button) then behave the same, so no one has to remember which one shows a hint
            AutoCheckToggle.IsEnabled = AppServices.UpdatesEnabled;
            UpdatesCard.Hint = AppServices.UpdatesEnabled
                ? string.Empty
                : AppServices.I18n.T("settings.devTooltip");
            // 代理也一并禁用
            // 更新整条链路不成立时还留一个能改的输入框，只会让人以为它仍有作用
            //
            // The proxy is disabled along with the rest
            // Leaving an editable box while none of that happens would suggest it still does something
            ProxyBox.IsEnabled = AppServices.UpdatesEnabled;
            ProxyBox.Text = AppStorage.GetUpdateProxy(AppServices.BaseDir);
            // 禁用而不给原因，看起来像界面坏了：悬浮时补一句说明
            // 提示挂**卡片**而不是输入框本身：禁用的控件不接收指针事件，挂上去不会弹出（见 SettingsCard.Hint）
            // 发布构建里更新功能可用，传空串即清除
            //
            // Disabled without a reason reads as a broken control, so the explanation goes on hover
            // The hint hangs on the **card** rather than on the box itself: a disabled control receives no pointer events, so a tooltip set on it would not open (see SettingsCard.Hint)
            // In a release build the feature works, and an empty string clears it
            ProxyCard.Hint = AppServices.UpdatesEnabled
                ? string.Empty
                : AppServices.I18n.T("settings.devTooltip");
        }
        finally
        {
            _suppressUpdateControls = previous;
        }
    }

    private void OnAutoCheckToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUpdateControls)
        {
            return;
        }
        AppStorage.SaveAutoCheckUpdates(AppServices.BaseDir, AutoCheckToggle.IsOn);
    }

    private void OnProxyChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUpdateControls)
        {
            return;
        }
        AppStorage.SaveUpdateProxy(AppServices.BaseDir, ProxyBox.Text);
        UpdateProxyHint();
    }

    /// <summary>
    /// 地址非法时给出即时反馈
    /// 静默接受一个写错的地址，会让"检查更新"莫名其妙地失败，而用户无法从界面看出原因
    ///
    /// Warns as soon as the address is invalid
    /// Silently accepting a typo would make update checks fail inexplicably, with nothing in the UI to explain why
    /// </summary>
    private void UpdateProxyHint()
    {
        Func<string, string> t = AppServices.I18n.T;
        var text = ProxyBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            ProxyCard.Description = t("settings.proxy.hint");
            return;
        }

        // 说明仍写回卡片那一行：无效时在句尾追加提示
        //
        // The description still goes to the card's own line
        // The invalid marker is appended when the address does not parse
        var usable = new Core.Update.UpdateOptions(text).HasUsableProxy;
        ProxyCard.Description = usable
            ? t("settings.proxy.hint")
            : t("settings.proxy.hint") + "  (" + t("settings.proxy.invalid") + ")";
    }

    // 指向仓库主页（而非 Releases 列表）：按钮名是"GitHub 仓库"
    //
    // Points at the repository home rather than the releases list, matching the button's label
    private void OnGitHub(object sender, RoutedEventArgs e) =>
        AppServices.Backend.OpenUrl(Core.Update.UpdateRepository.RepositoryUrl);

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (_checking)
        {
            return;
        }
        _checking = true;
        CheckUpdateBtn.IsEnabled = false;
        // 过程与结果都以右上角浮窗呈现：检查中的提示会因后续结果到来而被替换
        // 不必留在页面上占位（用户点完按钮就把注意力转移了）
        //
        // Both the progress and the result are shown as toasts in the top-right corner
        // The "checking" notice is replaced once the result arrives
        // So it does not need to hold a place on the page (the user looks away as soon as the button is clicked)
        ToastService.Show(AppServices.I18n.T("settings.checking"));

        var available = false;
        var failure = UpdateFailure.None;
        var httpStatus = 0;
        var detail = (string?)null;
        RateLimitInfo? rateLimit = null;
        try
        {
            // 手动检查同样按安装方式挑资产，否则"检查到有更新"和"能装的那个包"会对不上
            //
            // A manual check picks the asset for this install kind as well, or "an update was found" and "the package
            // that can be installed" would disagree
            var outcome = await Core.Update.UpdateChecker.CheckWithReasonAsync(
                AppServices.AppVersion,
                AppServices.Backend.UpdateOptions,
                kind: Core.Update.InstallKindDetector.PackageKindFor(AppServices.InstallKind));
            if (outcome.Ok && outcome.Info is not null)
            {
                available = true;
                ToastService.Clear();
                // 手动检查的结果同样走更新弹窗，只是不带「不再提醒更新」
                // 手动检查本就是用户主动要看结果，再问他要不要"别再提醒"是答非所问
                //
                // A manual check opens the same dialog, minus the "do not remind me again" link
                // The user asked for this result, so asking whether to stop telling them misses the point
                _ = UpdateNotifier.ShowAsync(outcome.Info, manual: true);
            }
            else if (!outcome.Ok)
            {
                failure = outcome.Failure;
                httpStatus = outcome.HttpStatus;
                detail = outcome.Detail;
                rateLimit = outcome.RateLimit;
            }
        }
        finally
        {
            if (!available)
            {
                // 先清掉"正在检查"，再给出明确结论，避免两条浮窗同时挂着
                // Clears the "checking" toast first, then gives a definite result, so two toasts are not up at the same time
                ToastService.Clear();
                if (failure != UpdateFailure.None)
                {
                    // 给出**可操作**的原因，而不是"失败（已静默处理）"，后者让用户无从下手
                    // 尤其是网络失败：绝大多数情况是网络需要代理，那就直接把这个可能说出来
                    //
                    // An ACTIONABLE reason rather than "failed (kept silent)", which gave the user no action to take
                    // For network failures in particular, the overwhelmingly common cause is that the network requires a proxy
                    // So that possibility is stated outright
                    ToastService.Show(
                        DescribeUpdateFailure(failure, httpStatus, detail, rateLimit),
                        severity: Core.Notifications.ToastSeverity.Warning);
                }
                else
                {
                    ToastService.Show(
                        AppServices.I18n.T("settings.upToDate"),
                        severity: Core.Notifications.ToastSeverity.Success);
                }
            }
            _checking = false;
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 把归类后的失败原因翻译成"用户接下来该做什么"
    /// 刻意区分"已配置代理"与"未配置代理"：前者要用户去检查代理，后者要提示他可能需要填一个
    ///
    /// Turns a categorised failure into "what to do next"
    /// Deliberately distinguishes a configured proxy from none
    /// The former means "check your proxy", the latter means "you may need one"
    /// </summary>
    private string DescribeUpdateFailure(
        UpdateFailure failure, int httpStatus, string? detail, RateLimitInfo? rateLimit)
    {
        Func<string, string> t = AppServices.I18n.T;
        var proxy = AppStorage.GetUpdateProxy(AppServices.BaseDir);
        var hasProxy = new UpdateOptions(proxy).HasUsableProxy;

        return failure switch
        {
            UpdateFailure.Network => hasProxy
                ? AppServices.I18n.T("update.failed.networkProxy", ("proxy", proxy))
                : t("update.failed.networkNoProxy"),
            // 403 有重置时间时给出具体时间；没有时退回通用说明
            // 文案刻意简短：浮窗宽度有限，长句会被截断成半句话
            //
            // A 403 with a reset time names the time; otherwise it falls back to a general note
            // Kept short on purpose: the toast is narrow and a long sentence is cut off mid-clause
            UpdateFailure.Http when httpStatus == 403 && rateLimit?.Reset is not null =>
                AppServices.I18n.T("update.failed.rateLimited",
                    ("time", rateLimit.Reset.Value.ToString("HH:mm"))),
            UpdateFailure.Http => AppServices.I18n.T(
                "update.failed.http", ("status", httpStatus.ToString())),
            UpdateFailure.Parse => t("update.failed.parse"),
            _ => AppServices.I18n.T("update.failed.other", ("detail", detail ?? string.Empty)),
        };
    }

    // ------------------------------------------------------------------ MIDIBridge / Remote playing

    // 程序化回填 host/port/密码框时抑制 TextChanged/PasswordChanged
    // 与 _suppressUpdateControls 同理：构造期间控件初始化会触发事件，那时还没读到真实设置，
    // 放行就会把空值写回存储、覆盖掉用户原来的配置
    //
    // Suppresses TextChanged/PasswordChanged while the host, port and password boxes are filled in
    // The reasoning matches _suppressUpdateControls: control initialisation during construction raises the events
    // before the real settings have been read, and letting them through would write empty values over the user's
    // existing config
    private bool _suppressBridgeControls = true;

    // 安装/更新/检查进行中：避免连点同时跑两次
    // An install, update or check is running: a second click would run a second one concurrently
    private bool _bridgeBusy;

    // 设置块是否展开
    // 默认收起：这几项填一次就不再动，占着版面会把状态卡挤在后面
    // 地址还没填时由 SyncBridgeControls 自动展开，状态行正好说着"请在下方填写"
    //
    // Whether the settings block is expanded
    // Collapsed by default: these values are filled in once and then left alone, and keeping them on screen pushes
    // the status card down
    // SyncBridgeControls expands it automatically while the address is still missing, which is the case the status
    // line already describes as "fill it in below"
    private bool _bridgeConfigExpanded;

    /// <summary>
    /// 把扩展卡片同步到磁盘上的真实状态
    /// 未安装：配置块收起，状态卡指向主页的安装入口；已安装：显示 host/port/密码与状态行
    ///
    /// 为什么整块切换而不是逐控件禁用：未安装时 host/port/密码都没有意义，
    /// 显示一排填不了的输入框只会让人以为功能坏了
    ///
    /// Syncs the extension cards to the real state on disk
    /// Not installed: the config block collapses and the status card points at the install entry on the home page;
    /// installed: host/port/password and the status line are shown
    ///
    /// Why the whole block is switched rather than individual controls disabled: without the extension the host,
    /// port and password mean nothing, and a row of unusable inputs reads as a broken feature
    /// </summary>
    private void SyncBridgeControls()
    {
        var previous = _suppressBridgeControls;
        _suppressBridgeControls = true;
        try
        {
            var baseDir = AppServices.BaseDir;
            var installed = Core.Bridge.BridgeLayout.IsInstalled(baseDir);
            // 地址、端口或密码还没填时自动展开：状态行这时正说着"请在下方填写",
            // 收着等于让用户去填一个看不见的输入框
            // 损坏时同样展开：重新安装那一行在下拉区域里，收着就等于把唯一的出路藏起来
            //
            // Expands on its own while the address, port or password is missing: the status line says "fill it in
            // below" in that case, and a collapsed block would ask the user to fill in inputs they cannot see
            // It expands for a damaged build as well: the reinstall row lives inside the drop-down, and a collapsed
            // block would hide the only way out
            if (installed
                && AppServices.Bridge.BridgeUnusableReason is BridgeUnusableReason.NotConfigured
                    or BridgeUnusableReason.Damaged)
            {
                _bridgeConfigExpanded = true;
            }
            SyncBridgeConfigPanel();
            // 状态卡无论装没装都显示：说明行与右侧按钮由 SyncBridgeStatus 按当前状态填写
            //
            // The status card is shown installed or not: SyncBridgeStatus fills its description line and the
            // right-hand button from the current state
            SyncBridgeStatus();
            if (!installed)
            {
                // 未安装时把两张常驻提示一并收起：没有扩展可谈协议，也没有密码条目可谈 ACL
                //
                // Both standing notices are collapsed along with the rest: with no extension there is no protocol to
                // talk about and no password entry for the ACL notice to speak of
                BridgeUnavailableCard.Visibility = Visibility.Collapsed;
                BridgeAclCard.Visibility = Visibility.Collapsed;
                return;
            }

            BridgeHostBox.Text = Core.Bridge.BridgeServerStore.ResolveCurrent(baseDir).Server?.Host ?? string.Empty;
            BridgePortBox.Text = Core.Bridge.BridgeServerStore.ResolveCurrent(baseDir).Server?.Port.ToString() ?? string.Empty;
            var selectionNow = Core.Bridge.BridgeServerStore.ResolveCurrent(baseDir);
            BridgePasswordBox.Password = Core.Bridge.BridgeServerStore.ReadPassword(baseDir, selectionNow) ?? string.Empty;
            SyncBridgeServerControls();
            _ = RefreshBridgeMetadataProbeAsync();
            SyncBridgeAclNotice();
            // 协议门禁按当前缓存先判一次（探测完成后再判一次，见 RefreshBridgeMetadataProbeAsync）
            // 缓存为空（页面刚建、还没探过）时按"未探测"处理：门禁放行，等探测结果回来再收口
            //
            // The protocol gate is judged once against the cached value first (and once more after the probe, see
            // RefreshBridgeMetadataProbeAsync)
            // An empty cache (the page was just built and nothing has been probed yet) reads as "not probed": the gate
            // stays open until the probe comes back
            SyncBridgeUnavailableNotice();
        }
        finally
        {
            _suppressBridgeControls = previous;
        }
    }

    /// <summary>
    /// 展开或收起设置块
    /// 箭头按钮是唯一入口：按钮自带键盘焦点与空格/回车，整行不再挂点击路径
    ///
    /// Expands or collapses the settings block
    /// The chevron button is the only entry: a button already handles focus and Space/Enter, and the row has no tap
    /// handler of its own
    /// </summary>
    private void OnToggleBridgeConfig(object sender, RoutedEventArgs e)
    {
        _bridgeConfigExpanded = !_bridgeConfigExpanded;
        SyncBridgeConfigPanel();
    }

    /// <summary>
    /// 按"已安装 + 是否展开"写入展开块的显隐、箭头方向与接缝
    /// 两个写入点（这里与 SyncBridgeControls）都只是改 _bridgeConfigExpanded 再调本方法，
    /// 箭头与面板因此不会出现一个展开、一个收起的错位
    /// 展开时两块合成一整张卡：去掉间距（XAML 里那个 Spacing=0 的容器负责）、
    /// 接缝处只留状态卡的下边框、圆角上下各一半；收起时恢复成两张完整圆角的卡片
    ///
    /// Writes the expanded block's visibility, the chevron's direction and the seam from "installed" plus "expanded"
    /// Both writers (this and SyncBridgeControls) only change _bridgeConfigExpanded and then call this method, so the
    /// chevron and the panel cannot end up disagreeing
    /// Expanded, the two halves read as one card: no gap (the Spacing=0 container in XAML sees to that), a single
    /// border line at the seam, and corner radii halved across it; collapsed, both are ordinary rounded cards again
    /// </summary>
    private void SyncBridgeConfigPanel()
    {
        var installed = Core.Bridge.BridgeLayout.IsInstalled(AppServices.BaseDir);
        var joined = installed && _bridgeConfigExpanded;
        BridgeConfigPanel.Visibility = joined ? Visibility.Visible : Visibility.Collapsed;
        BridgeConfigChevron.Glyph = _bridgeConfigExpanded ? "\uE70E" : "\uE70D";
        // 上半张：展开时下方两角削平，收起时四角都是 8
        //
        // The upper half: the bottom two corners are squared once expanded, and all four are 8 when collapsed
        BridgeStatusCard.CardCornerRadius = joined ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(8);
        // 下半张的上边框去掉：接缝处那条线由状态卡的下边框充当，两条叠在一起会画出双线
        //
        // The lower half drops its top border: the line at the seam is the status card's bottom border, and two of
        // them would draw as a double rule
        BridgeConfigPanel.BorderThickness = joined ? new Thickness(1, 0, 1, 1) : new Thickness(1);
    }

    /// <summary>
    /// 把状态卡的说明行与右侧按钮同步到当前安装状态
    /// 未安装：说明行告诉用户入口在主页的设备列表，按钮禁用——没有已装版本就谈不上检查更新
    /// 已安装：说明行报出版本与协议号，读不出来的那项写“未知”；地址还没填时追加一句，
    /// 这是"装了却连不上"最常见的原因
    ///
    /// 为什么按钮的可用性也归这里：安装状态、协议门禁、忙碌标记三个条件合在一起才决定它，
    /// 拆到多处各判一半，迟早会出现两个写入点打架
    ///
    /// Syncs the status card's description line and its right-hand button to the current install state
    /// Not installed: the description points at the device list on the home page, and the button is disabled, since
    /// a check needs an installed build to compare against
    /// Installed: the description reports the version and the protocol number, writing "unknown" for whichever one
    /// cannot be read, with one more sentence appended while the address is missing, which is the most common reason
    /// for "installed but cannot connect"
    ///
    /// Why the button's enabled state lives here too: the install state, the protocol gate and the busy flag decide
    /// it together, and splitting them across writers would let two of them fight sooner or later
    /// </summary>
    private void SyncBridgeStatus()
    {
        var reason = AppServices.Bridge.BridgeUnusableReason;
        var installed = reason != BridgeUnusableReason.NotInstalled;
        // 未安装时状态卡右侧摆"安装"，装好后收起它、只留展开箭头
        // 两个都常驻会让用户以为装好之后还能再装一次
        //
        // While nothing is installed the status card's right slot shows "install"; once the component is in place it
        // is hidden and only the chevron remains
        // Keeping both visible would suggest the component could be installed again after it already is
        BridgeInstallBtn.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        // 读不出来的那项填“未知”：状态行固定报两件事，缺一项时留空会让用户以为是界面出错
        // 版本号的判断走 UpdateChannel.IsUnknown —— 探测失败时它是 unknown 常量而不是空串
        //
        // A value that cannot be read becomes "unknown": the status line always reports both facts, and leaving one
        // blank would look like a UI defect to the user
        // The version check goes through UpdateChannel.IsUnknown, because a failed probe leaves the unknown constant
        // rather than an empty string
        var version = AppServices.Bridge.InstalledVersion;
        if (Core.Update.UpdateChannel.IsUnknown(version))
        {
            version = AppServices.I18n.T("settings.bridge.status.unknown");
        }
        var protocol = AppServices.Bridge.InstalledProtocol;
        if (string.IsNullOrEmpty(protocol))
        {
            protocol = AppServices.I18n.T("settings.bridge.status.unknown");
        }
        BridgeStatusCard.Description = !installed
            ? AppServices.I18n.T("settings.bridge.status.notInstalled")
            : reason == BridgeUnusableReason.NotConfigured
                ? AppServices.I18n.T("settings.bridge.status.installedNeedsConfig", ("version", version), ("protocol", protocol))
                : AppServices.I18n.T("settings.bridge.status.installed", ("version", version), ("protocol", protocol));
        // 两个按钮同源：装没装、门禁放不放行、有没有操作在跑，这三件事决定了它们的可用性
        // 只用一处写入，页面上任何一次刷新都不会因为调用顺序不同而留下一个残留的禁用态
        // 协议不被支持或可执行文件已损坏时禁用"检查更新"：那次检查只会在门禁处被挡回来
        // 同样禁用"重新连接"：这两种状态下连接得不到有意义的结果，点下去只会失败
        //
        // Both buttons share one source: whether the extension is installed, whether the gate lets it through and
        // whether an operation is already running decide their enabled state
        // Writing it in one place keeps any refresh from leaving a stale disabled state behind, whatever the call order
        // An unsupported protocol or a damaged build disables the update check, which would only be turned away at the
        // gate
        // Reconnect is disabled the same way: connecting means nothing in either state and would only fail after a click
        var blocked = reason is BridgeUnusableReason.ProtocolUnsupported or BridgeUnusableReason.Damaged;
        BridgeCheckUpdateBtn.IsEnabled = installed && !blocked && !_bridgeBusy;
        BridgeReconnectBtn.IsEnabled = installed && !blocked && !_bridgeBusy;
    }

    /// <summary>
    /// 确保已装版本与协议**已经被探过**，只在还没探过时起那两个子进程
    /// 启动流程会先调一次，因此进设置页时通常直接读缓存：切换页面不会再为同一件事卡一下
    /// 探测完成后重填状态行并重新过一遍协议门禁
    /// 失败时保留原值，卡片照常可用：这两项只是参考，读不出来不该挡住设置
    ///
    /// 为什么探测完要再判一次门禁：门禁读的是缓存的协议号，而首次打开设置页时缓存还是空的
    /// 不重判的话，一个协议不兼容的安装要等到下次进页面才会被标出来
    ///
    /// Makes sure the installed version and protocol have been probed, spawning those two subprocesses only while no
    /// round has run yet
    /// The startup sequence calls it first, so opening the settings page normally reads the cache and a page switch no
    /// longer stalls on the same work
    /// The status line and the protocol gate are refreshed once the probe has finished
    /// A failure keeps the previous values and the card stays usable: both are for reference, and an unreadable one
    /// must not get in the way of the settings
    ///
    /// Why the gate runs again after the probe: it reads the cached protocol, which is still empty when the settings
    /// page is opened for the first time
    /// Without this second pass an incompatible install would only be flagged on the next visit
    /// </summary>
    private async Task RefreshBridgeMetadataProbeAsync()
    {
        try
        {
            await AppServices.Bridge.EnsureInstalledMetadataAsync();
        }
        catch
        {
            return;
        }
        AppServices.RunOnUi(() =>
        {
            SyncBridgeStatus();
            SyncBridgeUnavailableNotice();
        });
    }

    /// <summary>
    /// 协议门禁：本地已装协议不被本 Tap 支持时，挂出醒目提示、提供重新安装入口，并禁用 MIDIBridge 的其余操作
    ///
    /// 为什么禁用而不是只提示：协议主版本不同意味着握手帧或帧头可能已经变了
    /// 这时"连接"与"检查更新"都得不到有意义的结果 —— 连上也消费不了一帧
    /// 让用户点下去再看到失败，比直接把路堵上更浪费时间
    ///
    /// 为什么仍保留重新安装入口：升级改变不了本地那个不受支持的协议，
    /// 换一个兼容版本装上才是能解决问题的那一步
    ///
    /// The protocol gate: when the installed protocol is unsupported by this Tap, a prominent notice appears with a
    /// reinstall entry, and the remaining MIDIBridge actions are disabled
    ///
    /// Why disable rather than merely inform: a different protocol major means the handshake or frame header may have
    /// changed, so neither "connect" nor "check for updates" yields a meaningful result — a connection would be
    /// established yet consume no frame
    /// Letting the user click through to a failure wastes more time than closing the path
    ///
    /// Why the reinstall entry stays: an upgrade cannot change the unsupported local protocol, and installing a
    /// compatible build is the step that actually resolves it
    /// </summary>
    private void SyncBridgeUnavailableNotice()
    {
        Func<string, string> t = AppServices.I18n.T;
        var reason = AppServices.Bridge.BridgeUnusableReason;
        // 两种"装了但用不了"各有各的位置：
        //   协议不支持 —— 连体卡片下方独立一张提示卡，那是"装错了版本"的结论，需要当场看见
        //   可执行文件损坏 —— 下拉区域里「更新」下面的一行，它说的是"现在装的这个能不能用"，属于这条设置
        // 未安装与配置不全各有自己的既有呈现（状态卡的状态行、输入框说明），不走这里
        //
        // The two "installed but unusable" states each have their own place:
        //   Unsupported protocol — a separate notice card below the joined block, a conclusion the user has to see
        //   Damaged executable — a row below the update row inside the drop-down, because it reports whether the
        //   installed build works at all, which is part of these settings
        // Not-installed and not-configured already have their own presentation (the status line, the input
        // descriptions) and never reach this method
        if (reason is not (BridgeUnusableReason.ProtocolUnsupported or BridgeUnusableReason.Damaged))
        {
            BridgeUnavailableCard.Visibility = Visibility.Collapsed;
            BridgeDamagedRow.Visibility = Visibility.Collapsed;
            // 「重新连接」的可用性不在这里写：它由 SyncBridgeStatus 单点决定，任何一次刷新都会把它带回正轨
            //
            // Reconnect's enabled state is not written here: SyncBridgeStatus owns it alone, so any refresh brings it
            // back in line
            // 两张提示收起时重装按钮也跟着藏起来，但它们的可用性仍要在忙碌结束后恢复：
            // 下次再次不可用时它们会重新出现，若此时仍是 disabled，用户就点不动重装了
            //
            // Both reinstall buttons disappear with their notices when collapsed, yet their enabled state still has to
            // be restored once the busy flag clears: the next time the feature is unusable they return, and a button
            // left disabled would block the reinstall
            ReinstallBridgeBtn.IsEnabled = !_bridgeBusy;
            ReinstallDamagedBtn.IsEnabled = !_bridgeBusy;
            _bridgeBlockedLogged = false;
            return;
        }

        var damaged = reason == BridgeUnusableReason.Damaged;
        var protocol = AppServices.Bridge.InstalledProtocol ?? string.Empty;
        var version = AppServices.Bridge.InstalledVersion ?? string.Empty;
        // 状态行与提示都要把那两个读不出的值换成"未知"：损坏态下读不到它们，值是 unknown 常量与 null
        // 否则提示会写成"已安装版本 unknown"
        //
        // Both the notice and the status line turn the two unreadable values into "unknown": in the damaged state
        // they read as the unknown constant and null, and the notice would otherwise read "installed version unknown"
        if (Core.Update.UpdateChannel.IsUnknown(version))
        {
            version = t("settings.bridge.status.unknown");
        }
        if (protocol.Length == 0)
        {
            protocol = t("settings.bridge.status.unknown");
        }
        // 损坏走下拉开里的那一行，协议不支持走下面那张卡：两者互斥，同时只会有一个可见
        //
        // Damage uses the row inside the drop-down, an unsupported protocol uses the card below; they are exclusive and
        // only one is visible at a time
        BridgeDamagedRow.Visibility = damaged ? Visibility.Visible : Visibility.Collapsed;
        BridgeUnavailableCard.Visibility = damaged ? Visibility.Collapsed : Visibility.Visible;
        if (damaged)
        {
            // 能读到的信息一条都没有，因此这一行只给一句结论，没有可写的细节
            // 说明行留空由 SettingsRow 自动收起（见 Controls/SettingsRow 的 Description setter）
            //
            // Not a single fact can be read from it, so this row carries one conclusion and no detail
            // An empty description collapses itself (see the Description setter in Controls/SettingsRow)
            BridgeDamagedRow.Header = t("settings.bridge.damaged");
            BridgeDamagedRow.Description = string.Empty;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                BridgeDamagedRow, t("settings.bridge.damaged"));
        }
        else
        {
            BridgeUnavailableCard.Header = t("settings.bridge.unavailable.title");
            BridgeUnavailableCard.Description =
                AppServices.I18n.T("settings.bridge.unavailable.hint", ("version", version), ("protocol", protocol));
        }
        ReinstallBridgeBtn.Content = t("settings.bridge.unavailable.reinstall");
        ReinstallDamagedBtn.Content = t("settings.bridge.unavailable.reinstall");

        // 连接在此状态下没有意义，禁用而不是让它点下去失败
        // 两个按钮的禁用都由 SyncBridgeStatus 依据同一个门禁结论写入，这里不再重复
        //
        // Connecting means nothing in this state, so it is disabled rather than failing after a click
        // SyncBridgeStatus disables both buttons from the same gate verdict, so this method does not repeat it
        // 重装入口本身保持可用：它是这个状态下唯一有意义的动作
        //
        // The reinstall entry stays enabled: it is the only action that means anything in this state
        ReinstallBridgeBtn.IsEnabled = !_bridgeBusy;
        ReinstallDamagedBtn.IsEnabled = !_bridgeBusy;

        // 常驻提示 + 一条 warn：这个状态用户需要知道，事后排查时日志是唯一还记着它的地方
        // 只在状态变化时记一次，而不是每次刷新卡片都记（否则翻设置页会把日志刷满）
        //
        // A standing notice plus one warn line: the user needs to know this state, and the log is the only place that
        // still records it afterwards
        // It is written when the state enters this branch, not on every card refresh, which would flood the log while
        // the user browses the settings page
        if (!_bridgeBlockedLogged)
        {
            _bridgeBlockedLogged = true;
            AppServices.Log.Warn(
                damaged
                    ? t("settings.bridge.damaged")
                    : AppServices.I18n.T(
                        "settings.bridge.unavailable.hint", ("version", version), ("protocol", protocol)));
        }
    }

    // 两种"装了但用不了"的 warn 是否已经记过：避免每次刷新卡片都写一条
    // Whether the warn for either "installed but unusable" state has been recorded, so a card refresh stays quiet
    private bool _bridgeBlockedLogged;

    /// <summary>
    /// 检查密码条目的访问范围，不达标时挂出常驻提示
    /// 与保存时的那次检查分开：这一处覆盖"用户手工改过 ACL"与"整个目录被搬到 U 盘上"两种情形，
    /// 那两种都不会经过保存动作
    ///
    /// Checks the access list of the password entry and raises a standing notice when it does not hold
    /// It is separate from the check at save time: this one covers "the user edited the ACL by hand" and "the whole
    /// directory was moved onto a thumb drive", neither of which goes through a save
    /// </summary>
    private void SyncBridgeAclNotice()
    {
        // 没有条目就没有可检查的对象：CheckAccess 会按"文件不存在"返回一个失败结论，那不该报给用户
        //
        // With no entry there is nothing to check: CheckAccess reports a failure for a missing file, which is not
        // something to tell the user about
        if (Core.Bridge.BridgeSecret.Read(AppServices.BaseDir) is null)
        {
            BridgeAclCard.Visibility = Visibility.Collapsed;
            return;
        }

        var acl = Core.Bridge.BridgeSecret.CheckAccess(AppServices.BaseDir);
        UpdateBridgeAclNotice(acl);
    }

    /// <summary>按一次 ACL 检查的结论更新常驻提示 / Updates the standing notice from one ACL check's outcome</summary>
    private void UpdateBridgeAclNotice(Core.Bridge.BridgeAclResult acl)
    {
        Func<string, string> t = AppServices.I18n.T;
        switch (acl.Outcome)
        {
            case Core.Bridge.BridgeAclOutcome.NotSupported:
                BridgeAclCard.Visibility = Visibility.Visible;
                BridgeAclCard.Header = t("settings.bridge.aclNotSupported");
                BridgeAclCard.Description = t("settings.bridge.aclNotSupported.hint");
                // 常驻提示同时留一条 warn：用户关掉设置页之后，日志是唯一还记着这件事的地方
                //
                // The standing notice also leaves a warn line: once the settings page is closed the log is the only
                // place still recording it
                AppServices.Log.Warn(t("settings.bridge.aclNotSupported"));
                break;
            case Core.Bridge.BridgeAclOutcome.Failed:
                BridgeAclCard.Visibility = Visibility.Visible;
                BridgeAclCard.Header = t("settings.bridge.aclFailed");
                BridgeAclCard.Description = t("settings.bridge.aclFailed.hint");
                AppServices.Log.Warn(t("settings.bridge.aclFailed"));
                break;
            default:
                BridgeAclCard.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void OnBridgeHostChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressBridgeControls)
        {
            return;
        }
        // 校验规则只在 Core 的 BridgeSettings 一处，本方法只负责把非法值标出来并拒绝落盘
        // 非法的 host 不写存储：写进去之后 BridgeService 启动时会拿它去连接，报错点会远离用户输入的那一刻
        //
        // The rule lives in Core's BridgeSettings alone; this only flags an invalid value and refuses to persist it
        // An invalid host is not written: once stored, BridgeService would connect with it, and the error would show
        // up far from the moment the user typed it
        var error = Core.Bridge.BridgeSettings.ValidateHost(BridgeHostBox.Text);
        BridgeHostCard.Description = error switch
        {
            Core.Bridge.BridgeHostError.None => AppServices.I18n.T("settings.bridge.host.hint"),
            Core.Bridge.BridgeHostError.Empty => AppServices.I18n.T("settings.bridge.host.empty"),
            Core.Bridge.BridgeHostError.Whitespace => AppServices.I18n.T("settings.bridge.host.whitespace"),
            Core.Bridge.BridgeHostError.TooLong => AppServices.I18n.T(
                "settings.bridge.host.tooLong", ("max", Core.Bridge.BridgeSettings.MaxHostLength.ToString())),
            _ => string.Empty,
        };
        if (error == Core.Bridge.BridgeHostError.None)
        {
            SaveSelectedServerHostPort(host: BridgeHostBox.Text);
        }
    }

    private void OnBridgePortChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressBridgeControls)
        {
            return;
        }
        // 端口按文本原样判断：TryParsePort 用不变文化，与保存侧的 int 解析同源
        //
        // The port is judged from its text: TryParsePort uses the invariant culture, the same source as the int
        // parse on the save side
        if (!Core.Bridge.BridgeSettings.TryParsePort(BridgePortBox.Text, out var port))
        {
            BridgePortCard.Description = AppServices.I18n.T("settings.bridge.port.invalid");
            return;
        }
        var error = Core.Bridge.BridgeSettings.ValidatePort(port);
        BridgePortCard.Description = error == Core.Bridge.BridgePortError.None
            ? AppServices.I18n.T("settings.bridge.port.hint")
            : AppServices.I18n.T("settings.bridge.port.invalid");
        if (error == Core.Bridge.BridgePortError.None)
        {
            SaveSelectedServerHostPort(port: port);
        }
    }

    /// <summary>
    /// 把当前的地址/端口写进选中服务器的条目
    /// 只传其中一个参数表示只改那一项（另一个保持原值），这样逐字符保存 host 时不会顺手把端口改成旧值
    /// 没有选中服务器时什么都不做：界面上此时地址与端口是禁用状态
    ///
    /// Writes the current address/port into the selected server's entry
    /// Passing only one of them changes just that field and keeps the other, so saving the host per keystroke cannot
    /// write a stale port back
    /// With no selected server nothing happens, since those two inputs are disabled in that state
    /// </summary>
    private void SaveSelectedServerHostPort(string? host = null, int? port = null)
    {
        var selection = Core.Bridge.BridgeServerStore.ResolveCurrent(AppServices.BaseDir);
        if (selection.Server is null)
        {
            return;
        }
        Core.Bridge.BridgeServerStore.Update(
            AppServices.BaseDir,
            selection.Server.Id,
            selection.Server.Name,
            host ?? selection.Server.Host,
            port ?? selection.Server.Port);
        // 状态行跟随这台服务器自己的配置：地址或端口一变就重算"已安装/需配置"的结论，
        // 不等切换页面，用户改完立刻看到状态从"请在下方填写"翻成"已安装版本 · 协议版本"
        //
        // The status line follows this server's own configuration: the moment an address or port changes the
        // "installed / needs config" verdict is recomputed, without waiting for a page switch, so the row flips from
        // "fill it in below" to "installed version · protocol version" right away
        SyncBridgeStatus();
    }

    private void OnBridgePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressBridgeControls)
        {
            return;
        }
        // 逐字符保存密码会给磁盘带来大量小写入，也会让 ACL 收紧反复执行
        // 这里只在失焦时落盘（见 OnBridgePasswordLostFocus），PasswordChanged 只负责更新提示
        //
        // Saving on every keystroke would mean many tiny writes and repeated ACL narrowing
        // The value is persisted on losing focus (see OnBridgePasswordLostFocus); this handler only updates the hint
        var hasPassword = BridgePasswordBox.Password.Length > 0;
        BridgePasswordCard.Description = hasPassword
            ? AppServices.I18n.T("settings.bridge.password.hint")
            : AppServices.I18n.T("settings.bridge.password.empty");
    }

    /// <summary>
    /// 密码落盘：走 BridgeSecret.SaveWithResult，因为只有它能回报 ACL 的结论
    /// 返回 bool 的 Save 会把"介质不支持 ACL"与"已安全保存"混为一谈，而这两件事对用户的意义完全不同
    ///
    /// Persists the password through BridgeSecret.SaveWithResult, the only call that reports the ACL verdict
    /// The bool-returning Save would conflate "the medium cannot hold an ACL" with "saved safely", and those two
    /// mean very different things to the user
    /// </summary>
    private void PersistBridgePassword()
    {
        var selection = Core.Bridge.BridgeServerStore.ResolveCurrent(AppServices.BaseDir);
        var result = selection.Server is null
            ? Core.Bridge.BridgeSecret.SaveWithResult(AppServices.BaseDir, BridgePasswordBox.Password)
            : Core.Bridge.BridgeSecret.SaveWithResultFor(
                AppServices.BaseDir, selection.Server.Id, BridgePasswordBox.Password);
        switch (result.Verdict)
        {
            case Core.Bridge.BridgeSecretVerdict.Saved:
            case Core.Bridge.BridgeSecretVerdict.Cleared:
                // 保存成功时顺手核对一次：CheckAccess 既是检查也把 ACL 重设一遍，
                // 覆盖"用户此前手工改过"的情形
                //
                // A successful save is followed by one check: CheckAccess both verifies and re-applies the ACL,
                // which covers an earlier manual edit
                SyncBridgeAclNotice();
                break;
            case Core.Bridge.BridgeSecretVerdict.AccessNotSupported:
            case Core.Bridge.BridgeSecretVerdict.AccessFailed:
                // 这两种结论意味着密码**没有落盘**：如实说出来，别让用户以为已经保存
                //
                // Both verdicts mean the password was NOT written to disk: say so rather than letting the user
                // believe it was saved
                UpdateBridgeAclNotice(new Core.Bridge.BridgeAclResult(
                    result.Verdict == Core.Bridge.BridgeSecretVerdict.AccessNotSupported
                        ? Core.Bridge.BridgeAclOutcome.NotSupported
                        : Core.Bridge.BridgeAclOutcome.Failed,
                    result.Error));
                break;
            default:
                ToastService.Show(
                    AppServices.I18n.T("settings.bridge.password.saveFailed", ("detail", result.Error ?? string.Empty)),
                    severity: Core.Notifications.ToastSeverity.Warning);
                break;
        }
    }

    private void OnBridgePasswordLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressBridgeControls)
        {
            return;
        }
        PersistBridgePassword();
        // 密码也是这台服务器配置的一部分：落盘后重算状态行，"请在下方填写"随之翻页
        //
        // The password is part of this server's configuration as well: once stored the status line is recomputed and
        // the "fill it in below" wording is replaced
        SyncBridgeStatus();
    }

    // ------------------------------------------------------------------ 服务器列表 / Server list

    /// <summary>
    /// 把服务器下拉框与名称框同步到当前列表与选中项
    /// 重新填充会触发 SelectionChanged，因此整段放在 _suppressBridgeControls 之下，
    /// 避免"填充期间"被当成用户选择
    ///
    /// Syncs the server ComboBox and the name box to the current list and selection
    /// Refilling raises SelectionChanged, so the whole block sits under _suppressBridgeControls to keep a fill from
    /// reading as a user choice
    /// </summary>
    private void SyncBridgeServerControls()
    {
        var previous = _suppressBridgeControls;
        _suppressBridgeControls = true;
        try
        {
            var selection = Core.Bridge.BridgeServerStore.ResolveCurrent(AppServices.BaseDir);

            // 先摘掉事件再清空：Items.Clear 会把 SelectedIndex 打回 -1 并触发一次 SelectionChanged，
            // 那一瞬间 SelectedItem 已经不是条目，处理函数会提前 return，
            // 结果就是重建之后下拉框没有任何选中项，显示为空白
            //
            // The handler is detached before clearing: Items.Clear resets SelectedIndex to -1 and raises one
            // SelectionChanged, at which point SelectedItem is already gone and the handler returns early, leaving the
            // rebuilt drop-down with nothing selected and a blank face
            BridgeServerBox.SelectionChanged -= OnBridgeServerSelected;
            try
            {
                BridgeServerBox.Items.Clear();
                for (var i = 0; i < selection.Servers.Count; i++)
                {
                    var server = selection.Servers[i];
                    // 下拉项与主页同一套命名：未命名显示 #序号，命名过显示名字
                    // 用带 Label 属性的对象 + DisplayMemberPath 绑定：直接塞 ComboBoxItem 时
                    // WinUI 在关闭状态下不会把它的 Content 渲染到框体里，框体会是空的
                    //
                    // The drop-down shares the home page's naming: unnamed entries read #index, named ones their name
                    // These are objects carrying a Label bound through DisplayMemberPath: with raw ComboBoxItem entries
                    // WinUI does not render their Content into the closed face, which leaves the box blank
                    var label = Core.Bridge.BridgeServers.ShortLabel(server.Name, i, selection.Servers.Count);
                    if (label.Length == 0)
                    {
                        label = AppServices.I18n.T("devices.bridge");
                    }
                    BridgeServerBox.Items.Add(new BridgeServerOption(server.Id, label));
                }

                // 选中项必须显式设置：重建后默认是 -1，界面上就是一片空白
                //
                // The selection has to be set explicitly: after a rebuild it defaults to -1, which shows as a blank face
                if (BridgeServerBox.Items.Count > 0)
                {
                    BridgeServerBox.SelectedIndex = selection.Index >= 0 ? selection.Index : 0;
                }
            }
            finally
            {
                BridgeServerBox.SelectionChanged += OnBridgeServerSelected;
            }

            BridgeServerRemoveBtn.IsEnabled = selection.Server is not null;
            BridgeServerNameBox.Text = selection.Server?.Name ?? string.Empty;
            var hasServer = selection.Server is not null;
            BridgeHostBox.IsEnabled = hasServer;
            BridgePortBox.IsEnabled = hasServer;
            BridgePasswordBox.IsEnabled = hasServer;
            BridgeServerNameBox.IsEnabled = hasServer;
        }
        finally
        {
            _suppressBridgeControls = previous;
        }
    }

    /// <summary>用户换选了服务器：写选中键，再按新服务器回填地址/端口/密码 / The user picked another server: store the choice, then refill the fields from it</summary>
    private void OnBridgeServerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBridgeControls)
        {
            return;
        }
        if (BridgeServerBox.SelectedItem is not BridgeServerOption option)
        {
            return;
        }
        // 用户在设置页显式换了一台服务器，就是在要求连它：这里总是重连，
        // 与主页下拉里点选同一台时的行为一致，两处不出现"一边会连、一边只换不连"的差别
        //
        // Picking a server explicitly in the settings page is a request to connect it, so this always reconnects —
        // the same as clicking the same entry in the home page drop-down, leaving no "one place connects and the other
        // only switches" difference between them
        AppServices.Bridge.ConnectServer(option.Id);
        SyncBridgeServerControls();
        SyncBridgeControls();
        // 主页那份列表也要跟着换：否则回到主页时下拉还停在上一台
        //
        // The home page's list has to follow: otherwise going back leaves its drop-down on the previous server
        AppServices.Backend.ListPorts();
    }

    /// <summary>
    /// 添加一台服务器：id 立即生成，新条目按空名字进列表并选中
    /// 地址/端口留空由用户接着填 —— 空条目在解析时判为"未配置"，不会带着空值去连接
    ///
    /// Adds a server: the id is generated at once and the new entry joins the list unnamed and selected
    /// The address and port stay empty for the user to fill in; an empty entry resolves to "not configured", so no
    /// connection is attempted with empty values
    /// </summary>
    private void OnBridgeServerAdd(object sender, RoutedEventArgs e)
    {
        var selection = Core.Bridge.BridgeServerStore.ResolveCurrent(AppServices.BaseDir);
        var id = Core.Bridge.BridgeServers.NewId(selection.Servers);
        if (!Core.Bridge.BridgeServerStore.Add(AppServices.BaseDir, new Core.Bridge.BridgeServer(id, string.Empty, string.Empty, 9003)))
        {
            ToastService.Show(
                AppServices.I18n.T("settings.bridge.server.saveFailed"),
                severity: Core.Notifications.ToastSeverity.Warning);
            return;
        }
        Core.Bridge.BridgeServerStore.Select(AppServices.BaseDir, id);
        SyncBridgeServerControls();
        SyncBridgeControls();
    }

    /// <summary>
    /// 删除选中的服务器：连它的密码文件一起清掉，选中键由 Core 在删除后清空并回落到第一台
    ///
    /// Removes the selected server together with its password file; Core clears the selection key afterwards and
    /// falls back to the first entry
    /// </summary>
    private void OnBridgeServerRemove(object sender, RoutedEventArgs e)
    {
        var selection = Core.Bridge.BridgeServerStore.ResolveCurrent(AppServices.BaseDir);
        if (selection.Server is null)
        {
            return;
        }
        if (!Core.Bridge.BridgeServerStore.Remove(AppServices.BaseDir, selection.Server.Id))
        {
            return;
        }
        SyncBridgeServerControls();
        SyncBridgeControls();
    }

    /// <summary>改名在失焦时落盘：逐字符写列表没有意义，与密码同一条约定 / A rename lands on losing focus: writing the list per keystroke is pointless, the same rule as the password</summary>
    private void OnBridgeServerNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressBridgeControls)
        {
            return;
        }
        var selection = Core.Bridge.BridgeServerStore.ResolveCurrent(AppServices.BaseDir);
        if (selection.Server is null)
        {
            return;
        }
        var name = BridgeServerNameBox.Text.Trim();
        if (string.Equals(name, selection.Server.Name, StringComparison.Ordinal))
        {
            return;
        }
        if (!Core.Bridge.BridgeServerStore.Update(
                AppServices.BaseDir, selection.Server.Id, name, selection.Server.Host, selection.Server.Port))
        {
            ToastService.Show(
                AppServices.I18n.T("settings.bridge.server.saveFailed"),
                severity: Core.Notifications.ToastSeverity.Warning);
            return;
        }
        SyncBridgeServerControls();
        SyncBridgeStatus();
    }

    /// <summary>
    /// 未安装时的安装入口：走与主页确认框同一条 BridgeInstallFlow
    /// 已安装时这一行是"检查更新"，本事件不会触发（按钮此时不可见）
    ///
    /// The install entry while not installed: the same BridgeInstallFlow as the home page's confirmation dialog
    /// Installed, the row reads "check for updates" and this handler cannot fire, because the button is hidden then
    /// </summary>
    private async void OnInstallBridgeClick(object sender, RoutedEventArgs e)
    {
        if (_bridgeBusy)
        {
            return;
        }
        _bridgeBusy = true;
        try
        {
            var outcome = await BridgeInstallFlow.RunAsync(AppServices.Bridge, XamlRoot);
            if (outcome is { Ok: true })
            {
                SyncBridgeControls();
                SyncBridgeServerControls();
                SyncBridgeStatus();
            }
        }
        finally
        {
            _bridgeBusy = false;
        }
    }

    private void OnRetryBridgeAcl(object sender, RoutedEventArgs e)
    {
        // 重新跑一次检查兼修复，然后把结论刷新到卡片上
        //
        // One more check-and-repair pass, then the verdict is refreshed on the card
        SyncBridgeAclNotice();
        if (BridgeAclCard.Visibility == Visibility.Visible)
        {
            ToastService.Show(
                AppServices.I18n.T("settings.bridge.aclStillFailing"),
                severity: Core.Notifications.ToastSeverity.Warning);
        }
    }

    private void OnBridgeReconnect(object sender, RoutedEventArgs e)
    {
        // 地址、端口或密码还缺着时，连上去无从谈起，把要填的东西说清楚，免得点了没反应像是坏了
        //
        // With the address, port or password still missing there is nothing to connect to, so spell out what has to be
        // filled in, rather than leaving the click with no visible effect
        if (AppServices.Bridge.BridgeUnusableReason == BridgeUnusableReason.NotConfigured)
        {
            ToastService.Show(
                AppServices.I18n.T("bridge.failed.notConfigured"),
                severity: Core.Notifications.ToastSeverity.Warning);
            return;
        }

        // 重连 = 停掉当前会话再来一次
        // 来源不是远程时也接上：这里点"连接"的用户意图就是"连这台服务器"，
        // 再要求他先回主页选一次设备等于把一句话拆成两步，且让按钮看起来没反应
        //
        // Reconnect means stopping the current session and starting over
        // It also attaches the route when the source is not the remote one: clicking "connect" here plainly means
        // "connect this server", and asking the user to go back to the home page first would split one intent into two
        // steps and make the button look inert
        AppServices.Bridge.Stop();
        if (AppServices.Backend.ActiveSource != MidiSource.Bridge)
        {
            AppServices.Backend.SelectBridgeSource();
            AppStorage.SaveBridgeSource(AppServices.BaseDir, "Bridge");
        }
        _ = AppServices.Bridge.StartAsync();
        RefreshBridgeStateText();
    }

    /// <summary>
    /// 刷新状态行文案：会话状态 + ACL 结论各占一行说明
    /// 文案键按约定拼出（bridge.session.&lt;枚举名&gt;）
    ///
    /// 右侧按钮的措辞也跟着会话状态走：已经有会话时它是"重新连接"（拆掉重来），
    /// 没有会话时它只是把这一路接起来，叫"连接"才名副其实
    ///
    /// Refreshes the status line: the session state and the ACL verdict each get one description line
    /// The keys are composed by convention (bridge.session.&lt;enum name&gt;)
    ///
    /// The wording of the right-hand button follows the session as well: with a session in place it means "reconnect"
    /// (tear it down and start over), while without one it only brings the route up, which "connect" describes
    /// </summary>
    private void RefreshBridgeStateText()
    {
        var state = AppServices.Bridge.State;
        // 会话文案带 {name}：多服务器时用户要知道"正在重连的是哪一台"
        // 标签取自 BridgeService 的统一实现；没有服务器时是空串，句子不会悬空
        //
        // The session copy carries {name}: with several servers the user needs to know which one is being reconnected
        // The label comes from BridgeService's shared implementation; with no server it is empty, leaving no dangling
        // sentence
        BridgeStateCard.Description = AppServices.I18n.T("bridge.session." + state, ("name", AppServices.Bridge.CurrentServerLabel));
        // 断线重连中仍算有会话：这时点它就是立刻再试一次，因此保持"重新连接"
        //
        // A connection being re-established still counts as a session: clicking then retries at once, so it keeps the
        // "reconnect" wording
        var connected = state is BridgeSessionState.Streaming or BridgeSessionState.Reconnecting;
        BridgeReconnectBtn.Content = AppServices.I18n.T(
            connected ? "settings.bridge.reconnect" : "settings.bridge.connect");
    }

    private async void OnCheckBridgeUpdate(object sender, RoutedEventArgs e)
    {
        if (_bridgeBusy)
        {
            return;
        }
        _bridgeBusy = true;
        BridgeCheckUpdateBtn.IsEnabled = false;
        ToastService.Show(AppServices.I18n.T("settings.checking"));
        try
        {
            // 扩展的"检查更新"查的是上游 midibridge-client 的发布，不是 MIDITap 自身的版本
            // 因此它**不受 AppServices.UpdatesEnabled 限制**：那个开关管的是本程序能否被更新，
            // 与用户是否愿意装这个可选扩展无关
            // 网络设置（代理）仍沿用同一个更新选项，用户不必为扩展再配一次代理
            //
            // 有序检查走 BridgeService.CheckUpdatesRespectingAppUpdateAsync：
            // 先查 MIDITap 自身，确认无更新才查 Bridge
            // 顺序是硬约束：先升 Bridge 会装出一个协议对接不上的组合
            //
            // The extension's "check for updates" looks at upstream midibridge-client releases, not MIDITap's own
            // version, so it is **not gated by AppServices.UpdatesEnabled**
            // That switch decides whether this program may be updated, which is a different question from whether the
            // user wants this optional extension
            // The network settings (proxy) are the same update options, so no second proxy has to be configured
            //
            // The ordered check goes through BridgeService.CheckUpdatesRespectingAppUpdateAsync: MIDITap itself first,
            // Bridge only once the app is confirmed current
            // The order is a hard constraint: upgrading Bridge first would land a build whose protocol this app
            // cannot consume
            var gate = await AppServices.Bridge.CheckUpdatesRespectingAppUpdateAsync(AppServices.AppVersion);
            ToastService.Clear();

            // 每种结论对应一种呈现，"跳过"不是错误，因此不弹 warning：
            // 跳过只是"这次没查"，把用户引去做对的事（先升 MIDITap）才是它的任务
            //
            // Each verdict maps to one presentation, and a skip is not an error, so no warning toast:
            // a skip only means "not checked this time", and its job is to point the user at the right next step
            switch (gate.Gate)
            {
                case BridgeUpdateGate.SkippedAfterAppUpdate:
                    // a) MIDITap 自身不是最新：先完成它，Bridge 的检查在下次会重新走到
                    // 不报最新版本号：用户要做的动作是"更新 MIDITap"，而不是比较两个版本号
                    //
                    // a) MIDITap itself is not current: finish that first, and the Bridge check runs again next time
                    // The latest version number is left out: the action for the user is "update MIDITap", not to
                    // compare two version strings
                    var skipUpdate = AppServices.I18n.T("settings.bridge.gate.appNotLatest");
                    ToastService.Show(skipUpdate, severity: Core.Notifications.ToastSeverity.Info);
                    AppServices.Log.Info(skipUpdate);
                    break;
                case BridgeUpdateGate.SkippedAfterAppCheckFailure:
                    // MIDITap 自身的检查都没成功（网络/API），这次也谈不上检查 Bridge
                    //
                    // Even the app's own check failed (network or API), so there was nothing to build a Bridge check on
                    var skipFailure = DescribeUpdateFailure(
                        gate.SelfFailure, 0, gate.SelfDetail, null);
                    ToastService.Show(skipFailure, severity: Core.Notifications.ToastSeverity.Warning);
                    AppServices.Log.Warn(skipFailure);
                    break;
                case BridgeUpdateGate.BlockedByRemoteProtocol:
                    // b) 云端 latest 的协议本 Tap 不支持：装上它也对接不了，需要先升 MIDITap
                    //
                    // b) The latest remote protocol is unsupported here, so installing it would not help either: the
                    // Tap has to move first
                    var remoteBlocked = AppServices.I18n.T("settings.bridge.gate.remoteProtocol");
                    ToastService.Show(remoteBlocked, severity: Core.Notifications.ToastSeverity.Warning);
                    AppServices.Log.Warn(remoteBlocked);
                    break;
                case BridgeUpdateGate.BlockedByLocalProtocol:
                    // 本地已装协议不被支持：这个状态由常驻提示处理，检查更新只补一条说明
                    //
                    // The installed protocol is unsupported: the standing notice covers the state, and the check only
                    // adds one remark
                    SyncBridgeUnavailableNotice();
                    var localBlocked = AppServices.I18n.T("settings.bridge.gate.localProtocol");
                    ToastService.Show(localBlocked, severity: Core.Notifications.ToastSeverity.Warning);
                    AppServices.Log.Warn(localBlocked);
                    break;
                case BridgeUpdateGate.BlockedByLocalProtocolUnknown:
                    // 装了但读不出协议号：探测失败，不是协议不对
                    // 说明"读不出已装版本的信息"，避免用户照着"不兼容"的提示白重装一次
                    //
                    // Installed, yet its protocol cannot be read: the probe failed, and the protocol is not wrong
                    // The message says the installed build could not be read, sparing the user a pointless reinstall
                    // prompted by an "incompatible" claim
                    var unprobed = AppServices.I18n.T("settings.bridge.gate.localProtocolUnknown");
                    ToastService.Show(unprobed, severity: Core.Notifications.ToastSeverity.Warning);
                    AppServices.Log.Warn(unprobed);
                    break;
                case BridgeUpdateGate.BridgeCheckFailed:
                    // Bridge 检查自身失败（网络/API/解析）：与 Tap 侧同一条失败文案
                    //
                    // The Bridge check itself failed (network, API, parse): the same failure copy as on the Tap side
                    var failed = DescribeUpdateFailure(
                        gate.BridgeFailure, 0, gate.SelfDetail, null);
                    ToastService.Show(failed, severity: Core.Notifications.ToastSeverity.Warning);
                    AppServices.Log.Warn(failed);
                    break;
                case BridgeUpdateGate.BridgeVersionUnknown:
                    // 已装版本无法比较（dev 或探测失败）：检查成功了，但给不出"有没有新版本"的结论
                    // 版本号读不出时填“未知”，否则界面上会出现未翻译的 unknown 常量
                    //
                    // The installed version cannot be compared (dev, or a failed probe): the check succeeded but no
                    // update verdict is possible
                    // An unreadable version becomes "unknown" in the UI language, otherwise the raw constant shows up
                    var unknownVersion = Core.Update.UpdateChannel.IsUnknown(gate.InstalledVersion)
                        ? AppServices.I18n.T("settings.bridge.status.unknown")
                        : gate.InstalledVersion ?? string.Empty;
                    var unknown = AppServices.I18n.T(
                        "settings.bridge.versionUnknown", ("version", unknownVersion));
                    ToastService.Show(unknown, severity: Core.Notifications.ToastSeverity.Warning);
                    AppServices.Log.Info(unknown);
                    break;
                case BridgeUpdateGate.NoBridgeUpdate:
                    // c) 正常：无更新
                    //
                    // c) The normal case: no update
                    var upToDate = AppServices.I18n.T(
                        "settings.bridge.upToDate", ("version", gate.InstalledVersion ?? string.Empty));
                    ToastService.Show(upToDate, severity: Core.Notifications.ToastSeverity.Success);
                    AppServices.Log.Info(upToDate);
                    break;
                case BridgeUpdateGate.BridgeUpdateAvailable:
                    // c) 正常：有更新且协议兼容
                    // 弹与本体更新同构的窗：版本对照 + 更新日志 + 下载安装，
                    // 下载与安装的进度与结果由弹窗自己呈现（它就是呈递者），
                    // 这里不再补任何成功 toast，弹窗里能看到的信息比一句话多
                    //
                    // c) The normal case: an update exists and the protocols are compatible
                    // The dialog mirrors the app-update one: version comparison, release notes, download and install
                    // Progress and results are shown by the dialog itself, as it is the presenter,
                    // so no success toast is added here: the dialog carries more than one line ever could
                    var dialog = new BridgeUpdateDialog(gate) { XamlRoot = XamlRoot };
                    await dialog.ShowAsync();
                    break;
            }

            // 检查过程会顺带把版本与协议探一遍：无论如何重画一次状态行与门禁
            //
            // The check probes the version and protocol along the way, so the status line and the gate are repainted
            // regardless of the verdict
            SyncBridgeStatus();
            SyncBridgeUnavailableNotice();
        }
        catch (Exception err)
        {
            // 检查链路自己应当返回失败结论而不是抛异常；这里兜住意外，避免一次点击把页面带崩
            //
            // The check chain returns a failure verdict rather than throwing; this catches the unexpected, so one
            // click cannot take the page down
            AppServices.Log.Warn(AppServices.I18n.T("settings.bridge.checkFailed", ("detail", err.Message)));
        }
        finally
        {
            _bridgeBusy = false;
            SyncBridgeStatus();
        }
    }

    /// <summary>
    /// 重新安装：协议不兼容时唯一有意义的动作
    /// 下载进度与结果弹窗都由 BridgeInstallFlow 负责，主页与设置页共用同一条链路，
    /// 这里只管把页面拉回安装后的真实状态
    ///
    /// Reinstall, the only action that means anything while the protocol is unsupported
    /// BridgeInstallFlow owns the download progress and the result dialog, and the home page and the settings page
    /// share that one chain
    /// This handler only pulls the page back to the real state after the install
    /// </summary>
    private async void OnReinstallBridge(object sender, RoutedEventArgs e)
    {
        if (_bridgeBusy)
        {
            return;
        }
        _bridgeBusy = true;
        ReinstallBridgeBtn.IsEnabled = false;
        ReinstallDamagedBtn.IsEnabled = false;
        BridgeReconnectBtn.IsEnabled = false;
        try
        {
            var outcome = await BridgeInstallFlow.RunAsync(AppServices.Bridge, XamlRoot);
            if (outcome is { Ok: true })
            {
                // 重装可能换上一个能用的版本：把"已经记过 warn"的标记清掉，
                // 否则重装后再次出问题时那条 warn 不会留下记录
                //
                // A reinstall may land a build that works, so the "already warned" flag is cleared; otherwise a later
                // problem after this reinstall would leave no trace
                _bridgeBlockedLogged = false;
                // 等新装的那个文件探完再刷新：装完只作废了缓存，此刻页面读到的还是旧值，
                // 门禁、状态行与两个按钮因此要到下一次刷新才对得上
                //
                // Wait for the freshly installed file to be probed before refreshing: the install only invalidates the
                // cache, so at this point the page still reads the old values and the gate, the status line and both
                // buttons would only line up on the next refresh
                await RefreshBridgeMetadataProbeAsync();
                SyncBridgeControls();
            }
        }
        finally
        {
            _bridgeBusy = false;
            SyncBridgeStatus();
            // 重装按钮是否可用由门禁决定，而不是无条件启用：重装成功后门禁可能仍然不通过
            //
            // Whether the reinstall button is enabled is the gate's call rather than an unconditional true: the gate
            // may still not pass after a reinstall
            SyncBridgeUnavailableNotice();
        }
    }

    // ------------------------------------------------------------------ i18n

    public void RefreshTexts()
    {
        // 语言切换后已显示的浮窗文案属于旧语言，清掉避免混排
        // After a language switch, a toast already on screen carries the old language; clearing it avoids mixing the two
        ToastService.Clear();
        Func<string, string> t = AppServices.I18n.T;
        // 卡片是「标题在左、控件在右」的结构（见 Controls/SettingsCard）
        // 因此这里填的是每张卡的标题与说明，而不是一行行独立的标签控件
        //
        // A card puts its title on the left and its control on the right (see Controls/SettingsCard)
        // So what is filled in here is each card's title and description rather than a row of separate label controls
        PageTitle.Text = t("nav.settings");
        LanguageCard.Header = t("settings.language");
        ThemeCard.Header = t("settings.theme");
        // 主题项每次都要**重建**（而不是改条目文案）
        // WinUI 的 ComboBox 把选中项的内容拷进了 SelectionBoxItem，改条目不会刷新那个框
        //
        // The theme items are **rebuilt** every time rather than relabelled
        // WinUI's ComboBox copied the selected item's content into SelectionBoxItem
        // Editing the item does not refresh that box
        BuildThemeChoices();
        ConfigCard.Header = t("settings.configDir");
        ConfigCard.Description = t("settings.configDir.hint");
        OpenConfigDirBtn.Content = t("settings.openConfigDir");
        LogCard.Header = t("settings.logToFile");
        LogCard.Description = t("settings.logToFile.hint");
        OpenLogDirBtn.Content = t("settings.openLogDir");
        UpdatesCard.Header = t("settings.updates");
        // 开发构建不参与更新，"启动时检查新版本"这句话对它是假的，换成如实的一句
        // 与代理框、检查更新按钮的悬浮提示共用同一条文案：同一件事只写一份，改起来不会漏掉某处
        //
        // A development build takes no part in updates
        // So "checks for a new version at startup" would be false for it
        // An accurate line replaces it
        // It is the same copy the proxy box and the check-for-updates button show on hover
        // One sentence for one fact, so a change cannot miss one of the three places
        UpdatesCard.Description = AppServices.UpdatesEnabled
            ? t("settings.updates.hint")
            : t("settings.devTooltip");
        ProxyCard.Header = t("settings.proxy");
        ProxyBox.PlaceholderText = t("settings.proxy.placeholder");
        // 两个开关没有可见标签（标题与说明已说清它们管什么），名字补给读屏
        //
        // The two switches have no visible label (their titles and descriptions say what they control)
        // So the names go to screen readers instead
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LogFileToggle, t("settings.logToFile.toggle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutoCheckToggle, t("settings.updates.auto"));
        UpdateProxyHint();
        SyncLogToggle();
        SyncUpdateControls();
        AboutCard.Header = t("settings.about");
        // 版本与协议合成卡片的一行说明：两者同属"关于本程序"的事实，各占一张卡会多出一块空白
        //
        // Version and licence share the card's one description line
        // Both are facts about the program, and giving each its own card would add a mostly empty one
        AboutCard.Description = $"{t("settings.version")} {AppServices.AppVersion}  ·  {t("settings.license")}";
        CheckUpdateBtn.Content = t("settings.checkUpdates");
        // 开发构建下这个按钮无事可做：禁用，而不是让它点下去没有任何反应
        //
        // In a development build the button has nothing to do: disabled, rather than doing nothing when clicked
        CheckUpdateBtn.IsEnabled = AppServices.UpdatesEnabled;
        // 与代理框同一做法：原因挂在卡片上，鼠标悬浮在卡片任意位置都能看到
        //
        // The reason sits on the card, shown while hovering anywhere on it
        AboutCard.Hint = AppServices.UpdatesEnabled ? string.Empty : t("settings.devTooltip");
        GitHubBtn.Content = t("settings.github");
        ApplyBridgeTexts(t);
        SyncThemeSelection();
        SyncLanguageSelection();
    }

    /// <summary>
    /// 扩展卡片上的全部文案（语言切换后要全部重填，否则切完语言一半是旧语言）
    /// 控件内容也从这里填：XAML 里不留写死的中英文
    ///
    /// Every string on the extension cards, refilled after a language switch, or half of them would keep the old
    /// language
    /// Control contents are filled here as well, so no hardcoded Chinese or English stays in the XAML
    /// </summary>
    private void ApplyBridgeTexts(Func<string, string> t)
    {
        BridgeStatusCard.Header = t("settings.bridge");
        // 状态说明行由 SyncBridgeStatus 按安装状态填写，这里只填标题与按钮文案
        // 按钮可用性也归它判定：语言切换不是一次状态变化，不在这里重判一遍
        //
        // The status description line is filled by SyncBridgeStatus from the install state, so only the title and the
        // button copy are set here
        // The button's enabled state belongs to SyncBridgeStatus too, and a language switch is not a state change, so
        // it is not judged a second time here
        BridgeHostCard.Header = t("settings.bridge.host");
        BridgeServerCard.Header = t("settings.bridge.server");
        BridgeServerCard.Description = t("settings.bridge.server.add");
        BridgeServerAddBtn.Content = t("settings.bridge.add");
        BridgeServerRemoveBtn.Content = t("settings.bridge.remove");
        BridgeServerNameCard.Header = t("settings.bridge.server.name");
        BridgeServerNameBox.PlaceholderText = t("settings.bridge.server.name.placeholder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            BridgeServerBox, t("settings.bridge.server"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            BridgeServerAddBtn, t("settings.bridge.server.add"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            BridgeServerRemoveBtn, t("settings.bridge.server.remove"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            BridgeServerNameBox, t("settings.bridge.server.name"));
        BridgeInstallBtn.Content = t("settings.bridge.install");
        BridgePortCard.Header = t("settings.bridge.port");
        BridgePasswordCard.Header = t("settings.bridge.password");
        BridgeStateCard.Header = t("settings.bridge.state");
        // 展开区最后一行的标题与说明：它承载"检查更新"，这一行是设置的最后一步
        //
        // The title and description of the expanded block's last row: it carries "check for updates", the final step
        // of the settings
        BridgeUpdateRow.Header = t("settings.bridge.update");
        BridgeUpdateRow.Description = t("settings.bridge.updateHint");
        // 展开箭头没有可见文字，读屏名字就是它唯一的解释
        //
        // The chevron has no visible text, so its screen-reader name is the only explanation it carries
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            BridgeConfigToggleBtn, t("settings.bridge.config"));
        // 连接/重新连接按钮的文案归 RefreshBridgeStateText：它按会话状态在两种措辞之间选，
        // 这里不写，否则语言切换后会被这一行覆盖成固定的一种
        //
        // The connect/reconnect copy belongs to RefreshBridgeStateText, which picks between the two wordings from the
        // session state; it is not written here, or a language switch would overwrite it with one fixed wording
        BridgeCheckUpdateBtn.Content = t("settings.bridge.checkUpdate");
        BridgeRetryAclBtn.Content = t("settings.bridge.retryAcl");
        // 重新安装按钮的文案由 SyncBridgeUnavailableNotice 按门禁填写：这里先给一个默认值，
        // 避免语言切换后、门禁重算之前它停在上一种语言的文字上
        // 损坏那一行的标题同理，也由 SyncBridgeUnavailableNotice 填写
        //
        // The reinstall buttons' copy is filled by SyncBridgeUnavailableNotice according to the gate; a default is
        // set here so it does not keep the previous language in the window between a language switch and the gate
        // recomputing
        // The damaged row's header is filled there as well, for the same reason
        ReinstallBridgeBtn.Content = t("settings.bridge.unavailable.reinstall");
        ReinstallDamagedBtn.Content = t("settings.bridge.unavailable.reinstall");
        BridgeDamagedRow.Header = t("settings.bridge.damaged");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            BridgeDamagedRow, t("settings.bridge.damaged"));

        BridgeHostBox.PlaceholderText = t("settings.bridge.host.placeholder");
        BridgePortBox.PlaceholderText = t("settings.bridge.port.placeholder");
        BridgePasswordBox.PlaceholderText = t("settings.bridge.password.placeholder");

        // 读屏名字：这些输入框的可见标签在卡片左侧，而 PasswordBox 本身没有标签
        //
        // Screen-reader names: the visible labels sit on the left of each card, and a PasswordBox carries none of its
        // own
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BridgeHostBox, t("settings.bridge.host"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BridgePortBox, t("settings.bridge.port"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(BridgePasswordBox, t("settings.bridge.password"));

        // host/port 的说明行同时承载校验结果，因此文案重填后要让校验再跑一次，
        // 否则切语言后合法的输入会停在旧语言的那句提示上
        //
        // The host and port description lines also carry the validation result, so the checks run again after the
        // copy is refilled; otherwise a valid value would keep the old language's hint
        RefreshBridgeValidation();
        RefreshBridgeStateText();
        SyncBridgeControls();
    }

    /// <summary>
    /// 重跑 host/port 的校验并只更新说明行（不写存储：语言切换不是一次用户输入）
    /// 保存动作只在 OnBridgeHostChanged / OnBridgePortChanged 里发生
    ///
    /// Re-runs the host and port checks and updates only the description lines (nothing is persisted: a language
    /// switch is not user input)
    /// Persisting happens in OnBridgeHostChanged and OnBridgePortChanged alone
    /// </summary>
    private void RefreshBridgeValidation()
    {
        var hostError = Core.Bridge.BridgeSettings.ValidateHost(BridgeHostBox.Text);
        BridgeHostCard.Description = hostError switch
        {
            Core.Bridge.BridgeHostError.None => AppServices.I18n.T("settings.bridge.host.hint"),
            Core.Bridge.BridgeHostError.Empty => AppServices.I18n.T("settings.bridge.host.empty"),
            Core.Bridge.BridgeHostError.Whitespace => AppServices.I18n.T("settings.bridge.host.whitespace"),
            Core.Bridge.BridgeHostError.TooLong => AppServices.I18n.T(
                "settings.bridge.host.tooLong", ("max", Core.Bridge.BridgeSettings.MaxHostLength.ToString())),
            _ => string.Empty,
        };

        var validPort = Core.Bridge.BridgeSettings.TryParsePort(BridgePortBox.Text, out var port)
            && Core.Bridge.BridgeSettings.ValidatePort(port) == Core.Bridge.BridgePortError.None;
        BridgePortCard.Description = validPort
            ? AppServices.I18n.T("settings.bridge.port.hint")
            : AppServices.I18n.T("settings.bridge.port.invalid");

        BridgePasswordCard.Description = BridgePasswordBox.Password.Length > 0
            ? AppServices.I18n.T("settings.bridge.password.hint")
            : AppServices.I18n.T("settings.bridge.password.empty");
    }
}

/// <summary>
/// 服务器下拉框的一项：显示文案与它对应的服务器 id
/// 用普通对象而不是 ComboBoxItem：DisplayMemberPath 绑的是属性，
/// ComboBoxItem 的 Content 在 ComboBox 关闭时不会被渲染到框体里
///
/// One entry of the server drop-down: the label to show and the server id it stands for
/// A plain object rather than a ComboBoxItem, because DisplayMemberPath binds to a property while a ComboBoxItem's
/// Content is not rendered into the closed ComboBox face
/// </summary>
public sealed class BridgeServerOption
{
    public BridgeServerOption(string id, string label)
    {
        Id = id;
        Label = label;
    }

    /// <summary>服务器 id / The server id</summary>
    public string Id { get; }

    /// <summary>下拉框里显示的文案 / The label shown in the drop-down</summary>
    public string Label { get; }
}
