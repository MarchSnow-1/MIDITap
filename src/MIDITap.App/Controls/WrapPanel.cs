// WrapPanel.cs — 一个支持**变宽子项**的换行面板
//
// 为什么需要自己写
//   WinUI 3 没有 WPF 的 WrapPanel（已核对二进制：WrapPanel 不存在）
//   框架自带的两个换行容器都不适用
//     * ItemsWrapGrid / VariableSizedWrapGrid 都按**等宽单元格**排布
//       （单元尺寸取自首个元素），而实时预览的 chip 宽度不一致
//       （"C4 → f13" 与 "C#4 → f16+f17" 相差明显），等宽会导致裁切或大片空隙
//   因此这里按每个子项各自的 DesiredSize 排布，长度超过可用宽度就换到下一行
//
// A wrap panel that supports VARIABLE-WIDTH children
//
// Why this exists: WinUI 3 has no WPF-style WrapPanel (verified against the binaries)
// The framework's two wrapping containers both lay out UNIFORM cells (cell size taken from the first item)
// The live-preview chips have noticeably different widths ("C4 → f13" vs "C#4 → f16+f17")
// So uniform cells would clip them or leave large gaps
// This panel lays out each child at its own DesiredSize
// It wraps once the line would exceed the available width
//
// 与父容器的约定（重要）：Measure 按 availableSize.Width 断行，Arrange 按 finalSize.Width 断行
// 这两个值由 WinUI 的布局过程各自给出，本控件无法强制它们相等
// 两者不一致时断行结果也不同，而到了Arrange 阶段，本控件已无法回头告诉父容器"我需要更高"
// 因此要求父容器按与测量时相同的宽度来排列
// ScrollViewer 在 HorizontalScrollMode=Disabled 时正是如此，本项目用的就是这一种
// 反过来，若放进横向可滚动的 ScrollViewer 或横向 StackPanel
// 测量会拿到无限宽、只排一行
// 排列时再按有限宽断行，就会溢出
//
// 单个子项宽于整行时：它独占一行，并按自己的 DesiredSize 排列
// 因此**可以超出本面板** —— 本面板不裁剪
// 当前用途下这条分支不参与实际布局
// chip 的文字是音名与键位（例如"C4 → f13"），自然宽度小于 chip 区的可用宽度
//
// Contract with the parent (important): Measure wraps at availableSize.Width and Arrange at finalSize.Width
// WinUI's layout pass supplies those two independently, and the panel cannot force them to agree
// When they differ the line breaking differs too
// By Arrange time the panel can no longer tell its parent it needs more height
// The panel therefore requires the parent to arrange with the same width it used for measuring
// A ScrollViewer does exactly that when HorizontalScrollMode=Disabled
// It measures with the viewport width and arranges with the viewport width, and that is the case used here
// Conversely, inside a horizontally scrollable ScrollViewer or a horizontal StackPanel
// The measure receives an infinite width and the panel lays out a single line
// Arranging at a finite width then wraps again and overflows
//
// A single child wider than a whole line gets a line of its own
// It is arranged at its own DesiredSize, so it can extend past the panel, which does not clip
// This branch currently takes no part in layout
// A chip's text is a note name plus key names (for example "C4 → f13")
// Its natural width is smaller than the chip area's available width

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MIDITap.App.Controls;

/// <summary>子项按自身宽度依次排列，放不下就换行</summary>
/// <remarks>
/// Children are laid out in order at their own widths
/// The panel wraps once the next child does not fit
/// </remarks>
public sealed class WrapPanel : Panel
{
    /// <summary>同一行内相邻子项的水平间距</summary>
    /// <remarks>The horizontal spacing between adjacent children on the same line</remarks>
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, OnSpacingChanged));

    /// <summary>相邻两行之间的垂直间距</summary>
    /// <remarks>The vertical spacing between adjacent lines</remarks>
    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    // 间距变化必须触发重新布局，否则改属性后画面不会更新
    //
    // A spacing change must invalidate layout, or the panel would not re-render
    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WrapPanel)d).InvalidateMeasure();

    // 间距在**读取处**兜底：布局算术只用下面这两个属性
    // 因此即使有人把间距设成 NaN 或负数，NaN 也不会进入 Rect、把整块面板的布局算坏
    // 负数按 0 处理（相邻子项重叠不是本控件要表达的意思）
    // 为什么不在写入处收敛：WinUI 3 的 PropertyMetadata 只有「默认值 + 变化回调」两个参数
    // 它没有 WPF 的 CoerceValueCallback
    // 因此收敛放在读取处
    // 间距在 XAML 里以字面值给出，这里兜的是绑定或代码传入异常值的情况
    //
    // The spacing is sanitised where it is READ: the layout arithmetic uses only the two properties below
    // So a NaN or negative spacing cannot reach a Rect and corrupt the whole panel's layout
    // A negative value is treated as 0, since overlapping children is not what this control means
    // Why not on write: WinUI 3's PropertyMetadata takes only a default value and a changed callback
    // It has no WPF-style CoerceValueCallback
    // So the clamping lives at the point of reading
    // The spacing is given as a literal in XAML; this guards the case of a bound or code-supplied value
    private double HorizontalGap
        => double.IsFinite(HorizontalSpacing) && HorizontalSpacing > 0 ? HorizontalSpacing : 0.0;

    private double VerticalGap
        => double.IsFinite(VerticalSpacing) && VerticalSpacing > 0 ? VerticalSpacing : 0.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        // 可用宽度可能是无穷（放在水平 StackPanel / ScrollViewer 里）
        // 那种情况下退化为"单行不换行"，并把结果如实报出去
        // 不能假装宽度无限还返回一个很大的数值，那会让外层容器被撑爆
        //
        // The available width can be infinite (inside a horizontal StackPanel or ScrollViewer)
        // In that case wrapping degrades to a single line, and the result is reported as it is
        // The panel must not pretend the width is unbounded and return a large number
        // That would blow the outer container up
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;

        var x = 0.0;        // 当前行已占用宽度
        var y = 0.0;        // 已完成行的总高度
        var lineHeight = 0.0;
        var widest = 0.0;   // 最宽一行的宽度
        var started = false;

        foreach (var child in Children)
        {
            // 子项以 availableSize.Width 作为**宽度约束**测量，高度不限
            // 注意这是给约束，不是强制宽度
            // 宽度自适应的子项（例如 Border 包一个短 TextBlock）仍会报出自己的自然宽度
            // 另外，这里传的就是 availableSize.Width，因此当它无限时子项也拿到无限宽
            // 其内部若有 TextWrapping 也不会生效
            // 本项目的 chip 不换行、文字短，不受这一条影响
            //
            // The child is measured with availableSize.Width as its width CONSTRAINT and an unbounded height
            // That is a constraint, not a forced width
            // A naturally-sized child (a Border wrapping a short TextBlock, say) still reports its own natural width
            // Note also that the value passed is availableSize.Width itself
            // So when that is infinite the child is measured unconstrained
            // Any TextWrapping inside it cannot take effect
            // This project's chips do not wrap and carry short text, so they are unaffected
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            var advance = (started ? HorizontalGap : 0) + size.Width;

            if (started && x + advance > maxWidth)
            {
                widest = Math.Max(widest, x);
                y += lineHeight + VerticalGap;
                x = 0;
                lineHeight = 0;
                started = false;
                advance = size.Width;
            }

            x += advance;
            lineHeight = Math.Max(lineHeight, size.Height);
            started = true;
        }

        widest = Math.Max(widest, x);
        y += lineHeight;

        var reportedWidth = double.IsInfinity(maxWidth) ? widest : Math.Min(widest, maxWidth);
        return new Size(reportedWidth, y);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 用与 Measure **同一套规则**重算位置
        // 子项尺寸在 Measure 之后已固定，唯一可能不同的是宽度
        // 因此只有当 finalSize.Width 等于测量时用的那个宽度，两次才得到相同的断行结果（见文件头的约定）
        // 这里必须用 finalSize.Width —— 它是本次真正被分配到的宽度
        // 用别的值只会让内容越界
        //
        // Positions are recomputed with the SAME rule as Measure
        // Child sizes are fixed by then, so the only thing that can differ is the width
        // The two passes agree exactly when finalSize.Width equals the width used for measuring
        // See the contract at the top of the file
        // finalSize.Width is the right value here because it is the space actually granted
        // Using anything else would place content outside it
        var x = 0.0;
        var y = 0.0;
        var lineHeight = 0.0;
        var started = false;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            var advance = (started ? HorizontalGap : 0) + size.Width;

            if (started && x + advance > finalSize.Width)
            {
                y += lineHeight + VerticalGap;
                x = 0;
                lineHeight = 0;
                started = false;
                advance = size.Width;
            }

            child.Arrange(new Rect(x + (started ? HorizontalGap : 0), y, size.Width, size.Height));
            x += advance;
            lineHeight = Math.Max(lineHeight, size.Height);
            started = true;
        }

        return finalSize;
    }
}
