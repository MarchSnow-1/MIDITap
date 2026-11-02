// BridgeInstallFlow.cs — MIDIBridge 组件的安装，以及安装过程与结果如何告知用户
//
// 主页的设备下拉与设置页都提供安装入口，两处共用这里，进度显示、成功/失败文案与日志记录才不会分叉
// 这里只负责"装一次 + 报告结果"，不碰页面状态：忙碌标记与按钮可用性仍由调用方决定
// 进度用弹窗承担，安装是一次性长任务，进度与结果正适合显示在同一处
//
// BridgeInstallFlow.cs — installing the MIDIBridge component, and how the process and its result reach the user
//
// Both the home page's device picker and the settings page offer an install entry point and share this class, so the
// progress display, the success/failure copy and the log record do not fork
// This owns "run one install and report the result" only; it touches no page state
// The busy flag and the button availability stay the caller's call
// Progress is presented in a dialog, an install being a one-shot long task whose progress and result belong together

using Microsoft.UI.Xaml;
using MIDITap.App.Dialogs;
using MIDITap.Core.Update;

namespace MIDITap.App.Services;

/// <summary>
/// 安装进度与结果的呈递者
/// 主页与设置页要一个独立的进度弹窗，而组件更新弹窗自己就是那块界面，进度与结果都显示在它里面
/// 两个入口经由同一层调用 InstallAsync 并呈现结果，文案只写一份
///
/// The presenter of the install's progress and result
/// The home page and the settings page want a dialog of their own, while the update dialog is itself that surface
/// and shows the progress and result inside it
/// Both entries call InstallAsync and present the result through this one layer, so the copy is written once
/// </summary>
public interface IBridgeInstallPresenter
{
    /// <summary>报告下载进度，取值 0 到 1 / Reports download progress, from 0 to 1</summary>
    void ShowProgress(double fraction);

    /// <summary>显示最终那一行结果 / Shows the final one-line result</summary>
    void ShowResult(string status);
}

public static class BridgeInstallFlow
{
    private static int _running;

    /// <summary>是否已有一轮安装在进行 / Whether an install run is already under way</summary>
    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// 跑一轮安装
    /// 返回 null 表示已有一轮在进行，本次什么都没做：两个入口共用同一个 guard，
    /// 因此用户在下载期间从另一处再点一次不会并发跑第二轮
    /// xamlRoot 用于承载进度弹窗，调用方传所在页面的那一个
    ///
    /// Runs one install
    /// A null return means a run is already in progress and nothing was done: both entry points share the guard,
    /// so a second click from elsewhere during a download cannot start a second run
    /// The xamlRoot hosts the progress dialog; the caller passes the one of the page it is on
    /// </summary>
    public static Task<BridgeInstallOutcome?> RunAsync(BridgeService bridge, XamlRoot xamlRoot)
        => RunAsync(bridge, xamlRoot, CancellationToken.None);

    /// <summary>
    /// 同上，但带上取消令牌：组件更新弹窗在下载中途被关掉时用它中止本轮
    ///
    /// The same, with a cancellation token: the update dialog uses it to abort the run when it is closed mid-download
    /// </summary>
    public static async Task<BridgeInstallOutcome?> RunAsync(
        BridgeService bridge, XamlRoot xamlRoot, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return null;
        }
        try
        {
            var dialog = new BridgeInstallProgressDialog { XamlRoot = xamlRoot };
            // ShowAsync 返回的 IAsyncOperation 不立刻 await，否则要等到用户关掉弹窗才继续，安装不会开始
            //
            // The operation ShowAsync returns is deliberately not awaited here, or the install would not begin
            // until the user closed the dialog
            var shown = dialog.ShowAsync();

            var result = await RunCoreAsync(bridge, dialog, cancellationToken);

            // 进度弹窗装完才允许关闭，因此这里等用户看完结果把窗关掉
            //
            // The progress dialog allows closing only after the result, so this waits for the user to read it and
            // close the window
            await shown;
            return result;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// 跑一轮安装，进度与结果交给调用方给的呈递者
    /// 返回 null 表示已有一轮在进行（与上面那个重载共用同一个 guard）
    ///
    /// Runs one install, handing progress and result to the caller's presenter
    /// A null return means a run is already under way (the same guard as the overload above)
    /// </summary>
    public static async Task<BridgeInstallOutcome?> RunAsync(
        BridgeService bridge, IBridgeInstallPresenter presenter, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return null;
        }
        try
        {
            return await RunCoreAsync(bridge, presenter, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static async Task<BridgeInstallOutcome> RunCoreAsync(
        BridgeService bridge, IBridgeInstallPresenter presenter, CancellationToken cancellationToken)
    {
        BridgeInstallOutcome result;
        try
        {
            result = await bridge.InstallAsync(
                new Progress<DownloadProgress>(p => presenter.ShowProgress(p.Fraction)), cancellationToken);
        }
        catch (Exception ex)
        {
            // 安装里未被捕获的异常若直接逃出去，界面只会看到一个没有解释的崩溃点
            // 这里兜住并转成用户能读的一条结果，同时把原始信息写进日志
            //
            // An uncaught exception escaping the install would leave the UI with an unexplained crash point
            // It is caught here and turned into one readable result, while the raw detail goes to the log
            AppServices.Log.Warn("bridge install threw: " + ex.Message);
            result = new BridgeInstallOutcome(false, ex.Message);
        }

        presenter.ShowResult(Describe(result));
        return result;
    }

    /// <summary>
    /// 把安装结果转成弹窗上的那一行
    /// 失败原因按枚举取本地化文案：原始的异常串对用户没有用，而"没有摘要所以拒绝"这类结论他能读懂并据以行动
    ///
    /// Turns the install result into the one line the dialog shows
    /// The failure is localised from the enum: a raw exception string is no use to the user, while a verdict
    /// such as "refused for want of a digest" is something they can understand and act on
    /// </summary>
    private static string Describe(BridgeInstallOutcome result)
    {
        if (result.Ok)
        {
            return AppServices.I18n.T("settings.bridge.installDone");
        }
        var text = AppServices.I18n.T(
            "settings.bridge.installFailed",
            ("detail", AppServices.I18n.T("settings.bridge.installFailure." + result.Failure)));
        AppServices.Log.Warn(text + " (" + (result.Detail ?? "no detail") + ")");
        return text;
    }
}
