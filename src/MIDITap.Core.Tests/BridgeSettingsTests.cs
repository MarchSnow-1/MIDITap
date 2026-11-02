// BridgeSettingsTests.cs — MIDIBridge 连接设置的取值规则
//
// host 与 port 是用户手输的，规则一旦松动
// 表现是"填了地址但连不上"，而真正的原因（多了一个空格、端口 0）很难从界面上看出来
// 这里把每个拒绝分支都钉住，界面才能按枚举给出准确的提示
//
// BridgeSettingsTests.cs — the value rules for the MIDIBridge connection settings
//
// Host and port are typed in by the user, and once the rules loosen the symptom is "the address was entered but it
// will not connect", while the real cause (a stray space, port 0) is hard to see from the interface
// Every rejection branch is pinned down here, so the UI can turn the enum into an accurate message

using MIDITap.Core.Bridge;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class BridgeSettingsTests
{
    // ================================================================ host 校验 / Host validation

    [Theory]
    [InlineData("example.com")]
    [InlineData("hostnames.example.internal")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::1")]
    [InlineData("example")]
    public void Accepts_a_plausible_host(string host)
    {
        // 内网简短名、IPv4、IPv6 字面量都应当通过
        // IPv6 的方括号在这里不是必需的：host 与 port 是两个字段，方括号只在"把地址与端口拼成一个字符串"时才需要
        //
        // Short intranet names, IPv4 and IPv6 literals all pass
        // Brackets around IPv6 are not required here: host and port are separate fields, and brackets matter only when
        // the address and the port are joined into one string
        Assert.Equal(BridgeHostError.None, BridgeSettings.ValidateHost(host));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Rejects_an_empty_host(string? host)
    {
        // 只含空白的输入等价于没填，与服务端"不配置即不连接"的行为一致
        //
        // An input of only whitespace is the same as nothing entered, consistent with "not configured means no
        // connection" on the server side
        Assert.Equal(BridgeHostError.Empty, BridgeSettings.ValidateHost(host));
    }

    [Theory]
    [InlineData("example com")]
    [InlineData("example.com x")]
    [InlineData("example\tcom")]
    public void Rejects_a_host_containing_whitespace(string host)
    {
        // 首尾空白先被修剪（修剪后为空的按 Empty 处理），中间的空白不是笔误就是两个地址粘在一起
        // 必须报出来而不是猜哪一个才是用户要的
        //
        // Surrounding whitespace is trimmed first (what remains empty falls to Empty), but whitespace in the middle is
        // either a typo or two addresses run together, so it is reported rather than guessed at
        Assert.Equal(BridgeHostError.Whitespace, BridgeSettings.ValidateHost(host));
    }

    [Fact]
    public void Trims_surrounding_whitespace_before_judging()
    {
        // 从别处复制粘贴常带首尾空格，那属于粘贴产物，不该让用户自己去删
        //
        // Pasted text often carries surrounding spaces, which are an artefact of the paste and should not require the
        // user to delete them by hand
        Assert.Equal(BridgeHostError.None, BridgeSettings.ValidateHost("  example.com  "));
    }

    [Fact]
    public void Accepts_a_host_of_exactly_the_maximum_length()
    {
        var host = new string('a', BridgeSettings.MaxHostLength);
        Assert.Equal(BridgeHostError.None, BridgeSettings.ValidateHost(host));
    }

    [Fact]
    public void Rejects_a_host_one_character_past_the_maximum()
    {
        // 边界两侧各测一次：上限本身可接受，多一个字符就拒绝
        //
        // Both sides of the boundary are tested: the cap itself passes and one character more is refused
        var host = new string('a', BridgeSettings.MaxHostLength + 1);
        Assert.Equal(BridgeHostError.TooLong, BridgeSettings.ValidateHost(host));
    }

    // ================================================================ 端口校验 / Port validation

    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(9003)]
    [InlineData(65535)]
    public void Accepts_a_port_inside_the_range(int port)
    {
        Assert.Equal(BridgePortError.None, BridgeSettings.ValidatePort(port));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Rejects_a_port_outside_the_range(int port)
    {
        // 0 被拒是刻意的：0 在 client 侧表示"由系统分配"，那是 MIDITap 自己起 IPC 监听时的用法
        // 而连接上游的端口必须明确，随机分配没有意义
        //
        // Refusing 0 is deliberate: on the client side 0 means "let the system assign", which is how MIDITap starts its
        // own IPC listener. A port that CONNECTS to the upstream must be explicit, and a random one would be meaningless
        Assert.Equal(BridgePortError.OutOfRange, BridgeSettings.ValidatePort(port));
    }

    // ================================================================ 整组校验 / The pair

    [Fact]
    public void The_pair_is_ok_only_when_both_fields_pass()
    {
        Assert.True(BridgeSettings.Validate("example.com", 9003).Ok);

        var badHost = BridgeSettings.Validate("", 9003);
        Assert.False(badHost.Ok);
        Assert.Equal(BridgeHostError.Empty, badHost.Host);
        Assert.Equal(BridgePortError.None, badHost.Port);

        var badPort = BridgeSettings.Validate("example.com", 0);
        Assert.False(badPort.Ok);
        Assert.Equal(BridgeHostError.None, badPort.Host);
        Assert.Equal(BridgePortError.OutOfRange, badPort.Port);

        // 两处都错时两个字段各自报出来，界面才能同时标出两个输入框
        //
        // When both are wrong each field reports its own verdict, so the UI can flag both inputs at once
        var both = BridgeSettings.Validate("", 70000);
        Assert.Equal(BridgeHostError.Empty, both.Host);
        Assert.Equal(BridgePortError.OutOfRange, both.Port);
    }

    // ================================================================ 端口文本解析 / Port text parsing

    [Theory]
    [InlineData("9003", 9003)]
    [InlineData("  9003  ", 9003)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    public void Parses_a_valid_port_text(string text, int expected)
    {
        Assert.True(BridgeSettings.TryParsePort(text, out var port));
        Assert.Equal(expected, port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("90.03")]
    [InlineData("9003abc")]
    public void Rejects_an_invalid_port_text(string? text)
    {
        // 解析失败时把 port 置 0 而不是留着上一次的值：调用方据此判断"这一项没填对"
        //
        // A failed parse sets port to 0 rather than leaving a previous value, so the caller can see "this field is not
        // usable"
        Assert.False(BridgeSettings.TryParsePort(text, out var port));
        Assert.Equal(0, port);
    }
}
