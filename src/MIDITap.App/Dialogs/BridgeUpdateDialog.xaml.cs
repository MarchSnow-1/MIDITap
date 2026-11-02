// BridgeUpdateDialog.xaml.cs — MIDIBridge 组件有新版本时的弹窗
//
// 为什么取日志要单独发一次请求：清单只给版本与产物，发布说明的 markdown 原文只在 GitHub API 的 body 里
// 因此弹窗一打开就去取，用户在看版本号的同时内容已经在路上；取不到也不阻塞别的事，只说明为什么看不到
//
// 为什么装完不重启也不暂存：组件是一个独立的可执行文件，安装就是把新文件换上去，下一次连它就是新版本
// 这正是它不能复用 UpdateService 那套状态机的原因
//
// 关闭行为：下载期间不关（关掉不会取消下载，只会让用户看不到结果），叉号会先取消本轮再关
// 取消由 BridgeInstaller 负责清理 .part，界面不参与判断
//
// BridgeUpdateDialog.xaml.cs — the dialog shown when the MIDIBridge component has a new version
//
// Why the notes cost a separate request: the manifest carries the version and the artifact, while the markdown
// source of the release notes exists only in the GitHub API body
// It is therefore fetched the moment the dialog opens, so the body is already on its way while the user reads the
// version numbers; a failure does not block anything else and is merely explained
//
// Why the install neither restarts nor stages: the component is a separate executable, so installing means putting
// the new file in place and the next launch of it is the new version
// That is exactly why this dialog cannot reuse UpdateService's state machine
//
// Closing: not during a download (closing would not cancel it, only hide the result), and the cross cancels the run
// before closing
// The cancellation and the .part cleanup belong to BridgeInstaller, not the UI

using MIDITap.App.Services;
using MIDITap.Core.Bridge;
using MIDITap.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace MIDITap.App.Dialogs;

public sealed partial class BridgeUpdateDialog : ContentDialog, IBridgeInstallPresenter
{
    private readonly BridgeUpdateGateResult _gate;

    // 只有本文件主动发起的关闭才放行，见类注释
    // Only a close started by this file is allowed through; see the class comment
    private bool _allowClose;

    // 本轮下载的取消源：叉号与关闭都要先把它掉，否则下载会在弹窗关掉之后继续跑完
    // 取消后由 BridgeInstaller 删掉 .part，界面不做清理
    //
    // The cancellation source of this round: both the cross and closing trigger it first, or the download would
    // keep running to completion after the dialog is gone
    // BridgeInstaller deletes the .part on cancel, so the UI cleans nothing up
    private readonly CancellationTokenSource _cts = new();

    // 取回来的发布说明；null 表示还在路上
    // The fetched release notes; null means still on its way
    private BridgeReleaseNotes? _notes;

    // 本轮是否已经装好：装好后主按钮变成「关闭」
    // Whether this round has installed: the primary button becomes "close" once it has
    private bool _installed;

    // 正在安装：期间主按钮置灰，避免同一次装两遍
    // Installing: the primary button is greyed out meanwhile, so one round cannot start twice
    private bool _busy;

    public BridgeUpdateDialog(BridgeUpdateGateResult gate)
    {
        InitializeComponent();
        _gate = gate;

        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;

        RefreshTexts();
    }

    /// <summary>发布页地址：门禁给的那个，缺了退回发布列表 / The release page address, falling back to the releases list</summary>
    private string ReleaseUrl =>
        string.IsNullOrEmpty(_gate.RemoteReleaseUrl) ? BridgeRepository.ReleasesBase : _gate.RemoteReleaseUrl;

    /// <summary>
    /// 这次更新能不能由本程序自己装上
    /// 清单路径要产物与摘要都在：没有摘要就无法校验，装着不校验的文件比不装更糟
    /// API 回退路径只按命名选资产，摘要另在资产里取，因此有资产即可
    ///
    /// Whether this update can be installed by the app itself
    /// The manifest path needs both the artifact and its digest: without a digest nothing can be verified, and
    /// running an unverified file is worse than not installing it
    /// The API fallback picks the asset by name and takes the digest from the asset, so having an asset is enough
    /// </summary>
    private bool CanInstall =>
        _gate.Artifact is { Sha256: not null } || _gate.Asset is not null;

    private async void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.I18n.LanguageChanged += RefreshTexts;

        // 日志与"有没有更新"是两件事：判定早已做完，这里只为把内容显示出来
        // 代理沿用更新选项，用户不必为组件再配一次
        //
        // The notes and "is there an update" are two questions: the verdict is already in, and this only fetches the
        // content to show
        // The proxy comes from the update options, so no second one has to be configured for the component
        try
        {
            _notes = await BridgeReleaseInfo.FetchAsync(
                _gate.RemoteVersion, _gate.RemoteReleaseUrl, AppServices.Backend.UpdateOptions);
        }
        catch (Exception ex)
        {
            // 取不到就是取不到：正文里只剩发布页入口，功能本身不受影响
            // A failure is just that: only the release-page entry remains in the body, and the feature is unaffected
            AppServices.Log.Warn("bridge release notes threw: " + ex.Message);
        }

        RenderNotes();
        RenderState();
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        // 退订：弹窗按需重建，不退订会让静态事件一直持有它
        // Unsubscribe: the dialog is rebuilt on demand, so a retained static handler would leak it
        AppServices.I18n.LanguageChanged -= RefreshTexts;

        // 关窗之后再没人等这次下载了，连同取消源一起释放
        // Nothing waits for this download after the window is gone, so the cancellation source goes with it
        _cts.Dispose();
    }

    /// <summary>阻止一切不是本文件发起的关闭（ESC、系统返回键）/ Blocks every close this file did not start (ESC, system back)</summary>
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_allowClose)
        {
            args.Cancel = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 先取消再关：关掉窗口不会自己停下下载，而安装无法中途回滚
        // BridgeInstaller 收到取消会删掉 .part 并返回 Cancelled，因此不会在 .update 里留半个文件
        //
        // Cancel first, then close: closing the window does not stop the download by itself, and an install cannot be
        // rolled back half way
        // BridgeInstaller deletes the .part on cancellation and returns Cancelled, so no half file is left behind
        if (_busy)
        {
            _cts.Cancel();
        }
        _allowClose = true;
        Hide();
    }

    private void OnOpenPageClick(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        // 正文最前面那行发布页链接：完整说明（含图片与折叠块）只有网页上有
        // The release-page link at the top of the body: the full notes, images and collapsed blocks included, exist
        // only there
        AppServices.Backend.OpenUrl(ReleaseUrl);
    }

    /// <summary>
    /// 表头被点击：直接显隐内容并换箭头方向，全程只改 Visibility
    ///
    /// The header was clicked: shows or hides the content outright and points the chevron the right way; nothing but
    /// Visibility changes
    /// </summary>
    private void OnNotesToggled(object sender, RoutedEventArgs e)
    {
        var expanded = NotesBodyHost.Visibility != Visibility.Visible;
        NotesBodyHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        NotesChevron.Glyph = expanded ? "\uE70E" : "\uE70D";
    }

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_installed)
        {
            _allowClose = true;
            Hide();
            return;
        }

        if (!CanInstall)
        {
            // 这个版本没有可自动安装的产物：把用户送到发布页，由他自行处理
            // That release carries nothing this app can install, so the user is sent to the release page
            AppServices.Backend.OpenUrl(ReleaseUrl);
            return;
        }

        _busy = true;
        ProgressArea.Visibility = Visibility.Visible;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        ActionBtn.IsEnabled = false;
        ActionBtn.Content = AppServices.I18n.T("settings.bridge.installing");
        CloseBtn.IsEnabled = false;

        BridgeInstallOutcome? outcome;
        try
        {
            // 呈递者就是本弹窗：进度与结果都显示在这块界面上，不另起一个进度弹窗
            //
            // This dialog is the presenter: progress and result show right here rather than in a second dialog
            outcome = await BridgeInstallFlow.RunAsync(AppServices.Bridge, this, _cts.Token);
        }
        finally
        {
            _busy = false;
            CloseBtn.IsEnabled = true;
            ActionBtn.IsEnabled = true;
        }

        if (outcome is null)
        {
            // 已有一轮安装在跑（例如用户从设置页的另一个入口同时开始），本轮什么都没做
            // A run is already under way (for instance started from the other entry on the settings page), so
            // nothing happened here
            RenderState();
            return;
        }

        if (outcome.Ok)
        {
            _installed = true;

            // 装完只作废了缓存，重新探一次才能让"当前版本"这一行说对新装的是哪个版本
            // The install only invalidates the cache, so one more probe is needed before the "current" line can name
            // the build that just landed
            try
            {
                await AppServices.Bridge.RefreshInstalledMetadataAsync();
            }
            catch (Exception ex)
            {
                AppServices.Log.Warn("bridge metadata probe threw: " + ex.Message);
            }
        }

        RenderState();
    }

    /// <summary>
    /// 日志正文的显示
    /// 没取到时区分两种情形：API 失败（可能带配额，能说清什么时候能再试）与该版本本来就没写说明
    ///
    /// How the notes body is shown
    /// An absent body splits into two cases: the API failed (possibly with quota information, which can say when to
    /// try again) and the release simply having no notes
    /// </summary>
    private void RenderNotes()
    {
        var notes = _notes;
        var hasNotes = !string.IsNullOrWhiteSpace(notes?.Notes);
        NotesMd.Text = hasNotes ? notes!.Notes! : string.Empty;
        NotesMd.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;

        // 卡片始终显示：发布页入口在正文里，没有 markdown 时也得有地方放
        // The card is always shown: the release-page entry lives in the body, and needs a place even with no markdown
        NotesCard.Visibility = Visibility.Visible;
        NotesUnavailableText.Visibility = hasNotes ? Visibility.Collapsed : Visibility.Visible;
        NotesUnavailableText.Text = hasNotes ? string.Empty : DescribeUnavailableNotes();
    }

    /// <summary>
    /// 预览不可用时的一句说明
    /// 失败若带着配额信息，就能说明"什么时候能再试"，那比只说"看不了"有用
    ///
    /// The one line shown when the preview is unavailable
    /// A failure carrying quota information can say when to try again, which beats merely saying it is missing
    /// </summary>
    private string DescribeUnavailableNotes()
    {
        var quota = _notes?.RateLimit;
        var reset = quota?.Reset;
        var now = DateTimeOffset.Now;

        // 重置时间已经过去时不再报时间，此刻配额其实已经恢复
        // A reset time in the past is not reported, since the quota has in fact been restored by then
        if (reset is null || reset <= now)
        {
            return AppServices.I18n.T("update.notes.unavailable");
        }

        // 不足一分钟也报 1，否则显示出来是"还有 0 分钟"
        // Anything under a minute still reads as 1, so the text does not come out as "in 0 minutes"
        var minutes = Math.Max(1, (int)Math.Ceiling((reset.Value - now).TotalMinutes));
        var time = reset.Value.ToString("HH:mm");

        if (quota!.Remaining is not null && quota.Limit is not null)
        {
            return AppServices.I18n.T("update.notes.rateLimit",
                ("remaining", quota.Remaining.Value.ToString()),
                ("limit", quota.Limit.Value.ToString()),
                ("reset", time),
                ("minutes", minutes.ToString()));
        }

        return AppServices.I18n.T("update.notes.rateLimitNoQuota",
            ("reset", time), ("minutes", minutes.ToString()));
    }

    /// <summary>把主按钮与进度区同步到当前状态 / Syncs the primary button and the progress area to the current state</summary>
    private void RenderState()
    {
        if (_installed)
        {
            // 装好之后只剩"关掉"这一条有意义的路
            // Once installed, only closing is left that means anything
            ProgressArea.Visibility = Visibility.Visible;
            ProgressBar.Visibility = Visibility.Collapsed;
            ActionBtn.Content = AppServices.I18n.T("update.close");
            return;
        }

        if (_busy)
        {
            // 下载与安装期间按钮由 OnActionClick 置灰并改文案，这里不动它
            // During the download and install the button is greyed out and relabelled by OnActionClick; it is left
            // alone here
            return;
        }

        ProgressArea.Visibility = Visibility.Collapsed;
        ActionBtn.Content = CanInstall
            ? AppServices.I18n.T("update.download")
            : AppServices.I18n.T("bridge.update.openRelease");
    }

    /// <summary>报告下载进度 / Reports download progress</summary>
    public void ShowProgress(double fraction)
    {
        var clamped = Math.Clamp(fraction, 0, 1);
        StatusText.Text = AppServices.I18n.T(
            "settings.bridge.installingPct", ("pct", ((int)(clamped * 100)).ToString()));
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = clamped * 100;
    }

    /// <summary>显示安装结束后的那一行结果 / Shows the one-line result once the install is over</summary>
    public void ShowResult(string status) => StatusText.Text = status;

    private void RefreshTexts()
    {
        Func<string, string> t = AppServices.I18n.T;
        TitleText.Text = t("bridge.update.title");
        CurrentLabel.Text = t("update.current");
        LatestLabel.Text = t("update.latest");
        // 版本号统一成不带 v 的形式：已装版本来自探测（1.3.3），远端来自 GitHub 的 tag（v1.3.3）
        // 两个来源不一致，直接显示会出现前后不齐的写法
        //
        // Versions are normalised to the no-v form: the installed one comes from a probe (1.3.3) and the remote one
        // from a GitHub tag (v1.3.3)
        // Showing them as they come would mix the two forms
        CurrentValue.Text = _installed
            ? StripVersionPrefix(_gate.RemoteVersion ?? string.Empty)
            : StripVersionPrefix(_gate.InstalledVersion ?? t("settings.bridge.status.unknown"));
        LatestValue.Text = StripVersionPrefix(_gate.RemoteVersion ?? string.Empty);

        NotesHeaderText.Text = t("update.notes");
        // 表头内容是自绘的，自动化名称取不到里面的文字，因此显式给出
        // The header is drawn by hand, so its text is not picked up as an automation name; it is set explicitly
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NotesToggle, t("update.notes"));

        // 发布页入口的文字随语言变，地址不随语言变，因此分开设
        // The release-page label follows the UI language while the address does not, so the two are set apart
        ReleasePageLabel.Text = t("update.notes.releasePage");
        ReleasePageLink.Inlines.Clear();
        ReleasePageLink.Inlines.Add(new Run { Text = ReleaseUrl });
        ReleasePageLink.NavigateUri = new Uri(ReleaseUrl, UriKind.Absolute);

        // 叉号只有图标，必须给一个可读名称：屏幕阅读器与自动化测试都靠它定位
        // The cross carries an icon only, so it needs a readable name; screen readers and automated tests both rely
        // on it to find the control
        ToolTipService.SetToolTip(CloseBtn, t("update.close"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CloseBtn, t("update.close"));

        RenderNotes();
        RenderState();
    }

    /// <summary>
    /// 去掉版本号前的 v（GitHub 的 tag 带，探测回来的版本不带）
    /// 只去开头那一个，版本号内部若出现 v 不动它
    ///
    /// Drops a leading v from a version (GitHub tags carry one, probed versions do not)
    /// Only the leading one is removed; a v inside the version is left alone
    /// </summary>
    private static string StripVersionPrefix(string version)
        => version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version[1..] : version;
}
