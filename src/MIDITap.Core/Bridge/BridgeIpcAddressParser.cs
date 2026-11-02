// BridgeIpcAddressParser.cs — 从上游启动日志里解析 IPC 的实际监听地址
//
// 为什么需要解析日志行而不是自己指定端口：上游 -ipc-listen 接受 127.0.0.1:0
// 端口为 0 时由系统分配，分配结果只出现在它打印的这一行里
// 让 MIDITap 自己挑一个固定端口会引入两个新问题：
//   1) 端口可能已被别的进程占用，而 MIDITap 无法区分"占用"与"上游没起来"
//   2) 同一台机器上多个 MIDITap 实例会互相抢那个固定端口
// 用 :0 加解析日志行，这两个分支都不存在
//
// 只解析，不约束上游能绑定哪些地址：地址由用户在设置页填写，非回环绑定属于他的决定
// 上游在这种情况下自己会在同一行标注 reachable from network，本类只给出结构化结果，不判断它是否属于安全提示
//
// Parses the actual IPC listen address out of the upstream program's start-up log
//
// Why parse the log line instead of naming a port: -ipc-listen accepts 127.0.0.1:0, and port 0 means
// the operating system picks one. That choice only appears in the line the program prints
// Picking a fixed port here would introduce two problems of its own:
//   1) the port may already belong to another process, and MIDITap could not tell that apart from
//      "the upstream failed to start"
//   2) several MIDITap instances on one machine would contend for that same fixed port
// With :0 plus log parsing, neither branch arises
//
// This only parses; it does not constrain what the upstream may bind to
// The address is the user's entry on the settings page, and a non-loopback bind is their decision
// The upstream marks that case on this very line with "reachable from network"; this class returns structure
// only and leaves it to the caller to decide whether that is worth surfacing

using System.Text.RegularExpressions;

namespace MIDITap.Core.Bridge;

/// <summary>
/// 解析出来的监听端点
/// Host 已去掉 IPv6 的方括号，可直接交给 TcpClient.Connect
/// Port 为 0 的端点不会出现在本类型里：TrySplit 把它当作解析失败，由调用方拿到 null
///
/// The parsed listen endpoint. Host has its IPv6 brackets stripped and goes straight to
/// TcpClient.Connect. An endpoint with port 0 does not appear in this type: TrySplit treats it as a
/// parse failure, so the caller receives null instead
/// </summary>
/// <param name="Host">主机部分，IPv6 不含方括号 / The host part, without IPv6 brackets</param>
/// <param name="Port">实际端口 / The port actually bound</param>
public sealed record BridgeEndpoint(string Host, int Port);

public static partial class BridgeIpcAddressParser
{
    /// <summary>
    /// 上游记录监听成功的那一行的前缀，取自它 src/ipc.go 的 golog.Info("IPC listening on " + ...)
    /// 大小写敏感：这一行由上游代码写死，不走它的 i18n，因此语言设置不会影响它
    ///
    /// The prefix of the line the upstream logs when it starts listening, taken from
    /// golog.Info("IPC listening on " + ...) in its src/ipc.go
    /// The match is case-sensitive: the upstream hard-codes that line and does not translate it,
    /// so its language setting cannot change it
    /// </summary>
    public const string ListenLinePrefix = "IPC listening on ";

    /// <summary>
    /// 抓取前缀后面到第一个空白或左括号之前的地址片段
    /// 括号用于收尾那段范围说明，如 "(loopback only, read-only)"，地址本身不含括号外的空白
    /// IPv6 的地址带方括号，因此片段里的 ] 也算地址的结尾
    ///
    /// Captures the address run after the prefix, up to the first space or opening bracket
    /// The bracket ends the scope note such as "(loopback only, read-only)", and an address itself
    /// contains no space outside brackets. An IPv6 address carries brackets, so ] also ends the run
    /// </summary>
    [GeneratedRegex(@"IPC listening on (?<addr>[^\s(]+)")]
    private static partial Regex ListenLineRegex();

    /// <summary>
    /// 从一行日志解析监听端点；不是这一行、或地址没有端口、或端口不是 1..65535 都返回 null
    /// 返回 null 而不是抛异常：调用方在逐行扫 stdout，噪声行是常态
    ///
    /// Parses one log line, returning null when it is not the listening line, the address carries no
    /// usable port, or the port falls outside 1..65535. Null rather than an exception, because the
    /// caller is scanning stdout line by line and noise lines are the normal case
    /// </summary>
    public static BridgeEndpoint? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        // 用正则而不是 IndexOf + Substring：上游在这一行前面还会带 "[时间] info: " 的日志格式
        // 位置不固定，锚定前缀再取到分隔符为止的片段更稳
        //
        // A regex rather than IndexOf and Substring: the upstream prefixes this line with
        // "[time] info: ", so the position is not fixed, while "after the prefix up to a separator" is
        var match = ListenLineRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        // 末尾的 ] 是 IPv6 的收尾括号，不是地址字符；剩下的片段交给下面的拆分逻辑
        // ":9003" 这种空主机的写法也在这里保留，host 为空串表示"监听所有接口"
        //
        // The trailing ] closes an IPv6 literal rather than belonging to the address, and the split
        // below handles the rest. The bare ":9003" form stays intact, where an empty host means all interfaces
        return TrySplit(match.Groups["addr"].Value, out var host, out var port)
            ? new BridgeEndpoint(host, port)
            : null;
    }

    /// <summary>
    /// 只解析地址部分，供调用方已经知道地址的场景复用（例如读 .storage 里记录的地址）
    /// 要求地址带端口：上游文档第 2 节写明缺少端口会被它自己拒绝启动
    ///
    /// Parses a bare address for callers that already have one, such as a recorded address from .storage
    /// A port is required, since section 2 of the upstream document states it rejects an address without one
    /// </summary>
    public static bool TryParseAddress(string? address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        return !string.IsNullOrWhiteSpace(address) && TrySplit(address.Trim(), out host, out port);
    }

    /// <summary>
    /// 拆分 host 与 port，两个分支分别处理
    ///   含 ] 的写法：IPv6，形如 [::1]:53124 或 [::]:53124，冒号在右括号之后
    ///   其余写法：IPv4 或主机名，按最后一个冒号切；只有一个冒号且前面为空时是 ":port"
    /// 端口必须全是数字且在 1..65535：为 0 时说明拿到的是配置里的 ":0" 而不是实际地址，判为失败
    ///
    /// Splits host and port along two branches
    ///   A run containing ] is IPv6, written [::1]:53124 or [::]:53124, with the colon after the bracket
    ///   Anything else is IPv4 or a host name, cut at the last colon; a single colon with nothing before
    ///   it is the ":port" form
    /// The port must be all digits within 1..65535. A 0 means the value is the configured ":0" rather
    /// than the real bound address, so it fails here
    /// </summary>
    private static bool TrySplit(string address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var lastColon = address.LastIndexOf(':');
        if (lastColon < 0)
        {
            return false;
        }

        var hostPart = address[..lastColon];
        var portPart = address[(lastColon + 1)..];

        // IPv6：剥掉方括号，并对方括号形式做存在性检查
        // 不带方括号的裸 IPv6（如 ::1:53124）在上游文档里不被接受，所以这里不做兼容猜测
        // 用 bracketed 标记它已通过方括号校验，后面那条"主机名不含冒号"的检查就不再适用
        // 剥掉方括号后的 IPv6 字面量本来就有冒号，用主机名规则去检查它会把合法地址判成非法
        //
        // IPv6: strip the brackets and require them in that form. A bracket-less IPv6 such as ::1:53124
        // is not accepted upstream, so no compatibility guess is made for it
        // The bracketed flag records that this run already passed the bracket check, so the later
        // "a host name contains no colon" rule does not apply to it
        // An IPv6 literal after stripping keeps its colons, and judging it by the host-name rule would
        // report a valid address as invalid
        var bracketed = false;
        if (hostPart.EndsWith(']'))
        {
            if (!hostPart.StartsWith('['))
            {
                return false;
            }
            hostPart = hostPart[1..^1];
            bracketed = true;
            if (hostPart.Length == 0 || !IsUsableIpv6Literal(hostPart))
            {
                return false;
            }
        }
        else if (hostPart.Contains(':'))
        {
            // 没有方括号却仍有多个冒号，说明不是文档允许的任何一种写法
            //
            // Several colons without brackets match none of the forms the document allows
            return false;
        }

        if (portPart.Length == 0 || portPart.Length > 5)
        {
            return false;
        }
        foreach (var character in portPart)
        {
            // 手工判数字而不是 int.TryParse：TryParse 会接受 "+80"、" 80" 与 "80.0"
            // 这些在这里都不是端口写法，接受它们等于把畸形行读成一个地址
            //
            // Digits are checked by hand rather than with int.TryParse, which accepts "+80", " 80" and "80.0"
            // None of those is a port spelling here, and accepting them would read a malformed line as an address
            if (character is < '0' or > '9')
            {
                return false;
            }
        }
        if (!int.TryParse(portPart, out port) || port is < 1 or > ushort.MaxValue)
        {
            return false;
        }

        // host 允许为空串（":port" 形式，上游据此监听所有接口）
        // 非空且不是方括号 IPv6 时再做一次主机名语法检查
        //
        // An empty host is allowed, since ":port" means all interfaces upstream
        // A non-empty host that is not a bracketed IPv6 literal gets the host-name syntax check
        if (!bracketed && hostPart.Length > 0 && !IsUsableHost(hostPart))
        {
            port = 0;
            return false;
        }
        host = hostPart;
        return true;
    }

    /// <summary>
    /// 方括号内 IPv6 字面量的字符约束：只允许十六进制数字、冒号与点
    /// 点用于 IPv4 映射写法（如 ::ffff:127.0.0.1），上游的 net.ParseIP 同样接受
    /// 这里不校验组数：那是 IPAddress.TryParse 的职责，重复实现只会引入两套不一致的规则
    ///
    /// The character rule for an IPv6 literal inside brackets: hex digits, colons and dots only
    /// A dot covers the IPv4-mapped form such as ::ffff:127.0.0.1, which the upstream net.ParseIP accepts too
    /// The group count is not checked here, since that is IPAddress.TryParse's job, and a second
    /// implementation would only introduce a second, divergent rule
    /// </summary>
    private static bool IsUsableIpv6Literal(string literal)
    {
        foreach (var character in literal)
        {
            if (!(char.IsAsciiHexDigit(character) || character is ':' or '.' or '%'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 主机名与 IP 字面量的共同语法约束：不含空白、方括号与冒号
    /// IPv6 已在调用前去掉方括号，因此这里的冒号检查不会误杀它
    ///
    /// The shared syntax rule for host names and IP literals: no whitespace, brackets or colons
    /// IPv6 has its brackets removed before this point, so the colon check cannot reject it
    /// </summary>
    private static bool IsUsableHost(string host)
    {
        foreach (var character in host)
        {
            if (char.IsWhiteSpace(character) || character is '[' or ']' or ':')
            {
                return false;
            }
        }
        return true;
    }
}
