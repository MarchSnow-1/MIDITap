// LogTextRenderer.cs — 把日志条目渲染成"一整块可选中的文本"，日志页与主页的活动日志共用这一份实现
//
// 为什么共用：两处显示的是**同一份**日志（同一个 AppServices.Log，同一份 LogFilter）
// 渲染规则集中在这里，两处就不可能再走样
//
// 为什么不做成控件：这里只依赖已有的 TextBlock 与 ScrollViewer，不需要新的可视树节点、模板或依赖属性
// 滚动容器、保留上限、筛选都仍由各自的页面持有，两边互不引用
//
// LogTextRenderer.cs — renders log entries as one selectable block of text
// It is shared by the log page and Home's activity log
//
// Why shared: the two places show the **same** log (one AppServices.Log, one LogFilter)
// Keeping the rendering rules here means the two cannot drift apart again
//
// Why not a control: this depends only on the existing TextBlock and ScrollViewer
// So it needs no new visual-tree node, template or dependency property
// The scroll host, the retention cap and the filter all stay with the page that owns them
// Neither page references the other

using MIDITap.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace MIDITap.App.Views;

internal static class LogTextRenderer
{
    // 一条日志在这段文本里占用的 inline 数：时间、消息、换行
    // 裁剪按这个数成组丢弃
    //
    // Inlines one entry occupies in the text: timestamp, message, line break
    // Trimming drops them in groups of this size
    public const int InlinesPerEntry = 3;

    // 与 App.xaml 的 MiditapMutedMonoStyle 保持一致（Consolas 12）
    // Run 上套不了 Style，因此字体与字号只能在这里再写一遍，改那个样式时这里要一起改
    //
    // Matches MiditapMutedMonoStyle in App.xaml (Consolas 12)
    // A Style cannot be applied to a Run, so the font and size are repeated here
    // Changing that style means changing this too
    private static readonly FontFamily MonoFont = new("Consolas");
    private const double TimeFontSize = 12;
    private const double MessageFontSize = 13;

    // ------------------------------------------------------------------ 间距 / Spacing
    //
    // 这一节是日志文本**全部**间距的来源，两个方向各一个值：纵向的行距、横向的时间与消息间隔
    // 改观感只动这两个（改完两侧同时生效）
    //
    // This section is the single source of **all** spacing in the log text
    // There is one value per direction: the vertical line height and the horizontal gap between timestamp and message
    // Changing the look means changing these two, and both surfaces follow

    /// <summary>
    /// 行高 —— 纵向，行与行之间的距离
    /// 比字号大出一截，否则条目挨得太紧、成片的日志读起来费劲
    ///
    /// Line height — the vertical distance between rows
    /// A good deal larger than the font size, because entries packed tightly together are hard to read in bulk
    /// </summary>
    public const double LineHeight = 24;

    /// <summary>
    /// 时间与消息之间的空格数 —— 横向距离
    /// 用空格而不是"两个控件 + ColumnSpacing"：整块文本才能跨行选中（见 LogFeed 的说明）
    /// 而控件之间是拉不开可选中文本内部的间距的
    ///
    /// Spaces between the timestamp and the message — the horizontal distance
    /// Spaces rather than two controls with a ColumnSpacing
    /// Only one block of text can be selected across lines (see LogFeed)
    /// No layout gap can be inserted inside selectable text
    /// </summary>
    public const int TimeGapSpaces = 2;

    /// <summary>
    /// 日志文本与**卡片边缘**之间的间距，四边都是这个值
    /// 只在外面套了卡片的页面用（日志页）
    /// 主页的日志直接放进栏里，外面已有页面内边距
    /// 再缩进一次就会比上方自己的标题多出一块空白
    ///
    /// The gap between the log text and the **card edge**, the same on all four sides
    /// It applies only where the log sits inside a card (the log page)
    /// On Home the log goes straight into its column, and the page padding is already the outer inset
    /// Indenting it once more would leave a blank strip wider than the heading above it
    /// </summary>
    public const double CardInset = 18;

    /// <summary>
    /// 把只作用于**整块文本**的排版套到元素上：行高与横向内边距给文本块，纵向内边距给**视口**
    /// 为什么按轴向分流，见 CardInset 的说明
    /// 逐行的样式在 Append 里给
    /// inset 由调用方给：有卡片的页面传 CardInset，没有卡片的传 0
    /// 排版归本类所有，因此由本类应用，而不是让调用方各自去设
    /// 由调用方设的话，"值在这里、应用在那里"就又分家了
    ///
    /// Applies the typography that concerns the **whole block** to the elements
    /// Line height and horizontal padding go to the text block, vertical padding goes to the **viewport**
    /// See CardInset for why the split follows the axis
    /// Per-line styling is given in Append
    /// The inset comes from the caller: a page with a card passes CardInset, one without passes 0
    /// Typography belongs to this class, so this class applies it rather than letting each caller set it
    /// If the caller set it, the value and its application would be apart again
    /// </summary>
    public static void ApplyTo(TextBlock target, ScrollViewer viewport, double inset)
    {
        target.LineHeight = LineHeight;
        target.Padding = new Thickness(inset, 0, inset, 0);
        viewport.Padding = new Thickness(0, inset, 0, inset);
    }

    /// <summary>
    /// 往文本末尾追加一行：时间用等宽字体、按级别着色，消息紧随其后
    /// 消息**不设 Foreground**，让它从容器继承
    /// 高对比度下背景翻转时，消息文字会跟着反色
    ///
    /// Appends one line: a mono timestamp coloured by level, then the message
    /// The message sets no Foreground so that it inherits from the container
    /// Under a contrast theme that flips the background, the message text inverts with it
    /// </summary>
    public static void Append(TextBlock target, LogEntry entry)
    {
        target.Inlines.Add(new Run
        {
            Text = entry.FormattedTime + new string(' ', TimeGapSpaces),
            FontFamily = MonoFont,
            FontSize = TimeFontSize,
            Foreground = LogBrushes.ForLevel(entry.Level),
        });
        target.Inlines.Add(new Run { Text = entry.Message, FontSize = MessageFontSize });
        target.Inlines.Add(new LineBreak());
    }

    /// <summary>
    /// 从文本开头丢弃 count 行
    /// 每行固定占 InlinesPerEntry 个 inline
    /// 因此调用方按同一个 count 裁剪自己的条目集合，文本就不会与那份集合错位
    ///
    /// Drops count lines from the front
    /// Every line occupies exactly InlinesPerEntry inlines
    /// So as long as the caller drops the same count from its own collection
    /// The text cannot fall out of step with it
    /// </summary>
    public static void TrimOldest(TextBlock target, int count)
    {
        for (var i = 0; i < count * InlinesPerEntry && target.Inlines.Count > 0; i++)
        {
            target.Inlines.RemoveAt(0);
        }
    }

    /// <summary>
    /// 把视图滚到底部，让最新一行进入视野
    /// 投到下一轮 dispatcher：新一行引起的布局那时才跑完，ScrollableHeight 才是新值
    /// 更早取到的是旧高度，会差一行
    /// 也不在这里强制 UpdateLayout
    /// 演奏时每秒可能追加多条，每条同步跑一遍布局会拖慢按键响应
    /// 动画同理关掉
    ///
    /// Scrolls the view to the bottom so that the newest line comes into view
    /// Posted to the next dispatcher pass: the layout caused by the new line has finished by then
    /// So ScrollableHeight is the new value, whereas reading it earlier returns the old height and stops one line short
    /// UpdateLayout is not forced here either
    /// Playing can append several entries per second
    /// Running a layout pass synchronously for each one would slow the key response down
    /// Animation is off for the same reason
    /// </summary>
    public static void ScrollToEnd(ScrollViewer scroll)
        => scroll.DispatcherQueue.TryEnqueue(() =>
            scroll.ChangeView(null, scroll.ScrollableHeight, null, true));
}
