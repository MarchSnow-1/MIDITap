// TextInputDialog.cs — 单行文本输入对话框（重命名 / 新建配置共用）
//
// 抽成独立对话框而不是页面内联输入框：改名/新建都是**低频操作**
// 所以内联常驻会永久占用版面
// 用对话框时它只在需要时出现，且能自然地做到"非空校验 + 阻止关闭"
//
// Single-line text input dialog shared by rename and new-config
//
// A dialog rather than an inline field: renaming and creating are LOW-FREQUENCY actions
// So a permanent inline box costs layout on every visit
// A dialog appears only when needed
// It also makes "validate non-empty and block closing" natural

using MIDITap.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Dialogs;

public sealed partial class TextInputDialog : ContentDialog
{
    private readonly Func<string, string?> _validate;

    /// <summary>
    /// 构造对话框
    /// validate 返回**已本地化的错误文案**，合法时返回 null
    /// 校验放在对话框内部而不是调用方：这样不合法就不让关是天然成立的，调用方也不必自己拼错误提示
    /// 省略 validate 时只做非空校验
    ///
    /// Builds the dialog
    /// validate returns an ALREADY LOCALISED error message, or null when the value is acceptable
    /// The check lives inside the dialog rather than at the call site
    /// That makes an invalid value unable to close it by construction, and spares the caller from assembling the message
    /// Omitting validate leaves the non-empty check alone
    /// </summary>
    public TextInputDialog(
        string title,
        string placeholder,
        string initialText,
        string primaryText,
        Func<string, string?>? validate = null)
    {
        InitializeComponent();
        _validate = validate ?? DefaultValidate;
        Title = title;
        PrimaryButtonText = primaryText;
        CloseButtonText = AppServices.I18n.T("edit.cancel");
        DefaultButton = ContentDialogButton.Primary;
        InputBox.PlaceholderText = placeholder;
        InputBox.Text = initialText;
        Closing += OnClosing;
        Opened += (_, _) =>
        {
            // 打开即聚焦并全选：重命名时用户多半想直接改掉整个名字
            // Focus and select all when it opens
            // When renaming, the user most likely wants to replace the whole name
            InputBox.Focus(FocusState.Programmatic);
            InputBox.SelectAll();
        };
    }

    /// <summary>用户确认后的文本（已去除首尾空白）/ The text the user confirmed, with leading and trailing whitespace removed</summary>
    public string Value => InputBox.Text.Trim();

    /// <summary>只做非空校验，供不传 validate 的调用方使用 / The non-empty check alone, for callers that pass no validate</summary>
    private static string? DefaultValidate(string value)
        => value.Length == 0 ? AppServices.I18n.T("log.warnEmptyName") : null;

    /// <summary>
    /// 校验不通过时阻止关闭，并把原因显示在对话框里，而不是静默什么都不做
    /// 用户就在输入框旁边，因此原因写在这里最直接
    ///
    /// Invalid input blocks the close and shows the reason inside the dialog rather than silently doing nothing
    /// The user is right next to the input box, so that is the most direct place for the reason
    /// </summary>
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result != ContentDialogResult.Primary)
        {
            return;
        }
        var error = _validate(Value);
        if (error is null)
        {
            return;
        }
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        args.Cancel = true;
    }
}
