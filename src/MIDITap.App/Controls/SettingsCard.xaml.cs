// SettingsCard.cs — 设置页一行卡片的外壳：左侧图标、标题与说明，右侧由调用方塞入控件
//
// 属性故意是普通 CLR 属性而不是 DependencyProperty
// 这些值在页面 XAML 解析时被赋一次
// 之后只有语言切换时由 RefreshTexts 重新赋一次
// 没有任何绑定或动画需要依赖属性系统
// 用 DP 会多出四份样板（注册、get/set、变更回调），换不来任何东西
//
// SettingsCard.cs — the shell of one settings row
// It has the icon, title and description on the left
// A control the caller supplies sits on the right
//
// The properties are deliberately plain CLR properties rather than DependencyProperties
// They are assigned once while the page XAML is parsed
// RefreshTexts reassigns them once more on a language switch
// Nothing binds to them and nothing animates them
// So DP plumbing (registration, getters/setters, change callbacks) would be four times the code for no benefit

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Controls;

public sealed partial class SettingsCard : UserControl
{
    public SettingsCard() => InitializeComponent();

    /// <summary>左侧图标。XAML 里用 <c>Glyph="&#xE8C1;"</c> 这样的转义直接给字符；留空则不显示图标
    /// The leading icon. In XAML pass the character directly, as in <c>Glyph="&#xE8C1;"</c>; empty hides it</summary>
    public string Glyph
    {
        get => GlyphIcon.Glyph;
        set
        {
            GlyphIcon.Glyph = value;
            GlyphIcon.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>标题（由 RefreshTexts 填入本地化文案，不写在 XAML 里）
    /// The title, filled in by RefreshTexts with localised copy rather than hardcoded in XAML</summary>
    public string Header
    {
        get => HeaderText.Text;
        set => HeaderText.Text = value;
    }

    /// <summary>标题下的一行说明；留空时整块收起，避免卡片里留一段空白
    /// The line under the title. Empty collapses it so the card keeps no blank line</summary>
    public string Description
    {
        get => DescriptionText.Text;
        set
        {
            DescriptionText.Text = value;
            DescriptionText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>右侧控件（开关、下拉框、按钮组等）
    /// The right-hand control: a switch, a drop-down, a group of buttons</summary>
    public object? Action
    {
        get => ActionHost.Content;
        set => ActionHost.Content = value;
    }

    /// <summary>整张卡片上的悬浮提示；留空表示不设
    /// 提示挂在**卡片外壳**上而不是右侧控件上，因为禁用的控件不接收指针事件，挂上去也不会弹出
    /// WinUI 3 的 ToolTipService 没有 WPF 那个 ShowOnDisabled 属性，因此只能在能接收事件的元素上挂
    /// 挂在卡片上还有个好处：说明文字周围任意位置悬浮都能看到，不只是那个已被禁用的控件
    ///
    /// A hover hint covering the whole card; empty means none is set
    /// It hangs on the **card shell** rather than on the right-hand control, because a disabled control receives no pointer events and a tooltip set on it would not open
    /// WinUI 3's ToolTipService has no ShowOnDisabled property as WPF's does, so the hint has to sit on an element that does receive events
    /// The card has a further advantage: hovering anywhere near the description shows it, not only over the control that is already disabled
    /// </summary>
    public string Hint
    {
        set => ToolTipService.SetToolTip(CardRoot, string.IsNullOrEmpty(value) ? null : value);
    }
}
