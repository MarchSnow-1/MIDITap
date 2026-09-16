// LogFilter.cs — 「哪些级别目前在显示」的共享状态，以及"某条日志是否通过筛选"的规则
//
// 为什么是共享状态而不是各页面各自持有
// 日志页与主页显示的是同一份日志
// 用户在日志页选了"只看警告"，切回主页却仍看到满屏 info
// 那是两个界面互相矛盾，用户无法判断到底哪个才算数
// 现在以本对象为唯一来源，改一处两页同步
//
// 模型刻意做成"一个级别集合"而不是"All 开关 + 级别开关"两套语义
// 于是"All + 只勾 info"这种自相矛盾的状态是合法的，界面上无法表达它到底显示什么
// 现在 All 只是"四个级别都选中"的一种情形，由集合推导
// 不存在需要额外解释的组合
//
// 集合为空时的兜底：不允许出现"一个级别都不选"
// 那种状态下界面是空白，而用户看不到任何原因
// 与其显示一个无法解释的空列表，不如回到全选
// 这条规则放在这里而不是界面里，是为了让两个页面（以及将来的调用方）共享同一种行为，并且可以被单元测试直接验证
//
// LogFilter.cs — the shared "which levels are currently shown" state
// It also holds the rule deciding whether an entry passes the filter
//
// Why shared state rather than one per page
// The log page and Home show the same log
// If the user picks "warnings only" on the log page, Home still shows a screenful of info lines
// The two views contradict each other, and the user cannot tell which one is authoritative
// This object is the single source of truth, so one change updates both
//
// The model is deliberately "a set of levels" rather than "an All switch plus level switches"
// A self-contradictory state like "All + info only" was therefore legal
// The UI could not say what it showed in that case
// All is now simply the case where all four levels are selected, derived from the set
// No combination therefore needs explaining
//
// Empty-set fallback: "no level selected at all" is not allowed
// In that state the UI is blank with no visible reason
// Falling back to all levels beats an unexplained empty list
// The rule lives here rather than in the UI
// That way both pages (and any future caller) behave identically, and a unit test can verify it directly

namespace MIDITap.Core.Logging;

/// <summary>
/// 当前显示哪些级别
/// 默认不含 debug（见 <see cref="LogLevels.DefaultVisible"/>）
///
/// Which levels are currently shown
/// Excludes debug by default (see <see cref="LogLevels.DefaultVisible"/>)
/// </summary>
public sealed class LogFilter
{
    // 只由 UI 线程读写：界面是唯一的调用方
    // 加锁只会让调用点变啰嗦而无实际收益
    //
    // Read and written on the UI thread only: the UI is the sole caller
    // Locking would add noise to every call site without buying anything real
    private readonly HashSet<string> _enabled = [.. LogLevels.DefaultVisible];

    /// <summary>筛选条件变化后触发（两个页面都据此重建各自的列表）</summary>
    /// <remarks>
    /// Raised after the selection changes; both pages rebuild their lists from it
    /// </remarks>
    public event Action? Changed;

    /// <summary>当前选中的级别（按严重程度升序）</summary>
    /// <remarks>The currently selected levels, in ascending severity order</remarks>
    public IReadOnlyList<string> Enabled => [.. LogLevels.All.Where(_enabled.Contains)];

    /// <summary>四个级别是否全部选中（即界面上"全部"按钮的勾选状态）</summary>
    /// <remarks>
    /// Whether all four levels are selected — i.e. the checked state of the UI's "All" button
    /// </remarks>
    public bool IsAll => LogLevels.All.All(_enabled.Contains);

    /// <summary>某条日志是否通过当前筛选。未知级别按 info 处理（见 LogLevels）</summary>
    /// <remarks>
    /// Whether an entry passes the current filter. An unknown level counts as info (see LogLevels)
    /// </remarks>
    public bool Matches(string? level) => _enabled.Contains(LogLevels.Normalize(level));

    /// <summary>选中全部级别（**含 debug**）</summary>
    /// <remarks>Selects every level, **debug included**</remarks>
    public void SelectAll()
    {
        if (IsAll)
        {
            return;
        }
        _enabled.Clear();
        _enabled.UnionWith(LogLevels.All);
        Changed?.Invoke();
    }

    /// <summary>回到默认可见级别（不含 debug）</summary>
    /// <remarks>Returns to the default visible levels (debug excluded)</remarks>
    public void SelectDefaults()
    {
        _enabled.Clear();
        _enabled.UnionWith(LogLevels.DefaultVisible);
        Changed?.Invoke();
    }

    /// <summary>
    /// 打开/关闭某个级别
    /// 关闭后若一个级别都不剩，则回到全选（见文件头的兜底说明）
    ///
    /// Turns one level on or off
    /// If switching it off would leave nothing selected, everything is selected again
    /// See the fallback note in the file header
    /// </summary>
    public void SetEnabled(string level, bool enabled)
    {
        var normalized = LogLevels.Normalize(level);
        var changed = enabled ? _enabled.Add(normalized) : _enabled.Remove(normalized);
        if (!changed)
        {
            return;
        }

        if (_enabled.Count == 0)
        {
            _enabled.UnionWith(LogLevels.All);
        }
        Changed?.Invoke();
    }
}
