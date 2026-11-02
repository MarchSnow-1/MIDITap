// BridgeInstallProgressDialog.xaml.cs — MIDIBridge 组件安装时的进度弹窗
//
// 下载期间不允许关闭：关掉弹窗并不会取消下载，只会让用户看不到结果，而安装本身无法中途回滚
// 因此 OnClosing 一直拦到 ShowResult 之后
//
// 本类实现 IBridgeInstallPresenter，好让 BridgeInstallFlow 在主页与设置页这条路径上把它当作界面
// 组件更新弹窗本身也是呈递者，走的是同一个重载
//
// BridgeInstallProgressDialog.xaml.cs — the progress dialog shown while the MIDIBridge component installs
//
// Closing is not allowed while downloading: closing would not cancel the download, it would only hide the
// result from the user, and the install itself cannot be rolled back half way; OnClosing therefore stays
// blocked until ShowResult runs
//
// This class implements IBridgeInstallPresenter so BridgeInstallFlow can treat it as the surface on the
// home-page and settings-page path; the bridge update dialog is a presenter too and goes through the same
// overload

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MIDITap.App.Services;

namespace MIDITap.App.Dialogs;

public sealed partial class BridgeInstallProgressDialog : ContentDialog, IBridgeInstallPresenter
{
    /// <summary>是否已经可以关闭：由 ShowResult 置位，之前一律拦下 / Whether closing is allowed yet, set by ShowResult and blocked before that</summary>
    private bool _allowClose;

    public BridgeInstallProgressDialog()
    {
        InitializeComponent();
        // 文案在代码里取而不写死在 XAML 里：语言在运行期可切换
        //
        // The strings are read here rather than hard-coded in XAML because the language can change at runtime
        TitleText.Text = AppServices.I18n.T("bridge.installProgress.title");
        StatusText.Text = AppServices.I18n.T("settings.bridge.installing");
        CloseButtonStyle = (Style)Resources["BridgeProgressCloseStyle"];
    }

    /// <summary>
    /// 报告下载进度
    /// fraction 取 0 到 1，超出范围的按边界夹住：进度条只是指示，不应因为上游多报一点就跑到头
    ///
    /// Reports download progress
    /// The fraction runs from 0 to 1 and is clamped at the bounds: the bar only indicates, and an
    /// over-report from upstream must not push it past the end
    /// </summary>
    public void ShowProgress(double fraction)
    {
        var clamped = Math.Clamp(fraction, 0, 1);
        StatusText.Text = AppServices.I18n.T(
            "settings.bridge.installingPct",
            ("pct", ((int)(clamped * 100)).ToString()));
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = clamped * 100;
    }

    /// <summary>
    /// 把界面切到最终状态并放开关闭
    /// 进度条整条收起：装完之后它停在哪里都没有意义，留着反而像是还没跑完
    ///
    /// Switches the dialog to its final state and allows closing
    /// The bar is withdrawn outright: where it stopped means nothing once the install is over, and
    /// leaving it there reads as still running
    /// </summary>
    public void ShowResult(string status)
    {
        StatusText.Text = status;
        ProgressBar.Visibility = Visibility.Collapsed;
        _allowClose = true;
        CloseButtonText = AppServices.I18n.T("bridge.installProgress.close");
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // 下载中途按 ESC 会走到这里：拦下来，弹窗一直留到结果出来
        //
        // ESC pressed mid-download lands here: it is cancelled so the dialog stays until the result is in
        if (!_allowClose)
        {
            args.Cancel = true;
        }
    }
}
