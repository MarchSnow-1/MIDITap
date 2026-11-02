// BridgeArguments.cs — 启动上游 midibridge-client 时的命令行参数拼装
//
// 参数形状错了的后果是"上游起不来"或"上游按预期之外的模式起来"，
// 这类判断放在 .xaml.cs 里既测不到也 review 不到
//
// 密码不在这里的理由有两条，都写在下面：
//   1) 上游 src/config.go 自己会警告：命令行参数在进程列表与 shell 历史里可见
//      它给的替代路径是环境变量 MIDIBRIDGE_PASSWORD 或 0600 权限的 data/config.json
//   2) MIDITap 这边日志会记录启动参数，密码进参数就等于进日志
// 因此本类的输出**结构上**不含密码位置：Build 只收 host 与 port，没有 password 形参
// 调用方要传密码只能走环境变量，写不出"把密码塞进参数"的这种代码
//
// 每个参数是一个独立字符串，交给 ProcessStartInfo.ArgumentList 逐条 Add
// 返回单个字符串并指望对方再切词，会在含空格的值上出错，而 .NET 也不会再替我们引号
//
// 布尔参数 --midi=false 必须是这一种一体写法
// Go 的 flag 包对布尔项只认 -flag=value：拆成 "--midi" "false" 两个 token 时
// --midi 取默认 true 而被忽略，false 变成一个多余的位置参数，纯 IPC 模式因此开不起来
// 非布尔项（--host/--port/--lang/--ipc-listen）没有这个限制，Go 会把紧随其后的那个 token 当值
//
// Builds the command-line arguments used to start the upstream midibridge-client
//
// A wrong argument shape means the upstream either fails to start or starts in an unintended mode,
// and such decisions in a .xaml.cs are neither testable nor reviewable
//
// Two reasons the password is not part of this, both stated in the upstream itself:
//   1) src/config.go warns that command-line arguments are visible in the process list and shell history,
//      and offers the environment variable MIDIBRIDGE_PASSWORD or a 0600 data/config.json instead
//   2) MIDITap logs the start-up arguments, so a password in the arguments would become a password in the log
// This class therefore has no place to put a password at all: Build takes host and port only, with no
// password parameter. A caller can only pass the secret through the environment, because the code that
// would stuff it into the arguments cannot be written against this signature
//
// Each argument is one string, added one by one to ProcessStartInfo.ArgumentList
// Returning a single string and hoping the caller splits it would break on a value containing a space,
// and .NET would not quote it for us afterwards
//
// The boolean --midi=false must stay in that single-token spelling
// Go's flag package accepts a boolean only as -flag=value: when the caller splits it into "--midi" and
// "false", the flag takes its default true and the stray false becomes a positional argument
// The virtual MIDI port then stays enabled and IPC-only mode is not entered
// The non-boolean flags (--host, --port, --lang, --ipc-listen) have no such limit, since Go takes the
// next token as the value

using System.Globalization;

namespace MIDITap.Core.Bridge;

public static class BridgeArguments
{
    /// <summary>
    /// 密码的环境变量名，取自上游 src/config.go 的 mergeEnv
    /// 这里是**变量名**，不是密码值：值由调用方从 .storage 读出后放进子进程环境
    ///
    /// The environment variable name carrying the password, taken from mergeEnv in the upstream
    /// src/config.go. This is the NAME, not the value: the caller reads the value from .storage and
    /// puts it into the child process environment
    /// </summary>
    public const string PasswordEnvironmentVariable = "MIDIBRIDGE_PASSWORD";

    /// <summary>
    /// 传给上游的语言
    /// 固定 en 有两个原因：状态短语由 BridgeStatusParser 按英文匹配；英文日志便于对照上游源码排查
    /// 用户在设置页看到的中文文案由 MIDITap 自己的 i18n 负责，不依赖上游输出
    ///
    /// The language handed to the upstream. Fixed to en for two reasons: BridgeStatusParser matches the
    /// English phrases, and English output is easier to line up against the upstream source when
    /// something needs diagnosing. The Chinese the user sees in the settings page comes from MIDITap's
    /// own i18n and does not depend on the upstream's output
    /// </summary>
    public const string Language = "en";

    /// <summary>
    /// 本机 IPC 监听地址，端口写 0 交给系统分配
    /// 只绑回环：这条流无鉴权，绑到网卡地址等于把它开放给整个局域网（上游文档第 7 节）
    /// 端口交给系统分配的理由见 BridgeIpcAddressParser 的文件头
    ///
    /// The local IPC bind address, with port 0 so the operating system assigns one
    /// Loopback only: the stream has no authentication, so binding an interface address would expose it
    /// to the whole LAN (section 7 of the upstream document). The reason the port is left to the OS is
    /// in the header of BridgeIpcAddressParser
    /// </summary>
    public const string IpcListenAddress = "127.0.0.1:0";

    /// <summary>
    /// 拼装参数：--host、--port、--lang en、--ipc-listen 127.0.0.1:0、--midi=false
    /// --midi=false 让上游不创建虚拟 MIDI 端口：本功能直接把字节喂给 MIDITap 的状态机
    /// 再开一个端口会让同一份演奏既走远程注入又走本地设备，出现重复按下
    ///
    /// 上游地址为空、含空白字符，或端口不在 1..65535 时抛 ArgumentException
    /// 走到这里仍不合法说明调用方跳过了校验，属于编程错误，不当成可恢复状态处理
    ///
    /// Assembles --host, --port, --lang en, --ipc-listen 127.0.0.1:0 and --midi=false
    /// --midi=false keeps the upstream from creating a virtual MIDI port: this feature hands the bytes
    /// straight to MIDITap's state machine, and opening a port as well would route one performance
    /// through both the remote path and a local device, producing a doubled note
    ///
    /// Throws ArgumentException when the host is empty or contains whitespace, or the port falls
    /// outside 1..65535. Reaching this method with an invalid value means a caller skipped the settings
    /// page's validation, so it is a programming error rather than a recoverable state
    /// </summary>
    public static string[] Build(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port), port, "The server port must be between 1 and 65535");
        }

        // 含空白的 host 在 ArgumentList 里会被引号包住，Go 那边仍是合法值
        // 但那多半是用户误粘了地址栏文本，与其连上一个错误主机不如直接拒绝
        // 控制字符也一并拒掉：\r \n 会污染被记录的启动行
        //
        // A host containing whitespace would survive ArgumentList quoting and still parse on the Go side
        // That input is usually a stray paste from an address bar, so refusing it beats connecting to a
        // wrong host. Control characters go too: \r and \n would corrupt the recorded start-up line
        if (host.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The server host must not contain whitespace", nameof(host));
        }

        return
        [
            "--host", host,
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--lang", Language,
            "--ipc-listen", IpcListenAddress,
            "--midi=false",
        ];
    }
}
