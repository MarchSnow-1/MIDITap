// BridgeService.cs — MIDIBridge Client 扩展的运行器：拉起上游进程、消费它发布的 IPC MIDI 流
// 上游是独立进程（midibridge-client.exe），它连用户的 midibridge-server，把收到的 MIDI
// 额外发布到一个本地 TCP socket（IPC）；本服务消费那条流并转交 BackendService
//
// 线程模型：
//   * StartAsync 与 Stop 由 _sessionGate（SemaphoreSlim）串行化，作用同 BackendService._lifecycleGate
//     启动可能来自主页选择远程源、启动时恢复上次来源或设置页
//     不加锁会让两个 Start 各拉起一个上游进程，各自的 IPC 又都往同一个状态机灌帧
//   * 上游进程的 stdout 与 stderr 各有一个异步排空循环
//     两侧都必须排空：只排一侧时，没人读的那条管道缓冲区写满后上游会被操作系统阻塞在写上
//   * IPC 帧读取跑在一个专用循环里；MIDI 帧交给 Backend.OnBridgeMessage
//     那个方法只在 Backend 的 _gate 内跑状态机并入队注入动作，本类不直接注入按键
//
// 为什么停止进程用 Kill(entireProcessTree: true) 而不是优雅关闭：
//   Windows 上我们发不出可用的 SIGINT：上游用 signal.Notify 等 SIGINT/SIGTERM，而控制台事件只在它自己
//   持有控制台时才会到达，CreateNoWindow 的重定向子进程没有控制台
//   上游以纯 IPC 模式运行（--midi=false）：它没有创建虚拟 MIDI 端口，因此没有需要它自己静音的东西
//   仍被按住的键由 MIDITap 抬起：通路断开时 Backend 会 ReleaseAllKeys 入队并 drain
//
// BridgeService.cs — the runner for the MIDIBridge Client extension: it starts the upstream process and consumes
// the IPC MIDI stream that process publishes
// The upstream is a separate program (midibridge-client.exe) connecting to the user's midibridge-server, which
// additionally publishes the MIDI it receives onto a local TCP socket (IPC)
// This service consumes that stream and hands it to BackendService
//
// Threading model:
//   * StartAsync and Stop are serialised by _sessionGate (a SemaphoreSlim), doing the job _lifecycleGate does in
//     BackendService
//     A start can come from picking the remote source on the Home page, restoring the previous source at startup,
//     or the settings page
//     Without the lock two Starts would each launch an upstream process, and both IPC streams would then feed the
//     same state machine
//   * The upstream process's stdout and stderr each get an asynchronous drain loop
//     Both sides have to be drained: drain only one and the unread pipe's buffer fills, blocking the upstream in
//     the OS on its next write
//   * IPC frames are read in one dedicated loop; MIDI frames go to Backend.OnBridgeMessage
//     That method runs the state machine only inside Backend's _gate and enqueues the injection actions; this
//     class does not inject a key itself
//
// Why stopping the process uses Kill(entireProcessTree: true) rather than a graceful shutdown:
//   On Windows there is no SIGINT we can deliver: the upstream waits on SIGINT/SIGTERM, and a console event only
//   reaches a process that owns its console, while this one is started with no console and redirected pipes
//   The upstream runs in IPC-only mode (--midi=false): it has created no virtual MIDI port, so there is nothing
//   for it to silence itself
//   A key still held is released by MIDITap: when the route drops, Backend enqueues ReleaseAllKeys and drains

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using MIDITap.Core.Bridge;
using MIDITap.Core.Settings;
using MIDITap.Core.Update;

namespace MIDITap.App.Services;

/// <summary>
/// 本服务自身的会话状态（与按上游日志行分类出的 <see cref="BridgeStatus"/> 是两回事）
/// Stopped = 无会话；Starting = 进程已拉起但还没拿到 IPC 端口；Streaming = IPC 已连上且在收帧
/// Reconnecting = IPC 断开正在重试；Exited = 上游进程自己退出；Failed = 会话因故障终止
///
/// This service's own session state, which is a different thing from <see cref="BridgeStatus"/> (a classification
/// of upstream log lines)
/// Stopped: no session; Starting: the process is up but the IPC port is not known yet; Streaming: the IPC
/// connection is up and frames are arriving
/// Reconnecting: the IPC connection dropped and is being retried; Exited: the process exited by itself; Failed:
/// the session ended because of a fault
/// </summary>
public enum BridgeSessionState
{
    Stopped,
    Starting,
    Streaming,
    Reconnecting,
    Exited,
    Failed,
}

/// <summary>
/// 会话失败的可归因分类，做法与 Core.Update.UpdateFailure 一致：分类而不只给一个 bool
/// 用户要分清"没配置"、"没安装"与"协议对不上"，才知道下一步该做什么
///
/// A categorised failure reason, after Core.Update.UpdateFailure's pattern: categories rather than a bare bool
/// The user has to tell "not configured" from "not installed" from "the protocol does not match" to know what to
/// do next
/// </summary>
public enum BridgeFailure
{
    None,
    /// <summary>没填上游地址或端口，功能无从启动 / No upstream host or port, so there is nothing to start</summary>
    NotConfigured,
    /// <summary>.bridge 目录里没有可执行文件 / No executable in the .bridge directory</summary>
    NotInstalled,
    /// <summary>进程拉起失败（文件被删、被安全软件拦截等） / The process could not be started (removed, blocked by security software, and so on)</summary>
    StartFailed,
    /// <summary>IPC 握手的协议主版本不是 1，消费不下去了 / The IPC handshake's protocol major version is not 1, so consuming cannot continue</summary>
    ProtocolMismatch,
}

/// <summary>
/// 一次安装尝试的结果；Detail 供日志展示、Failure 由界面取本地化文案
///
/// The result of one install attempt; Detail goes to the log and Failure supplies the UI's localised copy
/// </summary>
public sealed record BridgeInstallOutcome(
    bool Ok,
    string? Detail = null,
    BridgeInstallFailure Failure = BridgeInstallFailure.None);

/// <summary>
/// MIDIBridge 功能不可用的原因，按 <see cref="MIDITap.Core.Bridge.BridgeCompatibility"/> 的三方可判定的部分收窄成
/// 界面可直接使用的几类；None 表示可用
/// 每个成员对应一条界面文案，"协议未知"没有成员：它不算不可用（见 IsBridgeUsable 的注释）
/// 例外是 Damaged：那时连协议都读不出来，功能确实不可用，因此它单独有一类
///
/// Why the MIDIBridge feature is unusable, narrowed from the three-way
/// <see cref="MIDITap.Core.Bridge.BridgeCompatibility"/> verdict into the classes the UI can use directly;
/// None means usable. Each member maps to one UI string, and "protocol unknown"
/// has no member of its own: it does not count as unusable (see the IsBridgeUsable comment)
/// Damaged is the exception: there even the protocol cannot be read, so the feature really is unusable and it gets a
/// class of its own
/// </summary>
public enum BridgeUnusableReason
{
    /// <summary>可用 / Usable</summary>
    None,

    /// <summary>.bridge 里没有可执行文件 / No executable in .bridge</summary>
    NotInstalled,

    /// <summary>已装但从它读不出任何信息，需要重装 / Installed, yet nothing can be read from it, so a reinstall is needed</summary>
    Damaged,

    /// <summary>已装协议本 Tap 不支持，需要重装 / The installed protocol is unsupported by this Tap, so a reinstall is needed</summary>
    ProtocolUnsupported,

    /// <summary>host/port 没填全 / The host or the port is missing</summary>
    NotConfigured,
}

/// <summary>
/// 一次"按顺序的扩展更新检查"的结论
/// 每个成员都对应界面上一种不同的呈现：跳过（附 Tap 的更新或失败原因）、失败、被协议门禁拦下、无更新、有更新
/// 跳过与失败分成两个成员：跳过不是错误，界面不为它弹错误提示
///
/// The verdict of one ordered extension update check
/// Each member maps to a different presentation in the UI: skipped (with the app's update or failure reason), failed,
/// blocked by the protocol gate, no update, and an update is available
/// Skipped and failed are separate members: a skip is not an error, so the UI raises no error for it
/// </summary>
public enum BridgeUpdateGate
{
    /// <summary>Tap 自身检查失败，因此未检查 Bridge / The app's own check failed, so Bridge was not checked</summary>
    SkippedAfterAppCheckFailure,

    /// <summary>Tap 自身有更新，因此未检查 Bridge / The app itself has an update, so Bridge was not checked</summary>
    SkippedAfterAppUpdate,

    /// <summary>Bridge 检查失败（网络/API/解析） / The Bridge check failed (network, API, parse)</summary>
    BridgeCheckFailed,

    /// <summary>云端 latest 的协议本 Tap 不支持，因此不提示更新 / The latest remote protocol is unsupported here, so no update is offered</summary>
    BlockedByRemoteProtocol,

    /// <summary>本地已装协议的版本本 Tap 不支持，需要重装 / The installed protocol is unsupported, so a reinstall is needed</summary>
    BlockedByLocalProtocol,

    /// <summary>已装但读不出协议号，因此给不出兼容结论 / Installed, yet its protocol could not be read, so no compatibility verdict exists</summary>
    BlockedByLocalProtocolUnknown,

    /// <summary>已装版本无法比较（dev 或探测失败） / The installed version cannot be compared (dev, or a failed probe)</summary>
    BridgeVersionUnknown,

    /// <summary>无更新 / No update</summary>
    NoBridgeUpdate,

    /// <summary>有更新且协议兼容 / An update exists and the protocols are compatible</summary>
    BridgeUpdateAvailable,
}

/// <summary>
/// 有序检查的结果
/// <see cref="AppUpdate"/> 只在跳过于"自身有更新"时非空，供界面直接复用那段信息而不必再查一次
/// <see cref="RemoteProtocol"/> 在"云端协议不支持"时带上具体协议号，让提示能说清是哪个版本
///
/// The ordered check's result
/// <see cref="AppUpdate"/> is non-null only when skipping because the app itself has an update, so the UI can reuse that
/// information without checking again
/// <see cref="RemoteProtocol"/> carries the actual protocol number when the remote protocol is unsupported, letting the
/// message name the version it saw
/// <see cref="RemoteReleaseUrl"/> is filled only when an update is available: the update dialog needs somewhere to send
/// the user, and the two check paths know that address from different places
/// </summary>
public sealed record BridgeUpdateGateResult(
    BridgeUpdateGate Gate,
    UpdateInfo? AppUpdate = null,
    UpdateFailure SelfFailure = UpdateFailure.None,
    string? SelfDetail = null,
    string? InstalledVersion = null,
    string? InstalledProtocol = null,
    string? RemoteVersion = null,
    string? RemoteProtocol = null,
    BridgeManifestArtifact? Artifact = null,
    UpdateAsset? Asset = null,
    UpdateFailure BridgeFailure = UpdateFailure.None,
    string? RemoteReleaseUrl = null);

/// <summary>
/// 一次会话要用的连接参数（从当前选中的服务器解析而来）
/// 密码可为 null：没有密码时上游会以"无密码"尝试连接，能不能连上由服务端决定
///
/// The connection parameters one session uses, resolved from the currently selected server
/// The password may be null: without one the upstream tries to connect anyway, and whether that works is the
/// server's decision
/// </summary>
/// <param name="Server">选中的服务器条目 / The selected server entry</param>
/// <param name="Password">该服务器的密码；未配置为 null / The server's password; null when unset</param>
/// <param name="Index">该服务器在列表中的下标（0 起） / The server's zero-based index in the list</param>
/// <param name="ServerCount">列表长度 / Number of servers in the list</param>
public sealed record BridgeConnection(
    BridgeServer Server,
    string? Password,
    int Index,
    int ServerCount);

public sealed class BridgeService
{
    private readonly string _baseDir;
    private readonly BackendService _backend;

    // 本次会话用的连接参数：StartAsync 解析一次后保存，供启动进程时取密码
    // 不在 StartProcess 里再解析一次：两次解析之间用户可能删掉了那台服务器，那会让
    // "已选定的服务器"与"实际使用的密码"来自不同条目
    //
    // The connection parameters for the current session: StartAsync resolves them once, and the process launch reads
    // the password from here
    // They are not resolved a second time inside StartProcess: the user could delete that server in between, which
    // would leave "the server that was chosen" and "the password actually used" coming from different entries
    private BridgeConnection? _selection;

    // 会话级互斥：Start/Stop 串行化，理由见文件头
    // 用 SemaphoreSlim 而不是 lock：Start 的主体是异步的，Monitor 不能跨 await 持有
    //
    // Session-level mutual exclusion: Start and Stop are serialised, as the file header explains
    // A SemaphoreSlim rather than lock, since the body of Start is asynchronous and a Monitor cannot be held
    // across await
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    // 状态快照的自锁：IsStreaming / State 会被标题栏状态药丸频繁读取
    // 不直接复用 _sessionGate：读取路径不该排在会话切换后面
    //
    // A private lock for the state snapshot: IsStreaming and State are read often by the title-bar status pill
    // _sessionGate is not reused for this, since a read must not queue behind a session switch
    private readonly object _stateGate = new();

    private CancellationTokenSource? _sessionCts;
    private Process? _process;
    private TcpClient? _client;
    private bool _streaming;
    private BridgeSessionState _state = BridgeSessionState.Stopped;

    // 从 stdout 的 "IPC listening on ..." 行解析出的实际地址，由排空循环写入、连接循环取走
    // 之所以放字段而不是局部变量：读地址行的是排空任务，用它的是连接任务，两个任务
    //
    // The address parsed from the stdout line "IPC listening on ...", written by the drain loop and taken by the
    // connect loop
    // It is a field rather than a local because two different tasks are involved: the drain task reads the line,
    // the connect task uses it
    private BridgeEndpoint? _pendingEndpoint;

    /// <summary>上游日志行分类成连接状态时发出（None 已过滤）；本地化在 MainWindow / Raised when an upstream log line classifies into a connection status (None is filtered out); MainWindow localises it</summary>
    public event Action<BridgeStatus>? StatusReceived;

    /// <summary>会话状态变化；本地化在 MainWindow / Raised when the session state changes; MainWindow localises it</summary>
    public event Action<BridgeSessionState>? StateChanged;

    /// <summary>分类后的失败；MainWindow 把它翻译成"用户下一步该做什么" / A categorised failure; MainWindow turns it into "what the user should do next"</summary>
    public event Action<BridgeFailure, string?>? Failed;

    public BridgeService(string baseDir, BackendService backend)
    {
        _baseDir = baseDir;
        _backend = backend;
    }

    /// <summary>IPC 数据面是否真的在收帧；状态药丸据此区分"已在MIDIBridge"与"还在连" / Whether the IPC data plane is actually receiving frames; the status pill tells "remote playing works" from "still connecting" by it</summary>
    public bool IsStreaming
    {
        get
        {
            lock (_stateGate)
            {
                return _streaming;
            }
        }
    }

    /// <summary>当前会话状态（界面与排查用） / The current session state (for the UI and for triage)</summary>
    public BridgeSessionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// 当前选中的服务器及其解析出的连接参数
    /// 每次读取都重新解析：列表与选中项只由设置页改写，这里不做缓存，免得两处状态不同步
    /// 地址为空（刚添加还没填）或列表为空时返回 null，调用方按"未配置"处理
    ///
    /// The currently selected server with its resolved connection parameters
    /// It re-resolves on every read: the list and the selection are written only by the settings page, so keeping a
    /// copy here would just give the two places a chance to disagree
    /// Null when the list is empty or the selected entry has no address yet (freshly added and not filled in), which
    /// the caller treats as "not configured"
    /// </summary>
    public BridgeConnection? Connection
    {
        get
        {
            var selection = BridgeServerStore.ResolveCurrent(_baseDir);
            if (!selection.Configured)
            {
                return null;
            }
            var password = BridgeServerStore.ReadPassword(_baseDir, selection);
            return new BridgeConnection(selection.Server!, password, selection.Index, selection.Servers.Count);
        }
    }

    /// <summary>整份服务器列表 / The whole server list</summary>
    public IReadOnlyList<BridgeServer> Servers => BridgeServerStore.Load(_baseDir);

    /// <summary>选中一台服务器；会话在跑时先停下，由调用方决定是否重连 / Selects a server; a running session is stopped first</summary>
    public void SelectServer(string serverId)
    {
        BridgeServerStore.Select(_baseDir, serverId);
    }

    /// <summary>
    /// 切到另一台服务器并重连：主页下拉与设置页下拉共用这一条路径
    /// 顺序是先停旧会话再写选中键：正在跑的那台若已经被换掉再启动，旧进程会连到新地址上去
    /// 会话本来就在跑（或来源已是远程）时才自动重连：用户只是在主页挑设备、并没有在听远程时，
    /// 替他拉起一个上游进程属于替他做决定
    ///
    /// Switches to another server and reconnects: the home page drop-down and the settings page share this one path
    /// The old session is stopped before the selection key is written: starting while the previous process is still
    /// alive would have it connect to the new address
    /// It reconnects only when a session was already running (or the source is already remote): a user who is merely
    /// browsing the device list on the home page has not asked for an upstream process to be started on their behalf
    /// </summary>
    public void SwitchServer(string serverId)
    {
        var wasRemote = IsActive || _selection is not null;
        if (wasRemote)
        {
            Stop();
        }
        BridgeServerStore.Select(_baseDir, serverId);
        if (wasRemote)
        {
            _ = StartAsync();
        }
    }

    /// <summary>
    /// 切到另一台服务器并开始连接：主页下拉与设置页下拉共用这一条路径
    /// 与 <see cref="SwitchServer"/> 的差别只在"要不要连"：这里无论先前有没有会话都开始连接，
    /// 因为调用方是"用户在列表里点了这一台"，那就是在要求连它
    /// 先停旧会话：旧进程还活着时换掉选中键，它会继续连着上一台地址
    ///
    /// Switches to another server and starts connecting, the one path the home page and the settings page share
    /// It differs from <see cref="SwitchServer"/> only in whether to connect: here a connection starts regardless of
    /// whether a session was already running, because the caller is a user who picked this entry in a list, which is a
    /// request to connect it
    /// The old session stops first: swapping the selection key while the previous process is alive would leave it
    /// connected to the previous address
    /// </summary>
    public void ConnectServer(string serverId)
    {
        // 异步路径：Stop 内同步 Wait 会话锁，而 StartAsync 的持锁段虽然不含 await，
        // 它的尾巴（四个后台任务的首个 await）仍在不同线程上推进；在 UI 线程上同步等这把锁
        // 只要撞上一次就会被吊住，界面表现为"点了没反应"
        // 这里改成 fire-and-forget 的异步序列，让两次会话切换在后台线程上串起来
        //
        // The asynchronous path: Stop waits on the session gate synchronously, and while StartAsync holds no await in
        // its guarded section, its tail advances the four background tasks on other threads; waiting on that gate from
        // the UI thread stalls the moment the two collide, which reads as "clicking does nothing"
        // The switch therefore runs as a fire-and-forget asynchronous sequence, so the two session transitions queue up
        // on a background thread instead
        _ = ConnectServerAsync(serverId);
    }

    private async Task ConnectServerAsync(string serverId)
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopCore();
        }
        finally
        {
            _sessionGate.Release();
        }
        BridgeServerStore.Select(_baseDir, serverId);
        _backend.SelectBridgeSource();
        AppStorage.SaveBridgeSource(_baseDir, "Bridge");
        await StartAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 日志与状态行里指代当前服务器的短标签：命名过就用名字，否则「#序号」
    /// 会话在跑时描述的是"这次会话实际用的那台"，而不是"现在设置页选中的那台" ——
    /// 用户可能在连着 lab 的时候把设置页切到 lab2，此时状态行说 lab2 会把连接对象指错
    /// 空串表示还没有服务器，调用方据此拼出不留悬空的句子
    ///
    /// The short label naming the current server in log lines and status rows: the user-given name, otherwise "#index"
    /// While a session is running it describes the server that session actually uses, not the one currently selected in
    /// settings: a user can switch the settings selection to lab2 while still connected to lab, and naming lab2 there
    /// would point at the wrong target
    /// An empty string means there is no server yet, so the caller builds a sentence with nothing left dangling
    /// </summary>
    public string CurrentServerLabel
    {
        get
        {
            // 会话在跑：报它用的那台
            //
            // A session is running: report the server it uses
            var active = _selection;
            if (active is not null)
            {
                return BridgeServers.ShortLabel(active.Server.Name, active.Index, active.ServerCount);
            }

            var selection = BridgeServerStore.ResolveCurrent(_baseDir);
            return selection.Server is null
                ? string.Empty
                : BridgeServers.ShortLabel(selection.Server.Name, selection.Index, selection.Servers.Count);
        }
    }

    /// <summary>是否有会话在跑（进程存在即算，包含还没连上 IPC 的阶段） / Whether a session is running (a live process counts, including before the IPC connection is up)</summary>
    public bool IsActive
    {
        get
        {
            lock (_stateGate)
            {
                return _process is not null;
            }
        }
    }

    // ------------------------------------------------------------------ start / stop

    /// <summary>
    /// 拉起上游进程并消费它的 IPC 流；已在跑时直接返回（幂等）
    /// 前置条件不满足（未配置、未安装、布局有歧义）时只发出 <see cref="Failed"/>，会话保持 Stopped
    ///
    /// Starts the upstream process and consumes its IPC stream; idempotent while a session is already running
    /// When a precondition fails (not configured, not installed, an ambiguous layout) only
    /// <see cref="Failed"/> is raised and the session stays Stopped
    /// </summary>
    public async Task StartAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsActive)
            {
                return;
            }

            var connection = Connection;
            if (connection is null)
            {
                RaiseFailure(BridgeFailure.NotConfigured, null);
                return;
            }
            var host = connection.Server.Host;
            var port = connection.Server.Port;
            _selection = connection;

            var lookup = LocateExecutable();
            if (lookup.Status != BridgeExeStatus.Found || lookup.Path is null)
            {
                RaiseFailure(BridgeFailure.NotInstalled, null);
                return;
            }

            var process = StartProcess(host, port, lookup.Path);
            if (process is null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            lock (_stateGate)
            {
                _pendingEndpoint = null;
            }
            _sessionCts = cts;
            _process = process;
            SetState(BridgeSessionState.Starting);

            // stdout 与 stderr 都要排空（文件头写了为什么）；进程退出时两边各自读到结尾，循环自然结束
            //
            // Both stdout and stderr are drained (the file header says why); on exit each reaches its end and its
            // loop finishes
            _ = DrainOutputAsync(process, stderr: false, cts.Token);
            _ = DrainOutputAsync(process, stderr: true, cts.Token);
            _ = WatchProcessExitAsync(process, cts.Token);
            _ = ConnectIpcLoopAsync(process, cts.Token);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// 停止会话：断开 IPC、结束读取循环、结束上游进程；没有会话时什么都不做（幂等）
    ///
    /// 按键抬起不在这里做：Backend.OnBridgeDisconnected（读循环的断开路径调用）与
    /// Backend.SelectPhysicalSource / Backend.Stop 各自抬起并 drain
    /// 本类不碰注入，那是 BackendService 那条队列的职责
    ///
    /// Stops the session: the IPC connection is closed, the read loops end and the upstream process is killed
    /// With no session it does nothing (idempotent)
    ///
    /// Releasing keys does not happen here: Backend.OnBridgeDisconnected (called by the read loop's disconnect
    /// path), plus Backend.SelectPhysicalSource and Backend.Stop, each release and drain on their own
    /// This class does not touch injection, which is the job of BackendService's queue
    /// </summary>
    public void Stop()
    {
        // 这里同步等待：StartAsync 的持锁段不含 await（四个后台任务都是发后即走），所以不会被长时间占住
        // 退出路径（窗口关闭）本身是同步的，等一个异步 Stop 反而要把窗口吊着
        //
        // This waits synchronously: the held section of StartAsync contains no await (the four background tasks are
        // fire-and-forget), so the gate is not held long
        // The exit path (window close) is itself synchronous, and awaiting an async Stop would hold the window up
        _sessionGate.Wait();
        try
        {
            StopCore();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private void StopCore()
    {
        Process? process;
        TcpClient? client;
        lock (_stateGate)
        {
            process = _process;
            client = _client;
            _process = null;
            _client = null;
            _pendingEndpoint = null;
            _streaming = false;
            // 会话结束就忘掉这次用的那台：之后的状态行应回到"当前选中项"的说法，
            // 否则停掉 lab 之后它还报 lab，而设置页里选中的可能是另一台
            //
            // Once the session ends the server it used is forgotten, so later status rows fall back to the current
            // selection; otherwise stopping lab would keep reporting lab while settings may have another entry selected
            _selection = null;
        }
        if (process is null)
        {
            SetState(BridgeSessionState.Stopped);
            return;
        }

        _sessionCts?.Cancel();

        // 先关 socket 再 Kill：读循环能尽快从 ReadAsync 里退出来
        //
        // The socket is closed before the Kill so the read loop leaves ReadAsync as soon as it can
        try
        {
            client?.Close();
        }
        catch
        {
            // ignore
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程刚好已经自己退出：Kill 落空属于正常路径，不是错误
            //
            // The process may have exited by itself in the meantime: a Kill that finds nothing is the normal path,
            // not an error
        }
        finally
        {
            process.Dispose();
        }

        _sessionCts?.Dispose();
        _sessionCts = null;
        SetState(BridgeSessionState.Stopped);
        // 会话停掉之后这一路不再有输入：交给 Backend 抬起按键并把通路摘掉
        // 不经过这里的话，"点重连"或"切来源的中途"这两种路径下，最后一次按下会没有对应的抬起帧
        // 相邻的 Backend.SelectPhysicalSource 与 Backend.Stop 也会摘一次通路，重复调用是幂等的
        //
        // Once the session stops this route carries no more input, so Backend releases the keys and detaches the
        // route
        // Without this, the last press would have no matching release frame on two paths: clicking reconnect, and
        // the middle of a source switch
        // The neighbouring Backend.SelectPhysicalSource and Backend.Stop detach the route as well, and a repeat is
        // idempotent
        _backend.OnBridgeDisconnected();
    }

    // ------------------------------------------------------------------ the upstream process

    /// <summary>
    /// 拉起上游进程。参数由 Core 的 BridgeArguments.Build 产出，那里是参数唯一的来源
    /// 密码只经环境变量 MIDIBRIDGE_PASSWORD 传入：命令行参数会出现在进程列表里（任何本机进程都能查到），
    /// 而子进程的环境块只有同用户或管理员能读，暴露面小得多
    ///
    /// Starts the upstream process, with the arguments produced by Core's BridgeArguments.Build, the single source
    /// of them
    /// The password is passed only through the MIDIBRIDGE_PASSWORD environment variable
    /// Command-line arguments show up in the process list, which any local process can query, while a child's
    /// environment block is readable by the same user or an administrator only, a far smaller exposure
    /// </summary>
    private Process? StartProcess(string host, int port, string exePath)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            // 上游把配置锚定到 exe 所在目录（读它的 data/config.json），工作目录跟 exe 走
            // 目录路径取自 AppPaths.BridgeDir：".bridge" 这个名字由 AppPaths 拥有，
            // BridgeLayout 只负责 exe 的挑选
            //
            // The upstream anchors its config to the exe's directory (reading its data/config.json), so the working
            // directory follows the exe
            // The directory path comes from AppPaths.BridgeDir: the ".bridge" name is owned by AppPaths,
            // while BridgeLayout only picks the exe
            WorkingDirectory = AppPaths.BridgeDir(_baseDir),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 上游输出 UTF-8（--lang en 由 BridgeArguments 固定，因此日志行是英文）
            // 不指定编码时 .NET 按控制台代码页解码，某些区域设置下地址行会读成乱码而解析不出端口
            //
            // The upstream writes UTF-8, and --lang en (fixed by BridgeArguments) keeps the lines English
            // Without the encoding, .NET decodes by the console code page, so under some locales the address line
            // turns to mojibake and its port cannot be parsed
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in BridgeArguments.Build(host, port))
        {
            startInfo.ArgumentList.Add(argument);
        }
        var password = _selection?.Password;
        if (!string.IsNullOrEmpty(password))
        {
            startInfo.EnvironmentVariables[BridgeArguments.PasswordEnvironmentVariable] = password;
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception err)
        {
            // 文件可能在上面的检查之后、这里之前被删（用户清理、安全软件隔离）
            //
            // The file can be deleted between the check above and this call (cleaned up by the user,
            // quarantined by security software)
            RaiseFailure(BridgeFailure.StartFailed, err.Message);
            SetState(BridgeSessionState.Failed);
            return null;
        }

        if (process is null)
        {
            // Process.Start 可以返回 null（例如启动被策略拦下），这不代表进程已经拉起来
            //
            // Process.Start can return null (a start blocked by policy, for example), which does not mean the
            // process is running
            RaiseFailure(BridgeFailure.StartFailed, "Process.Start returned no process");
            SetState(BridgeSessionState.Failed);
            return null;
        }
        return process;
    }

    /// <summary>
    /// 排空上游进程的一条输出管道（stdout 或 stderr），按行读
    /// 除了喂给状态解析，行原文进 debug 日志：上游那边的问题大多只能从它自己的输出里看出来
    ///
    /// Drains one output pipe of the upstream process (stdout or stderr) line by line
    /// Besides feeding the status parser, the raw lines go to the debug log
    /// Upstream problems are mostly visible only in the upstream's own output
    /// </summary>
    private async Task DrainOutputAsync(Process process, bool stderr, CancellationToken token)
    {
        try
        {
            var reader = stderr ? process.StandardError : process.StandardOutput;
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                HandleOutputLine(line, stderr);
            }
        }
        catch (OperationCanceledException)
        {
            // 会话停止引发的取消：正常路径
            //
            // Cancellation from stopping the session: the normal path
        }
        catch (Exception err)
        {
            // 进程退出时读管道会抛异常：进程已经不在了，没有东西剩下可排
            //
            // Reading a pipe throws while the process goes away: it is gone, so nothing is left to drain
            Console.Error.WriteLine("[miditap.bridge] output drain ended: " + err.Message);
        }
    }

    private void HandleOutputLine(string line, bool stderr)
    {
        // 上游日志本来不该出现密码，但那输出不由我们控制：落日志前先脱敏一次
        // 这是纵深防御的一层，不是唯一屏障：真正不让密码出现在日志里的是上游自己
        //
        // The upstream has no reason to log the password, yet its output is not ours to control, so the line is
        // redacted before logging
        // This is one layer of defence in depth rather than the only barrier: keeping passwords out of logs is the
        // upstream's own job
        var safe = line;
        var password = _selection?.Password ?? BridgeServerStore.ReadPassword(_baseDir, BridgeServerStore.ResolveCurrent(_baseDir));
        if (!string.IsNullOrEmpty(password) && safe.Contains(password, StringComparison.Ordinal))
        {
            safe = safe.Replace(password, "***", StringComparison.Ordinal);
        }

        // 两侧原文都只进 debug：日常使用不需要它们
        // stderr 不额外提级：上游把错误写在这里，但"有错误输出"不等于"用户需要处理"
        //
        // Both sides' raw lines go to debug only: they are not needed day to day
        // stderr is not escalated: the upstream writes its errors there, but "error output exists" is not the same
        // as "the user must act"
        AppServices.Log.Debug("[midibridge] " + safe);

        // 地址行既是状态行也是端口来源：先解析地址，再按状态分类
        // Parse 对不匹配的行返回 null，因此这里无条件尝试；状态解析只负责分类，不承担端口提取
        //
        // The address line is both a status line and the source of the port: parse the address first, then
        // classify the status
        // Parse returns null for a line that does not match, so it is tried unconditionally
        // The status parser only classifies; it does not carry the port extraction
        if (!stderr && BridgeIpcAddressParser.Parse(line) is { } endpoint)
        {
            lock (_stateGate)
            {
                // 只记第一次拿到的地址：端口在进程一次生命周期里不变，重复行没有新信息
                //
                // Only the first address is kept: the port does not change during one process lifetime, so a repeat
                // line carries nothing new
                _pendingEndpoint ??= endpoint;
            }
        }

        var status = BridgeStatusParser.Parse(line);
        if (status != BridgeStatus.None)
        {
            StatusReceived?.Invoke(status);
        }
    }

    /// <summary>
    /// 等上游进程退出（谁让它退的都一样），然后收掉会话
    /// 用户主动 Stop 已经把 _process 清空，这里的判断让两条路径不会重复收尾
    ///
    /// Waits for the upstream process to exit, whoever caused it, and then closes the session out
    /// A user-initiated Stop has already cleared _process, so the check here keeps the two paths from both
    /// finishing the session
    /// </summary>
    private async Task WatchProcessExitAsync(Process process, CancellationToken token)
    {
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.bridge] exit watch failed: " + err.Message);
            return;
        }

        // 只有"不是用户主动停止"的退出需要收尾：StopCore 已经把 _process 置 null，比较引用即可判定
        //
        // Only an exit not caused by the user's Stop needs closing out: StopCore sets _process to null, so
        // comparing the reference decides which path this is
        bool ours;
        lock (_stateGate)
        {
            ours = ReferenceEquals(_process, process);
            if (ours)
            {
                _process = null;
                _client = null;
                _pendingEndpoint = null;
                _streaming = false;
            }
        }
        if (!ours)
        {
            return;
        }

        _sessionCts?.Cancel();
        SetState(BridgeSessionState.Exited);
        // 上游进程没了，这一路输入随之消失：抬起按键并把通路摘掉
        //
        // With the upstream process gone this route has no more input: release the keys and detach the route
        _backend.OnBridgeDisconnected();
    }

    // ------------------------------------------------------------------ consuming the IPC stream

    /// <summary>
    /// 拿到 IPC 地址后建立连接、消费帧；断开则按指数退避重试，进程退出就停
    /// 为什么退避而不是固定间隔：上游刚启动时（拨号、认证重试）socket 已在监听但还没有数据
    //   固定间隔的重试只是白跑；退避到上限后停在那里等，等真实断开发生再重来
    ///
    /// Once the IPC address is known, connects, consumes frames, retries with exponential backoff after a drop,
    /// and stops when the process is gone
    /// Why backoff rather than a fixed interval: right after the upstream starts (dialling, retrying
    /// authentication) the socket is already listening but carries no data, so fixed-interval retries accomplish
    /// nothing
    /// Backing off to a cap and waiting there, then starting over on a real drop, is enough
    /// </summary>
    private async Task ConnectIpcLoopAsync(Process process, CancellationToken token)
    {
        var backoffMs = 500;
        while (!token.IsCancellationRequested)
        {
            BridgeEndpoint? endpoint;
            lock (_stateGate)
            {
                endpoint = _pendingEndpoint;
            }
            if (endpoint is null)
            {
                // 地址行还没读到（上游日志有缓冲延迟）：等一会儿再看，进程退出则结束
                //
                // The address line has not arrived yet (the upstream's logging is buffered): wait and look again,
                // ending once the process has exited
                try
                {
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (process.HasExited)
                {
                    return;
                }
                continue;
            }

            var client = new TcpClient { NoDelay = true };
            try
            {
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await client.ConnectAsync(endpoint.Host, endpoint.Port, connectTimeout.Token)
                        .ConfigureAwait(false);
                }

                lock (_stateGate)
                {
                    _client = client;
                }
                backoffMs = 500;
                await ConsumeFramesAsync(client, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                client.Close();
                return;
            }
            catch (Exception err)
            {
                // 连不上或读循环异常：记一条 debug，走下面的退避重试
                // 上游没起来、地址被防火墙拦下、或对端主动断开都会落到这里，区分它们的日志行由上游负责
                //
                // Unreachable, or the read loop faulted: one debug line, then the backoff below retries
                // The upstream not being up, the address being blocked, and a peer disconnect all land here, and
                // distinguishing them is the upstream's own logging job
                AppServices.Log.Debug("[miditap.bridge] IPC connect or read failed: " + err.Message);
            }
            finally
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                        _streaming = false;
                    }
                }
                client.Close();
            }

            if (token.IsCancellationRequested || process.HasExited)
            {
                return;
            }

            // 这一路的输入已经断了（进程还在跑，但 IPC 连接断了）：抬起按键，等重连
            // 不抬起的话最后一次按下没有对应的抬起帧会再来，那些键会一直按着
            //
            // This route's input is gone (the process is still running but the IPC connection is not): release the
            // keys and wait for the reconnect
            // Without that, the last press has no matching release frame coming, and those keys stay held down
            _backend.OnBridgeDisconnected();
            SetState(BridgeSessionState.Reconnecting);
            try
            {
                await Task.Delay(backoffMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoffMs = Math.Min(backoffMs * 2, 5000);
        }
    }

    /// <summary>
    /// 一次连接上的帧消费循环
    /// EndOfStream / InvalidFrame 返回后交给连接循环重连；ProtocolMismatch 属于版本问题，重连不会自己变好，
    /// 因此发出失败并停掉整个会话
    ///
    /// The frame-consumption loop for one connection
    /// The connect loop retries after EndOfStream or InvalidFrame; ProtocolMismatch is a version matter that no
    /// retry fixes on its own, so a failure is raised and the whole session is stopped
    /// </summary>
    private async Task ConsumeFramesAsync(TcpClient client, CancellationToken token)
    {
        var reader = new BridgeIpcFrameReader(client.GetStream());
        var announced = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(token).ConfigureAwait(false);
                if (result.HasFrame)
                {
                    if (result.Kind == BridgeIpcFrameKind.Midi && result.Body is { Length: > 0 } body)
                    {
                        if (!announced)
                        {
                            announced = true;
                            // 收到第一条 MIDI 帧才宣布 Streaming：握手成功但没有数据时不应谎报"在收"
                            //
                            // Streaming is announced on the first MIDI frame, so a completed handshake without any
                            // data is not mistaken for "receiving"
                            SetState(BridgeSessionState.Streaming);
                        }
                        // 与 OnInputMessage 同形：由 Backend 在它的 _gate 内跑状态机并入队注入动作
                        //
                        // Shaped like OnInputMessage: Backend runs the state machine inside its own _gate and
                        // enqueues the injection actions
                        _backend.OnBridgeMessage(body);
                    }
                    // 控制帧（hello 之外）与未知 kind 一律忽略：协议把未知类型定义为"跳过并继续读"
                    //
                    // Control frames (beyond hello) and unknown kinds are ignored: the protocol defines an unknown
                    // type as "skip and keep reading"
                    continue;
                }

                switch (result.Status)
                {
                    case BridgeIpcReadStatus.EndOfStream:
                        // 上游关掉了这条连接：连接循环负责重连
                        //
                        // The upstream closed this connection; the connect loop handles reconnecting
                        return;
                    case BridgeIpcReadStatus.InvalidFrame:
                        // 帧已错位：重连才会重建帧边界，因为读循环不能从半个帧里恢复
                        //
                        // The frames are out of alignment: only a reconnect rebuilds the boundary, since the loop
                        // cannot recover from half a frame
                        AppServices.Log.Debug(
                            "[miditap.bridge] invalid frame: " + (result.Detail ?? "unspecified"));
                        return;
                    case BridgeIpcReadStatus.ProtocolMismatch:
                        RaiseFailure(BridgeFailure.ProtocolMismatch, result.Detail);
                        SetState(BridgeSessionState.Failed);
                        Stop();
                        return;
                    default:
                        return;
                }
            }
        }
        finally
        {
            // 退出这段循环就意味着这条连接上没有数据了：数据面标记归位，State 交给连接循环去改成 Reconnecting
            //
            // Leaving this loop means this connection carries no more data: the data-plane flag is reset, while the
            // connect loop is the one that turns State into Reconnecting
            lock (_stateGate)
            {
                if (ReferenceEquals(_client, client))
                {
                    _streaming = false;
                }
            }
        }
    }

    private void SetState(BridgeSessionState state)
    {
        lock (_stateGate)
        {
            _state = state;
            // Streaming 同时置数据面标记：这里是在"确定在收帧"的调用点上，不是靠猜
            // 反过来 false 由各退出路径分别设置，见 ConsumeFramesAsync 与 StopCore
            //
            // The data-plane flag is set together with Streaming, at a call site where frames are known to be
            // arriving rather than inferred
            // Clearing it goes the other way: each exit path sets it, see ConsumeFramesAsync and StopCore
            if (state == BridgeSessionState.Streaming)
            {
                _streaming = true;
            }
        }
        StateChanged?.Invoke(state);
    }

    private void RaiseFailure(BridgeFailure failure, string? detail)
    {
        Failed?.Invoke(failure, detail);
    }

    /// <summary>
    /// 定位 .bridge/ 里的可执行文件，并在第一次定位成功后把文件名记进 .storage
    /// 记录名是启动的唯一依据：目录里被放进别的 exe 也不会改变启动目标
    /// 手动解压的老安装没有记录，第一次定位成功后补上，之后都走记录名这条快路
    ///
    /// 补记失败只写一条日志：记录只让下次启动更确定，缺了它仍会回落到约定名与目录枚举
    ///
    /// Locates the executable in .bridge/ and, on the first successful lookup, records its file name in .storage
    /// The recorded name is the sole basis for starting: another exe dropped into the directory does not change the
    /// start target
    /// A hand-unpacked install has no record, and the name is filled in once the first lookup succeeds, after which
    /// the recorded-name path is taken
    ///
    /// A failed write only logs a line: the record makes the next start deterministic, and without it the conventional
    /// name and the directory scan still apply
    /// </summary>
    private BridgeExeLookup LocateExecutable()
    {
        var lookup = BridgeLayout.ExePath(_baseDir, AppStorage.GetBridgeExecutableName(_baseDir));
        if (lookup.Status != BridgeExeStatus.Found || lookup.Path is null)
        {
            return lookup;
        }

        // 记录名与本次解析到的名字一致时不写：绝大多数启动都落在这里，没必要每次都动一次文件
        //
        // Nothing is written when the recorded name already matches the resolved one: most starts land here, and
        // touching the file on each of them buys nothing
        var resolved = Path.GetFileName(lookup.Path);
        if (!string.Equals(AppStorage.GetBridgeExecutableName(_baseDir), resolved, StringComparison.OrdinalIgnoreCase)
            && !AppStorage.SaveBridgeExecutableName(_baseDir, resolved))
        {
            AppServices.Log.Debug("[miditap.bridge] could not record the executable name");
        }

        return lookup;
    }

    // ------------------------------------------------------------------ installing the upstream

    /// <summary>
    /// 下载并安装上游程序
    /// 检查发布（拿到资产）与落地（下载、校验、解压、改名）是两步，这里把第一步放在本方法里：
    /// 界面只需要一个"点一下"的入口，不必自己拼资产挑选与错误分类
    ///
    /// 检查走 Core 的 BridgeUpdateChecker 而不是 MIDITap 自己的 UpdateChecker：
    /// 后者按 MIDITap 的资产命名约定筛选（后缀 -win-x64.zip、名字须含 MIDITap），对上游的
    /// MIDIBridge-Client-&lt;tag&gt;-windows-amd64.zip 筛不出任何资产
    /// 端点与回退顺序仍来自同一套实现，扩展没有理由再写一份
    ///
    /// 为什么装完不自动启动：用户可能只是想先把扩展装好，而连接上游还依赖 host/port 与密码
    /// 让界面在装完之后刷新卡片、由用户自己决定何时接入，比替他拉起一个可能立刻失败的会话更清楚
    ///
    /// Downloads and installs the upstream program
    /// Checking the release (to get the asset) and landing it (download, verify, extract, rename) are two steps, and
    /// the first happens here: the UI needs one clickable entry rather than assembling asset selection and error
    /// classification itself
    ///
    /// The check goes through Core's BridgeUpdateChecker rather than MIDITap's own UpdateChecker: the latter picks
    /// assets by MIDITap's naming convention (a -win-x64.zip suffix and "MIDITap" in the name) and finds nothing for
    /// the upstream's MIDIBridge-Client-&lt;tag&gt;-windows-amd64.zip
    /// The endpoints and the fallback order still come from one implementation; the extension has no reason to grow
    /// a second one
    ///
    /// Why the session does not start on success: the user may only want the extension in place, while connecting
    /// upstream still depends on host/port and the password
    /// Letting the UI refresh its card and the user decide when to attach is clearer than starting a session on
    /// their behalf that may fail a second later
    /// </summary>
    public async Task<BridgeInstallOutcome> InstallAsync(
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // 清单优先：一次请求就拿到版本、本平台产物、大小与 SHA-256，且不消耗 API 配额
        // 清单取不到才回退到 API/网页路径，两者失败的语义不同，因此回退只在"拿不到清单"时发生
        // 安装不做版本比较：首次安装时盘上还没有上游，读出来的已装版本只会是 unknown，比较只会得出"不可比较"
        //
        // The manifest comes first: one request yields the version, this platform's artifact, its size and SHA-256,
        // spending no API quota
        // Only when the manifest cannot be fetched does it fall back to the API/web path; the two failures read
        // differently, so the fallback happens on "no manifest" alone
        // Installing compares no versions: a first install has no upstream on disk, so the installed version reads as
        // unknown and the comparison can only yield "not comparable"
        var manifestOutcome = await BridgeUpdateChecker
            .CheckManifestAsync(
                null, AppServices.Backend.UpdateOptions, cancellationToken, forInstall: true)
            .ConfigureAwait(false);

        if (manifestOutcome.Info?.Manifest is { } manifest)
        {
            // 协议不兼容时明确拒绝，而不是装上一个对接不上的版本
            // 主版本不同意味着握手帧或帧头可能已变，整条消费链路需要按新协议重新验证
            //
            // An incompatible protocol is refused outright rather than installing a build the two sides cannot use
            // A different major means the handshake or frame header may have changed, so the whole consumption chain
            // needs re-validating against the new protocol
            if (!manifestOutcome.Info.ProtocolCompatible)
            {
                return new BridgeInstallOutcome(
                    false,
                    "the release speaks ipc protocol " + manifest.IpcProtocol +
                    ", which this build cannot consume",
                    BridgeInstallFailure.NoDigest);
            }
        }
        else
        {
            // 清单拿不到：回退到 API/网页路径
            //
            // No manifest: fall back to the API/web path
            return await InstallViaApiAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        if (manifestOutcome.Artifact is null)
        {
            // 远端确实有版本，但没有本平台的产物：这是发布方的问题，用户做什么都没用
            //
            // The remote does have a version but no build for this platform: that is the publisher's side, and nothing
            // the user does changes it
            return new BridgeInstallOutcome(
                false, "the release publishes no build for this platform", BridgeInstallFailure.Download);
        }

        if (manifestOutcome.Artifact.Sha256 is null)
        {
            // 提前拦住，而不是留给 BridgeInstaller 拒绝：用户点完"下载并安装"等一阵才被告知"这个版本装不了"，
            // 与当场说明相比是更差的一步
            //
            // Refused here rather than left to BridgeInstaller: having the user wait out a click only to be told
            // "this release cannot be installed" is a worse step than saying so at once
            return new BridgeInstallOutcome(
                false, "the release publishes no checksum for its artifact", BridgeInstallFailure.NoDigest);
        }

        var landed = await BridgeInstaller
            .InstallAsync(
                manifestOutcome.Artifact,
                manifest.Version,
                manifestOutcome.Artifact.Url!,
                AppServices.Backend.UpdateOptions,
                _baseDir,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return AfterInstall(landed);
    }

    /// <summary>
    /// 清单取不到时的回退：走 Releases API / 网页路径挑资产，再交给同一个落地步骤
    /// 保留这条路的理由：v1.3.1 之前的发布不带清单，而网络中间层拦住清单地址时 API 仍可能通
    ///
    /// The fallback when no manifest can be fetched: pick the asset through the Releases API / web path, then hand it
    /// to the same landing step
    /// The path stays because releases before v1.3.1 carry no manifest, and an intermediary may block the manifest
    /// address while the API still works
    /// </summary>
    private async Task<BridgeInstallOutcome> InstallViaApiAsync(
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        // 同样不做版本比较：这条路也只是"挑一份能装的产物"
        //
        // This path compares no versions either: it only picks an artifact that can be installed
        var outcome = await BridgeUpdateChecker
            .CheckAsync(
                null, AppServices.Backend.UpdateOptions, cancellationToken, forInstall: true)
            .ConfigureAwait(false);

        // 检查失败与没有可用资产两种情形分开：前者等一会儿或配代理，后者是发布方没有提供 Windows 构建
        //
        // A failed check and a missing asset stay apart: the first needs waiting or a proxy, the second means the
        // publisher shipped no Windows build
        if (!outcome.Ok)
        {
            return new BridgeInstallOutcome(
                false, outcome.Detail ?? outcome.Failure.ToString(), BridgeInstallFailure.Download);
        }
        if (outcome.Info?.Asset is null)
        {
            return new BridgeInstallOutcome(
                false, "no downloadable asset in the latest release", BridgeInstallFailure.Download);
        }
        if (!outcome.Info.DigestAvailable)
        {
            return new BridgeInstallOutcome(
                false, "the release publishes no checksum for its asset", BridgeInstallFailure.NoDigest);
        }

        // API 路径给的是 UpdateAsset，改写成清单一侧的形状后交给同一个落地步骤：
        // 落地只关心"名字、地址、大小、摘要"，两边这几项含义一致
        //
        // The API path yields an UpdateAsset, which is reshaped into the manifest side's form before the same landing
        // step: landing only cares about name, address, size and digest, and those mean the same on both sides
        var asset = outcome.Info.Asset;
        var artifact = new BridgeManifestArtifact(
            BridgePlatform.WindowsAmd64.ToString(),
            BridgePlatform.WindowsAmd64.Os,
            BridgePlatform.WindowsAmd64.Arch,
            asset.Name,
            asset.Size,
            asset.Sha256,
            asset.DownloadUrl);

        var landed = await BridgeInstaller
            .InstallAsync(
                artifact,
                outcome.Info.Latest,
                asset.DownloadUrl,
                AppServices.Backend.UpdateOptions,
                _baseDir,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return AfterInstall(landed);
    }

    /// <summary>
    /// 落地之后的共同收尾：成功后作废版本缓存，失败时把原因原样交给界面
    ///
    /// The shared wrap-up after landing: a success invalidates the version cache, and a failure hands its reason to the UI
    /// </summary>
    private BridgeInstallOutcome AfterInstall(BridgeInstallResult installed)
    {
        if (installed.Ok)
        {
            // 装完把两处缓存**立即**作废：下一次读版本/协议会重新探测新装的那个文件
            // 作废与重探都放在这里而不是留给界面：新装的文件与缓存里那个已经不是同一个程序，
            // 中间任何一次读都会拿到旧值
            //
            // Both caches are invalidated at once on success, so the next version or protocol read probes the newly
            // installed file
            // Invalidation and re-probing happen here rather than being left to the UI: the installed file is no longer
            // the program the cache describes, so any read in between would return a stale value
            lock (_stateGate)
            {
                _installedVersion = Core.Update.UpdateChannel.UnknownVersion;
                _installedProtocol = null;
                // 刚换上的文件还没探测过，先把上一个文件的损坏结论作废
                //
                // The freshly landed file has not been probed yet, so the previous file's damaged verdict is dropped
                _buildDamaged = false;
                // 新文件要重新探一轮：旧文件读出的版本与协议都不再作数
                //
                // The new file needs a fresh round: whatever the old one reported no longer applies
                _metadataProbed = false;
            }
        }
        // 失败原因按 BridgeInstaller.Failure 取本地化文案，Detail 只进日志：一个原始异常串摆在界面上
        // 既不好读也不可控
        //
        // The failure reason is localised from BridgeInstaller.Failure; Detail goes to the log alone, because a raw
        // exception string on screen reads badly and is not ours to control
        return new BridgeInstallOutcome(installed.Ok, installed.Detail, installed.Failure);
    }

    /// <summary>
    /// 安装成功后的重探：把新装的版本号与协议号写回存储
    /// 单独成方法而不是塞进 <see cref="AfterInstall"/>：那条路径要返回 <see cref="BridgeInstallOutcome"/>，
    /// 而重探要起两个子进程，把它做成同步收尾会让"安装完成"这一步跟着探测的耗时一起卡住
    ///
    /// 探测失败不改安装结果：文件已经落地，版本/协议读不出来只影响展示与下一次更新比较
    ///
    /// 返回值是两个探测的结果，调用方要拿来比较更新时就不必再探一次
    /// 损坏判定也在这里：两项都读不出来说明这个可执行文件跑不起来，而这个判断只有两个探测都跑完才做得了
    ///
    /// The re-probe after a successful install: writes the newly installed version and protocol back to storage
    /// It is a separate method rather than part of <see cref="AfterInstall"/> because that path returns a
    /// <see cref="BridgeInstallOutcome"/>, while re-probing spawns two subprocesses; making it part of the synchronous
    /// wrap-up would hold "install finished" behind the probes
    ///
    /// A failed probe does not change the install result: the file has landed, and an unreadable version or protocol
    /// only affects display and the next update comparison
    ///
    /// The return value carries both probe results, so a caller that compares versions need not probe again
    /// The damaged verdict is settled here as well: an unreadable version means the executable cannot report itself,
    /// and that judgement is only possible once the version probe has finished
    /// </summary>
    public async Task<(string Version, string? Protocol)> RefreshInstalledMetadataAsync()
    {
        var version = await RefreshInstalledVersionAsync().ConfigureAwait(false);
        var protocol = await RefreshInstalledProtocolAsync().ConfigureAwait(false);
        // 版本读不出就算损坏：版本是那个可执行文件自报身份的唯一途径，读不出来说明它在这台机器上跑不起来
        // 不看协议一侧：协议能读出来也不改变"这个文件跑不完整"的事实，而只凭一个可读的协议号放行，
        // 用户要等到握手或取帧时才看到失败
        // 开发构建不受影响：它的版本号是 dev 而不是 unknown 常量
        //
        // An unreadable version alone means damaged: the version is the only way that executable reports its own
        // identity, and not being able to read it means it does not run on this machine
        // The protocol side is not consulted: a readable protocol does not change the fact that the file does not run
        // properly, and letting it through on one readable number would only show the failure at handshake or frame time
        // A development build is unaffected: its version is "dev", not the unknown constant
        lock (_stateGate)
        {
            _buildDamaged = BridgeLayout.IsInstalled(_baseDir)
                && Core.Update.UpdateChannel.IsUnknown(version);
            // 探过一轮就记下来，之后的调用方走 EnsureInstalledMetadataAsync 直接读缓存
            //
            // Records that one round has run, after which callers go through EnsureInstalledMetadataAsync and read the
            // cache straight away
            _metadataProbed = BridgeLayout.IsInstalled(_baseDir);
        }
        return (version, protocol);
    }

    /// <summary>
    /// 已探过一轮就直接返回缓存，否则探一次
    /// 启动时调一次，主页与设置页都调它，因此整个进程只会起那两个子进程一次：设置页不会为同一件事再卡一下
    ///
    /// 未安装时不探：没有可执行文件可跑，起进程只会立刻失败
    ///
    /// Returns the cache when one round has already run, and probes otherwise
    /// The startup sequence calls it once and so do the home page and the settings page, so the two subprocesses are
    /// spawned once per process and the settings page does not stall on the same work a second time
    ///
    /// Nothing is probed while the extension is not installed: there is no executable to run and the spawn would fail
    /// straight away
    /// </summary>
    public async Task<(string Version, string? Protocol)> EnsureInstalledMetadataAsync()
    {
        lock (_stateGate)
        {
            if (_metadataProbed)
            {
                return (_installedVersion, _installedProtocol);
            }
        }
        return await RefreshInstalledMetadataAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 已安装上游的版本号，供界面显示与更新比较
    /// 探测由 Core 的 BridgeUpdateChecker.InstalledVersionAsync 完成（起一次 -v 读输出），这里只缓存结果：
    /// 界面每次刷新卡片都会读它，而探测要起一个进程
    ///
    /// 为什么不能读 Windows 版本资源：上游是 Go 的 go build 产物，其 CI 只用 ldflags 往 main.version 注入
    /// 一个字符串变量，没有生成任何 Windows 版本资源
    /// 因此 FileVersionInfo 的 ProductVersion/FileVersion 读出来是空的，不是"偶尔读不到"
    ///
    /// The installed upstream version, for the UI and for the update comparison
    /// Core's BridgeUpdateChecker.InstalledVersionAsync does the probing (one -v run reading its output), and this
    /// only caches the result, since the UI reads it on every card refresh while probing spawns a process
    ///
    /// Why the Windows version resource cannot be read: the upstream is a Go build, and its CI only injects a string
    /// variable main.version through ldflags, producing no Windows version resource at all
    /// FileVersionInfo therefore reads an empty ProductVersion/FileVersion, which is the normal outcome rather than
    /// an occasional failure
    /// </summary>
    public string InstalledVersion
    {
        get
        {
            lock (_stateGate)
            {
                return _installedVersion;
            }
        }
    }

    // 缓存的上游版本；unknown 表示没探过或探不出来
    // Cached upstream version; unknown means either not probed yet or not readable
    private string _installedVersion = Core.Update.UpdateChannel.UnknownVersion;

    /// <summary>
    /// 探测已安装上游的版本并写进缓存，返回探测结果
    /// 探测失败（exe 缺失、超时、输出不符）返回 unknown：更新检查把它当成"不可比较"，
    /// 界面显示"版本未知"，而不是谎称已是最新
    ///
    /// Probes the installed upstream version and caches it, returning the probe's result
    /// A failed probe (missing exe, timeout, unexpected output) yields unknown: the update check treats that as
    /// "not comparable" and the UI shows "version unknown" rather than claiming it is up to date
    /// </summary>
    public async Task<string> RefreshInstalledVersionAsync()
    {
        var lookup = LocateExecutable();
        var version = Core.Update.UpdateChannel.UnknownVersion;
        if (lookup.Status == BridgeExeStatus.Found && lookup.Path is not null)
        {
            try
            {
                var probed = await BridgeUpdateChecker
                    .InstalledVersionAsync(lookup.Path)
                    .ConfigureAwait(false);
                // 只缓存能比较的版本；dev 或解析不出的值同样记为 unknown，
                // 因为下游 UpdateChecker 拿 "dev" 去比较只会得到一次无意义的版本判断
                //
                // Only a comparable version is cached; dev or an unparseable value is recorded as unknown too,
                // because feeding "dev" to the downstream comparison yields a meaningless verdict
                if (probed.Comparable && probed.Version is { Length: > 0 })
                {
                    version = probed.Version;
                }
            }
            catch
            {
                // 探测内部已经吞掉了预期异常（见 Core 的说明）；这里兜住任何意外，
                // 版本读不出来不该让设置页出问题
                //
                // The probe already absorbs its expected exceptions (see Core); this catches anything unexpected,
                // since an unreadable version must not break the settings page
                version = Core.Update.UpdateChannel.UnknownVersion;
            }
        }
        lock (_stateGate)
        {
            _installedVersion = version;
        }
        PersistInstalledVersion(version);
        return version;
    }

    /// <summary>
    /// 把探测到的版本号写回 .storage
    /// 只写"探测成功"的值：unknown 不是版本号，写进去会让下次启动误以为查过了，
    /// 因此探测失败时保留上一次的记录，而不是用 unknown 覆盖它
    ///
    /// Writes the probed version back into .storage
    /// Only a successful probe is written: unknown is not a version, and recording it would make the next launch
    /// believe the value had already been read, so a failed probe keeps the previous record instead of overwriting it
    /// </summary>
    private void PersistInstalledVersion(string version)
    {
        if (Core.Update.UpdateChannel.IsUnknown(version))
        {
            return;
        }
        if (!AppStorage.SaveBridgeInstalledVersion(_baseDir, version))
        {
            // 落盘失败只影响下次启动的展示初值，不影响本次运行：不把它当成功能故障
            //
            // A failed write only affects the next launch's initial display, not this run, so it is not a functional fault
            AppServices.Log.Debug("[miditap.bridge] could not persist the installed version");
        }
    }

    // ------------------------------------------------------------------ protocol probing & gating

    /// <summary>
    /// 已安装上游的 IPC 协议版本（探测 --ipc-protocol 并缓存）
    /// 探测失败返回 null，调用方按"协议未知"处理，而不是按"不兼容"处理：
    /// 前者只需要重试或让用户看清状态，后者会禁用整个功能
    ///
    /// The installed upstream's IPC protocol version (probing --ipc-protocol and caching it)
    /// A failed probe returns null, which the caller treats as "protocol unknown" rather than "incompatible":
    /// the former only needs a retry or a clearer status line, while the latter disables the whole feature
    /// </summary>
    public string? InstalledProtocol
    {
        get
        {
            lock (_stateGate)
            {
                return _installedProtocol;
            }
        }
    }

    // 缓存的上游协议号；null 表示没探过或探不出来（与"探到了但为空"区分开）
    // Cached upstream protocol; null means not probed yet or not readable, which is kept apart from "probed and empty"
    private string? _installedProtocol;

    // 已装可执行文件是否跑不起来（版本与协议都读不出）
    // 只在 RefreshInstalledMetadataAsync 里赋值，初值为 false：探测没跑完时不该先把界面标成损坏
    //
    // Whether the installed executable cannot run at all (neither version nor protocol reads back)
    // Only RefreshInstalledMetadataAsync assigns it, and it starts false so that an unfinished probe does not label the
    // UI as damaged in advance
    private bool _buildDamaged;

    // 已装版本与协议是否已经探过一轮
    // 探一次要起两个子进程，所以整个进程只探一次：启动时探完，设置页与主页都读缓存
    // 安装成功后置回 false，让新落地的文件重新探
    //
    // Whether the installed version and protocol have been probed once already
    // One round spawns two subprocesses, so the process probes once: at startup, after which the settings page and the
    // home page read the cache
    // A successful install sets it back to false so the freshly landed file is probed again
    private bool _metadataProbed;

    /// <summary>
    /// 探测已安装上游的协议版本，成功则写回存储并更新缓存
    /// 探测走 Core 的 BridgeUpdateChecker.ProbeProtocolAsync（一次 --ipc-protocol，上游在那一步就退出，无副作用）
    ///
    /// 为什么单独存协议号而不从版本号推：两者回答的问题不同（要不要升级 / 能不能对接），
    /// 上游也可能只升版本号而协议不动
    ///
    /// Probes the installed upstream's protocol version, writing it back to storage and updating the cache on success
    /// Probing goes through Core's BridgeUpdateChecker.ProbeProtocolAsync (one --ipc-protocol run; the upstream exits at
    /// that step, so there is no side effect)
    ///
    /// Why the protocol is stored apart from the version instead of being derived from it: the two answer different
    /// questions (should it be upgraded / can the two sides talk), and the upstream may raise its version without
    /// touching the protocol
    /// </summary>
    public async Task<string?> RefreshInstalledProtocolAsync()
    {
        var lookup = LocateExecutable();
        string? protocol = null;
        if (lookup.Status == BridgeExeStatus.Found && lookup.Path is not null)
        {
            try
            {
                var probed = await BridgeUpdateChecker
                    .ProbeProtocolAsync(lookup.Path)
                    .ConfigureAwait(false);
                if (probed.Comparable && probed.Protocol is { Length: > 0 })
                {
                    protocol = probed.Protocol;
                }
            }
            catch
            {
                // 同版本探测：协议读不出来不该让设置页出问题，按"未知"处理
                //
                // As with the version probe: an unreadable protocol must not break the settings page, so it reads as unknown
                AppServices.Log.Debug("[miditap.bridge] the protocol probe failed unexpectedly");
                protocol = null;
            }
        }

        lock (_stateGate)
        {
            _installedProtocol = protocol;
        }
        if (protocol is not null && !AppStorage.SaveBridgeInstalledProtocol(_baseDir, protocol))
        {
            AppServices.Log.Debug("[miditap.bridge] could not persist the installed protocol");
        }
        return protocol;
    }

    /// <summary>
    /// 本 Tap 的协议判定：本地已装协议是否被支持，以及（可选）云端 latest 的协议是否被支持
    /// 判定本身交给 Core 的 BridgeCompatibilityPolicy：三方比较只写一处，
    /// App 层若自己也拼一遍"主版本是否等于 ExpectedSchema"，两处迟早判得不一样
    ///
    /// <paramref name="localInstalled"/> 为 false（未安装）时 <paramref name="localProtocol"/> 被忽略
    /// <paramref name="remoteSchema"/> 为 0 表示云端未知（清单没取到）：它不等于"云端不兼容"
    ///
    /// This Tap's protocol verdict: whether the installed protocol is supported, and optionally whether the latest
    /// remote protocol is
    /// The verdict itself comes from Core's BridgeCompatibilityPolicy, so the three-way comparison is written once;
    /// were the App layer to assemble its own "does the major equal ExpectedSchema" test, the two would eventually
    /// disagree
    ///
    /// <paramref name="localProtocol"/> is ignored when <paramref name="localInstalled"/> is false, and a
    /// <paramref name="remoteSchema"/> of 0 means the remote side is unknown (no manifest fetched), which is not the
    /// same as the remote side being incompatible
    /// </summary>
    public static BridgeCompatibility JudgeCompatibility(
        string? localProtocol, bool localInstalled, int remoteSchema = 0)
    {
        // 映射规则收在 Core 的字符串重载里：那里连注释一起解释了"装了不等于读得到协议号"
        // App 层只负责把缓存值递过去，不自己拼主版本
        //
        // The mapping rule lives in Core's string overload, which explains alongside its comments why "installed" is
        // not the same as "a protocol number was read"
        // The App layer only passes the cached value along and does not assemble the major itself
        return BridgeCompatibilityPolicy.Judge(localProtocol, localInstalled, remoteSchema);
    }

    // ------------------------------------------------------------------ usability (protocol gate)

    /// <summary>
    /// MIDIBridge 功能当前是否可用
    /// 与 <see cref="IsUsable"/> 的分工：那个只问"装没装、配没配"，这个还问"已装协议本 Tap 支持不支持"
    /// 界面用这个决定是否禁用整个功能并给出"重新安装"入口
    ///
    /// 协议未知**不算**不可用：本地协议号探测不出时按乐观一侧处理，
    /// 因为把它判成不可用会让一个可能可用的安装被禁用，而真正的问题是探测失败
    /// 真正不可用的是 <see cref="BridgeCompatibility.LocalUnsupported"/>，以及版本与协议都读不出的
    /// <see cref="BridgeUnusableReason.Damaged"/>
    ///
    /// Whether the MIDIBridge feature is usable right now
    /// It differs from <see cref="IsUsable"/>: that one only asks "installed and configured", while this also asks
    /// "does this Tap support the installed protocol"
    /// The UI uses this to disable the whole feature and offer a reinstall entry
    ///
    /// An unknown protocol does NOT count as unusable: when the local protocol cannot be probed the optimistic side
    /// wins, because treating it as unusable would disable an install that may well work, while the real problem is the
    /// failed probe
    /// What does make it unusable is <see cref="BridgeCompatibility.LocalUnsupported"/>, plus
    /// <see cref="BridgeUnusableReason.Damaged"/> where neither the version nor the protocol reads back
    /// </summary>
    public bool IsBridgeUsable()
    {
        if (!IsUsable())
        {
            return false;
        }
        return BridgeUnusableReason == BridgeUnusableReason.None;
    }

    /// <summary>
    /// 不可用的原因，供界面区分文案；可用时返回 <see cref="BridgeUnusableReason.None"/>
    ///
    /// The reason it is unusable, so the UI can distinguish its copy; returns <see cref="BridgeUnusableReason.None"/>
    /// when it is usable
    /// </summary>
    public BridgeUnusableReason BridgeUnusableReason
    {
        get
        {
            if (!BridgeLayout.IsInstalled(_baseDir))
            {
                return BridgeUnusableReason.NotInstalled;
            }

            // 装了却读不出任何信息：这个可执行文件跑不起来，后面的协议判断与配置判断都失去意义
            // 结论来自 RefreshInstalledMetadataAsync 的一次成功探测，因此探测没跑完时不会走到这里
            //
            // Installed, yet nothing can be read from it: the executable cannot run, which makes the protocol and
            // configuration verdicts below meaningless
            // The verdict comes from a completed RefreshInstalledMetadataAsync probe, so it cannot be reached before a
            // probe has finished
            lock (_stateGate)
            {
                if (_buildDamaged)
                {
                    return BridgeUnusableReason.Damaged;
                }
            }

            // 本地已装但协议不被本 Tap 支持：这是"装错了版本"，升级解决不了，只能重装或换回旧版
            // 判定走 Core，App 层不重复"主版本等于 1"这类规则
            //
            // Installed, yet its protocol is unsupported by this Tap: the wrong build is in place, and an upgrade does
            // not fix it — only a reinstall or an older build does
            // The verdict comes from Core, so the App layer does not repeat a rule like "the major equals 1"
            var localProtocol = InstalledProtocol;
            if (localProtocol is not null)
            {
                var compatibility = JudgeCompatibility(localProtocol, localInstalled: true);
                if (compatibility == BridgeCompatibility.LocalUnsupported)
                {
                    return BridgeUnusableReason.ProtocolUnsupported;
                }
            }

            // 当前选中的服务器必须存在：没有服务器就没有可连的地址与端口
            // 密码同样是必填项：上游组件不带密码无法通过服务端认证，缺了它这一路连不上
            // 放在最后判：地址端口缺时提示"填地址与端口"，密码缺时提示的才是密码
            //
            // A selected server must exist: without one there is no address or port to connect to
            // The password is required as well: the upstream cannot authenticate to a server without it, so this
            // route cannot connect
            // It is judged last: a missing address or port is reported as such, and only then does a missing password
            // get its own line
            var selection = BridgeServerStore.ResolveCurrent(_baseDir);
            if (!selection.Configured)
            {
                return BridgeUnusableReason.NotConfigured;
            }

            if ((BridgeServerStore.ReadPassword(_baseDir, selection) ?? string.Empty).Length == 0)
            {
                return BridgeUnusableReason.NotConfigured;
            }
            return BridgeUnusableReason.None;
        }
    }

    // ------------------------------------------------------------------ startup restore
    /// <summary>
    /// 启动序列调用：上次运行选的是远程源且扩展可用，就自动接上
    /// 扩展不可用（未装、配置不全）时保持物理源不动，但存储里的选择保留
    /// 用户装好扩展再回来时，他的选择还在
    ///
    /// Called by the startup sequence: when the previous run picked the remote source and the extension is usable,
    /// re-attach it
    /// When the extension is not usable (not installed, or the config is incomplete) the physical source is left
    /// alone while the stored choice is kept
    /// Once the user installs the extension and comes back, their choice is still there
    /// </summary>
    public async Task RestoreAsync()
    {
        if (!string.Equals(AppStorage.GetBridgeSource(_baseDir), "Bridge", StringComparison.Ordinal))
        {
            return;
        }
        if (!IsUsable())
        {
            AppServices.Log.Debug(
                "[miditap.bridge] stored source was Bridge, but the extension is not usable right now");
            return;
        }

        _backend.SelectBridgeSource();
        await StartAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 扩展是否已经可用：已安装、且 host/port 都填好了
    /// 主页的下拉框用前半部分（IsInstalled）决定是否列出远程项，这里还多问一步配置是否齐全
    ///
    /// Whether the extension is usable: installed, with both the host and the port filled in
    /// The Home drop-down uses the first half (IsInstalled) to decide whether to list the remote entry, while this
    /// also asks whether the config is complete
    /// </summary>
    public bool IsUsable()
    {
        if (!BridgeLayout.IsInstalled(_baseDir))
        {
            return false;
        }
        return BridgeServerStore.ResolveCurrent(_baseDir).Configured;
    }

    // ------------------------------------------------------------------ ordered update check

    /// <summary>
    /// 按"先查 MIDITap 自身、确认无更新才查 Bridge"的顺序检查扩展更新
    ///
    /// 为什么顺序是硬性约束而不是优化：
    ///   MIDITap 是**消费方**，Bridge 是**被消费方**，两者之间有一份 IPC 协议契约
    ///   新版 Bridge 可能提升协议主版本，而旧版 MIDITap 只认旧协议
    ///   若先更新 Bridge，用户会得到一个"装好了但一帧都消费不了"的组合：现象是 MIDIBridge 演奏完全没反应，
    ///   而界面还显示"已安装、已连接"，比明确的"暂不支持"难排查得多
    ///   先让 Tap 升到位，协议契约由消费方那一侧先就绪，Bridge 的升级才有意义
    ///   因此：Tap 自身检查失败（网络/API）时**不**查 Bridge
    ///     —— 拿不到 Tap 的更新信息，就无从判断该不该先升 Tap，此时查 Bridge 等于在信息不全时做决定
    ///   Tap 自身有更新时（无论用户是否已安装）也不查 Bridge
    ///     —— 正确顺序是先装 Tap，用户装完重开应用后本方法会再走到 Bridge 这一步
    ///
    /// Checks for extension updates in the order "MIDITap itself first, Bridge only once it is confirmed current"
    ///
    /// Why the order is a hard constraint rather than an optimisation:
    ///   MIDITap is the CONSUMER and Bridge the PRODUCER side, with an IPC protocol contract between them
    ///   A newer Bridge may raise the protocol major while an older MIDITap only speaks the old one
    ///   Updating Bridge first would leave a combination that installs and connects yet consumes no frame: remote play
    ///   simply does nothing, while the UI still reads "installed, connected", which is far harder to diagnose than a
    ///   plain "not supported yet"
    ///   Letting the Tap move first means the consumer side is ready before Bridge changes underneath it
    ///   Hence a failed self-check (network/API) does NOT go on to check Bridge
    ///     Without the Tap's update information there is no way to know whether the Tap should move first, and checking
    ///     Bridge then means deciding on incomplete information
    ///   An available Tap update (installed or not) also skips the Bridge check
    ///     The right order is to install the Tap; after the user reopens the app this method runs again and reaches the
    ///     Bridge step
    /// </summary>
    /// <param name="currentAppVersion">当前 MIDITap 版本，通常取 AppServices.AppVersion / The running MIDITap version, normally AppServices.AppVersion</param>
    /// <param name="cancellationToken">取消 / Cancellation</param>
    public async Task<BridgeUpdateGateResult> CheckUpdatesRespectingAppUpdateAsync(
        string currentAppVersion, CancellationToken cancellationToken = default)
    {
        var options = _backend.UpdateOptions;

        // ① 先查 MIDITap 自身
        // 包装种类跟着安装方式走，与 BackendService.CheckForUpdatesAsync 同一来源，
        // 否则这里挑 zip、真正落地时挑 setup.exe，两边会各挑一个
        //
        // 1) Check MIDITap itself first
        // The package kind follows the install kind, the same source BackendService.CheckForUpdatesAsync uses; a
        // different choice here would pick a zip while the actual landing picks setup.exe
        var selfOutcome = await UpdateChecker
            .CheckWithReasonAsync(
                currentAppVersion,
                options,
                kind: InstallKindDetector.PackageKindFor(AppServices.InstallKind))
            .ConfigureAwait(false);

        if (!selfOutcome.Ok)
        {
            // 自身检查失败：不查 Bridge（理由见方法头），把失败原因原样带出去让界面显示
            //
            // The self-check failed, so Bridge is not checked (see the method header), and the reason is carried out
            // for the UI to show
            AppServices.Log.Debug("[miditap.bridge] skipping the extension check: the app update check failed");
            return new BridgeUpdateGateResult(
                BridgeUpdateGate.SkippedAfterAppCheckFailure,
                SelfFailure: selfOutcome.Failure,
                SelfDetail: selfOutcome.Detail);
        }

        if (selfOutcome.Info is not null)
        {
            // 自身有更新：不查 Bridge，先让用户把 Tap 升上去
            //
            // The app has an update, so Bridge is not checked and the Tap is upgraded first
            AppServices.Log.Debug(
                "[miditap.bridge] skipping the extension check: MIDITap " + selfOutcome.Info.Latest + " is available");
            return new BridgeUpdateGateResult(
                BridgeUpdateGate.SkippedAfterAppUpdate, AppUpdate: selfOutcome.Info);
        }

        // ② 确认自身无更新，才查 Bridge
        // 版本与协议一次探完，顺带刷新"损坏"判定：这里本来就要两个值，分成两次探会多起两个子进程
        //
        // 2) Only now, with the app confirmed current, is Bridge checked
        // One probe run yields both values and refreshes the damaged verdict; splitting it would spawn two extra
        // processes for data this path needs anyway
        var (installedVersion, installedProtocol) = await RefreshInstalledMetadataAsync().ConfigureAwait(false);

        var check = await BridgeUpdateChecker
            .CheckManifestAsync(
                installedVersion,
                options,
                BridgeRepository.LatestManifestUrl,
                cancellationToken)
            .ConfigureAwait(false);

        if (!check.Ok || check.Info is null)
        {
            // 清单取不到：回退到 API/网页路径，行为与安装入口一致
            //
            // No manifest, so fall back to the API/web path, matching the install entry point
            var fallback = await BridgeUpdateChecker
                .CheckAsync(installedVersion, options, cancellationToken)
                .ConfigureAwait(false);
            if (!fallback.Ok)
            {
                return new BridgeUpdateGateResult(
                    BridgeUpdateGate.BridgeCheckFailed, BridgeFailure: fallback.Failure);
            }
            return await JudgeBridgeUpdateAsync(installedVersion, installedProtocol, fallback, cancellationToken)
                .ConfigureAwait(false);
        }

        // 协议门禁（云端一侧）：主版本不被本 Tap 支持时不提示更新，并把原因说成"协议不支持"而不是"检查失败"
        // 判定交给 Core 的三方策略，App 层不重复"主版本等于 1"这条规则
        //
        // The remote-side protocol gate: an unsupported major offers no update, and the reason is told as
        // "unsupported protocol" rather than "check failed"
        // The verdict comes from Core's three-way policy, so the App layer does not repeat "the major equals 1"
        var remoteSchema = check.Info.Manifest.IpcProtocolSchema;
        // "本地是否已装"用 BridgeLayout.IsInstalled 而不是 IsUsable：后者还要求 host/port 已填，
        // 而协议判定只关心"这台机器上装的是哪个版本"
        // 用 IsUsable 会让"装了但还没填地址"的用户被当作未安装，LocalUnsupported 因此报不出来
        //
        // "Is a local install present" uses BridgeLayout.IsInstalled rather than IsUsable: the latter also requires a
        // host and port, while the protocol verdict only cares which build is on this machine
        // Using IsUsable would treat "installed but no address yet" as not installed, so the LocalUnsupported verdict
        // could not be reached on that path
        var localInstalled = BridgeLayout.IsInstalled(_baseDir);

        // 装了但协议读不出来：既不能说它不兼容（探测失败不等于协议不对），也不能照常判断有没有更新
        // 因为"新版本能不能对接"这件事此刻没有本地事实可比
        // 单列一条结论，界面据此说明"读不出已装版本的信息"，而不是报"不兼容"让人白重装一次
        //
        // Installed, yet its protocol cannot be read: calling it incompatible would be wrong (a failed probe is not a
        // wrong protocol), and judging an update normally is equally unavailable, since nothing local tells whether a
        // newer build could talk to this Tap
        // It therefore gets its own outcome, and the UI says the installed build could not be read rather than
        // "incompatible", which would send the user through a reinstall for nothing
        if (localInstalled && string.IsNullOrEmpty(installedProtocol))
        {
            AppServices.Log.Debug("[miditap.bridge] the installed protocol could not be probed, so no update verdict");
            return new BridgeUpdateGateResult(
                BridgeUpdateGate.BlockedByLocalProtocolUnknown,
                InstalledVersion: installedVersion);
        }

        var compatibility = JudgeCompatibility(installedProtocol, localInstalled, remoteSchema);

        if (compatibility == BridgeCompatibility.RemoteUnsupported)
        {
            AppServices.Log.Debug(
                "[miditap.bridge] the latest release speaks ipc protocol " +
                check.Info.Manifest.IpcProtocol + ", which this build does not support");
            return new BridgeUpdateGateResult(
                BridgeUpdateGate.BlockedByRemoteProtocol,
                RemoteProtocol: check.Info.Manifest.IpcProtocol);
        }

        if (compatibility == BridgeCompatibility.LocalUnsupported)
        {
            // 本地协议不被支持：功能应被禁用（IsBridgeUsable 已反映），这里不提供"更新 Bridge"的建议
            // 因为升级改变不了本地那个不受支持的协议，只有重装或换回兼容版本才行
            //
            // The local protocol is unsupported, so the feature is disabled (IsBridgeUsable reflects that) and no
            // "update Bridge" advice is offered: an upgrade cannot change the unsupported local protocol, only a
            // reinstall or a compatible build can
            return new BridgeUpdateGateResult(
                BridgeUpdateGate.BlockedByLocalProtocol,
                InstalledProtocol: installedProtocol);
        }

        if (!check.UpdateAvailable)
        {
            return new BridgeUpdateGateResult(
                BridgeUpdateGate.NoBridgeUpdate,
                InstalledVersion: installedVersion,
                InstalledProtocol: installedProtocol);
        }

        // 云端确实有更新，且双方协议兼容：把远端信息交给界面
        // 发布页地址优先用清单自带的，没有就用 tag 拼标准形状
        //
        // The remote really has a newer version and both sides are compatible, so the remote information goes to the UI
        // The release page prefers the address the manifest carries and falls back to the standard shape built from the
        // tag
        return new BridgeUpdateGateResult(
            BridgeUpdateGate.BridgeUpdateAvailable,
            InstalledVersion: installedVersion,
            InstalledProtocol: installedProtocol,
            RemoteVersion: check.Info.Manifest.Version,
            RemoteProtocol: check.Info.Manifest.IpcProtocol,
            Artifact: check.Info.Artifact,
            RemoteReleaseUrl: check.Info.Manifest.ReleaseUrl ?? BridgeRepository.ReleasePageUrl(check.Info.Manifest.Version));
    }

    /// <summary>
    /// API/网页回退路径上的同一套判定：协议门禁 + 是否有更新
    /// 与清单路径分开是因为两条路返回的类型不同（清单侧有 protocolSchema，资产侧只有命名），
    /// 但结论必须一致，因此判定顺序在这里照抄一遍并共用 <see cref="JudgeCompatibility"/>
    ///
    /// The same judgement on the API/web fallback path: the protocol gate plus whether an update exists
    /// It is separate because the two paths return different types (the manifest side carries protocolSchema, the asset
    /// side carries only names), while the verdict must agree, so the order is repeated here over the shared
    /// <see cref="JudgeCompatibility"/>
    /// </summary>
    private async Task<BridgeUpdateGateResult> JudgeBridgeUpdateAsync(
        string installedVersion,
        string? installedProtocol,
        BridgeUpdateCheckOutcome outcome,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (outcome.Info is null)
        {
            // 资产路径拿不到协议号：云端协议按"未知"（0）处理，不当成不兼容
            // 把一个网络或页面结构问题说成"上游不兼容"会误导用户
            //
            // The asset path carries no protocol number, so the remote side reads as unknown (0) rather than
            // incompatible
            // Reporting a network or markup problem as "the upstream is incompatible" would mislead the user
            if (outcome.NotComparable)
            {
                return new BridgeUpdateGateResult(
                    BridgeUpdateGate.BridgeVersionUnknown,
                    InstalledVersion: installedVersion,
                    InstalledProtocol: installedProtocol);
            }
            return new BridgeUpdateGateResult(
                BridgeUpdateGate.NoBridgeUpdate,
                InstalledVersion: installedVersion,
                InstalledProtocol: installedProtocol);
        }

        // 与清单路径同一来源：本地是否已装看 BridgeLayout.IsInstalled（理由见那里的注释）
        //
        // The same source as the manifest path: a local install is BridgeLayout.IsInstalled (see the comment there)
        var localInstalled = BridgeLayout.IsInstalled(_baseDir);

        // 与清单路径同一处判断：装了却读不出协议时不给更新结论（理由见那里的注释）
        //
        // The same test as on the manifest path: an install whose protocol cannot be read gets no update verdict (see
        // the comment there)
        if (localInstalled && string.IsNullOrEmpty(installedProtocol))
        {
            return new BridgeUpdateGateResult(
                BridgeUpdateGate.BlockedByLocalProtocolUnknown,
                InstalledVersion: installedVersion);
        }

        var compatibility = JudgeCompatibility(installedProtocol, localInstalled, remoteSchema: 0);
        if (compatibility == BridgeCompatibility.LocalUnsupported)
        {
            return new BridgeUpdateGateResult(
                BridgeUpdateGate.BlockedByLocalProtocol,
                InstalledVersion: installedVersion,
                InstalledProtocol: installedProtocol);
        }

        // API 路径没有清单，发布页只能按 tag 拼标准形状
        //
        // The API path has no manifest, so the release page is built from the tag in the standard shape
        return new BridgeUpdateGateResult(
            BridgeUpdateGate.BridgeUpdateAvailable,
            InstalledVersion: installedVersion,
            InstalledProtocol: installedProtocol,
            RemoteVersion: outcome.Info.Latest,
            Asset: outcome.Info.Asset,
            RemoteReleaseUrl: BridgeRepository.ReleasePageUrl(outcome.Info.Latest));
    }
}
