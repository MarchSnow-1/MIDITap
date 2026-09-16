// LogFeed.cs — 「把实时日志呈现到一块可选中文本里」这件事的完整实现：订阅日志与筛选、按级别过滤、封顶裁剪、渲染、滚到最新
//
// 为什么要有这个类型：日志页与主页显示的是**同一份**日志（同一个 AppServices.Log，同一份 LogFilter）
// 渲染已经合并进LogTextRenderer，取数是剩下的另一半
// 现在两个页面各自只持有**一个 LogFeed**，其余交给它
//
// 为什么不做成控件：它只依赖已有的 TextBlock 与 ScrollViewer，不需要新的可视树节点或依赖属性
// 上限由各页面自己决定，滚动容器也由页面交进来，两边互不引用
//
// LogFeed.cs — the full implementation of "showing the live log in one selectable block of text"
// It subscribes to the log and the filter and filters by level
// It trims to the cap, renders, and scrolls to the newest line
//
// Why this type exists: the log page and Home show the **same** log (one AppServices.Log, one LogFilter)
// Rendering has already been merged into LogTextRenderer, and fetching is the remaining half
// Both pages now hold **one LogFeed** each and leave the rest to it
//
// Why it is not a control: it depends only on the existing TextBlock and ScrollViewer
// So it needs no new visual-tree node or dependency property
// The cap is decided by each page and the scroll host is handed in by the page
// So the two do not reference each other


using MIDITap.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Views;

internal sealed class LogFeed
{
    private readonly TextBlock _target;
    private readonly ScrollViewer _scroll;
    private readonly List<LogEntry> _entries = [];
    private readonly int _capacity;
    private bool _attached;

    public LogFeed(TextBlock target, ScrollViewer scroll, int capacity, double inset)
    {
        _target = target;
        _scroll = scroll;
        _capacity = capacity;
        // 排版（字号、行距、时间与消息的间隔）与内边距的取值全部归 LogTextRenderer 所有
        // 内边距由调用方按有无卡片选定
        // 连应用到元素这一步也交给它，免得"值在一处、应用在另一处"
        // 内边距给的是**滚动视口**：打到文本块上会随内容滚走
        //
        // Typography — font sizes, line height, the timestamp-to-message gap — and the inset value belong to LogTextRenderer
        // That includes applying it, so that the value and its application do not end up in different places
        // The caller picks the inset according to whether it has a card
        // The inset goes on the **viewport**: applied to the text block it would scroll away with the content
        LogTextRenderer.ApplyTo(_target, _scroll, inset);
    }

    /// <summary>当前显示的条目（最旧在前）/ The entries currently shown, oldest first</summary>
    public IReadOnlyList<LogEntry> Entries => _entries;

    /// <summary>当前显示的条数（供计数与空态使用）
    /// How many entries are shown; used for the counter and the empty state</summary>
    public int Count => _entries.Count;

    /// <summary>内容或筛选变化后触发，调用方据此刷新计数与空态
    /// Raised after the content or the filter changed, so the caller can refresh its counter and empty state</summary>
    public event Action? Changed;

    /// <summary>
    /// 开始跟随日志与筛选，并按当前状态先渲染一次（同时滚到最新）
    /// 与 Detach 成对调用：页面是按导航重建的，不摘订阅会让旧页面继续被事件持有
    ///
    /// Starts following the log and the filter and renders the current state once (scrolling to the newest line)
    /// Always paired with Detach: pages are rebuilt on navigation
    /// Not unsubscribing would leave the old page held by the events
    /// </summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }
        _attached = true;
        AppServices.Log.Added += OnLogAdded;
        AppServices.LogFilter.Changed += OnFilterChanged;
        Reload();
    }

    public void Detach()
    {
        if (!_attached)
        {
            return;
        }
        _attached = false;
        AppServices.Log.Added -= OnLogAdded;
        AppServices.LogFilter.Changed -= OnFilterChanged;
    }

    /// <summary>
    /// 按当前筛选从日志服务重建整块文本。页面进入时与筛选变化时使用
    /// 只取**最近** _capacity 条**通过筛选的**条目
    /// 先筛后截，倒过来先截后筛的话，被过滤掉的条目会占掉名额，明明还有匹配的行却显示不出来
    ///
    /// Rebuilds the whole block from the log service under the current filter
    /// Used when the page appears and whenever the filter changes
    /// It takes the most recent _capacity entries **that pass the filter**
    /// Filter first, then truncate
    /// Truncating first would let filtered-out entries eat the quota and hide matching lines
    /// </summary>
    public void Reload()
    {
        _entries.Clear();
        _target.Inlines.Clear();
        var entries = AppServices.Log.Entries;
        var picked = new List<LogEntry>();
        for (var i = entries.Count - 1; i >= 0 && picked.Count < _capacity; i--)
        {
            if (AppServices.LogFilter.Matches(entries[i].Level))
            {
                picked.Add(entries[i]);
            }
        }
        picked.Reverse();
        foreach (var entry in picked)
        {
            _entries.Add(entry);
            LogTextRenderer.Append(_target, entry);
        }
        LogTextRenderer.ScrollToEnd(_scroll);
        Changed?.Invoke();
    }

    private void OnLogAdded(LogEntry entry) => AppServices.RunOnUi(() =>
    {
        // 追加到末尾（最新的在最下面）
        //
        // Appended at the END
        if (!AppServices.LogFilter.Matches(entry.Level))
        {
            return;
        }
        _entries.Add(entry);
        LogTextRenderer.Append(_target, entry);
        Trim();
        LogTextRenderer.ScrollToEnd(_scroll);
        Changed?.Invoke();
    });

    private void OnFilterChanged() => AppServices.RunOnUi(Reload);

    /// <summary>
    /// 超出上限时丢掉最旧的行：条目集合与文本按同一个行数裁剪，不会错位
    /// 每行占几个 inline 由 LogTextRenderer 负责，这里不必知道
    ///
    /// Drops the oldest lines past the cap
    /// The entry list and the text are trimmed by the same line count and cannot drift apart
    /// How many inlines a line occupies is LogTextRenderer's business, not this type's
    /// </summary>
    private void Trim()
    {
        while (_entries.Count > _capacity)
        {
            _entries.RemoveAt(0);
            LogTextRenderer.TrimOldest(_target, 1);
        }
    }
}
