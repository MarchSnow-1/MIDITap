// LogLevels.cs — 日志级别的名字、顺序与归一化规则
//
// 为什么把级别名放进 Core 而不是各页面自己写字面量
// 级别名要跨越三处使用：LogService 产出、日志页筛选、落盘文件与导出文件里的 [level] 标记
// 任何一处拼错（"warning" 与 "warn"）都会让筛选静默失效
// 条目照常产生，却永远不被任何筛选命中，表现为"日志丢了"
// 集中成常量后，拼错在编译期就暴露
//
// 归一化（Normalize）同样属于规则而非界面细节
// 未知级别**归入 info** 而不是被丢弃
// 这样新增一个级别、旧版本界面上也不会让它凭空消失
// 宁可显示得略粗，也不要静默隐藏
//
// LogLevels.cs — the log level names, their order, and the normalization rule
//
// Why the level names live in Core rather than as literals in each page
// A level name is used in three places
// LogService produces it, and the log page filters by it
// The on-disk and exported files write it as the [level] marker
// A typo in any one of them ("warning" versus "warn") silently breaks filtering
// Entries are still produced but never match any filter, which looks like "the log lost lines"
// Centralized constants turn such a typo into a compile error
//
// Normalization is a rule rather than a UI detail for the same reason
// An unknown level is folded into info instead of being dropped
// Adding a level later therefore cannot make it vanish from an older UI
// Showing it a little coarsely beats hiding it silently

namespace MIDITap.Core.Logging;

public static class LogLevels
{
    public const string Debug = "debug";
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Error = "error";

    /// <summary>全部级别，按严重程度升序（也是界面上筛选按钮的顺序）</summary>
    /// <remarks>
    /// Every level, ordered by severity — which is also the order of the filter buttons in the UI
    /// </remarks>
    public static readonly IReadOnlyList<string> All = [Debug, Info, Warn, Error];

    /// <summary>
    /// 界面默认显示的级别：**不含 debug**
    ///
    /// debug 用于排查（环境摘要、设备枚举、配置列表等），对日常使用是噪音
    /// 它会以每秒数十条的速率把真正想看的 info/warn/error 挤出 1000 条的环形缓冲
    /// 因此默认隐藏，但**照常记录**
    /// 导出日志默认包含它，见 LogFilter.All
    ///
    /// The levels shown by default — **debug excluded**
    ///
    /// Debug exists for triage (environment summary, port enumeration, config listing)
    /// For everyday use it is noise, and it arrives at dozens of lines per second
    /// It would therefore push the info/warn/error entries one actually wants out of the 1000-entry ring buffer
    /// So it is hidden by default, but still **recorded**
    /// An exported log includes it by default; see LogFilter.All
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultVisible = [Info, Warn, Error];

    /// <summary>
    /// 把任意级别名归一化成四个已知级别之一
    /// 未知级别归入 info（见文件头说明）
    ///
    /// Normalizes any level name to one of the four known levels
    /// Unknown names fold into info (see the file header)
    /// </summary>
    public static string Normalize(string? level) => level switch
    {
        Debug => Debug,
        Warn => Warn,
        Error => Error,
        _ => Info,
    };

    /// <summary>
    /// 严重程度排序值，越小越详细
    /// 用于按级别比较与排序
    /// </summary>
    /// <remarks>
    /// Severity rank, lower meaning more verbose; used to compare and order by level
    /// </remarks>
    public static int Rank(string? level) => Normalize(level) switch
    {
        Debug => 0,
        Info => 1,
        Warn => 2,
        _ => 3,
    };
}
