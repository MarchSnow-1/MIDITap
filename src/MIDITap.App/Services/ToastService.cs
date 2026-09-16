// ToastService.cs — 应用级的右上角浮窗提示
//
// 为什么不用系统 Toast：unpackaged（便携）应用没有 AppUserModelID，发系统通知要额外注册快捷方式，与"单文件便携 exe"的定位冲突
// 而且系统 Toast 会离开应用窗口，用户在应用里操作时反而要去通知中心找
// 这里用应用内浮层，跟随窗口、样式可控
//
// 只用于"用户主动操作的结果"与"重要系统事件"（更新、错误、明确的操作警告）
// 演奏过程中的高频事件（重复按下、异常抬起）**绝不**浮窗 —— 那会在弹奏时刷屏
//
// ToastService.cs — app-level toast notifications in the top-right corner
//
// Why not a system toast: an unpackaged (portable) app has no AppUserModelID
// Sending a system notification would require registering a shortcut first
// That conflicts with the "single-file portable exe" positioning
// A system toast would also leave the app window
// So while working in the app the user would have to go to the notification centre instead
// An in-app overlay is used here: it follows the window, and its styling stays under our control
//
// It is used only for "the result of a user's own action" and "important system events"
// Those are updates, errors and clear operational warnings
// High-frequency events during playing (duplicate note-on, unexpected note-off) are not shown as toasts
// That would flood the screen while the user plays

using MIDITap.Core.Notifications;

namespace MIDITap.App.Services;

/// <summary>一条浮窗提示<paramref name="Action"/> 非空时显示操作按钮，且不自动消失</summary>
/// <remarks>
/// One toast
/// When <paramref name="Action"/> is non-null an action button is shown and the toast does not disappear on its own
/// </remarks>
public sealed record ToastMessage(
    string Title,
    string? Message,
    ToastSeverity Severity,
    string? ActionLabel = null,
    Action? Action = null);

public static class ToastService
{
    /// <summary>浮窗宿主（MainWindow）订阅此事件进行呈现</summary>
    /// <remarks>The toast host (MainWindow) subscribes to this event and renders the toasts</remarks>
    public static event Action<ToastMessage>? Raised;

    /// <summary>请求关闭全部浮窗（如切换语言后文案需要重建）</summary>
    /// <remarks>
    /// Requests that all toasts be dismissed
    /// That is needed for instance because the copy has to be rebuilt after a language switch
    /// </remarks>
    public static event Action? Cleared;

    public static void Show(ToastMessage message)
    {
        try
        {
            Raised?.Invoke(message);
        }
        catch
        {
            // 呈现失败不能影响主流程（提示只是附加信息）
            //
            // A failed render must not affect the main flow: a toast is only extra information
        }
    }

    public static void Show(
        string title,
        string? message = null,
        ToastSeverity severity = ToastSeverity.Info,
        string? actionLabel = null,
        Action? action = null)
        => Show(new ToastMessage(title, message, severity, actionLabel, action));

    public static void Clear()
    {
        try
        {
            Cleared?.Invoke();
        }
        catch
        {
            // ignore
        }
    }
}
