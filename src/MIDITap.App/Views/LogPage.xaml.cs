// LogPage.cs — 完整活动日志
//
// 导出：本页只负责收集用户的选择与目标路径，打包交给 Core 的 LogExporter（不依赖 WinUI，因此可单元测试）
// 导出**默认包含 debug**，与界面默认隐藏 debug 相反，这正是把"记录""显示""导出"三件事分开的原因
//
// LogPage.cs — the full activity log
// Export: this page only collects the user's selection and the target path
// The packaging is Core's LogExporter, which does not depend on WinUI, so it can be unit-tested
// An export **includes debug by default**, the opposite of the UI hiding debug by default
// That is exactly why recording, showing and exporting are kept apart


using MIDITap.App.Dialogs;
using MIDITap.App.Services;
using MIDITap.Core.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MIDITap.App.Views;

public sealed partial class LogPage : Page
{
    // 实时日志的呈现（订阅、筛选、封顶、渲染、滚动）全部在 LogFeed 里
    // 本页持有它，计数与空态也从它读
    // 上限 1000 条由 LogFeed 执行
    //
    // Showing the live log — subscribing, filtering, capping, rendering, scrolling — all lives in LogFeed
    // This page holds it and reads its counter and empty state from it
    // LogFeed enforces the 1000-line cap
    private readonly LogFeed _feed;

    // 程序化设置 ToggleButton 时抑制 Click，避免把"同步状态"当成用户操作
    //
    // Click is suppressed while the ToggleButton is set in code
    // So reflecting the state is not mistaken for a user action
    private bool _suppressFilterEvents;

    // 级别筛选按钮：级别名 -> 控件
    // 由 BuildFilterButtons 按 Core 的 LogLevels.All 生成
    // 因此这里不需要为每个级别各留一个字段
    //
    // Level filter buttons: level name -> control
    // They are built by BuildFilterButtons from Core's LogLevels.All
    // So no field has to be kept for each level here
    private readonly Dictionary<string, ToggleButton> _levelFilterButtons = [];

    /// <summary>共享的筛选状态（两个页面共用一份） / Filter state shared by both pages, one instance</summary>
    private static LogFilter Filter => AppServices.LogFilter;

    public LogPage()
    {
        InitializeComponent();
        BuildFilterButtons();
        // 日志与筛选的订阅、过滤、封顶、渲染、滚动都交给 LogFeed（见该类型）
        // 本页只把自己的文本块与滚动容器交给它
        //
        // Subscribing, filtering, capping, rendering and scrolling are all LogFeed's job (see that type)
        // This page only hands over its own text block and scroll host
        _feed = new LogFeed(LogText, LogScroll, capacity: 1000, inset: LogTextRenderer.CardInset);
        _feed.Changed += UpdateChrome;
        AppServices.LogFilter.Changed += OnFilterStateChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SyncFilterButtons();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.I18n.LanguageChanged += RefreshTexts;
        RefreshTexts();
        SyncFilterButtons();
        // Attach 会按当前筛选先渲染一次并滚到最新，因此这里不再单独重建或滚动
        //
        // Attach renders once under the current filter and scrolls to the newest line
        // So there is no separate rebuild or scroll here
        _feed.Attach();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.I18n.LanguageChanged -= RefreshTexts;
        AppServices.LogFilter.Changed -= OnFilterStateChanged;
        _feed.Detach();
    }

    /// <summary>
    /// 筛选状态被别处改动时（本页自己改的也会走到这里）同步按钮
    /// 用 _suppressFilterEvents 把"同步显示"与"用户操作"分开：否则按钮的赋值会再次触发 Click
    ///
    /// Syncs the buttons whenever the filter changes elsewhere (including changes made here)
    /// The suppression flag separates "reflecting state" from "the user clicked"
    /// That would otherwise re-enter the click handler
    /// </summary>
    private void OnFilterStateChanged()
    {
        // 列表由 LogFeed 自己重取（它订阅的是同一个事件）；这里只管按钮的勾选状态
        //
        // The list is reloaded by LogFeed, which subscribes to the same event
        // This handles the buttons' check state only
        SyncFilterButtons();
    }


    // ------------------------------------------------------------------ filtering

    /// <summary>
    /// 按 Core 的 LogLevels.All 生成级别筛选按钮
    /// 现在级别的来源只有 Core 一处，多一个级别这里自动跟着长出来
    ///
    /// Builds the level filter buttons from Core's LogLevels.All
    /// Core is now the single source of the level list, and one more level simply appears here
    /// </summary>
    private void BuildFilterButtons()
    {
        foreach (var level in LogLevels.All)
        {
            var button = new ToggleButton { MinWidth = 0 };
            button.Click += OnFilterChanged;
            _levelFilterButtons[level] = button;
            FilterButtons.Children.Add(button);
        }
    }

    private void SyncFilterButtons()
    {
        _suppressFilterEvents = true;
        try
        {
            FilterAll.IsChecked = Filter.IsAll;
            foreach (var (level, button) in _levelFilterButtons)
            {
                button.IsChecked = Filter.Matches(level);
            }
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    /// <summary>
    /// 筛选切换：把界面上的勾选状态换算成一次集合操作
    /// "全部"是**推导出来的**状态（四个级别都选中即勾上），不再是会覆盖其它勾选的独立模式
    /// 一个级别都不剩时由 LogFilter 自动回到全选，界面因此永远不会莫名其妙变空
    ///
    /// A filter click turns the button states into one set operation
    /// "All" is a **derived** state (all four levels selected)
    /// It is no longer a separate mode that overrides the level buttons
    /// When nothing would remain selected, LogFilter falls back to all
    /// So the list can never become mysteriously empty
    /// </summary>
    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        if (ReferenceEquals(sender, FilterAll))
        {
            if (FilterAll.IsChecked == true)
            {
                Filter.SelectAll();
            }
            else
            {
                // 取消"全部"回到默认可见级别（不含 debug），而不是回到"一个都不选"
                // Unchecking All returns to the default visible levels (no debug), not to "none"
                Filter.SelectDefaults();
            }
            return;
        }

        // 由控件反查级别：按钮是生成的，没法再拿具名控件逐个比对
        //
        // Maps the control back to its level: the buttons are generated
        // So they cannot be compared one by one against named controls any more
        foreach (var (level, button) in _levelFilterButtons)
        {
            if (ReferenceEquals(sender, button))
            {
                Filter.SetEnabled(level, button.IsChecked == true);
                return;
            }
        }
    }


    // ------------------------------------------------------------------ actions

    private void OnClear(object sender, RoutedEventArgs e)
    {
        AppServices.Log.Entries.Clear();
        // 重建同时清掉文本、条目与计数 / Rebuilding clears the text, the entries and the counter in one go
        _feed.Reload();
    }

    /// <summary>
    /// 导出本次运行的日志
    /// 对话框只负责收集级别选择与目标路径，其余交给 Core 的 LogExporter
    /// 级别选择**默认全选（含 debug）**：导出的用途是排查，而排查时最缺的往往正是最详细的那一档
    ///
    /// Exports this run's log
    /// The dialog only collects the level selection and the target path
    /// The rest is Core's LogExporter
    /// The selection **defaults to all levels, debug included**
    /// An export exists for triage, and the most detailed level is usually the first thing missing
    /// </summary>
    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new ExportLogsDialog(AppServices.Log.Entries.Count)
        {
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    /// <summary>更新计数与空态提示 / Updates the counter and the empty-state hint</summary>
    private void UpdateChrome()
    {
        var total = AppServices.Log.Entries.Count;
        CountText.Text = Filter.IsAll
            ? AppServices.I18n.T("log.count", ("count", total.ToString()))
            : AppServices.I18n.T("log.countFiltered", ("shown", _feed.Count.ToString()), ("total", total.ToString()));

        var empty = _feed.Count == 0;
        EmptyHint.Text = AppServices.I18n.T(total == 0 ? "log.empty" : "log.emptyFiltered");
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RefreshTexts()
    {
        Func<string, string> t = AppServices.I18n.T;
        FilterAll.Content = t("log.filter.all");
        // 级别文案与提示的键都按约定从级别名拼出：log.level.<级别> 与 log.level.<级别>Hint
        // 提示是**可选**的，只有确实需要解释的级别才有那个键（目前只有 debug），存在才挂上
        //
        // Both the label and the hint key are derived from the level name by convention
        // They are log.level.<level> and log.level.<level>Hint
        // The hint is **optional** — only a level that genuinely needs explaining has it (debug, currently)
        // It is attached only when it exists
        foreach (var (level, button) in _levelFilterButtons)
        {
            button.Content = t("log.level." + level);
            var hintKey = "log.level." + level + "Hint";
            if (AppServices.I18n.Has(hintKey))
            {
                ToolTipService.SetToolTip(button, t(hintKey));
            }
        }
        // "全部"含 debug，而 debug 默认不勾选 —— 不说明的话，用户会以为"全部"就是"默认那样"
        //
        // "All" includes debug while debug is off by default
        // Without a tooltip the user would assume "All" just means "the default view"
        ToolTipService.SetToolTip(FilterAll, t("log.filter.allHint"));
        ExportLabel.Text = t("log.export");
        ToolTipService.SetToolTip(ExportBtn, t("log.export.hint"));
        ClearLabel.Text = t("log.clear");
        ToolTipService.SetToolTip(ClearBtn, t("log.clear"));
        UpdateChrome();
    }
}
