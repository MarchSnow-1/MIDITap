// BridgePicker.cs — 设备下拉框该列出什么：空行要不要出现、MIDIBridge 项该用哪种标签
//
// BridgePicker.cs — what the device drop-down should list: whether a blank row belongs there, and which label the
// MIDIBridge entry carries

namespace MIDITap.Core.Bridge;

/// <summary>
/// MIDIBridge 项该显示哪一种标签
/// 没有"隐藏"这一档：条目未安装时也常驻，以便露出安装入口
///
/// Which label the MIDIBridge entry carries
/// There is no "hidden" member: the entry stays visible before installation so the install entry point is reachable
/// </summary>
public enum BridgePickerLabel
{
    /// <summary>普通标签，未安装与已安装可用都是它 / The plain label, used both before installation and after a usable install</summary>
    Normal = 0,

    /// <summary>带说明的标签：装着，但协议与本构建对不上 / The annotated label: installed, but its protocol does not match this build</summary>
    Unavailable,
}

/// <summary>
/// 一次列表编排的结论
/// 只描述"该有哪些条目"，不含条目内容：名称与端口号仍由调用方给出
///
/// The outcome of one list layout
/// It describes which entries exist, not their contents: names and port indexes still come from the caller
/// </summary>
/// <param name="ShowEmptyRow">是否在顶部插入一行空行 / Whether a blank row is inserted at the top</param>
/// <param name="BridgeLabel">MIDIBridge 项使用的标签种类 / Which label kind the MIDIBridge entry uses</param>
public readonly record struct BridgePickerPlan(bool ShowEmptyRow, BridgePickerLabel BridgeLabel);

public static class BridgePicker
{
    /// <summary>
    /// 编排设备下拉框的内容
    ///
    /// 空行的条件只有"没有物理设备"：它占的是本来该显示第一台设备的那一行
    /// 物理设备为 0 而扩展也没装时同样插入空行，此时列表是空行加 MIDIBridge
    ///
    /// 负数按 0 处理：调用方给的是计数，负数按"没有设备"处理与它的本意一致
    ///
    /// Lays out the device drop-down's contents
    ///
    /// The blank row's only condition is "no physical device": it occupies the row the first device would have used
    /// The row is inserted too when there are no physical devices and the extension is absent, leaving a blank row
    /// plus MIDIBridge
    ///
    /// A negative count is treated as zero: the caller passes a count, and reading a negative one as "no devices"
    /// matches its intent
    /// </summary>
    /// <param name="physicalDeviceCount">物理设备台数，不含 MIDIBridge 项 / Number of physical devices, excluding the MIDIBridge entry</param>
    /// <param name="installed">.bridge/ 里是否已有可执行程序 / Whether .bridge/ already holds a runnable executable</param>
    /// <param name="protocolSupported">已装程序的协议是否为本构建所支持 / Whether the installed program's protocol is supported by this build</param>
    public static BridgePickerPlan Describe(int physicalDeviceCount, bool installed, bool protocolSupported)
    {
        // 未安装时 protocolSupported 无从谈起，一律按普通标签
        // 只有"装着、且协议对不上"才带说明，让它与"没装"区分开
        //
        // Before installation protocolSupported has no meaning, so the plain label always applies
        // Only "installed, and the protocol does not match" carries the annotation, keeping it apart from "not installed"
        var label = installed && !protocolSupported
            ? BridgePickerLabel.Unavailable
            : BridgePickerLabel.Normal;

        return new BridgePickerPlan(physicalDeviceCount <= 0, label);
    }
}
