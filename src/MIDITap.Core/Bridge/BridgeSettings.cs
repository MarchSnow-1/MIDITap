// BridgeSettings.cs — MIDIBridge 连接设置的取值规则
//
// 规则只有这一处：AppStorage 的 Save* 落盘，界面层在提交前调用这里拦截非法值
// 若把校验写在界面里，规则就跟着界面走，Core 层的读写与界面各判一遍、迟早判得不一样
//
// 规则边界：
//   host —— 非空、不含空白字符、长度有上限
//     这里**不**校验"是不是合法域名/IP"：host 可能是域名、IPv4、IPv6 字面量、或带端口被拆出去的主机名
//     完整的 DNS/IP 语法校验会把内网主机名这类合法取值误判为非法
//     真正的判断发生在连接时：填错的 host 表现为"连不上"，由状态行展示，而不是提交时拦死
//   port —— 1..65535 的整数
//     0 不接受：0 在 client 侧是"由系统分配"（-ipc-listen 127.0.0.1:0），那是 MIDITap 自己起 IPC 时的用法
//     而"连接上游"的端口必须明确指定，让系统随机分配没有意义
//
// BridgeSettings.cs — the value rules for the MIDIBridge connection settings
//
// The rules live in exactly one place: AppStorage's Save* methods put values on disk, and the UI calls here before
// accepting a value
// Should the validation live in the UI instead, the rule would follow the interface, and the Core read/write side and
// the UI would each judge once — and eventually judge differently
//
// Rule boundaries:
//   host — non-empty, no whitespace characters, with a length cap
//   This does NOT check "is it a valid domain/IP": the host may be a domain, an IPv4 or IPv6 literal, or a host name
//   with the port already split off. Full DNS/IP syntax checking would misjudge a legitimate intranet name as invalid
//   The real verdict happens at connect time: a mistyped host shows up as "not connected" on the status line instead of
//   being blocked at submit
//   port — an integer in 1..65535
//   0 is refused: on the client side 0 means "let the system assign" (-ipc-listen 127.0.0.1:0), which is how MIDITap
//   starts its own IPC listener. A port that CONNECTS to the upstream must be explicit; letting the system pick one
//   at random would be meaningless

namespace MIDITap.Core.Bridge;

/// <summary>
/// host 校验的结论；<see cref="None"/> 之外的每个成员对应一条界面文案（i18n 键由界面侧提供）
///
/// The verdict for the host check; every member other than <see cref="None"/> maps to one UI string
/// (i18n keys supplied on the UI side)
/// </summary>
public enum BridgeHostError
{
    /// <summary>通过 / Passed</summary>
    None,

    /// <summary>为空 / Empty</summary>
    Empty,

    /// <summary>含空白字符（空格、制表符等） / Contains whitespace (space, tab, and so on)</summary>
    Whitespace,

    /// <summary>超过长度上限 / Longer than the cap</summary>
    TooLong,
}

/// <summary>端口校验的结论 / The verdict for the port check</summary>
public enum BridgePortError
{
    /// <summary>通过 / Passed</summary>
    None,

    /// <summary>不在 1..65535 范围内 / Outside 1..65535</summary>
    OutOfRange,
}

/// <summary>
/// 一次整组校验的结论；两个字段各自独立，界面可以同时标出两处错误
///
/// The verdict of one combined check; the two fields are independent, so the UI can flag both at once
/// </summary>
public sealed record BridgeSettingsResult(BridgeHostError Host, BridgePortError Port)
{
    /// <summary>两个字段都通过 / Both fields passed</summary>
    public bool Ok => Host == BridgeHostError.None && Port == BridgePortError.None;
}

public static class BridgeSettings
{
    /// <summary>
    /// host 的长度上限
    /// 取 255：文本形式的全限定域名最长 253 字符（RFC 1035 的 255 八位组上限含内部长度字节），
    /// IPv6 字面量最长 45 字符，255 同时盖住两者且留有余量
    ///
    /// The host length cap, set to 255
    /// A fully-qualified domain name is at most 253 characters in textual form (RFC 1035's 255-octet limit counts the
    /// internal length byte), an IPv6 literal is at most 45, so 255 covers both with room to spare
    /// </summary>
    public const int MaxHostLength = 255;

    /// <summary>端口下限 / The port lower bound</summary>
    public const int MinPort = 1;

    /// <summary>端口上限 / The port upper bound</summary>
    public const int MaxPort = 65535;

    /// <summary>
    /// 校验 host。参数按界面文本框原样传入，先修剪再判定
    /// 修剪后为空按 Empty 处理 —— 只含空格的输入等价于没填
    ///
    /// Validates the host; pass the text box's value as-is, it is trimmed before judging
    /// Empty after trimming counts as Empty, since an input of only spaces is the same as nothing entered
    /// </summary>
    public static BridgeHostError ValidateHost(string? host)
    {
        var trimmed = host?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return BridgeHostError.Empty;
        }
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                return BridgeHostError.Whitespace;
            }
        }
        return trimmed.Length > MaxHostLength ? BridgeHostError.TooLong : BridgeHostError.None;
    }

    /// <summary>校验端口 / Validates the port</summary>
    public static BridgePortError ValidatePort(int port)
        => port is >= MinPort and <= MaxPort ? BridgePortError.None : BridgePortError.OutOfRange;

    /// <summary>整组校验（host + port）/ Validates the pair together (host + port)</summary>
    public static BridgeSettingsResult Validate(string? host, int port)
        => new(ValidateHost(host), ValidatePort(port));

    /// <summary>
    /// 把界面文本框里的端口文本解析成端口值
    /// 解析失败或超出范围返回 false 并把 <paramref name="port"/> 置 0
    /// 这里统一用不变文化解析：端口是纯数字，随系统区域变化没有意义
    ///
    /// Parses the port text from the UI's text box into a port value
    /// A parse failure or an out-of-range value returns false and sets <paramref name="port"/> to 0
    /// Parsing uses the invariant culture: a port is nothing but digits, and following the system locale would be
    /// meaningless
    /// </summary>
    public static bool TryParsePort(string? text, out int port)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (int.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value is >= MinPort and <= MaxPort)
        {
            port = value;
            return true;
        }
        port = 0;
        return false;
    }
}
