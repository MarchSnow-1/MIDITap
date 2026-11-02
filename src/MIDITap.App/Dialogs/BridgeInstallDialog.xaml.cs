// BridgeInstallDialog.xaml.cs — 询问用户是否现在安装 MIDIBridge 组件
// 对话框本身不做安装：它只把四段文案显示出来，安装由 BridgeInstallFlow 负责
//
// BridgeInstallDialog.xaml.cs — asks the user whether to install the MIDIBridge component now
// The dialog installs nothing: it only shows the four paragraphs, and BridgeInstallFlow does the install

using MIDITap.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Dialogs;

public sealed partial class BridgeInstallDialog : ContentDialog
{
    public BridgeInstallDialog()
    {
        InitializeComponent();
        Title = AppServices.I18n.T("bridge.installDialog.title");
        PrimaryButtonText = AppServices.I18n.T("bridge.installDialog.confirm");
        CloseButtonText = AppServices.I18n.T("edit.cancel");
        DefaultButton = ContentDialogButton.Primary;
        BodyText.Text = AppServices.I18n.T("bridge.installDialog.body");
        SupportText.Text = AppServices.I18n.T("bridge.installDialog.support");
        BenefitText.Text = AppServices.I18n.T("bridge.installDialog.benefit");
        NoticeText.Text = AppServices.I18n.T("bridge.installDialog.notice");
        // 两个按钮的圆角由样式给出：主题默认值取不到 ContentDialog 生成的按钮
        //
        // The two button styles carry the corner radius: the theme default does not reach the buttons ContentDialog
        // generates
        PrimaryButtonStyle = (Style)Resources["BridgeDialogPrimaryStyle"];
        CloseButtonStyle = (Style)Resources["BridgeDialogCloseStyle"];
    }
}
