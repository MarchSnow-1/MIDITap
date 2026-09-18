// UpdateDialog.xaml.cs — 发现新版本的灰色弹窗
//
// 为什么它取代了右上角浮窗：浮窗只能提示，真正的"下载并安装"在设置页里
// 而设置页那行会在启动检查发现新版本时凭空出现，与用户当时在哪个页面无关
// 弹窗把提示与操作合到一处，用户在哪一页都能直接完成更新
//
// 关闭约束：ContentDialog 点深灰遮罩本来就关不掉（它没有该行为），但 ESC 与系统返回键会关
// 因此 OnClosing 拦下所有不是本文件主动发起的关闭，唯一出口是右上角叉号与三个操作按钮
//
// UpdateDialog.xaml.cs — the grey dialog shown when a new version is found
//
// Why it replaces the top-right toast: the toast could only announce, while the actual
// "download and install" lived on the settings page. That row also appeared out of nowhere
// when the startup check found a version, regardless of the page the user was on
// The dialog keeps the announcement and the action together, so an update can be done from anywhere
//
// Closing: clicking the scrim does not close a ContentDialog to begin with (it has no such behaviour),
// but ESC and the system back button do. OnClosing therefore blocks every close this file did not start.
// The only exits are the top-right cross and the three action buttons

using CommunityToolkit.WinUI.UI.Controls;
using MIDITap.App.Services;
using MIDITap.Core.Settings;
using MIDITap.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Dialogs;

public sealed partial class UpdateDialog : ContentDialog
{
    private readonly UpdateInfo _info;
    private readonly bool _showNeverRemind;

    // 只有本文件主动发起的关闭才放行，见类注释
    // Only a close started by this file is allowed through; see the class comment
    private bool _allowClose;

    /// <summary>用户是否点了「忽略此版本」 / Whether the user chose "ignore this version"</summary>
    public bool IgnoredVersion { get; private set; }

    /// <summary>用户是否点了「不再提醒更新」 / Whether the user chose "do not remind me again"</summary>
    public bool NeverRemind { get; private set; }

    /// <param name="showNeverRemind">
    /// 是否显示「不再提醒更新」小字：仅启动时自动弹出的那个窗口为 true
    /// 从设置页手动检查后弹出的不带它 —— 手动检查本身就是用户主动要看结果
    ///
    /// Whether to show the "do not remind me again" link: true only for the dialog raised automatically at startup
    /// The one opened from a manual check on the settings page omits it, since that check is the user asking for the result
    /// </param>
    public UpdateDialog(UpdateInfo info, bool showNeverRemind)
    {
        InitializeComponent();
        _info = info;
        _showNeverRemind = showNeverRemind;

        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;

        // 更新内容的 markdown 直接交给控件渲染
        // 原始 HTML（例如居中用的 <div>、<img> 与 <details>）不在它的支持范围内，会以字面文本出现
        // 这是所选控件的边界，因此发布说明以纯 markdown 书写时呈现最完整
        //
        // The markdown of the release notes goes straight to the control
        // Raw HTML such as a centring <div>, <img> and <details> is outside what it supports and shows up as literal text
        // That is the boundary of the chosen control, so notes written in plain markdown render best
        NotesMd.Text = _info.Notes ?? string.Empty;
        var hasNotes = !string.IsNullOrWhiteSpace(_info.Notes);
        NotesCard.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;

        // 没有发布说明时区分两种情形，而不是一律留白
        //   * 走了网页回退：GitHub 只给渲染后的 HTML，说明预览不可用并给出发布页入口
        //   * 该版本本来就没写说明：什么都不显示，那是正常情况
        //
        // Absent notes are split into two cases rather than left blank
        //   * the web fallback was used: GitHub served rendered HTML only, so say the preview is unavailable
        //     and offer the release page
        //   * the release simply has no notes: show nothing, which is normal
        NotesUnavailableText.Visibility = !hasNotes && _info.ViaFallback
            ? Visibility.Visible
            : Visibility.Collapsed;

        NeverRemindBtn.Visibility = showNeverRemind ? Visibility.Visible : Visibility.Collapsed;

        RefreshTexts();
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        UpdateService.StageChanged += OnStageChanged;
        // 进度变化单独订阅：它不动阶段，只订阅阶段的话进度条只会在 0% 与 100% 各画一次
        //
        // Progress is subscribed separately: it does not move the stage, and a stage-only subscription
        // would paint the bar just twice, at 0% and 100%
        UpdateService.ProgressChanged += OnStageChanged;
        AppServices.I18n.LanguageChanged += RefreshTexts;
        RenderState();
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        // 退订：弹窗按需重建，不退订会让静态事件一直持有它
        // Unsubscribe: the dialog is rebuilt on demand, so a retained static handler would leak it
        UpdateService.StageChanged -= OnStageChanged;
        UpdateService.ProgressChanged -= OnStageChanged;
        AppServices.I18n.LanguageChanged -= RefreshTexts;
    }

    private void OnStageChanged() => AppServices.RunOnUi(RenderState);

    /// <summary>阻止一切不是本文件发起的关闭（ESC、系统返回键）/ Blocks every close this file did not start (ESC, system back)</summary>
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
        }
    }

    private void CloseAfter(bool ignored, bool neverRemind)
    {
        IgnoredVersion = ignored;
        NeverRemind = neverRemind;
        _allowClose = true;
        Hide();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 关窗即放弃本轮更新，取消要真的把下载停下来并丢弃已下载的内容
        // 否则用户会留下一个"看起来取消了、磁盘上却躺着几百 MB"的暂存区
        // 取消与清理的先后顺序由 UpdateService 负责，界面不参与判断，理由见 UpdateRun 的文件头
        // 这里不等结果，窗口要立刻关掉，清理在后台完成
        //
        // Closing gives up this round, and that has to actually stop the download and discard it
        // Otherwise the user ends up with a staging area holding hundreds of MB after a cancel that looked complete
        // The ordering between cancelling and cleaning belongs to UpdateService, not the UI; UpdateRun explains why
        // The result is not awaited because the window has to close at once, so the cleanup finishes in the background
        _ = UpdateService.DiscardAsync();
        CloseAfter(ignored: false, neverRemind: false);
    }

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        // 预览不可用时的出口：把用户送到发布页，说明原文在那里
        //
        // The way out when the preview is unavailable: send the user to the release page, where the notes are
        AppServices.Backend.OpenUrl(_info.Url);
    }

    private void OnIgnoreClick(object sender, RoutedEventArgs e)
    {
        // 忽略只针对这一个版本：下一个版本的弹窗照常出现
        // Ignoring covers this version alone: the next version still raises the dialog
        CloseAfter(ignored: true, neverRemind: false);
    }

    private void OnNeverRemindClick(object sender, RoutedEventArgs e)
    {
        // 与设置页的「启动时检查更新」开关是同一个开关：那才是这件事唯一的事实来源
        // 另建一个无声标志会让用户无从发现怎么恢复
        //
        // This is the same switch as "check for updates on startup" on the settings page, which is the single source of truth
        // A separate silent flag would leave the user with no way to find out how to restore it
        AppStorage.SaveAutoCheckUpdates(AppServices.BaseDir, false);
        CloseAfter(ignored: false, neverRemind: true);
    }

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Stage == UpdateStage.ReadyToRestart)
        {
            // 交给辅助脚本并退出：脚本会等待本进程结束，所以必须真的关掉窗口
            //
            // Hand off to the helper and exit: it waits for this process to end, so the window must really close
            if (UpdateService.LaunchApplyAndExit())
            {
                _allowClose = true;
                App.MainWindowInstance?.Close();
            }
            else
            {
                RenderState();
            }
            return;
        }

        if (_info.Asset is null)
        {
            // 该版本没有本平台的包：退回 Releases 页面，由用户自行下载
            // No package for this platform in that release: fall back to the Releases page
            AppServices.Backend.OpenUrl(UpdateChecker.ReleasesUrl);
            return;
        }

        RenderState();
        await UpdateService.PrepareAsync(_info);
        RenderState();
    }

    /// <summary>把界面同步到 UpdateService 的当前阶段 / Syncs the UI to the current UpdateService stage</summary>
    private void RenderState()
    {
        // 显式写委托类型：T 有单参与 params 两个重载，var 推断不出是哪一个
        // 需要插值的那几处直接调 AppServices.I18n.T
        //
        // The delegate type is explicit: T has both a single-argument and a params overload, so var cannot pick one
        // The few interpolating calls go through AppServices.I18n.T directly
        Func<string, string> t = AppServices.I18n.T;
        switch (UpdateService.Stage)
        {
            case UpdateStage.Downloading:
                RestoreDismissAffordances();
                ProgressArea.Visibility = Visibility.Visible;
                StatusText.Text = AppServices.I18n.T("update.downloading.pct",
                    ("pct", Math.Round(UpdateService.Progress * 100).ToString("F0")));
                ProgressBar.Visibility = Visibility.Visible;
                ProgressBar.Value = UpdateService.Progress * 100;
                SetActionsEnabled(false);
                break;

            case UpdateStage.Extracting:
                RestoreDismissAffordances();
                // 解压没有可用的百分比：包已经下完，这一步在磁盘上做
                // Extraction has no percentage to show: the package is fully downloaded and this step is disk work
                ProgressArea.Visibility = Visibility.Visible;
                StatusText.Text = t("update.extracting");
                ProgressBar.Visibility = Visibility.Collapsed;
                SetActionsEnabled(false);
                break;

            case UpdateStage.ReadyToRestart:
                ProgressArea.Visibility = Visibility.Visible;
                StatusText.Text = t("update.ready.hint");
                ProgressBar.Visibility = Visibility.Collapsed;
                SetActionsEnabled(true);
                ActionBtn.Content = t("update.restart");
                // 包已经躺在磁盘上，此时只剩"重启"这一条有意义的路
                // 因此收起叉号与「忽略此版本」：在这个状态下关掉窗口只会把已下载好的更新搁在那里，
                // 而用户下次启动仍会看到同一个窗口，白下一次；「忽略」更是会让他从此错过这个版本
                //
                // The package is on disk, so only "restart" is a meaningful path at this point
                // The cross and "ignore this version" are therefore withdrawn
                // Closing the window here would merely park a finished download, and the next launch would show
                // the same window again after a needless re-download
                // Ignoring would make the user miss this version for good
                CloseBtn.Visibility = Visibility.Collapsed;
                IgnoreBtn.Visibility = Visibility.Collapsed;
                break;

            case UpdateStage.Failed:
                RestoreDismissAffordances();
                ProgressArea.Visibility = Visibility.Visible;
                StatusText.Text = t("update.failed") + ": " + (UpdateService.LastError ?? string.Empty);
                ProgressBar.Visibility = Visibility.Collapsed;
                SetActionsEnabled(true);
                ActionBtn.Content = t("update.download");
                break;

            default:
                RestoreDismissAffordances();
                ProgressArea.Visibility = Visibility.Collapsed;
                SetActionsEnabled(true);
                ActionBtn.Content = t("update.download");
                break;
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        ActionBtn.IsEnabled = enabled;
        IgnoreBtn.IsEnabled = enabled;
    }

    /// <summary>
    /// 恢复叉号与「忽略此版本」的显示（离开"将重启更新"这一阶段时调用）
    /// 它们在"将重启更新"时被收起，而失败后重试、或取消下载回到空闲时都要重新出现，
    /// 否则用户会被困在一个既不能关也不能忽略的窗口里
    ///
    /// Restores the cross and "ignore this version" (called when leaving the "restart to update" stage)
    /// They are withdrawn there, and must come back after a failure, a retry, or a cancelled download,
    /// or the user would be stuck in a window that can neither be closed nor ignored
    /// </summary>
    private void RestoreDismissAffordances()
    {
        CloseBtn.Visibility = Visibility.Visible;
        IgnoreBtn.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 去掉版本号前的 v（GitHub 的 tag 带，程序集版本不带）
    /// 只去开头那一个，版本号内部若出现 v 不动它
    ///
    /// Drops a leading v from a version (GitHub tags carry one, assembly versions do not)
    /// Only the leading one is removed; a v inside the version is left alone
    /// </summary>
    private static string StripVersionPrefix(string version)
        => version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version[1..] : version;

    /// <summary>
    /// 表头被点击：直接显隐内容，并把箭头换成对应的方向
    /// 全程只改 Visibility，不涉及任何 Storyboard —— 高度在同一次布局里就切换完成，没有过渡
    ///
    /// The header was clicked: shows or hides the content outright and points the chevron the right way
    /// Nothing but Visibility changes, with no storyboard involved, so the height switches within one layout pass
    /// </summary>
    private void OnNotesToggled(object sender, RoutedEventArgs e)
    {
        // 以当前可见性取反，而不是读某个勾选状态
        // 这样切换的依据只有一处事实来源，不会出现"状态说展开了、界面却收着"的分歧
        //
        // The new state is the inverse of what is on screen rather than a checked flag read back
        // That leaves one source of truth, so the display cannot disagree with the state
        var expanded = NotesBodyHost.Visibility != Visibility.Visible;
        NotesBodyHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        // 箭头方向：向下表示"点了会展开"，向上表示"点了会收起"
        // 字形用字面量而不是主题资源：省一次资源查找，也避免资源名拼错时整段 XAML 加载失败
        //
        // Chevron direction: down means "clicking expands", up means "clicking collapses"
        // The glyph is a literal rather than a theme resource: one fewer lookup, and a mistyped resource name
        // would fail the whole XAML load
        NotesChevron.Glyph = expanded ? "\uE70E" : "\uE70D";
    }

    private void RefreshTexts()
    {
        Func<string, string> t = AppServices.I18n.T;
        TitleText.Text = t("update.title");
        CurrentLabel.Text = t("update.current");
        // 版本号显示时去掉 v 前缀
        // Current 来自程序集版本（1.9.0，本就没有 v），Latest 来自 GitHub 的 tag（v2.0.0）
        // 两个来源不一致，直接显示会出现"1.9.0 与 v2.0.0"这种前后不齐的写法
        // 这里统一成不带 v 的形式，与「关于」区里那一行版本号保持一致
        //
        // The v prefix is dropped for display
        // Current comes from the assembly version (1.9.0, no v) and Latest from the GitHub tag (v2.0.0)
        // Showing both as they come would put "1.9.0" next to "v2.0.0"
        // They are normalised here to the no-v form, matching the version line under About
        CurrentValue.Text = StripVersionPrefix(_info.Current);
        LatestLabel.Text = t("update.latest");
        LatestValue.Text = StripVersionPrefix(_info.Latest);
        NotesHeaderText.Text = t("update.notes");
        // 表头内容是自绘的，自动化名称取不到里面的文字，因此显式给出
        // 屏幕阅读器与自动化测试都靠它定位这个控件
        //
        // The header is drawn by hand, so its text is not picked up as an automation name; it is set explicitly
        // Screen readers and automated tests both rely on it to find this control
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NotesToggle, t("update.notes"));
        NotesUnavailableText.Text = t("update.notes.unavailable");
        PageBtn.Content = t("update.notes.viewOnPage");
        IgnoreBtn.Content = t("update.ignoreVersion");
        NeverRemindBtn.Content = t("update.neverRemind");
        ToolTipService.SetToolTip(CloseBtn, t("update.close"));
        // 叉号只有图标，必须给一个可读名称：屏幕阅读器与自动化测试都靠它定位
        // The cross carries an icon only, so it needs a readable name
        // Screen readers and automated tests both rely on it to find the control
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CloseBtn, t("update.close"));
        RenderState();
    }
}
