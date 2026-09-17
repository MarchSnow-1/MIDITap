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
    private CancellationTokenSource? _cts;

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
        NotesExpander.Visibility = string.IsNullOrWhiteSpace(_info.Notes)
            ? Visibility.Collapsed
            : Visibility.Visible;

        NeverRemindBtn.Visibility = showNeverRemind ? Visibility.Visible : Visibility.Collapsed;

        RefreshTexts();
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        UpdateService.StageChanged += OnStageChanged;
        AppServices.I18n.LanguageChanged += RefreshTexts;
        RenderState();
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        // 退订：弹窗按需重建，不退订会让静态事件一直持有它
        // Unsubscribe: the dialog is rebuilt on demand, so a retained static handler would leak it
        UpdateService.StageChanged -= OnStageChanged;
        AppServices.I18n.LanguageChanged -= RefreshTexts;
        _cts?.Dispose();
        _cts = null;
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
        // 下载中点叉号 = 取消。取消要真的把下载停下来并丢弃已下载的内容
        // 否则用户会留下一个"看起来取消了、磁盘上却躺着几百 MB"的暂存区
        //
        // The cross during a download means cancel, and cancelling must actually stop the download and discard it
        // Otherwise the user ends up with a staging area holding hundreds of MB after a cancel that looked complete
        CancelDownloadIfRunning();
        CloseAfter(ignored: false, neverRemind: false);
    }

    private void CancelDownloadIfRunning()
    {
        if (UpdateService.Stage is UpdateStage.Downloading or UpdateStage.Extracting)
        {
            _cts?.Cancel();
            UpdateService.CleanStaging();
            UpdateService.Reset();
        }
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
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        await UpdateService.PrepareAsync(_info, _cts.Token);
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
                ProgressArea.Visibility = Visibility.Visible;
                StatusText.Text = AppServices.I18n.T("update.downloading.pct",
                    ("pct", Math.Round(UpdateService.Progress * 100).ToString("F0")));
                ProgressBar.Visibility = Visibility.Visible;
                ProgressBar.Value = UpdateService.Progress * 100;
                SetActionsEnabled(false);
                break;

            case UpdateStage.Extracting:
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
                break;

            case UpdateStage.Failed:
                ProgressArea.Visibility = Visibility.Visible;
                StatusText.Text = t("update.failed") + ": " + (UpdateService.LastError ?? string.Empty);
                ProgressBar.Visibility = Visibility.Collapsed;
                SetActionsEnabled(true);
                ActionBtn.Content = t("update.download");
                break;

            default:
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

    private void RefreshTexts()
    {
        Func<string, string> t = AppServices.I18n.T;
        TitleText.Text = t("update.title");
        CurrentLabel.Text = t("update.current");
        CurrentValue.Text = _info.Current;
        LatestLabel.Text = t("update.latest");
        LatestValue.Text = _info.Latest;
        NotesExpander.Header = t("update.notes");
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
