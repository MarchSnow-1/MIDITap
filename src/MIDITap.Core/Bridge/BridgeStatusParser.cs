// BridgeStatusParser.cs — 把上游 midibridge-client 的日志行归成结构化状态
//
// 本类只产出枚举，不产出文案：界面上"已连接"还是"已认证"该显示什么字，归 i18n
// 这样翻译改错时不会牵连状态判断，状态判断改错时也不会改出两份不一致的文案
//
// 英文短语来自上游 src/i18n.go 的 enStrings 与 src/main.go、src/wsclient.go 的字面量
// 上游渲染日志时只替换参数占位符，其余文字固定，因此匹配可以在"参数出现前的前缀"上进行
// 例如 index.disconnected 渲染成 "Disconnected from server (code: 1000)"，前缀到 "(code:" 为止固定
//
// 映射表写在这里而不是调用方，是因为它对应的是上游源码里的常量
// 上游改了文案或加了新状态，这里逐条更新，测试里也有逐条用例互相对照
//
// 该接口假设调用方以 --lang en 启动上游（见 BridgeArguments）
// 上游的 index.* 与 wsClient.* 短语随语言变化，而 IPC 运行时日志固定英文
// 传了别的语言时中文短语不会被匹配到，界面会把那一行当作"暂未识别的状态"处理
//
// Classifies upstream log lines into structured states
//
// This produces the enum only and no copy: what the UI should read for each state belongs to the
// i18n files. A copy edit then cannot break state detection, and a detection change
// cannot leave two inconsistent copies behind
//
// The English phrases come from enStrings in the upstream src/i18n.go and the literals in its
// src/main.go and src/wsclient.go. The upstream renders a line by substituting its placeholders into
// otherwise fixed text, so matching can stop right before the parameters appear
// For example index.disconnected renders as "Disconnected from server (code: 1000)", and everything up
// to "(code:" stays fixed
//
// The mapping lives here rather than at the call site because it mirrors constants in the upstream source
// When the upstream changes a phrase or adds a state, this list is updated entry by entry, and the tests
// carry one case per entry so the two sides are compared directly
//
// The class assumes the upstream is started with --lang en (see BridgeArguments)
// The index.* and wsClient.* phrases change with the upstream language while the IPC runtime logs stay
// in English. Under another language the Chinese phrases match nothing here, and the UI treats those
// lines as an unrecognized state

namespace MIDITap.Core.Bridge;

/// <summary>
/// 上游连接生命周期里的状态
/// 成员按它日志里出现的先后排列，None 表示"这一行不属于状态转移"
///
/// The states of the upstream connection lifecycle, ordered as they appear in its log
/// None marks a line that is not a state transition
/// </summary>
public enum BridgeStatus
{
    /// <summary>不是已知的状态行 / Not a known state line</summary>
    None = 0,

    /// <summary>进程启动横幅那一行 / The start-up banner line</summary>
    Starting,

    /// <summary>IPC 已开始监听 / The IPC listener is up</summary>
    ListeningIpc,

    /// <summary>正在连服务端 / Dialing the server</summary>
    Connecting,

    /// <summary>已连上服务端，尚未认证 / The server socket is up, not authenticated yet</summary>
    Connected,

    /// <summary>认证成功，开始接收 MIDI / Authentication passed and MIDI is arriving</summary>
    Authenticated,

    /// <summary>认证失败 / Authentication was refused</summary>
    AuthFailed,

    /// <summary>被服务端踢出 / The server kicked this client</summary>
    Kicked,

    /// <summary>与服务端断开 / The server connection dropped</summary>
    Disconnected,

    /// <summary>正在重连 / A reconnect attempt is under way</summary>
    Reconnecting,

    /// <summary>达到最大重连次数 / The reconnect attempt limit was reached</summary>
    MaxReconnects,

    /// <summary>上游进程退出 / The upstream process is exiting</summary>
    Exited,
}

public static class BridgeStatusParser
{
    /// <summary>
    /// 上游退出时打印的那一行，取自 main.go 的 golog.Info("Goodbye.")
    /// 正常退出与被踢后退出两条路径都打印它，因此它只表示"进程即将结束"
    /// 不区分退出原因：那由前面的 Kicked 或 MaxReconnects 行表达
    ///
    /// The line the upstream prints on the way out, from golog.Info("Goodbye.") in main.go
    /// Both the normal exit and the exit after a kick print it, so it only says "the process is ending"
    /// The cause is not distinguished here, since the earlier Kicked or MaxReconnects line carries it
    /// </summary>
    public const string ExitLine = "Goodbye.";

    /// <summary>
    /// 从一行日志解析状态；不是已知短语时返回 None
    /// 返回枚举而不抛异常：调用方逐行消费 stdout，未知行是常态而不是错误
    ///
    /// Parses one log line, yielding None for anything outside the known phrases
    /// An enum rather than an exception, because the caller consumes stdout line by line
    /// where unknown lines are normal rather than exceptional
    /// </summary>
    public static BridgeStatus Parse(string? line)
    {
        // "Goodbye." 的判断在最前面：main.go 在正常退出与被踢后退出两条路径都打印它
        // 用 EndsWith 而不是整行相等：日志行带着 "[时间] info: " 前缀
        // 退回 "Goodbye." 这一行不含参数，位置也在行尾，因此按结尾判断足够精确
        //
        // "Goodbye." is decided first, since main.go prints it both on a normal exit and after a kick
        // EndsWith rather than whole-line equality, because the line carries a "[time] info: " prefix
        // The exit line has no parameter and sits at the end, so a suffix test is precise enough
        if (line?.TrimEnd().EndsWith(ExitLine, StringComparison.Ordinal) == true)
        {
            return BridgeStatus.Exited;
        }

        foreach (var mapping in Mappings)
        {
            // Ordinal 且区分大小写：短语全部来自上游源码常量，没有随运行环境变大小写的路径
            // 日志行带着 "[时间] info: " 前缀，所以用 Contains 而不是 StartsWith
            //
            // Ordinal and case-sensitive: every phrase is a constant in the upstream source, so no
            // runtime setting changes its casing. The line carries a "[time] info: " prefix, so the
            // test is Contains rather than StartsWith
            if (line?.Contains(mapping.Phrase, StringComparison.Ordinal) == true)
            {
                return mapping.Status;
            }
        }
        return BridgeStatus.None;
    }

    /// <summary>
    /// 短语到状态的映射表，每一条的前缀都截在第一个可变参数之前
    /// wsClient.error、wsClient.connectingFailed、wsClient.unparseable 这类错误行不进表：
    /// 它们描述的是传输层故障而不是连接状态，断开本身已经由 disconnected 那一行表达
    ///
    /// The phrase to status mapping, each prefix cut before its first variable parameter
    /// Error lines such as wsClient.error, wsClient.connectingFailed and wsClient.unparseable stay out:
    /// they describe transport failures rather than connection states, and the disconnected line already
    /// expresses the disconnect
    /// </summary>
    private static (string Phrase, BridgeStatus Status)[] Mappings { get; } =
    [
        // Starting 放在最后：它是最泛化的一条（任何包含 "MIDIBridge Client " 的行都会命中）
        // 让更具体的短语先匹配，可以把这类行误判的机会压到最低
        //
        // Starting sits last because it is the broadest entry, matching any line that contains
        // "MIDIBridge Client ". Letting the more specific phrases match first keeps such misreads rare
        ("IPC listening on ", BridgeStatus.ListeningIpc),
        ("Connecting to server: ", BridgeStatus.Connecting),
        ("Connected to server", BridgeStatus.Connected),
        ("Authenticated, receiving MIDI signals", BridgeStatus.Authenticated),
        ("Authentication failed: ", BridgeStatus.AuthFailed),
        ("Kicked by server: ", BridgeStatus.Kicked),
        ("Disconnected from server (code: ", BridgeStatus.Disconnected),
        ("Reconnecting in ", BridgeStatus.Reconnecting),
        ("Max reconnect attempts reached (", BridgeStatus.MaxReconnects),
        ("MIDIBridge Client ", BridgeStatus.Starting),
    ];
}
