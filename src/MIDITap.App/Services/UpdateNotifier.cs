// UpdateNotifier.cs — 发现新版本时弹窗的唯一入口
//
// 为什么需要它：更新可用这件事原先由主页与设置页**各自**订阅并各自提示
// 于是同一个事件可能弹两次，两个页面的表现也互不相同（主页那个甚至没有下载按钮）
// 提示与操作因此集中到一处：订阅一次、弹一个窗、在任何页面都能完成更新
//
// 为什么弹窗本身不在这里：UpdateDialog 只管界面，本类只管"什么时候弹、弹不弹"
//
// UpdateNotifier.cs — the single entry point for the "update available" dialog
//
// Why it exists: the "an update is available" event used to be subscribed by the home page and
// the settings page separately, each raising its own prompt. The same event could therefore show up
// twice, and the two looked nothing alike (the home page one had not even a download button)
// Announcing and acting now happen in one place: one subscription, one dialog, usable from any page
//
// Why the dialog itself is not here: UpdateDialog owns the UI, this class owns when and whether to show it

using MIDITap.App.Dialogs;
using MIDITap.App.Services;
using MIDITap.Core.Settings;
using MIDITap.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Services;

public static class UpdateNotifier
{
    // 弹窗显示期间又收到同一事件时不再弹第二个
    // A second event arriving while the dialog is up does not open another one
    private static bool _showing;

    /// <summary>在 XamlRoot 可用的窗口上显示更新弹窗 / Shows the update dialog on the window whose XamlRoot is ready</summary>
    /// <param name="manual">
    /// 是否来自设置页的「检查更新」：手动检查不带「不再提醒更新」
    /// 手动检查本身就是用户主动要看结果，再给他一个"别再提醒我"是答非所问
    ///
    /// Whether it came from the manual "check for updates" on the settings page
    /// A manual check omits the "do not remind me again" link: the user asked for this result,
    /// so offering to stop telling them answers a question they did not ask
    /// </param>
    public static async Task ShowAsync(UpdateInfo info, bool manual = false)
    {
        if (_showing)
        {
            return;
        }

        // 用户点过「忽略此版本」的版本不再自动弹；手动检查仍然要弹，显式意图优先
        //
        // A version the user chose to ignore is not raised automatically again
        // A manual check still shows it, since an explicit request takes priority
        if (!manual && string.Equals(AppStorage.GetIgnoredUpdate(BaseDir), info.Latest, StringComparison.Ordinal))
        {
            return;
        }

        var root = App.MainWindowInstance?.Content?.XamlRoot;
        if (root is null)
        {
            return;
        }

        _showing = true;
        try
        {
            var dialog = new UpdateDialog(info, showNeverRemind: !manual) { XamlRoot = root };
            await dialog.ShowAsync();

            // 弹窗关掉之后再决定要不要记下"忽略"
            // 记在关闭之后：用户中途取消下载时不应被当成忽略了这个版本
            //
            // The "ignored" record is written after the dialog closes
            // Writing it afterwards keeps a cancelled download from counting as ignoring the version
            if (dialog.IgnoredVersion)
            {
                AppStorage.SaveIgnoredUpdate(BaseDir, info.Latest);
            }
        }
        finally
        {
            _showing = false;
        }
    }

    private static string BaseDir => AppServices.BaseDir;
}
