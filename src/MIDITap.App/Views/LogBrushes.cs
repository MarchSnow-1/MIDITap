// LogBrushes.cs — 日志页的「级别 -> 画笔」映射（静态函数，x:Bind 模板可直接调用）
//
// 命名解析顺序：优先系统主题资源，其次应用自带调色板（App.xaml 里的 Miditap*Brush）
// 两者都没有就返回 null 让 TextBlock 继承前景色
//
// LogBrushes.cs — level -> brush mapping for the log page (static functions usable from x:Bind templates)
//
// Resolution order: the system theme brush first, then the app's own palette (Miditap*Brush in App.xaml)
// When neither exists it returns null, so the TextBlock inherits the foreground

using MIDITap.Core.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MIDITap.App.Views;

public static class LogBrushes
{
    // 级别与 LogService 一一对应：LogService 产生 debug/info/warn/error 四种
    // 级别名走 Core 的 LogLevels 常量而不是字面量：拼错时编译期就暴露，而不是让筛选静默失效
    //
    // Levels correspond one-to-one with LogService, which produces the four levels debug, info, warn and error
    // Level names come from Core's LogLevels constants rather than from literals
    // So a misspelling surfaces at compile time instead of letting the filter fail silently

    public static Brush? ForLevel(string level) => LogLevels.Normalize(level) switch
    {
        LogLevels.Warn => Resolve("SystemFillColorCautionBrush", "MiditapWarnBrush"),
        LogLevels.Error => Resolve("SystemFillColorCriticalBrush", "MiditapErrorBrush"),
        // debug 用**更弱**的一档（三级文本色）：它默认不显示，一旦显示也不该与 info 抢注意力
        // 否则用户开了 debug 之后，真正要看的 info/warn/error 反而被淹没
        //
        // Debug uses a **fainter** step (tertiary text)
        // It is hidden by default, and when shown it must not compete with info for attention
        // Otherwise enabling debug drowns the very lines the user was looking for
        LogLevels.Debug => Resolve("TextFillColorTertiaryBrush", "MiditapDimBrush"),
        _ => Resolve("TextFillColorSecondaryBrush", "MiditapDimBrush"),
    };

    /// <summary>按顺序取第一个存在的画刷；全部缺失时返回 null（继承前景色），不抛异常 / Takes the first brush that exists, in the given order; when none exists it returns null (the foreground is inherited) rather than throwing</summary>
    private static Brush? Resolve(params string[] keys)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return null;
        }
        foreach (var key in keys)
        {
            if (resources.TryGetValue(key, out var value) && value is Brush brush)
            {
                return brush;
            }
        }
        return null;
    }
}
