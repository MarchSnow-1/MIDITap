// BridgeArgumentsTests.cs — 启动参数拼装，重点是密码不进去
//
// 这条约束是整件事里风险最高的一条：上游程序连的是远端服务端，密码是它的唯一凭据
// 命令行参数在进程列表与 shell 历史里可见（上游 src/config.go 自己就为此打警告）
// MIDITap 也会记录启动参数，所以密码进了参数等于进了日志
//
// 因此本文件的断言分三层：
//   1) 参数里没有任何一项含 password 字样，也没有以 "--password" 开头的项
//   2) Build 的签名里没有可放密码的位置（反射断言），调用方写不出"把密码塞进参数"的代码
//   3) 布尔项 --midi=false 是一体写法，不拆成 "--midi" "false" 两个 token
// 第 3 条容易被当成多余：Go 的 flag 包对布尔项只认 -flag=value，拆开写时 --midi 取默认 true
// 而那个 false 会变成一个多余的位置参数，纯 IPC 模式因此开不起来
//
// BridgeArgumentsTests.cs — assembling the start-up arguments, above all that the password stays out
//
// This constraint is the highest-risk item in the whole feature: the upstream connects to a remote
// server and the password is its only credential. Command-line arguments are visible in the process
// list and shell history, which the upstream's own src/config.go warns about, and MIDITap logs the
// arguments it passes, so a password in the arguments becomes a password in the log
//
// The assertions therefore work at three levels
//   1) no argument contains the word password and none starts with "--password"
//   2) Build's signature has nowhere to put one (the reflection check), so the code that would pass a
//      password as an argument cannot be written against it
//   3) --midi=false stays a single token rather than "--midi" and "false"
// The third may look fussy: Go's flag package accepts a boolean only as -flag=value, so a split spelling
// makes the flag take its default true, turns the stray false into a positional argument, and IPC-only
// mode is then not entered

using System.Reflection;
using MIDITap.Core.Bridge;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class BridgeArgumentsTests
{
    /// <summary>一个通用的远端地址占位 / A generic placeholder for the remote address</summary>
    private const string ExampleHost = "example.com";

    private const int ExamplePort = 9001;

    [Fact]
    public void Builds_the_expected_arguments_in_order()
    {
        Assert.Equal(
        [
            "--host", "example.com",
            "--port", "9001",
            "--lang", "en",
            "--ipc-listen", "127.0.0.1:0",
            "--midi=false",
        ],
            BridgeArguments.Build(ExampleHost, ExamplePort));
    }

    [Fact]
    public void Never_carries_a_password()
    {
        // 三层断言之第一层：任何一项都不含 password 字样
        // 上游的 --password 与 MIDIBRIDGE_ 前缀的环境变量名都算命中，所以这里也覆盖后者
        // 环境变量名本身不是密码，但它出现在参数里只有一种解释：值也要跟着来了
        //
        // The first of the three levels: no entry contains the word password. Both the upstream's
        // --password flag and the MIDIBRIDGE_ environment variable name would match, and the latter is
        // included because its presence in the arguments has exactly one reading: the value follows
        var arguments = BridgeArguments.Build(ExampleHost, ExamplePort);

        Assert.All(arguments, argument => Assert.DoesNotContain("password", argument, StringComparison.OrdinalIgnoreCase));
        Assert.All(arguments, argument => Assert.False(argument.StartsWith("--password", StringComparison.Ordinal)));
    }

    [Fact]
    public void Build_has_no_parameter_that_could_hold_a_password()
    {
        // 第二层：签名上就无可放之处，host 与 port 之外的形参都不该存在
        // 这一条防的是后来给 Build 加 password 形参的改动
        //
        // The second level: the signature offers no place for one, so no parameter beyond host and port
        // should exist. This guards against a later change that adds a password parameter
        var build = typeof(BridgeArguments)
            .GetMethod(nameof(BridgeArguments.Build), BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal([typeof(string), typeof(int)],
            build.GetParameters().Select(parameter => parameter.ParameterType).ToArray());

        Assert.All(build.GetParameters(), parameter
            => Assert.DoesNotContain("password", parameter.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Password_goes_through_the_environment_variable_name_only()
    {
        // 这里给出的是变量名，值由调用方放进子进程环境
        // 名字取自上游 src/config.go 的 mergeEnv，写错一个字符上游就读不到密码
        //
        // This is the variable NAME, whose value the caller puts into the child environment
        // The name comes from mergeEnv in the upstream's src/config.go, and one wrong character means the
        // upstream cannot read the password
        Assert.Equal("MIDIBRIDGE_PASSWORD", BridgeArguments.PasswordEnvironmentVariable);
    }

    [Fact]
    public void Keeps_the_boolean_midi_flag_as_one_token()
    {
        // 第三层：--midi=false 必须是一体写法，且 false 不单独成项
        // Go 的 flag 只认 -flag=value 的布尔写法，拆开写时 --midi 取默认 true，虚拟端口因此仍被启用
        //
        // The third level: --midi=false stays one token and false is not an argument of its own
        // Go accepts a boolean only as -flag=value, so the split spelling lets --midi take its default
        // true and the virtual port stays enabled
        var arguments = BridgeArguments.Build(ExampleHost, ExamplePort);

        Assert.Contains("--midi=false", arguments);
        Assert.DoesNotContain("false", arguments);
        Assert.DoesNotContain("--midi", arguments);
    }

    [Fact]
    public void Binds_the_ipc_stream_to_loopback_with_an_assigned_port()
    {
        // 端口写 0：由系统分配，实际地址从 "IPC listening on ..." 那一行解析（见 BridgeIpcAddressParser）
        // 只绑回环的理由见 BridgeArguments 的注释（这条流无鉴权）
        //
        // Port 0 lets the operating system assign one, and the real address comes from the "IPC listening
        // on ..." line (see BridgeIpcAddressParser). Loopback only, since the stream has no authentication
        var arguments = BridgeArguments.Build(ExampleHost, ExamplePort);
        var index = Array.IndexOf(arguments, "--ipc-listen");

        Assert.True(index >= 0);
        Assert.Equal("127.0.0.1:0", arguments[index + 1]);
    }

    [Fact]
    public void Asks_the_upstream_for_english_output()
    {
        // 固定 en 是 BridgeStatusParser 能按英文短语匹配的前提
        //
        // Fixing the language to en is what lets BridgeStatusParser match the English phrases
        var arguments = BridgeArguments.Build(ExampleHost, ExamplePort);
        var index = Array.IndexOf(arguments, "--lang");

        Assert.True(index >= 0);
        Assert.Equal("en", arguments[index + 1]);
    }

    [Fact]
    public void Uses_invariant_culture_for_the_port()
    {
        // 端口是数字，不能按当前区域设置写成带分组符或别的数字形式
        // 这里用一个具体值确认输出就是十进制文本
        //
        // A port must not take a group separator or another digit form from the current culture
        // One concrete value confirms the output is plain decimal text
        Assert.Equal("65535", BridgeArguments.Build(ExampleHost, 65535)[3]);
    }

    [Fact]
    public void Rejects_an_empty_or_whitespace_host()
    {
        Assert.Throws<ArgumentException>(() => BridgeArguments.Build(string.Empty, ExamplePort));
        Assert.Throws<ArgumentException>(() => BridgeArguments.Build("   ", ExamplePort));
        Assert.Throws<ArgumentNullException>(() => BridgeArguments.Build(null!, ExamplePort));
    }

    [Fact]
    public void Rejects_a_host_containing_whitespace()
    {
        // 多半是误粘了地址栏文本（"https://example.com/path" 前后带空格之类）
        // 与其连到错误主机不如直接拒绝
        //
        // Such input is usually a stray paste from an address bar. Connecting to a wrong host is worse
        // than refusing
        Assert.Throws<ArgumentException>(() => BridgeArguments.Build("example .com", ExamplePort));
        Assert.Throws<ArgumentException>(() => BridgeArguments.Build("example.com\nextra", ExamplePort));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Rejects_a_port_outside_the_valid_range(int port)
    {
        // 界面输入先由 BridgeSettings 校验，这里是最后一道
        // 走到这里仍不合法说明调用方跳过了校验，按编程错误抛出而不是静默修正
        //
        // The settings page validates first through BridgeSettings; this is the last line
        // An invalid value reaching here means that check was skipped, so it throws as a programming
        // error rather than being corrected quietly
        Assert.Throws<ArgumentOutOfRangeException>(() => BridgeArguments.Build(ExampleHost, port));
    }
}
