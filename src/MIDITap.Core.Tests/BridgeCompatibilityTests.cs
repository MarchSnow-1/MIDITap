// BridgeCompatibilityTests.cs — 协议版本探测的解析，与三方兼容判定
//
// 解析与判定是同一条链：解析错了，本地主版本就是错的，三方判定的输入也就是错的
// 因此两组用例放在一处
//
// 守卫的重点是输入缺失时的方向：探测不到本地、清单取不到云端，都不能被读成"不支持"
// 把"没拿到数据"判成"不兼容"，界面就会在断网或探测失败时谎报上游不可用
// Unknown 与 RemoteUnsupported 的边界因此单独用几条用例钉住
//
// BridgeCompatibilityTests.cs — parsing the protocol probe, and the three-way compatibility verdict
//
// Parsing and judging form one chain: a wrong parse makes the local major wrong, which makes the three-way input
// wrong, so both groups of cases live in one file
//
// The focus is the direction taken when an input is missing: a failed local probe and an unfetchable remote
// manifest must not read as "unsupported"
// Reading "no data" as "incompatible" makes the UI claim the upstream is unusable while offline or while a probe
// fails
// The Unknown and RemoteUnsupported boundary therefore has cases of its own

using MIDITap.Core.Bridge;
using Xunit;

namespace MIDITap.Core.Tests;

/// <summary>
/// ParseProtocolOutput 的用例：上游 --ipc-protocol 打印单行协议版本
///
/// Cases for ParseProtocolOutput: the upstream --ipc-protocol prints the protocol version alone on one line
/// </summary>
public sealed class ParseProtocolOutputTests
{
    [Fact]
    public void Parses_the_single_line_the_upstream_prints()
    {
        // 上游 main.go 是 fmt.Println(IPCOProtoVersion)，取值是 "1.0.0"（其 ipc.go 的常量）
        //
        // The upstream main.go runs fmt.Println(IPCOProtoVersion), whose value is "1.0.0" (the constant in its ipc.go)
        var parsed = BridgeUpdateChecker.ParseProtocolOutput("1.0.0\n");

        Assert.True(parsed.Parsed);
        Assert.True(parsed.Comparable);
        Assert.Equal("1.0.0", parsed.Protocol);
        Assert.Equal(BridgeProtocolContract.ExpectedSchema, parsed.Schema);
        Assert.Null(parsed.Detail);
    }

    [Fact]
    public void Tolerates_blank_lines_and_surrounding_whitespace()
    {
        // 取第一个非空行：前后空行、行尾多余空白、CRLF 都不影响结果
        // CRLF 单独列出，因为 Windows 上重定向输出可能带 \r
        //
        // The first non-empty line is taken: leading and trailing blank lines, trailing spaces and CRLF change
        // nothing. CRLF gets its own case because redirected output on Windows can carry a \r
        Assert.Equal("1.0.0", BridgeUpdateChecker.ParseProtocolOutput("\n\n  1.0.0  \n\n").Protocol);
        Assert.Equal("1.0.0", BridgeUpdateChecker.ParseProtocolOutput("\r\n1.0.0\r\n").Protocol);
    }

    [Fact]
    public void Reads_the_major_from_any_minor_and_patch()
    {
        // 判定只比主版本（上游文档 §6：同主版本向下兼容）
        // 因此 1.4.2 与 1.0.0 得到同一个 Schema，界面据此不提示"重新安装"
        //
        // Only the major is compared (§6 of the upstream documentation: compatibility within one major)
        // So 1.4.2 and 1.0.0 yield the same Schema, and the UI does not ask for a reinstall
        Assert.Equal(1, BridgeUpdateChecker.ParseProtocolOutput("1.4.2").Schema);
        Assert.Equal(2, BridgeUpdateChecker.ParseProtocolOutput("2.0.0").Schema);
    }

    [Fact]
    public void Rejects_dev_output_because_dev_is_not_a_protocol_version()
    {
        // dev 的 --ipc-protocol 仍打印真实协议常量，因此走到这里说明输出不是那条命令
        // "dev" 没有主版本，按解析失败处理：读不出主版本就给不出兼容结论
        //
        // A dev build still prints the real protocol constant for --ipc-protocol, so reaching this branch means the
        // output did not come from that command
        // "dev" has no major and counts as a parse failure: without a major there is no compatibility verdict
        var parsed = BridgeUpdateChecker.ParseProtocolOutput("dev\n");

        Assert.False(parsed.Parsed);
        Assert.False(parsed.Comparable);
        Assert.Null(parsed.Protocol);
        Assert.Equal(0, parsed.Schema);
        Assert.NotNull(parsed.Detail);
    }

    [Fact]
    public void Rejects_malformed_output()
    {
        // 四种写法都不是"单行协议版本"：非数字开头、带前缀、多出 token、把 -v 的两行一起喂进来
        // 上游用 fmt.Println 打印单个值，出现多个 token 说明这不是那一行
        //
        // None of the four shapes is "a single-line protocol version": a non-numeric start, a prefix, extra
        // tokens, and -v's two lines fed in together
        // The upstream prints one value with fmt.Println, so several tokens mean this is not that line
        Assert.False(BridgeUpdateChecker.ParseProtocolOutput("not-a-version").Parsed);
        Assert.False(BridgeUpdateChecker.ParseProtocolOutput("ipc-protocol 1.0.0").Parsed);
        Assert.False(BridgeUpdateChecker.ParseProtocolOutput("1.0.0 extra").Parsed);
        Assert.False(BridgeUpdateChecker.ParseProtocolOutput("midibridge-client v1.3.1\nipc-protocol 1.0.0").Parsed);
        Assert.False(BridgeUpdateChecker.ParseProtocolOutput(".1").Parsed);
    }

    [Fact]
    public void Rejects_empty_output()
    {
        // 空输出与只有空行的输出都是"没拿到"，不是"协议是 0"
        //
        // Empty output, and output of blank lines alone, both mean "nothing was obtained" rather than "the protocol is 0"
        Assert.False(BridgeUpdateChecker.ParseProtocolOutput(null).Parsed);
        Assert.False(BridgeUpdateChecker.ParseProtocolOutput(string.Empty).Parsed);
        Assert.False(BridgeUpdateChecker.ParseProtocolOutput("  \n\n  ").Parsed);
        Assert.Null(BridgeUpdateChecker.ParseProtocolOutput(null).Protocol);
    }
}

/// <summary>
/// 三方协议兼容判定的用例
///
/// Cases for the three-way protocol compatibility verdict
/// </summary>
public sealed class BridgeCompatibilityTests
{
    /// <summary>本构建支持的协议主版本 / The protocol major this build supports</summary>
    private const int Supported = BridgeProtocolContract.ExpectedSchema;

    /// <summary>一个本构建不支持的主版本 / A major this build does not support</summary>
    private const int OtherMajor = 2;

    // ================================================================ 四种结论 / the four outcomes

    [Fact]
    public void Reports_compatible_when_all_three_sides_agree()
    {
        Assert.Equal(BridgeCompatibility.Compatible, BridgeCompatibilityPolicy.Judge(Supported, Supported, localInstalled: true));
    }

    [Fact]
    public void Reports_local_unsupported_when_the_installed_protocol_is_foreign()
    {
        Assert.Equal(
            BridgeCompatibility.LocalUnsupported,
            BridgeCompatibilityPolicy.Judge(OtherMajor, Supported, localInstalled: true));
    }

    [Fact]
    public void Reports_remote_unsupported_when_the_remote_protocol_is_foreign()
    {
        Assert.Equal(
            BridgeCompatibility.RemoteUnsupported,
            BridgeCompatibilityPolicy.Judge(0, OtherMajor, localInstalled: false));
    }

    [Fact]
    public void Reports_unknown_when_nothing_is_installed_and_the_remote_side_is_unavailable()
    {
        // 这条守着"断网不该被读成上游不兼容"
        //
        // This case keeps "offline" from being read as "the upstream is incompatible"
        Assert.Equal(BridgeCompatibility.Unknown, BridgeCompatibilityPolicy.Judge(0, 0, localInstalled: false));
    }

    // ================================================================ 顺序与边界 / order and bounds

    [Fact]
    public void Local_unsupported_outranks_remote_unsupported()
    {
        // 两边都不兼容时结论是 LocalUnsupported：本地已装却对不上话时，提示重装比告知云端有个装不了的新版更有用
        // 本地不兼容而云端兼容时同样是 LocalUnsupported —— 云端兼容修不好已装的这一份
        //
        // With both sides incompatible the verdict is LocalUnsupported: when the installed copy cannot talk to this
        // Tap, suggesting a reinstall beats announcing a remote release that cannot be installed anyway
        // A locally incompatible copy with a COMPATIBLE remote one is also LocalUnsupported: a compatible remote does
        // not fix the installed copy
        Assert.Equal(
            BridgeCompatibility.LocalUnsupported,
            BridgeCompatibilityPolicy.Judge(OtherMajor, OtherMajor, localInstalled: true));
        Assert.Equal(
            BridgeCompatibility.LocalUnsupported,
            BridgeCompatibilityPolicy.Judge(OtherMajor, Supported, localInstalled: true));
    }

    [Fact]
    public void Ignores_the_local_schema_when_nothing_is_installed()
    {
        // 防的是调用方传了一个残留的本地值：未安装时只看云端
        //
        // Guards against a caller passing a stale local value: nothing installed means the remote side alone decides
        Assert.Equal(
            BridgeCompatibility.Compatible,
            BridgeCompatibilityPolicy.Judge(OtherMajor, Supported, localInstalled: false));
    }

    [Fact]
    public void Treats_an_unavailable_remote_side_as_compatible_when_the_local_side_works()
    {
        // "能不能对接"只取决于两个已确定的主版本，云端未知不影响本地这一份现在可用
        // 云端有没有新版本是另一件事，由更新检查单独回答
        //
        // "Can the two sides talk" depends on the two definite majors alone, and an unknown remote does not affect
        // the local copy being usable right now
        // Whether a newer release exists is a different question, answered by the update check
        Assert.Equal(
            BridgeCompatibility.Compatible,
            BridgeCompatibilityPolicy.Judge(Supported, 0, localInstalled: true));
    }

    [Fact]
    public void Does_not_read_a_negative_remote_schema_as_unsupported()
    {
        // 负数与 0 同义地表示"云端未知"：非正值不得被判成 RemoteUnsupported
        //
        // Negative and zero alike mean "the remote side is unknown": a non-positive value is not judged
        // RemoteUnsupported
        Assert.Equal(
            BridgeCompatibility.Compatible,
            BridgeCompatibilityPolicy.Judge(Supported, -1, localInstalled: true));
        Assert.Equal(BridgeCompatibility.Unknown, BridgeCompatibilityPolicy.Judge(0, -1, localInstalled: false));
    }

    [Fact]
    public void Is_supported_compares_the_major_alone()
    {
        // 上游文档 §6 只承诺同主版本向下兼容，因此判据就是"等于 ExpectedSchema"
        //
        // §6 of the upstream documentation promises compatibility within one major alone, so the test is exactly
        // equality with ExpectedSchema
        Assert.True(BridgeCompatibilityPolicy.IsSupported(Supported));
        Assert.False(BridgeCompatibilityPolicy.IsSupported(OtherMajor));
        Assert.False(BridgeCompatibilityPolicy.IsSupported(0));
    }

    // ================================================================ 探测结果重载 / the probe overload

    [Fact]
    public void The_probe_overload_reads_a_failed_probe_as_not_installed()
    {
        // 探测失败（Parsed=false）时 Schema 为 0，把它当成"已装"会让结论变成 LocalUnsupported
        // 那是把"探测不出"谎报成"协议不对"，因此重载只在 Comparable 时才算已装
        //
        // On a failed probe (Parsed false) Schema is 0, and counting that as installed would yield LocalUnsupported,
        // mistaking "could not probe" for "wrong protocol"
        // The overload therefore counts a result as installed only when it is Comparable
        var failed = new BridgeInstalledProtocol(null, false, "the probe timed out");

        Assert.Equal(BridgeCompatibility.Compatible, BridgeCompatibilityPolicy.Judge(failed, Supported));
        Assert.Equal(BridgeCompatibility.Unknown, BridgeCompatibilityPolicy.Judge(failed, 0));
    }

    [Fact]
    public void The_probe_overload_agrees_with_the_explicit_overload()
    {
        // 两条入口对同一个输入给出同一结论：避免界面与进程各用一个入口却吵起来
        //
        // The two entry points give one verdict for one input, so the UI and the process cannot disagree by using
        // different ones
        var installed = new BridgeInstalledProtocol("1.0.0", true, null);
        var foreign = new BridgeInstalledProtocol("2.0.0", true, null);

        Assert.Equal(
            BridgeCompatibilityPolicy.Judge(installed.Schema, Supported, localInstalled: true),
            BridgeCompatibilityPolicy.Judge(installed, Supported));
        Assert.Equal(
            BridgeCompatibilityPolicy.Judge(foreign.Schema, Supported, localInstalled: true),
            BridgeCompatibilityPolicy.Judge(foreign, Supported));
    }

    [Fact]
    public void The_probe_overload_carries_the_schema_of_the_parsed_protocol()
    {
        // 探测结果的 Schema 与解析函数一致：两个函数对同一个 token 给出同一个主版本
        //
        // A probe result's Schema matches the parser: both functions give one major for one token
        Assert.Equal(1, new BridgeInstalledProtocol("1.0.0", true, null).Schema);
        Assert.Equal(2, new BridgeInstalledProtocol("2.3.4", true, null).Schema);
        Assert.Equal(0, new BridgeInstalledProtocol(null, false, "no output").Schema);
    }

    // ================================================================ App 侧字符串重载 / the App-side string overload

    [Fact]
    public void Judge_compatibility_reads_an_unreadable_local_protocol_as_not_installed()
    {
        // exe 存在但 --ipc-protocol 拿不到输出（例如缺运行库）时，本地事实是未知的，不能按"已装且不兼容"处理
        // 只有解析出正数主版本才算拿到本地事实，其余按"本地无信息"处理：
        // 云端未知时给 Unknown（还判断不了），云端兼容时给 Compatible（本地没有反证）
        //
        // When the exe exists but --ipc-protocol yields no output (a missing runtime, for instance), the local fact
        // is unknown and must not be treated as "installed and incompatible"
        // Only a parsed positive major counts as a local fact, and anything else enters as "no local information":
        // an unknown remote side yields Unknown, a compatible one yields Compatible (nothing local contradicts it)
        Assert.Equal(
            BridgeCompatibility.Unknown,
            BridgeCompatibilityPolicy.Judge(localProtocol: null, localInstalled: true));
        Assert.Equal(
            BridgeCompatibility.Compatible,
            BridgeCompatibilityPolicy.Judge(localProtocol: null, localInstalled: true, remoteSchema: Supported));
        Assert.Equal(
            BridgeCompatibility.RemoteUnsupported,
            BridgeCompatibilityPolicy.Judge(localProtocol: null, localInstalled: true, remoteSchema: OtherMajor));
    }

    [Fact]
    public void Judge_compatibility_keeps_the_local_verdict_when_the_protocol_parses()
    {
        // 解析成功就照常参与三方判定
        // 这条与上一条成对，防止修误报时把真正的本地不兼容也放过
        //
        // A successful parse takes part in the three-way verdict as before
        // This pairs with the case above, so fixing the false positive does not also let a genuine local
        // incompatibility through
        Assert.Equal(
            BridgeCompatibility.Compatible,
            BridgeCompatibilityPolicy.Judge("1.0.0", localInstalled: true, remoteSchema: Supported));
        Assert.Equal(
            BridgeCompatibility.LocalUnsupported,
            BridgeCompatibilityPolicy.Judge("2.0.0", localInstalled: true, remoteSchema: Supported));
        Assert.Equal(
            BridgeCompatibility.LocalUnsupported,
            BridgeCompatibilityPolicy.Judge("2.0.0", localInstalled: true));
    }

    [Fact]
    public void Judge_compatibility_ignores_the_local_side_when_nothing_is_installed()
    {
        // 未安装时本地协议号不参与判定，即使调用方误传了一个残留值
        //
        // With nothing installed the local protocol plays no part, even when a caller passes a stale value
        Assert.Equal(
            BridgeCompatibility.Compatible,
            BridgeCompatibilityPolicy.Judge("2.0.0", localInstalled: false, remoteSchema: Supported));
        Assert.Equal(
            BridgeCompatibility.Unknown,
            BridgeCompatibilityPolicy.Judge("2.0.0", localInstalled: false));
    }
}
