// SettingsRow.cs — 设置页一排的排布，不含卡片外观
//
// 与 SettingsCard 的唯一区别是没有背景、边框与圆角：折叠块里的一排排设置共用外层那一块卡片，
// 逐排再画一次边框就成了"独立一项一框"
//
// SettingsRow.cs — the layout of one settings row, without the card chrome
//
// The only difference from SettingsCard is the missing background, border and corner radius: rows inside a
// collapsible block share the enclosing card, and drawing one frame per row would turn each into a box of its own

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Controls;

public sealed partial class SettingsRow : UserControl
{
    public SettingsRow() => InitializeComponent();

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

    /// <summary>标题下的一行说明；留空时整块收起，避免这一排里留一段空白
    /// The line under the title. Empty collapses it so the row keeps no blank line</summary>
    public string Description
    {
        get => DescriptionText.Text;
        set
        {
            DescriptionText.Text = value;
            DescriptionText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>右侧控件（输入框、按钮等）
    /// The right-hand control: an input, a button</summary>
    public object? Action
    {
        get => ActionHost.Content;
        set => ActionHost.Content = value;
    }
}
