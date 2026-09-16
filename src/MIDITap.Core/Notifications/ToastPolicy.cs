// ToastPolicy.cs — 浮窗提示的显示时长与队列上限策略
// 单独放在 Core 而不写在控件里，是因为"多久消失""最多堆几条"这类规则最容易写错，又完全不需要 UI 才能推理
// 放这里可以单测，UI 只负责呈现
//
// Display-duration and queue-limit policy for toast notifications
// This policy is kept in Core rather than in the control
// "When does it disappear" and "how many may stack" are easy to get wrong
// They also need no UI to reason about, so they are unit-tested
// The control only renders

namespace MIDITap.Core.Notifications;

/// <summary>提示级别 / Toast severity level</summary>
public enum ToastSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public static class ToastPolicy
{
    /// <summary>
    /// 同时最多堆叠的浮窗数量
    /// 超出时移除最早的一条
    ///
    /// Maximum number of toasts stacked at once
    /// The oldest one is removed when exceeded
    /// </summary>
    public const int MaxVisible = 3;

    private static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SuccessDuration = TimeSpan.FromSeconds(5);
    // 警告/错误看得越久越好：用户可能需要读完整句话才能判断怎么处理
    //
    // Warnings and errors deserve a longer look
    // The user may need the whole sentence to judge what to do
    private static readonly TimeSpan WarningDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 该浮窗自动消失的时间
    /// 带操作按钮的**不自动消失**（返回 null）
    /// 这类提示（如"发现新版本，去下载"）让用户在还没读完就被收走是最糟的体验，必须由用户操作或手动关闭
    ///
    /// Automatic dismissal delay, or null to stay until dismissed
    /// Notifications carrying an action never auto-dismiss
    /// Yanking one away before the user can act on it is the worst possible behaviour
    /// </summary>
    public static TimeSpan? AutoDismissAfter(ToastSeverity severity, bool hasAction)
    {
        if (hasAction)
        {
            return null;
        }
        return severity switch
        {
            ToastSeverity.Success => SuccessDuration,
            ToastSeverity.Warning => WarningDuration,
            ToastSeverity.Error => ErrorDuration,
            _ => InfoDuration,
        };
    }
}
