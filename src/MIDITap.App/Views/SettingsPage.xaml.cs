// SettingsPage.cs — 全应用唯一的偏好页：外观（主题 + 语言）、活动日志、更新、关于
//
// SettingsPage.cs — the application's only preferences page
// That covers appearance (theme + language), activity log, updates and about


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
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshTexts();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.I18n.LanguageChanged -= RefreshTexts;
    }

    // ------------------------------------------------------------------ theme

    /// <summary>
    /// 重建主题项
    /// **每次语言切换都要调用**，因为文案是本地化字符串、必须重新生成
    ///
    /// 为什么是"重建数据源"而不是"改现有条目的文案"
    /// WinUI 的 ComboBox 在选中时会把条目的内容拷进 SelectionBoxItem
    /// 之后改条目本身**不会刷新那个框**，于是切语言后外观项一直显示旧语言
    /// 换 ItemsSource 会整块重建，选中框随之刷新，随后按存储值恢复选中项
    ///
    /// 条目来自 Core 的 SupportedThemes，文案键按约定拼出（settings.theme.&lt;模式&gt;）
    /// 与日志级别用 log.level.&lt;级别&gt; 是同一条约定，因此不必再维护一张配对表
    ///
    /// Rebuilds the theme items
    /// **This has to be called on every language switch**
    /// The labels are localised strings and must be regenerated
    ///
    /// Why rebuild the data source instead of relabelling the existing items
    /// A WinUI ComboBox copies the item's content into SelectionBoxItem when it is selected
    /// Editing the item itself afterwards does not refresh that box
    /// So the appearance item kept showing the old language after a switch
    /// Replacing the ItemsSource rebuilds the whole set, which refreshes the selection box
    /// The selection is then restored from the stored value
    ///
    /// The items come from SupportedThemes in Core and the label keys are composed by convention (settings.theme.&lt;mode&gt;)
    /// That is the same convention as log.level.&lt;level&gt; for log levels, so no pairing table has to be maintained
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
        // 右对齐的一列控件里用下拉框承载三选一（见 SettingsPage.xaml 的说明）
        // A drop-down carries the three-way choice in the right-aligned control column (see SettingsPage.xaml)
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
            // 这一条与卡片常驻的说明重复，是有意为之：那行字在卡片左侧，而用户把指针移到开关上时视线也在开关上
            // 三处（开关、代理框、检查更新按钮）行为一致，不必让使用者记住哪一个才会弹
            //
            // The switch likewise: in a development build it has nothing to control
            // This duplicates the card's standing description on purpose: that line sits on the left of the card, while the pointer and the eye are both on the switch
            // All three controls (the switch, the proxy box and the check-for-updates button) then behave the same, so no one has to remember which one shows a hint
            AutoCheckToggle.IsEnabled = AppServices.UpdatesEnabled;
            UpdatesCard.Hint = AppServices.UpdatesEnabled
                ? string.Empty
                : AppServices.I18n.T("settings.devTooltip");
            // 代理也一并禁用
            // 它的用途只与更新有关（文案见 settings.proxy.hint，此处不重复一遍）
            // 更新整条链路不成立时还留一个能改的输入框，只会让人以为它仍有作用
            //
            // The proxy is disabled along with the rest
            // Its purpose is tied entirely to updates
            // The copy lives at settings.proxy.hint and is deliberately not repeated here
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

        // 说明仍写回卡片那一行（不再是独立控件）：无效时在句尾追加提示
        //
        // The description still goes to the card's own line (no longer a separate control)
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
            var outcome = await Core.Update.UpdateChecker.CheckWithReasonAsync(
                AppServices.AppVersion, AppServices.Backend.UpdateOptions);
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
            // 文案刻意简短：浮窗宽度有限，长句会被截断成半句话（用户报告过这个问题）
            //
            // A 403 with a reset time names the time; otherwise it falls back to a general note
            // Kept short on purpose: the toast is narrow and long sentences got cut off mid-clause
            UpdateFailure.Http when httpStatus == 403 && rateLimit?.Reset is not null =>
                AppServices.I18n.T("update.failed.rateLimited",
                    ("time", rateLimit.Reset.Value.ToString("HH:mm"))),
            UpdateFailure.Http => AppServices.I18n.T(
                "update.failed.http", ("status", httpStatus.ToString())),
            UpdateFailure.Parse => t("update.failed.parse"),
            _ => AppServices.I18n.T("update.failed.other", ("detail", detail ?? string.Empty)),
        };
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
        // 这正是切语言后外观项显示旧语言的原因，详见 BuildThemeChoices
        //
        // The theme items are **rebuilt** every time rather than relabelled
        // WinUI's ComboBox copied the selected item's content into SelectionBoxItem
        // Editing the item does not refresh that box
        // That is exactly why the Appearance row showed the old language
        // See BuildThemeChoices
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
        // 两个开关不再带可见标签（标题与说明已说清它们管什么），名字补给读屏
        //
        // The two switches no longer carry a visible label (their titles and descriptions say what they control)
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
        // 同上：原因挂在卡片上，鼠标悬浮在卡片任意位置都能看到
        //
        // Same as the proxy box: the reason sits on the card, shown while hovering anywhere on it
        AboutCard.Hint = AppServices.UpdatesEnabled ? string.Empty : t("settings.devTooltip");
        GitHubBtn.Content = t("settings.github");
        SyncThemeSelection();
        SyncLanguageSelection();
    }
}
