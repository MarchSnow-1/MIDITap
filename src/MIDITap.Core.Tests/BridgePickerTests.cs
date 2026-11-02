// BridgePickerTests.cs — 设备下拉框编排的用例：空行什么时候出现、标签什么时候带说明
//
// 这些输入决定了"没插琴的用户看到什么"：
//   没有空行时，列表第一项就是 MIDIBridge，用户会以为那是一个被识别到的设备
//   标签判断反了时，用户会被引向"下载"而不是"重装"，而这两步完全不同
//   两者都不抛异常、也不在日志里留下痕迹，只能靠这里的断言钉住
//
// 断言的是"输入到结论"，不是实现细节：用例只调用 Describe，不检查它的内部分支
//
// BridgePickerTests.cs — cases for laying out the device drop-down: when the blank row appears, when the label is annotated
//
// These inputs decide what a user with no keyboard plugged in sees:
//   Without the blank row the first item is MIDIBridge itself, and the user reads that as a device that was detected
//   With the label test inverted the user is pointed at a download rather than a reinstall, and those are different steps
//   Neither slip throws or leaves a trace in the log, so only the assertions here pin them down
//
// What is asserted is "input to outcome", not internals: the cases call Describe and never inspect its branches

using MIDITap.Core.Bridge;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class BridgePickerTests
{
    [Fact]
    public void No_physical_device_asks_for_a_blank_row()
    {
        // 一台物理设备都没有：第一行留空，MIDIBridge 排在它下面
        // 扩展装没装都成立，因此两种都断言
        //
        // No physical device at all: the first row stays blank and MIDIBridge sits below it
        // This holds whether the extension is installed or not, so both are asserted
        Assert.True(BridgePicker.Describe(0, installed: false, protocolSupported: true).ShowEmptyRow);
        Assert.True(BridgePicker.Describe(0, installed: true, protocolSupported: true).ShowEmptyRow);
    }

    [Fact]
    public void One_physical_device_needs_no_blank_row()
    {
        // 有一台琴时第一行就是它，再插空行会让下拉框多出一个空白项
        //
        // With one piano the first row is that piano, and an extra blank row would just add an empty item
        Assert.False(BridgePicker.Describe(1, installed: false, protocolSupported: true).ShowEmptyRow);
        Assert.False(BridgePicker.Describe(4, installed: true, protocolSupported: true).ShowEmptyRow);
    }

    [Fact]
    public void Negative_count_reads_as_no_device()
    {
        // 负数只可能来自调用方自己的计算失误，按"没有设备"处理与它的本意一致
        // 这条用例的价值在于钉住"不抛异常"，而不是给出一个有用的结果
        //
        // A negative count can only come from the caller's own arithmetic slip, and reading it as "no devices" matches
        // its intent
        // The value of this case is pinning down that nothing throws, not that the result is useful
        Assert.True(BridgePicker.Describe(-1, installed: false, protocolSupported: true).ShowEmptyRow);
    }

    [Fact]
    public void Installed_with_a_supported_protocol_uses_the_plain_label()
    {
        Assert.Equal(
            BridgePickerLabel.Normal,
            BridgePicker.Describe(1, installed: true, protocolSupported: true).BridgeLabel);
    }

    [Fact]
    public void Installed_with_an_unsupported_protocol_uses_the_annotated_label()
    {
        // 装着但对不上：条目要带说明，把用户引向重装
        //
        // Installed but mismatched: the entry carries the annotation that points the user at a reinstall
        Assert.Equal(
            BridgePickerLabel.Unavailable,
            BridgePicker.Describe(1, installed: true, protocolSupported: false).BridgeLabel);
    }

    [Fact]
    public void Not_installed_ignores_the_protocol_flag()
    {
        // 没装时"协议是否受支持"无从谈起，两个取值都必须得到普通标签
        // 若这里返回 Unavailable，未安装的用户会看到"不可用"而不是"可以安装"，安装入口等于被关掉
        //
        // Before installation "is the protocol supported" has no meaning, so both values must yield the plain label
        // Returning Unavailable here would show "unusable" instead of "installable" to a user with nothing installed,
        // which amounts to closing the install entry point
        Assert.Equal(
            BridgePickerLabel.Normal,
            BridgePicker.Describe(1, installed: false, protocolSupported: false).BridgeLabel);
        Assert.Equal(
            BridgePickerLabel.Normal,
            BridgePicker.Describe(1, installed: false, protocolSupported: true).BridgeLabel);
    }
}
