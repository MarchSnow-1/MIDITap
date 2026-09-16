// NoteGrid.cs — 主页中央的音符盘：12 列（音级 C..B）× 9 行（八度）的平坦格子矩阵
//
// 性能：88 个格子的视觉树只在构造时创建一次
// 实时高亮只改画笔与字重，不重建任何元素
// 弹奏时每秒可能有几十次更新
//
// NoteGrid.cs — the note board at the centre of Home
// It is a flat matrix of 12 columns (the pitch classes C..B) by 9 rows (the octaves)
//
// Performance: the visual tree for all 88 cells is built once, at construction
// Live highlighting only swaps brushes and font weight and rebuilds no elements
// Playing can update it dozens of times a second

using MIDITap.App.Helpers;
using MIDITap.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace MIDITap.App.Controls;

public sealed class NoteGrid : UserControl
{
    public event Action<byte>? NoteClicked;

    private const byte FirstNote = 21;  // A0
    private const byte LastNote = 108;  // C8

    // 矩阵的维度。列 = 十二个音级（C C# D … B），行 = 八度
    // A0 在第 0 八度、C8 在第 8 八度，因此一共 9 行
    // Matrix dimensions. Columns are the twelve pitch classes (C C# D … B) and rows are octaves
    // A0 sits in octave 0 and C8 in octave 8, so there are 9 rows
    private const int Columns = 12;
    private const int Rows = 9;
    private const int TopOctave = Rows - 1;

    // 盘面内边距与格间距。间距随格子缩放但有上下限
    // 太窄时格子会粘成一片
    // 太宽时矩阵会散开，失去"一块棋盘"的整体感
    // Board padding and the gap between cells. The gap scales with the cell but is clamped
    // Too narrow and the cells merge into one mass
    // Too wide and the matrix stops reading as a board
    private const double PadX = 14;
    private const double PadY = 12;
    private const double GapRatio = 0.10;
    private const double MinGap = 3;
    private const double MaxGap = 8;

    // 高度允许范围
    // 这是 **MeasureOverride 的兜底**，只在父容器不给定高度时生效
    // 例如 Auto 行、无约束测量等
    // 主页把本控件放在 3* 的比例行里，那时高度由父容器分配
    // 因此这里的限幅不参与布局
    // 下限是"两行文字还放得下"的下界
    // 上限防止盘面在宽窗口下把下方的配置与日志区挤出屏幕
    // 名字刻意避开 MinHeight / MaxHeight
    // 那两个是 FrameworkElement 的成员，同名会隐藏它们并触发 CS0108
    // 同名也会让"这两个值到底管谁"变得含糊
    //
    // The allowed height range
    // This is a **fallback for MeasureOverride**, used only when the parent does not allot a height
    // For example an Auto row, an unconstrained measure, and so on
    // Home puts this control in a 3* proportional row, where the parent allots the height
    // There this clamp takes no part in layout
    // The floor is the point below which the two text lines no longer fit
    // The ceiling stops the board from pushing the config and log areas off screen in a wide window
    // The names deliberately avoid MinHeight / MaxHeight
    // Those are FrameworkElement members, and reusing them hides the inherited ones (CS0108)
    // Reuse also muddies what the values govern
    private const double MinBoardHeight = 280;
    private const double MaxBoardHeight = 480;

    /// <summary>格子的三种状态。是否绑定是配置，是否发声是实时状态，两者正交</summary>
    /// <remarks>
    /// The three cell states
    /// Being bound is configuration; sounding is live state
    /// The two are orthogonal
    /// </remarks>
    private enum PadState
    {
        Idle,
        Mapped,
        Active,
    }

    // ------------------------------------------------------------------ 内置兜底配色 / fallbacks

    // 仅当主题字典查不到键时使用（正常情况下取不到）
    // 取值与 App.xaml 的 Default 字典一致
    // 保证资源字典出问题时盘面也不会是一片空白
    // Only used when the theme dictionary lacks a key (which should not happen)
    // Values match App.xaml's Default dictionary
    // So a broken resource dictionary does not leave a blank board
    private static readonly Color FallbackBoard = Color.FromArgb(0xFF, 0x2A, 0x2D, 0x31);
    private static readonly Color FallbackIdle = Color.FromArgb(0xFF, 0x1F, 0x21, 0x24);
    private static readonly Color FallbackMapped = Color.FromArgb(0xFF, 0x24, 0x40, 0x5F);
    private static readonly Color FallbackActive = Color.FromArgb(0xFF, 0x25, 0x63, 0xEB);
    private static readonly Color FallbackNote = Color.FromArgb(0xFF, 0x8B, 0x93, 0xA1);
    private static readonly Color FallbackKeyLabel = Color.FromArgb(0xFF, 0x93, 0xC5, 0xFD);
    private static readonly Color FallbackActiveLabel = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>
    /// 一次解析出来的一整套画笔与描边
    /// 逐角色显式命名，避免用「是否激活」三元判断拼装颜色
    /// 高对比度下激活态是整体反转，各角色的前景色并不相同
    ///
    /// 常规主题下三个 Thickness 全是 0（无描边，靠文字与底色分档）
    /// 只有高对比度才用描边
    /// 作为"已绑定"的第二通道，因为那里颜色由系统决定，不能只靠颜色区分状态
    ///
    /// A full set of brushes and outlines resolved once
    /// Roles are named explicitly instead of being composed from an is-active ternary
    /// Under contrast themes the active state is a full inversion
    /// So roles do not share a foreground colour
    ///
    /// Outside contrast themes all three Thickness values are 0 (nothing is outlined)
    /// The text and the fill levels carry the states
    /// Only contrast themes use an outline as the second channel for "bound"
    /// There the colours belong to the system, so colour must not be the sole signal
    /// </summary>
    private sealed record Palette(
        Brush BoardFill,
        Brush IdleFill,
        Brush MappedFill,
        Brush ActiveFill,
        Brush NoteText,
        Brush NoteTextActive,
        Brush KeyText,
        Brush KeyTextActive,
        Brush Outline,
        Thickness IdleOutline,
        Thickness MappedOutline,
        Thickness ActiveOutline);

    /// <summary>
    /// 一个格子的全部可视化部件，构造后不再增删（实时更新只改属性）
    /// Note 是音高（每个格子都有），Key 是绑定的键位（只有已绑定的格子显示）
    /// </summary>
    /// <remarks>
    /// Every visual part of one cell
    /// Nothing is added or removed after construction (live updates only change properties)
    /// Note is the pitch, present on every cell
    /// Key is the bound key, shown only where a mapping exists
    /// </remarks>
    private sealed record Cell(
        Border Body,
        TextBlock Note,
        TextBlock Key,
        int Row,
        int Column);

    private readonly BoardPanel _panel;
    private readonly Grid _host = new();
    private readonly Border _board = new() { CornerRadius = new CornerRadius(12) };
    private readonly Dictionary<byte, Cell> _cells = new();
    private readonly HashSet<byte> _activeNotes = new();
    private IReadOnlyDictionary<byte, string> _mapping = new Dictionary<byte, string>();
    private Palette _palette;

    /// <summary>
    /// 按可用宽度回报一个合理的高度
    /// **主页并不依赖它**
    /// 那里控件放在 3* 比例行里，高度由父容器按窗口高度分配（盘面约占 60%，见 HomePage.xaml 的行定义）
    /// 本方法只是"被放进 Auto 行或无约束容器"时的兜底，保证那种情况下也有个像样的高度
    ///
    /// 格子的几何**不由这里决定**
    /// 12 列 × 9 行交给布局引擎的星号列/行去分摊（见构造函数）
    /// 因此不存在"自己算的尺寸与引擎给的尺寸不一致"这类问题
    ///
    /// Reports a sensible height from the available width
    /// **Home does not rely on it**
    /// There the control sits in a 3* proportional row
    /// The parent allots the height from the window height (the board takes about 60%)
    /// See the row definitions in HomePage.xaml
    /// This method is only a fallback for when the control is placed in an Auto row or an unconstrained container
    /// Even then it reports a reasonable height
    ///
    /// The cell geometry is **not** decided here
    /// The 12 columns and 9 rows are divided by the layout engine's star columns/rows (see the constructor)
    /// So both the size computed here and the size the engine finally hands out come from the same division
    /// The two agree by construction
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
        {
            // 宽度未知（不限宽）时不做推算，交给父容器
            // When the width is unknown (unconstrained), do not guess; let the parent decide
            return base.MeasureOverride(availableSize);
        }

        // **父容器给了确定高度就照抄，不回报更大的期望值**
        // 这是比例布局成立的前提
        // 星号行会为"期望尺寸比分配值更大"的子元素让路
        // 于是回报 480 会让 3:2 的行分配失效
        // 只有高度不确定时（Auto 行 / 无约束容器）才用算出来的理想值做兜底
        //
        // 注：这里**不做下钳制**
        // 给一个最小高度看似稳妥，却会在窗口很矮时回报超过可用高度
        // 于是行又被撑开 —— 那正是上面那个问题的翻版
        // 矮窗口下格子自己会变扁（见 ArrangeCells）
        //
        // **When the parent allots a definite height, report exactly that rather than a larger desired value.**
        // This is what makes the proportional layout hold
        // A star row gives way to a child whose desired size exceeds its share
        // So reporting 480 defeated the 3:2 split
        // Only when the height is unknown (an Auto row or an unconstrained container)
        // Then the computed ideal is used as a fallback
        //
        // Note there is deliberately **no lower clamp** here
        // A minimum looks safe but would report more than the available height in a short window
        // That inflates the row again — the same bug in another guise
        // In a short window the cells simply flatten (see ArrangeCells)
        var height = double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height)
            ? IdealHeightFor(width)
            : availableSize.Height;
        return new Size(width, height);
    }

    /// <summary>
    /// 高度不确定时的理想值：按宽度算出格子边长
    /// 再按"格子接近正方形"推出总高
    /// 最后限幅到 <see cref="MinBoardHeight"/> / <see cref="MaxBoardHeight"/>
    /// 仅在 Auto 行或无约束容器里生效
    ///
    /// The ideal height when no height is allotted
    /// The cell side comes from the width, and the total height from "cells near square"
    /// It is clamped to <see cref="MinBoardHeight"/> and <see cref="MaxBoardHeight"/>
    /// Only applies inside an Auto row or an unconstrained container
    /// </summary>
    private static double IdealHeightFor(double width)
    {
        var gap = GapFor(width);
        var cell = (width - PadX * 2 - gap * (Columns - 1)) / Columns;
        var height = PadY * 2 + Rows * cell + (Rows - 1) * gap;
        return Math.Clamp(height, MinBoardHeight, MaxBoardHeight);
    }

    public NoteGrid()
    {
        _palette = ResolvePalette();
        _panel = new BoardPanel(this);

        // 格子的几何完全在 **ArrangeOverride** 里决定（见 ArrangeCells）
        // 星号行在**无限高度**测量时会退化成"按内容撑开"
        // 现在由 BoardPanel 在**测量时**给每个格子一个确定的尺寸，并在**排布时**按最终尺寸摆放
        // 排布尺寸就是实际尺寸，这类不一致从根上消失
        //
        // 盘面卡片负责背景、圆角与内边距；格子由面板摆放，控件本身不参与坐标计算
        // HighContrastAdjustment=None：官方建议在画笔已按对比度主题正确配对后关闭平台的自动调整
        // 否则平台可能改写我们选定的 SystemColor 配对，反而破坏可读性
        //
        // The cell geometry is decided entirely in **ArrangeOverride** (see ArrangeCells)
        // A star row degenerates into "size to content"
        // That happens when it is measured with INFINITE height
        // BoardPanel now gives every cell a definite size at MEASURE time
        // It positions each cell from the final size at ARRANGE time
        // So the size used for layout IS the size received, and this whole class of inconsistency is gone
        //
        // The card carries the background, the rounding and the padding
        // The panel places the cells, and the control itself does no coordinate arithmetic
        // HighContrastAdjustment=None: the docs recommend turning off the platform's automatic adjustment
        // That is recommended once our brushes already pair up correctly for the contrast theme
        // Otherwise the platform may rewrite our SystemColor pairing and hurt readability
        var host = _host;
        host.HorizontalAlignment = HorizontalAlignment.Stretch;
        host.HighContrastAdjustment = ElementHighContrastAdjustment.None;
        _board.Child = _panel;
        host.Children.Add(_board);
        Content = host;
        _board.Background = _palette.BoardFill;

        BuildCells();
    }

    /// <summary>
    /// 把内容按控件**分到的**尺寸摆放（唯一权威）
    /// 测量阶段的可用宽度可能大于最终分到的宽度
    /// 若不在这里强制收口，卡片会按期望宽度撑开、右侧超出窗口、最后一列（B 列）被切掉
    ///
    /// Lays the content out at the size the control was **allotted**, the single authority
    /// The measure-time width can exceed the width finally allotted
    /// Without clamping here the card would grow to its desired width
    /// It would spill past the window's right edge and cut the last column (B)
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _host.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }

    /// <summary>
    /// 摆放 88 个格子的极简面板：每个格子按行列索引落在 12 × 9 的网格里
    ///
    /// 为什么自己写而不是用 Grid 的星号行列
    /// 星号行在无限高度下按内容撑开（见构造函数）
    /// 而这里在测量阶段就给出确定的格子尺寸、在排布阶段按最终尺寸摆放
    /// 两端都不依赖"父容器会告诉我多大"这一假设
    ///
    /// A minimal panel that places the 88 cells
    /// Every cell lands on a 12 x 9 grid by its row and column index
    ///
    /// Why not the Grid's star rows/columns: a star row sizes to content under infinite height (see the constructor)
    /// Here a definite cell size is handed out at measure time and the final size is used at arrange time
    /// Neither end assumes "the parent will tell me how big I am"
    /// </summary>
    private sealed class BoardPanel(NoteGrid owner) : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            // 测量阶段只做两件事：让子元素被量到，以及**不索取任何尺寸**
            //
            // 回报 "0 x 0" 是关键：测量时给出的可用宽度可能大于最终分到的宽度
            // 一旦把这些数字当作自己的期望尺寸回报上去，卡片就会按该宽度撑开，右侧越出窗口，最后一列（B）被切掉
            // 回报 0 表示"我完全按分到的尺寸排布"，子尺寸在 ArrangeOverride 里按最终分配值算
            // 因此内容按该宽度排布，不会超出控件
            //
            // 唯一的例外是高度不确定时（Auto 行/无约束容器）
            // 那时没有"分到的尺寸"可用，于是回报理想高度当兜底
            // 宽度仍回报 0
            //
            // This pass does two things only: let the children be measured, and **request no size**
            //
            // Reporting "0 x 0" is the crux: the width offered at measure time can exceed the width finally allotted
            // Once such a number is reported as our desired size, the card grows to that width
            // It then spills past the window and cuts the last column (B)
            // Reporting 0 says "I lay out strictly at whatever I am given"
            // Sizing happens in ArrangeOverride from the final allotted size
            // So the content stays within the control's width
            //
            // The one exception is an unknown height (an Auto row or an unconstrained container)
            // There is no allotted size to work with, so the ideal height is reported as a fallback
            // The width is still 0
            var finiteWidth = !double.IsInfinity(availableSize.Width) && !double.IsNaN(availableSize.Width);
            var finiteHeight = !double.IsInfinity(availableSize.Height) && !double.IsNaN(availableSize.Height);
            var width = finiteWidth ? Math.Max(1, availableSize.Width) : 1024;
            var height = finiteHeight ? Math.Max(1, availableSize.Height) : IdealHeightFor(width);

            var measureSize = new Size(width, height);
            foreach (var child in Children)
            {
                child.Measure(measureSize);
            }

            return new Size(0, finiteHeight ? 0 : height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            owner.ArrangeCells(finalSize);
            return finalSize;
        }
    }

    // ------------------------------------------------------------------ 调色板 / palette

    private static Palette ResolvePalette()
        => ThemeService.IsHighContrast ? BuildContrastPalette() : BuildDesignedPalette();

    /// <summary>
    /// 常规主题：全部取 App.xaml 里的平涂色，**不做任何派生**（不生成渐变、不加描边）
    /// 现在只在三个平涂色之间分档，颜色该是什么就是什么
    ///
    /// Designed themes: plain flat colours from App.xaml, with NO derivation at all (no gradients, no outlines)
    /// Now the states are simply three flat fills, so a colour is exactly the colour it was chosen to be
    /// </summary>
    private static Palette BuildDesignedPalette()
    {
        var transparent = new Thickness(0);
        return new Palette(
            BoardFill: Resolve("MiditapSurfaceAltBrush", FallbackBoard),
            IdleFill: Resolve("MiditapNoteGridIdleBrush", FallbackIdle),
            MappedFill: Resolve("MiditapNoteGridMappedBrush", FallbackMapped),
            ActiveFill: Resolve("MiditapNoteGridActiveBrush", FallbackActive),
            NoteText: Resolve("MiditapNoteGridNoteBrush", FallbackNote),
            NoteTextActive: Resolve("MiditapNoteGridActiveLabelBrush", FallbackActiveLabel),
            KeyText: Resolve("MiditapNoteGridLabelBrush", FallbackKeyLabel),
            KeyTextActive: Resolve("MiditapNoteGridActiveLabelBrush", FallbackActiveLabel),
            Outline: Resolve("MiditapNoteGridIdleBrush", FallbackIdle),
            IdleOutline: transparent,
            MappedOutline: transparent,
            ActiveOutline: transparent);
    }

    /// <summary>
    /// 对比度主题：只用 ButtonFace/ButtonText 这一对官方保证的高对比色
    /// 并按**实测亮度**决定谁当盘面、谁当格子
    /// 不能把角色写死：High Contrast White 的 ButtonFace 是近白（#FFFAEF）
    /// ButtonText 是近黑 (#202020)，与另外三套正好相反
    /// 写死会让整块盘面反色
    ///
    /// 三态都是平涂，不用渐变与透明度
    /// 那里颜色由系统决定，半透明会混出不可控的中间色
    ///   * 空槽   —— 亮面 + 暗字
    ///   * 已绑定 —— 亮面 + **3px 暗描边** + 键位
    ///     描边是这里唯一的第二通道
    ///     对比度主题下不能只靠颜色区分"这一格有绑定"
    ///   * 正在响 —— 整体反色（暗面 + 亮字）
    ///
    /// Contrast themes: use only the officially guaranteed ButtonFace/ButtonText pair
    /// MEASURED luminance decides which one is the board and which the cells
    /// Roles cannot be pinned: of the four built-in schemes, High Contrast White has ButtonFace near-white (#FFFAEF)
    /// Its ButtonText is near-black (#202020), the opposite of the other three
    /// So a fixed assignment inverts the board
    ///
    /// All three states are flat, with no gradients and no transparency
    /// There the colours belong to the system
    /// Translucency would blend into an uncontrolled intermediate colour
    ///   * empty slot — light face with dark text
    ///   * bound      — light face with a **3px dark outline** plus the key binding
    ///     The outline is the only second channel here
    ///     Under a contrast theme colour alone must not carry "this cell is bound"
    ///   * sounding   — a full inversion (dark face with light text)
    /// </summary>
    private static Palette BuildContrastPalette()
    {
        var face = HighContrastDetector.ButtonFace;
        var text = HighContrastDetector.ButtonText;

        // 亮的那个当格子面，暗的当盘面与描边
        // Whichever is lighter becomes the cell face; the darker one the board and the outline
        var light = Luminance(face) > Luminance(text) ? face : text;
        var dark = Luminance(face) > Luminance(text) ? text : face;

        var lightBrush = new SolidColorBrush(light);
        var darkBrush = new SolidColorBrush(dark);

        // 每个格子的前景色都取所在面的反色
        // 这两个颜色是系统主题给出的官方配对，因此它们的对比度由系统保证
        // 此处不做第二次判断
        // Each cell's foreground is the other colour of the pair
        // Those two come from the contrast theme as an official pairing
        // So their contrast is guaranteed by the system
        // No second-guessing happens here
        return new Palette(
            BoardFill: darkBrush,
            IdleFill: lightBrush,
            MappedFill: lightBrush,
            ActiveFill: darkBrush,
            NoteText: darkBrush,
            NoteTextActive: lightBrush,
            KeyText: darkBrush,
            KeyTextActive: lightBrush,
            Outline: darkBrush,
            IdleOutline: new Thickness(0),
            MappedOutline: new Thickness(3),
            ActiveOutline: new Thickness(0));
    }

    /// <summary>相对亮度（WCAG 定义），用于判断哪一个颜色更亮</summary>
    /// <remarks>
    /// Relative luminance as defined by WCAG, used to tell which of two colours is lighter
    /// </remarks>
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>
    /// 取主题字典里的画笔
    /// 由 ThemeService 按**元素有效主题**挑字典，保证与 XAML 里的 {ThemeResource} 取到同一套值
    /// 手动选深色 + 系统浅色时也不会错配
    /// 取不到时退回内置设计色
    ///
    /// Resolves a brush through ThemeService
    /// That service selects the dictionary from the ELEMENT's effective theme, so it matches XAML's {ThemeResource}
    /// This holds even when the user forces dark on a light system
    /// Falls back to the built-in designed colour
    /// </summary>
    private static Brush Resolve(string key, Color fallback)
        => ThemeService.Brush(key) ?? new SolidColorBrush(fallback);

    /// <summary>
    /// 主题（含对比度主题）变化后重新解析并应用配色。当前按下的音符会被保留：只换画笔，不改状态
    ///
    /// Re-resolves and applies the palette after a theme change (contrast themes included)
    /// Held notes are preserved: only the brushes change, the state is not touched
    /// </summary>
    public void ApplyTheme()
    {
        _palette = ResolvePalette();
        _board.Background = _palette.BoardFill;

        foreach (var (note, cell) in _cells)
        {
            Paint(note, cell);
        }
    }

    // ------------------------------------------------------------------ 状态着色 / state painting

    private PadState StateOf(byte note)
    {
        if (_activeNotes.Contains(note))
        {
            return PadState.Active;
        }
        return _mapping.ContainsKey(note) ? PadState.Mapped : PadState.Idle;
    }

    /// <summary>把某个格子按其当前状态重刷一遍（唯一的状态 -> 外观映射入口）</summary>
    /// <remarks>Repaints one cell from its current state (the only state-to-appearance mapping entry point)</remarks>
    private void Paint(byte note, Cell cell)
    {
        var state = StateOf(note);

        cell.Body.Background = state switch
        {
            PadState.Idle => _palette.IdleFill,
            PadState.Mapped => _palette.MappedFill,
            _ => _palette.ActiveFill,
        };
        cell.Body.BorderBrush = _palette.Outline;
        cell.Body.BorderThickness = state switch
        {
            PadState.Idle => _palette.IdleOutline,
            PadState.Mapped => _palette.MappedOutline,
            _ => _palette.ActiveOutline,
        };

        cell.Note.Foreground = state == PadState.Active ? _palette.NoteTextActive : _palette.NoteText;
        if (cell.Key.Visibility == Visibility.Visible)
        {
            cell.Key.Foreground = state == PadState.Active ? _palette.KeyTextActive : _palette.KeyText;
            // 加粗是刻意的第二通道：对比度下的颜色由系统主题决定，不能只靠颜色区分"正在响"
            // 只对键位加粗而不对音高加粗，是为了让"响的是哪一格"在文字层级上也看得出来
            // Bold is a deliberate second channel: under contrast themes the colours are the system's choice
            // So "sounding" must not rely on colour alone
            // Only the key binding is bolded, not the pitch
            // So the text hierarchy shows which cell is sounding
            cell.Key.FontWeight = state == PadState.Active
                ? Microsoft.UI.Text.FontWeights.Bold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    // ------------------------------------------------------------------ 构建 / build

    private void BuildCells()
    {
        for (var note = FirstNote; note <= LastNote; note++)
        {
            var cell = CreateCell(note);
            _cells[note] = cell;
            // 只挂进面板
            // 位置由 ArrangeCells 在排布阶段按行列索引决定
            // Added to the panel only
            // The position is decided by ArrangeCells from the row/column indices during the arrange pass
            _panel.Children.Add(cell.Body);
        }
    }

    private Cell CreateCell(byte note)
    {
        // 行列由音高直接推出：列是音级（C=0 … B=11），行是八度
        // 高八度在上，与"音越高越靠上"的直觉一致
        // Row and column come straight from the pitch: the column is the pitch class (C=0 … B=11)
        // The row is the octave
        // Higher octaves sit at the top, matching the intuition that pitch rises upwards
        var column = note % 12;
        var octave = note / 12 - 1;
        var row = TopOctave - octave;

        // 两行文字：上行是音高（每格都有），下行是绑定的键位（只有已绑定的格子有）
        // 音高放在上面而不是下面，是因为它是这个格子**恒定**的身份标识
        // 键位是会随配置变化的可变信息，放在下方更符合阅读方向
        //
        // 不设 FontWeight：键位常是中文（如"空格"），而中文字体没有 SemiBold
        // 只能由 DirectWrite 合成，笔画边缘会发虚（详见 App.xaml 中副标题样式的说明）
        // 层级由字号与颜色承担
        // 激活时的 Bold 是真实的字重，不存在合成问题
        //
        // Two lines of text: the pitch on top (present on every cell) and the bound key below
        // The bound key appears only where a mapping exists
        // The pitch goes first because it is the cell's CONSTANT identity
        // The key binding is variable information that changes with the config
        // That ordering reads in the natural direction
        //
        // No FontWeight is set: a key binding is often Chinese (e.g. "空格"), and CJK fonts have no SemiBold
        // So DirectWrite would synthesise it and blur the stroke edges
        // See the subtitle style notes in App.xaml
        // Hierarchy comes from size and colour
        // The Bold used while active is a real weight and incurs no synthesis
        var noteText = new TextBlock
        {
            Text = NoteNames.Name(note),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var keyText = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 1,
        };
        stack.Children.Add(noteText);
        stack.Children.Add(keyText);

        var body = new Border
        {
            CornerRadius = new CornerRadius(4),
            Child = stack,
        };
        body.PointerPressed += (_, _) => NoteClicked?.Invoke(note);

        return new Cell(body, noteText, keyText, row, column);
    }

    /// <summary>
    /// 按**最终**尺寸摆放所有格子并同步字号
    /// 由 BoardPanel 在排布阶段调用
    /// 排布阶段传入的即为控件实际分到的尺寸（不是测量期的中间值）
    /// 矩阵按它计算，不会溢出卡片
    ///
    /// Place every cell for the **final** size and update the font sizes
    /// Called by BoardPanel during the arrange pass
    /// So the size here is the size the control actually received rather than a measure-time intermediate
    /// The matrix is computed from it, so it does not overflow the card
    /// </summary>
    private void ArrangeCells(Size finalSize)
    {
        var gap = GapFor(finalSize.Width);
        // 内边距由**面板自己**算，不用 Border.Padding
        // Border 的 Padding 会把子元素整体下移
        // 而面板是按"收到的整个高度"分摊行高的，两者叠加就把最后一行推出卡片
        // 现在尺寸与偏移用同一套内边距，闭合无溢出
        //
        // The padding is handled by the PANEL, not by Border.Padding
        // The Border's padding shifts the child down while the panel divides the FULL height it received
        // The two together pushed the last row out of the card
        // Now the same padding value drives both the size and the offset, so it closes exactly
        var innerW = Math.Max(1, finalSize.Width - PadX * 2);
        var innerH = Math.Max(1, finalSize.Height - PadY * 2);
        // 宽度决定格子宽（12 列各占宽度的 1/12），高度决定格子高
        // 高度不足时格子变扁，而不是整体缩小后在两侧留下大片空白
        // 两者互不限定，因此任何窗口尺寸下都成立
        //
        // The width decides the cell width (each of the 12 columns takes a twelfth of it)
        // The height decides the cell height
        // When height runs short the cells flatten
        // They do not shrink the whole board and leave a wide void on both sides
        // Neither constrains the other, so the shape holds at any window size
        var cellW = Math.Max(6, (innerW - gap * (Columns - 1)) / Columns);
        var cellH = Math.Max(6, (innerH - gap * (Rows - 1)) / Rows);

        // 字号随格子缩放并限幅
        // 格子是横排的（宽 > 高），因此字号由**高度**决定
        // 否则扁格子里的两行文字会顶到上下边
        // 下限（8 / 9）保证"音高与键位两行始终放得下"
        // 音高文字因此始终可见：字号按格高缩放并设了下限，格子变矮时文字不会消失
        // Font sizes scale with the cell and are clamped
        // A cell is a horizontal slot (wider than tall), so the size follows the HEIGHT
        // Otherwise the two text lines would touch the top and bottom edges
        // The floors (8 / 9) guarantee that both the pitch and the key binding fit
        // That keeps the pitch visible: the font size scales with the cell height and has a floor
        // So the text does not disappear as the cell shrinks
        var noteSize = Math.Clamp(cellH * 0.30, 8, 13);
        var keySize = Math.Clamp(cellH * 0.42, 9, 17);

        foreach (var item in _cells.Values)
        {
            item.Body.Arrange(new Rect(
                PadX + item.Column * (cellW + gap),
                PadY + item.Row * (cellH + gap),
                cellW,
                cellH));

            if (Math.Abs(item.Note.FontSize - noteSize) > 0.01)
            {
                item.Note.FontSize = noteSize;
            }
            if (Math.Abs(item.Key.FontSize - keySize) > 0.01)
            {
                item.Key.FontSize = keySize;
            }
        }
    }

    /// <summary>格间距：随格子缩放并限幅（见常量处的说明）</summary>
    /// <remarks>The gap between cells: it scales with the cell and is clamped (see the notes on the constants above)</remarks>
    private static double GapFor(double width)
    {
        var approx = (width - PadX * 2) / Columns;
        return Math.Clamp(approx * GapRatio, MinGap, MaxGap);
    }

    // ------------------------------------------------------------------ 状态 / state

    /// <summary>
    /// 应用（新）映射的键位标签。空槽只显示音高，已绑定的格子额外显示键位
    /// "哪些音符有绑定"因此由文字本身表达，不需要任何装饰性图元
    ///
    /// Applies the key labels of a (new) mapping
    /// An empty slot shows only its pitch, while a bound cell also shows the key
    /// So "which notes are bound" is carried by the text itself, with no decorative shapes involved
    /// </summary>
    public void UpdateMapping(IReadOnlyDictionary<byte, string> mapping)
    {
        _mapping = mapping;
        foreach (var (note, cell) in _cells)
        {
            if (mapping.TryGetValue(note, out var text))
            {
                cell.Key.Text = text;
                cell.Key.Visibility = Visibility.Visible;
            }
            else
            {
                cell.Key.Visibility = Visibility.Collapsed;
            }
            Paint(note, cell);
        }
    }

    /// <summary>
    /// 设置音符的实时高亮状态：发声时该格换成实心强调色、文字反色加粗，抬起后恢复
    ///
    /// Sets a note's live highlight
    /// While sounding the cell switches to a solid accent fill with inverted, bold text
    /// It returns to normal on release
    /// </summary>
    public void SetNoteActive(byte note, bool active)
    {
        if (!_cells.TryGetValue(note, out var cell))
        {
            return;
        }
        if (active)
        {
            _activeNotes.Add(note);
        }
        else
        {
            _activeNotes.Remove(note);
        }
        Paint(note, cell);
    }

    /// <summary>清除全部高亮（停止监听 / 热切换配置时）</summary>
    /// <remarks>Clears every highlight (on stop, or when the config is hot-swapped)</remarks>
    public void ClearActive()
    {
        foreach (var note in _activeNotes.ToArray())
        {
            SetNoteActive(note, false);
        }
    }
}
