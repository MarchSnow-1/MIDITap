// BackendService.cs — 界面背后的那一层：MIDI 会话（枚举设备、启停监听、捕获音符）
// 配置与映射的读写、更新检查，也都由它落地
// 它持有状态，把结果通过事件广播出去；界面只调用它，不直接碰驱动与配置文件
//
// 实现要点：输入端口由 NAudio（WinMM）打开；按键注入交给 MIDITap.Core.KeyInjector
//
// 线程模型：NAudio 在 WinMM 回调线程上触发 MessageReceived，因此监听期间有一个非 UI 线程在跑
//   * 回调线程只在 _gate 内跑状态机，然后把"该做什么"入队
//     它自己既不注入按键也不派发事件
//   * 一个专用工作线程按入队顺序执行：注入按键、广播事件
//     动作因此严格有序、不会两个交错
//     Stop 先排空队列（Drain）再收尾，所以退出前"按下的键已抬起"必然已经落地
//   * 启停切换由 _lifecycleGate 串行化
//     启动可能来自启动序列、热插拔轮询或主页选择设备
//     不加锁会让两个 Start 交错打开同一端口、泄漏其中一个 MidiIn
// 事件处理器不得同步回调本服务 —— UI 一侧先经 DispatcherQueue 编组过去
//
// BackendService.cs — the layer behind the UI
// It covers the MIDI session (enumerating devices, starting and stopping monitoring, capturing notes)
// It also covers config and mapping reads and writes, and the update check
// It holds state and broadcasts the results through events
// The UI only calls it and does not touch the driver or the config files directly
//
// Implementation notes: the input port is opened by NAudio (WinMM); key injection goes to MIDITap.Core.KeyInjector
//
// Threading model: NAudio raises MessageReceived on a WinMM callback thread, so one non-UI thread runs while monitoring
//   * The callback thread runs the state machine only inside _gate, then enqueues "what to do next"
//     It neither injects keys nor dispatches events itself
//   * One dedicated worker thread executes in enqueue order
//     It injects keys and broadcasts events
//     Actions are therefore strictly ordered and two of them do not interleave
//     Stop drains the queue (Drain) before it finishes
//     So the "keys that were pressed have been released" work has landed before exit
//   * Start/stop transitions are serialised by _lifecycleGate
//     A start can come from the startup sequence, the hot-plug poll or a device selection in the Home page
//     Without the lock two Start calls would interleave on the same port and leak one of the MidiIn handles
// Event handlers must not call back into this service synchronously
// The UI side marshals through the DispatcherQueue first


using System.Collections.Concurrent;
using MIDITap.Core.Config;
using MIDITap.Core.Keys;
using MIDITap.Core.Midi;
using MIDITap.Core.Update;
using NAudio.Midi;

namespace MIDITap.App.Services;

public sealed record MidiPortInfo(int Index, string Name);

/// <summary>
    /// 一条映射的展示数据：键位标签 + 它是单键还是组合键
    /// "是否组合键"由**权威来源**判定（该音符的 VK 码个数 > 1），而不是在 UI 层用 "标签里是否含 '+ '" 反推
    /// 后者只是恰好等价，一旦出现名字带 '+' 的按键就会失效
    ///
    /// Display data for one mapping: the key label plus whether it is a combo
    /// Combo-ness is taken from the authoritative source (more than one VK code for that note)
    /// It is not inferred in the UI from "the label contains '+'"
    /// That inference only happens to be equivalent, and it would break if a single key ever had '+' in its name
/// </summary>
public sealed record MappingEntry(string KeyLabel, bool IsCombo);

public sealed record ConfigLoadedInfo(
    string Path,
    string Filename,
    string Name,
    int NoteCount,
    IReadOnlyDictionary<byte, MappingEntry> Mapping);

public sealed class BackendService
{
    private readonly object _gate = new();

    // 启停互斥：监听启动是自动的（启动序列、设备热插拔轮询、主页选择设备都可能触发）
    // UI 线程与轮询线程可能同时发起切换，若不加锁会让两个 Start 交错打开同一端口并泄漏其中一个 MidiIn
    //
    // Serialises monitoring transitions: monitoring starts automatically
    // A transition can be triggered by the startup sequence, the hot-plug poll or a device selection in the Home page
    // Without a lock two interleaved Start calls could each open the same port and leak one of the MidiIn handles
    private readonly object _lifecycleGate = new();
    private readonly MidiNoteStateMachine _stateMachine = new();
    private readonly string _baseDir;
    private MidiIn? _input;
    private MidiIn? _captureInput;

    // 按键注入专用队列：注入动作入队后异步执行，以免阻塞事件循环
    // 这里同样不在 WinMM 回调线程上同步调用 SendInput（慢速低级键盘钩子会拖慢注入并反压 MIDI 消息），而是交给独立的工作线程按序执行
    // Stop 时通过 Drain 标记等待队列清空，保证退出前 release-all-keys 落地
    //
    // Dedicated key-injection queue: injection actions are enqueued and executed asynchronously
    // That way they do not block the event loop
    // This code likewise does not call SendInput synchronously on the WinMM callback thread
    // A slow low-level keyboard hook would slow injection down and back-pressure MIDI messages
    // So a dedicated worker thread runs the actions in order instead
    // On Stop a Drain marker waits for the queue to empty, so release-all-keys has landed before exit
    private readonly BlockingCollection<MidiAction> _injectionQueue = new(new ConcurrentQueue<MidiAction>());

    public event Action<IReadOnlyList<MidiPortInfo>>? PortsListed;
    public event Action<int, string>? Started;
    public event Action? Stopped;
    public event Action<string>? Error;
    // 映射增删的日志需要本地化，但后端不该依赖 UI 的 i18n（分层：后端只发结构化数据，由 MainWindow 负责翻译）
    // 沿用 NoteOn/ConfigRenamed 等既有的"结构化事件 + 前端本地化"模式
    //
    // The log entries for adding and removing mappings need localising
    // But the backend must not depend on the UI's i18n
    // That is the layering: the backend only emits structured data and MainWindow does the translating
    // It follows the existing "structured event plus front-end localisation" pattern
    // The same pattern is used by NoteOn, ConfigRenamed and the rest

    /// <summary>映射已添加（note 字符串, key 文本）</summary>
    /// <remarks>A mapping was added (note string, key text)</remarks>
    public event Action<string, string>? MappingAdded;
    /// <summary>映射已删除（note 字符串）</summary>
    /// <remarks>A mapping was deleted (note string)</remarks>
    public event Action<string>? MappingDeleted;
    /// <summary>配置文件已新建（文件名）</summary>
    /// <remarks>A config file was created (filename)</remarks>
    public event Action<string>? ConfigCreated;
    public event Action<ConfigLoadedInfo>? ConfigLoaded;
    /// <summary>页面状态重放专用：不触发活动日志（区别于真实加载）</summary>
    /// <remarks>For replaying page state only: it does not raise an activity-log entry (unlike a real load)</remarks>
    public event Action<ConfigLoadedInfo>? ConfigStateRestored;
    public event Action<IReadOnlyList<ConfigFileInfo>>? ConfigList;
    public event Action<string, string>? ConfigRenamed;

    /// <summary>
    /// 某个配置文件存在却读不出来（损坏或格式错误）
    /// 参数是（绝对路径，文件名）
    /// 与 Error 分开：Error 表示这次操作失败了，而这个表示某个文件当前不可用
    /// 界面据此挂出常驻提示并禁用写操作，Error 则只弹一下就过去
    ///
    /// A config file exists but cannot be read (corrupt or malformed)
    /// The arguments are (absolute path, file name)
    /// It is kept apart from Error: Error says an operation failed, while this is one file being unusable right now
    /// The UI shows a standing hint from it and disables the write actions, whereas Error is a one-off notice
    /// </summary>
    public event Action<string, string>? ConfigInvalid;
    public event Action<byte, byte, string?>? NoteOn;
    public event Action<byte>? NoteOff;
    public event Action<byte>? DuplicateOn;
    public event Action<byte>? UnexpectedOff;
    public event Action<byte>? NoteCaptured;
    public event Action? CaptureReady;
    public event Action<UpdateInfo>? UpdateAvailable;

    /// <summary>主页当前选中的设备端口（捕获音符时复用）</summary>
    /// <remarks>The device port currently selected on the Home page (reused when capturing notes)</remarks>
    public int SelectedPort { get; set; }

    public string? CurrentConfigPath { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>当前正在监听的端口（未监听时为 null）。主页在页面重建后用它恢复下拉框选中项</summary>
    /// <remarks>
    /// The port currently being monitored (null when not monitoring)
    /// After a page rebuild the Home page uses it to restore the drop-down selection
    /// </remarks>
    public int? ActivePortIndex => _activePortIndex;

    // 页面是按导航重建的（NavigationCacheMode=Disabled）
    // 而启动序列在页面创建之前就广播过一次
    // 缓存 configLoaded / updateAvailable 负载，供页面 OnLoaded 时重放
    //
    // Pages are rebuilt by navigation (NavigationCacheMode=Disabled)
    // The startup sequence already broadcast once before the page was created
    // The configLoaded and updateAvailable payloads are cached, so a page can replay them in OnLoaded
    private ConfigLoadedInfo? _lastConfigLoaded;
    private UpdateInfo? _lastUpdate;

    // 设备热插拔轮询：定期枚举 WinMM 设备，变化时刷新列表并自动启停监听
    // （插入设备自动选择并启动；拔出自动停止/热切换），无需手动刷新或启动
    //
    // Device hot-plug polling: enumerate the WinMM devices periodically
    // Refresh the list when it changes and start or stop monitoring automatically
    // Plugging a device in selects and starts it, unplugging stops or hot-switches it
    // No manual refresh or start is needed
    private System.Threading.Timer? _deviceTimer;
    private List<MidiPortInfo> _knownPorts = [];
    private int? _activePortIndex;
    private int _polling;

    // 配置文件热更新：监视 config/ 目录
    // 当前配置文件被外部编辑器修改后自动重新加载
    // 防抖 + 内容签名去重，避免应用自身写入引起的重复加载
    //
    // Config hot reload: watch the config/ directory
    // Reload the current config file automatically once an external editor has modified it
    // Debounce plus content-signature dedup keeps the app's own writes from causing a duplicate load
    private FileSystemWatcher? _configWatcher;
    private System.Threading.Timer? _configReloadDebounce;
    private string? _lastLoadedSignature;

    public BackendService(string baseDir)
    {
        _baseDir = baseDir;
        var worker = new Thread(InjectionLoop)
        {
            IsBackground = true,
            Name = "MIDITap.KeyInjection",
        };
        worker.Start();
    }

    private void InjectionLoop()
    {
        foreach (var action in _injectionQueue.GetConsumingEnumerable())
        {
            switch (action)
            {
                case MidiAction.DrainMarker marker:
                    marker.Done.Set();
                    break;
                default:
                    ApplyAction(action);
                    break;
            }
        }
    }

    private void EnqueueActions(IEnumerable<MidiAction> actions)
    {
        foreach (var action in actions)
        {
            _injectionQueue.Add(action);
        }
    }

    /// <summary>等待注入队列清空（含 release-all-keys），退出/停止时保证按键全部落地</summary>
    /// <remarks>
    /// Waits for the injection queue to empty (release-all-keys included)
    /// So the keys have landed when the app stops or exits
    /// </remarks>
    private void DrainInjectionQueue()
    {
        using var done = new ManualResetEventSlim(false);
        _injectionQueue.Add(new MidiAction.DrainMarker(done));
        done.Wait(TimeSpan.FromSeconds(2));
    }

    private static LoadOptions SilentOptions => new(Silent: true);

    // ------------------------------------------------------------------ ports

    private List<MidiPortInfo> EnumeratePorts()
    {
        var ports = new List<MidiPortInfo>();
        for (var i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            ports.Add(new MidiPortInfo(i, MidiIn.DeviceInfo(i).ProductName));
        }
        return ports;
    }

    /// <summary>枚举 MIDI 输入设备并广播 midiPorts</summary>
    /// <remarks>Enumerates the MIDI input devices and broadcasts midiPorts</remarks>
    public void ListPorts()
    {
        try
        {
            var ports = EnumeratePorts();
            _knownPorts = ports;
            PortsListed?.Invoke(ports);
        }
        catch (Exception err)
        {
            Error?.Invoke("MIDI error: " + err.Message);
            PortsListed?.Invoke([]);
        }
    }

    /// <summary>
    /// 启动设备热插拔监视（2s 轮询）与配置文件热更新监视
    /// 设备列表变化时自动刷新下拉框，并按需自动启动/停止/热切换监听
    /// 结束后立即调用 <see cref="EnsureMonitoring"/>，让"默认启动"不依赖轮询时序
    ///
    /// Starts the device hot-plug watcher (a 2 s poll) and the config hot-reload watcher
    /// A change in the device list refreshes the drop-down and starts, stops or hot-switches monitoring as needed
    /// It calls EnsureMonitoring() immediately afterwards, so the default start does not depend on the poll's timing
    /// </summary>
    public void StartDeviceWatcher()
    {
        if (_deviceTimer is null)
        {
            _deviceTimer = new System.Threading.Timer(
                PollDevicesCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
        StartConfigWatcher();
        // 轮询只在"设备列表发生变化"时才接管启停
        // 而启动序列已经先枚举过一次设备（_knownPorts 已填充）
        // 首轮轮询看不到变化
        // 这里显式兜底一次，保证窗口一出现就已经在监听，而不是碰运气等轮询
        //
        // Polling takes over start/stop only when "the device list changes"
        // The startup sequence has already enumerated the devices once (_knownPorts is filled)
        // So the first poll sees no change
        // This explicit fallback makes sure the window is already monitoring when it appears
        // It does not depend on when the poll happens to run
        EnsureMonitoring();
    }

    /// <summary>
    /// 默认启动：尚未监听时，按当前选中的端口（不可用时回退到第一台设备）立即开始监听
    /// 没有设备则什么都不做，交给热插拔轮询在设备出现时自动启动
    /// 监听启动是自动的，因此这里是"应用始终在监听"的唯一兜底入口
    ///
    /// Default start: when not monitoring yet, start at once on the currently selected port
    /// Fall back to the first device when that port is unavailable
    /// With no device present it does nothing and leaves the start to the hot-plug poll once a device appears
    /// Monitoring starts automatically, so this is the only fallback entry point for "the app is monitoring"
    /// </summary>
    public void EnsureMonitoring()
    {
        lock (_lifecycleGate)
        {
            if (IsRunning)
            {
                return;
            }
            var ports = _knownPorts.Count > 0 ? _knownPorts : EnumeratePorts();
            var port = PreferredPort(ports);
            if (port < 0)
            {
                return; // 当前没有 MIDI 设备：等轮询发现后自动启动 / No MIDI device yet; the poll starts it once one appears
            }
            // configPath 传 null：配置在启动序列/热更新中已经装载，避免重复加载与重复广播
            //
            // configPath is passed as null: the config has already been loaded by the startup sequence or a hot reload
            // That avoids a duplicate load and a duplicate broadcast
            Start(port, configPath: null);
        }
    }

    /// <summary>
    /// 选择要监听的端口：优先用户在主页选中的设备，它已消失时回退到第一台可用设备
    ///
    /// Picks the port to monitor: the device the user selected on the Home page has priority
    /// Once it has disappeared the first available device is used instead
    /// </summary>
    private int PreferredPort(IReadOnlyList<MidiPortInfo> ports)
    {
        if (ports.Count == 0)
        {
            return -1;
        }
        var selected = SelectedPort;
        return ports.Any(p => p.Index == selected) ? selected : ports[0].Index;
    }

    private void PollDevicesCallback(object? state)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }
        try
        {
            var ports = EnumeratePorts();
            var changed = ports.Count != _knownPorts.Count
                || ports.Zip(_knownPorts).Any(pair =>
                    pair.First.Index != pair.Second.Index || pair.First.Name != pair.Second.Name);
            if (!changed)
            {
                return;
            }
            _knownPorts = ports;
            PortsListed?.Invoke(ports);
            HandleDeviceChange(ports);
        }
        catch
        {
            // 枚举失败（驱动瞬时状态）忽略，下次轮询再试
            //
            // An enumeration failure (a transient driver state) is ignored; the next poll tries again
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void HandleDeviceChange(IReadOnlyList<MidiPortInfo> ports)
    {
        lock (_lifecycleGate)
        {
            if (ports.Count == 0)
            {
                // 设备全部拔出：停止监听并抬起所有按键
                //
                // Every device was unplugged: stop monitoring and release all keys
                if (IsRunning)
                {
                    Stop();
                }
                return;
            }

            // 正在监听的设备仍在：不干预（用户可能只是插了第二台设备）
            //
            // The device being monitored is still present: leave it alone (the user may just have plugged in a second device)
            if (IsRunning && _activePortIndex is int active && ports.Any(p => p.Index == active))
            {
                return;
            }

            // 未监听（应用启动即有设备，或设备插入）
            // 或正在监听的设备被拔出（自动热切换），都切到当前选中/第一台可用设备
            // 监听在 v2 里是常态，因此这里不再判断"用户是否需要开始监听"
            //
            // Not monitoring (the app started with a device already present, or a device was plugged in)
            // Or the monitored device was unplugged (automatic hot switch)
            // Either way, switch to the currently selected or first available device
            // Monitoring is the normal state in v2, so this no longer asks whether the user wants to start monitoring
            if (IsRunning)
            {
                Stop();
            }
            Start(PreferredPort(ports), configPath: null);
        }
    }

    /// <summary>
    /// 向（重新创建的）页面重放最近一次 configLoaded
    /// 走专用的 ConfigStateRestored 事件：页面恢复状态用，不写活动日志
    ///
    /// Replays the most recent configLoaded to a (recreated) page
    /// It goes through the dedicated ConfigStateRestored event
    /// That event is for restoring page state and does not write an activity-log entry
    /// </summary>
    public void ReemitConfigLoaded()
    {
        if (_lastConfigLoaded is not null)
        {
            ConfigStateRestored?.Invoke(_lastConfigLoaded);
        }
    }

    // ------------------------------------------------------------------ start / stop

    /// <summary>先停旧会话，可选加载配置，再打开端口</summary>
    /// <remarks>Stop the old session first, optionally load a config, then open the port</remarks>
    public void Start(int portIndex, string? configPath)
    {
        lock (_lifecycleGate)
        {
            StartCore(portIndex, configPath);
        }
    }

    private void StartCore(int portIndex, string? configPath)
    {
        var wasRunning = IsRunning;
        StopMonitoringCore();

        if (!string.IsNullOrEmpty(configPath))
        {
            var resolved = Path.IsPathRooted(configPath)
                ? configPath
                : Path.GetFullPath(Path.Combine(_baseDir, configPath));
            var configResult = ConfigLoader.LoadConfig(_baseDir, SilentOptions with { ConfigPath = resolved });
            if (configResult is null)
            {
                FailStart(wasRunning, "Failed to load config: " + configPath);
                return;
            }
            ConfigLocator.SaveLastConfigPath(_baseDir, configResult.ConfigPath);
            InstallConfig(configResult);
            EmitConfigLoaded(configResult);
        }

        var portCount = MidiIn.NumberOfDevices;
        if (portCount == 0)
        {
            FailStart(wasRunning, "No MIDI devices found");
            return;
        }
        if (portIndex >= portCount)
        {
            FailStart(wasRunning, $"Port {portIndex} not found (available: 0-{portCount - 1})");
            return;
        }

        try
        {
            var input = new MidiIn(portIndex);
            input.MessageReceived += OnInputMessage;
            // 先发布再 Start：消除“回调已到但 _input 尚为 null”的丢消息窗口
            //
            // Publish before Start
            // This closes the window in which a callback has arrived while _input is still null and the message is lost
            lock (_gate)
            {
                _input = input;
            }
            input.Start();
            var portName = MidiIn.DeviceInfo(portIndex).ProductName;
            IsRunning = true;
            _activePortIndex = portIndex;
            Started?.Invoke(portIndex, portName);
        }
        catch (Exception err)
        {
            // 半打开的端口必须释放而不是泄漏
            //
            // A half-opened port must be released rather than leaked
            IsRunning = false;
            MidiIn? leaked = null;
            lock (_gate)
            {
                leaked = _input;
                _input = null;
            }
            if (leaked is not null)
            {
                leaked.MessageReceived -= OnInputMessage;
                CloseMidiInSync(leaked);
            }
            FailStart(wasRunning, "Failed to open port: " + err.Message);
        }
    }

    /// <summary>停止监听并广播 midiStopped / Stops monitoring and broadcasts midiStopped</summary>
    /// <remarks>
    /// 停止监听只有两个调用方：设备全部拔出（热插拔兜底）与窗口关闭（退出前抬起所有按键）
    /// 两者都必须保证按键全部落地，所以这里不允许与并发的 Start 交错
    ///
    /// Stopping has only two callers
    /// The first is every device unplugged (the hot-plug fallback)
    /// The second is window close (releasing all keys before exit)
    /// Both have to make sure the pressed keys have landed, so interleaving with a concurrent Start is not permitted here
    /// </remarks>
    public void Stop()
    {
        lock (_lifecycleGate)
        {
            StopMonitoringCore();
        }
        Stopped?.Invoke();
    }

    // 让一次启动失败以用户可见错误告终
    // 若此前有会话正在运行，同时广播 midiStopped，避免 GUI 停留在假的 "Running" 状态
    //
    // Ends a failed start with an error the user can see
    // If a session was running before, it also broadcasts midiStopped, so the GUI does not stay in a fake "Running" state
    private void FailStart(bool wasRunning, string message)
    {
        if (wasRunning)
        {
            Stopped?.Invoke();
        }
        Error?.Invoke(message);
    }

    /// <summary>
    /// stopMonitoring：先摘除监听，再关闭连接（绝不在回调线程内关闭），最后 release-all-keys
    /// 连接关闭放在锁外，回调线程若正等锁不会死锁
    ///
    /// stopMonitoring: detach the monitoring first, then close the connection
    /// The connection is not closed on the callback thread
    /// Finally it releases all keys
    /// Closing the connection happens outside the lock, so a callback thread waiting on that lock does not deadlock
    /// </summary>
    private void StopMonitoringCore()
    {
        MidiIn? input;
        MidiIn? capture;
        lock (_gate)
        {
            input = _input;
            _input = null;
            capture = _captureInput;
            _captureInput = null;
            IsRunning = false;
            _activePortIndex = null;
        }
        if (input is not null)
        {
            input.MessageReceived -= OnInputMessage;
            CloseMidiInSync(input);
        }
        if (capture is not null)
        {
            CloseMidiInSync(capture);
        }
        List<MidiAction> actions;
        lock (_gate)
        {
            actions = _stateMachine.ReleaseAllKeys();
        }
        // 释放动作入队后等待注入线程清空队列
        // 顺序保证先于 Close 生效，且退出时被按住的键一定被抬起
        //
        // After the release actions are enqueued it waits for the injection thread to empty the queue
        // The order guarantees they take effect before the Close, and a key held at exit is released
        EnqueueActions(actions);
        DrainInjectionQueue();
    }

    // 同步关闭：仅允许从非回调线程调用（UI 线程 / 线程池）
    // 在命令线程上同步 closePort，保证 Stop 后立刻重新 Start 同一端口不会撞上 MMSYSERR_ALLOCATED
    //
    // Synchronous close: only permitted from a non-callback thread (the UI thread or the thread pool)
    // The port is closed synchronously on the command thread
    // So starting the same port again right after Stop does not hit MMSYSERR_ALLOCATED
    private static void CloseMidiInSync(MidiIn midiIn)
    {
        try
        {
            midiIn.Stop();
        }
        catch
        {
            // ignore
        }
        try
        {
            midiIn.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>异步关闭：供采集连接自身的回调线程使用（绝不在回调内同步 Dispose）</summary>
    /// <remarks>
    /// Asynchronous close, for the capture connection's own callback thread
    /// A synchronous Dispose inside that callback is not performed
    /// </remarks>
    private static void CloseMidiInAsync(MidiIn midiIn)
        => Task.Run(() => CloseMidiInSync(midiIn));

    // ------------------------------------------------------------------ MIDI messages

    private void OnInputMessage(object? sender, MidiInMessageEventArgs e)
    {
        var raw = (uint)e.RawMessage;
        var status = (byte)(raw & 0xFF);
        var type = (byte)(status & 0xF0);
        if (type != 0x80 && type != 0x90)
        {
            // 只处理音符消息（外加状态机自身的消息类型过滤）
            //
            // Only note messages are handled (plus the state machine's own message-type filter)
            return;
        }

        lock (_gate)
        {
            if (_input is null)
            {
                return;
            }
            var message = new byte[3] { status, (byte)((raw >> 8) & 0xFF), (byte)((raw >> 16) & 0xFF) };
            var actions = _stateMachine.HandleMessage(message);
            EnqueueActions(actions);
        }
    }

    private void ApplyAction(MidiAction action)
    {
        switch (action)
        {
            case MidiAction.KeyDown keyDown:
                KeyInjector.SendKey(keyDown.VkCode, up: false);
                break;
            case MidiAction.KeyUp keyUp:
                KeyInjector.SendKey(keyUp.VkCode, up: true);
                break;
            case MidiAction.Broadcast broadcast:
                RaiseBroadcast(broadcast);
                break;
        }
    }

    private void RaiseBroadcast(MidiAction.Broadcast broadcast)
    {
        switch (broadcast.Kind)
        {
            case BroadcastKind.NoteOn:
                NoteOn?.Invoke(broadcast.Note, broadcast.Velocity, broadcast.Key);
                break;
            case BroadcastKind.NoteOff:
                NoteOff?.Invoke(broadcast.Note);
                break;
            case BroadcastKind.DuplicateOn:
                DuplicateOn?.Invoke(broadcast.Note);
                break;
            case BroadcastKind.UnexpectedOff:
                UnexpectedOff?.Invoke(broadcast.Note);
                break;
        }
    }

    // ------------------------------------------------------------------ capture

    /// <summary>
    /// 监听中直接 Ready（前端改听 midiNoteOn）
    /// 否则开临时采集连接，收到第一个音符后广播 midiNoteCaptured 并关闭
    ///
    /// While monitoring it goes straight to Ready (the front end listens to midiNoteOn instead)
    /// Otherwise it opens a temporary capture connection, broadcasts midiNoteCaptured on the first note and closes it
    /// </summary>
    public void CaptureNote(int portIndex)
    {
        if (IsRunning)
        {
            CaptureReady?.Invoke();
            return;
        }

        MidiIn? previous;
        lock (_gate)
        {
            previous = _captureInput;
            _captureInput = null;
        }
        if (previous is not null)
        {
            CloseMidiInSync(previous);
        }

        var portCount = MidiIn.NumberOfDevices;
        if (portCount == 0)
        {
            Error?.Invoke("No MIDI devices found");
            return;
        }
        if (portIndex >= portCount)
        {
            Error?.Invoke($"Port {portIndex} not found");
            return;
        }

        MidiIn? capture = null;
        try
        {
            capture = new MidiIn(portIndex);
            capture.MessageReceived += OnCaptureMessage;
            // 先发布再 Start，与 Start() 的顺序一致
            //
            // Publish before Start, in the same order as Start()
            lock (_gate)
            {
                _captureInput = capture;
            }
            capture.Start();
            CaptureReady?.Invoke();
        }
        catch (Exception err)
        {
            if (capture is not null)
            {
                capture.MessageReceived -= OnCaptureMessage;
                lock (_gate)
                {
                    if (ReferenceEquals(_captureInput, capture))
                    {
                        _captureInput = null;
                    }
                }
                CloseMidiInSync(capture);
            }
            Error?.Invoke("Failed to open port for capture: " + err.Message);
        }
    }

    private void OnCaptureMessage(object? sender, MidiInMessageEventArgs e)
    {
        var raw = (uint)e.RawMessage;
        var status = (byte)(raw & 0xFF);
        var note = (byte)((raw >> 8) & 0xFF);
        var velocity = (byte)((raw >> 16) & 0xFF);
        if ((status & 0xF0) != 0x90 || velocity == 0)
        {
            return;
        }

        MidiIn? toClose;
        lock (_gate)
        {
            toClose = _captureInput;
            _captureInput = null;
        }
        if (toClose is null)
        {
            // 关闭落地前又来了一条 note-on：这里忽略后续消息，保证只捕获第一个音符
            //
            // Another note-on arrived before the close landed: later messages are ignored here, so only the first note is captured
            return;
        }
        NoteCaptured?.Invoke(note);
        // 绝不从采集连接自身的回调线程内同步 Dispose —— 交给线程池
        //
        // The capture connection's own callback thread does not Dispose synchronously — that work goes to the thread pool
        CloseMidiInAsync(toClose);
    }

    /// <summary>停止音符捕获并关闭临时采集连接</summary>
    /// <remarks>Stops note capture and closes the temporary capture connection</remarks>
    public void StopCapture()
    {
        MidiIn? capture;
        lock (_gate)
        {
            capture = _captureInput;
            _captureInput = null;
        }
        if (capture is not null)
        {
            CloseMidiInSync(capture);
        }
    }

    // ------------------------------------------------------------------ config

    /// <summary>
    /// 无路径时恢复上次配置
    /// 启动时若那份记录已经失效（文件被改名或删掉），改为挑一份能用的，而不是报错
    /// 只有确实挑不出任何可用配置时才广播 midiError
    ///
    /// Restores the last config when no path is given
    /// At startup, a record that no longer holds (the file was renamed or deleted) falls back to a usable one instead of erroring
    /// midiError is broadcast only when no usable config can be found at all
    /// </summary>
    public void LoadConfig(string? path)
    {
        var resolvedPath = path;
        if (string.IsNullOrEmpty(resolvedPath))
        {
            resolvedPath = ConfigLocator.GetLastConfigPath(_baseDir);
        }
        if (resolvedPath is not null && !Path.IsPathRooted(resolvedPath))
        {
            resolvedPath = Path.GetFullPath(Path.Combine(_baseDir, resolvedPath));
        }

        if (ApplyConfig(resolvedPath, persist: true) is not null)
        {
            return;
        }

        // 文件在磁盘上却读不出来 = 损坏；路径本身无效是另一回事
        // 前者是用户可以自己去修的状态，因此单独广播，让界面挂出常驻提示
        // 后者只说明这次操作没成
        //
        // A file that exists on disk yet cannot be read is corrupt; an invalid path is something else
        // The former is a state the user can go and fix, so it is broadcast separately for a standing hint
        // The latter only means this one operation did not succeed
        if (resolvedPath is not null && File.Exists(resolvedPath))
        {
            ConfigInvalid?.Invoke(resolvedPath, Path.GetFileName(resolvedPath));
            return;
        }

        // 走到这里说明记录的那份配置已经不在磁盘上了（改名、删除、或者上次运行后被人动过）
        // 启动时这属于正常情况：v1 迁移就会重命名文件，而 last_config 里记的还是老路径
        // 因此先退一步挑一份能读的，避免把一个可恢复的局面报成错误
        // 显式指定路径（用户在下拉框里选的）不走这条路：那时失败就是失败，应当如实报出来
        //
        // Reaching here means the recorded config is gone from disk (renamed, deleted, or moved since the last run)
        // At startup that is normal: the v1 migration renames files while last_config still holds the old path
        // So it steps back and picks a readable one rather than reporting a recoverable situation as an error
        // An explicit path (picked in the drop-down) does not take this route: a failure there is a real failure and is reported as one
        if (path is null && FallbackToAnyConfig())
        {
            return;
        }

        Error?.Invoke("Failed to load config: " + (path ?? "default"));
    }

    /// <summary>
    /// 从 config/ 里挑一份能读的配置装上，成功时同时更新 last_config
    /// 跳过读不出来的（损坏的那些），全部不可用时返回 false
    ///
    /// Picks a readable config out of config/ and installs it, updating last_config on success
    /// Unreadable (corrupt) files are skipped; false means none of them worked
    /// </summary>
    private bool FallbackToAnyConfig()
    {
        foreach (var candidate in ConfigLocator.ListConfigFiles(_baseDir))
        {
            if (ApplyConfig(candidate.Path, persist: true) is not null)
            {
                // 列表因此重新广播一次，下拉框的选中项跟上这份配置
                //
                // The list is broadcast again so the drop-down selects this config
                ListConfigs();
                return true;
            }
        }
        return false;
    }

    /// <summary>广播配置文件列表</summary>
    /// <remarks>Broadcasts the list of config files</remarks>
    public void ListConfigs()
    {
        ConfigList?.Invoke(ConfigLocator.ListConfigFiles(_baseDir));
    }

    /// <summary>
    /// 重命名配置文件：改的是磁盘上的文件名
    /// 名字非法或已有同名文件时拒绝，并由调用方在对话框里说明原因
    ///
    /// Renames a config file: it changes the file name on disk
    /// A rejected name or an existing target is refused, and the caller explains why inside the dialog
    /// </summary>
    public void RenameConfig(string filename, string typedName)
    {
        if (string.IsNullOrEmpty(filename))
        {
            Error?.Invoke("Rename requires a filename");
            return;
        }

        var newPath = ConfigEditor.RenameConfigFile(_baseDir, filename, typedName);
        if (newPath is null)
        {
            Error?.Invoke($"Failed to rename config: {filename}");
            return;
        }

        // 重命名的正是当前配置时，.storage/last_config 里记的绝对路径已经指向不存在的文件
        // 不同步更新它，用户下次启动就会掉回另一份配置，而界面上什么都没说
        //
        // When the renamed file IS the current config, the absolute path recorded in .storage/last_config points at nothing
        // Without updating it the next launch silently falls back to a different config
        if (string.Equals(filename, _lastConfigLoaded?.Filename, StringComparison.OrdinalIgnoreCase))
        {
            ConfigLocator.SaveLastConfigPath(_baseDir, newPath);
            CurrentConfigPath = newPath;
        }

        ConfigRenamed?.Invoke(filename, newPath);
        ListConfigs();
    }

    /// <summary>
    /// 校验用户输入的配置文件名，供对话框在关闭前给出具体原因
    /// excludeFilename 用于重命名自己：与自己同名不算冲突
    ///
    /// Validates a user-typed config file name so the dialog can state the exact reason before closing
    /// excludeFilename is for renaming a file onto its own name, which is not a clash
    /// </summary>
    public ConfigNameError ValidateConfigName(string typedName, string? excludeFilename = null)
        => ConfigLocator.ValidateConfigName(_baseDir, typedName, excludeFilename);

    /// <summary>
    /// 新建配置文件并**立即切换过去**
    /// 用户点"+“的意图是"开始用一套新配置"
    /// 若只是建好文件却不切换，还得再去下拉框里找一遍，多一步无意义的操作
    /// 返回新文件名，失败则广播错误
    ///
    /// Creates a config file and **switches to it immediately**
    /// Clicking "+" means "start using a new set of mappings"
    /// Building the file without switching would leave the user to find it in the drop-down again
    /// That is one pointless extra step
    /// Returns the new filename, or broadcasts an error on failure
    /// </summary>
    public string? CreateConfig(string name)
    {
        var path = ConfigEditor.CreateConfigFile(_baseDir, name);
        if (path is null)
        {
            Error?.Invoke("Failed to create config: " + name);
            return null;
        }

        var filename = Path.GetFileName(path);
        ListConfigs();
        LoadConfig(path);
        // 沿用"后端发结构化事件、前端本地化"的既有模式（后端不依赖 UI 的 i18n）
        //
        // Follows the existing "the backend emits structured events and the front end localises" pattern
        // The backend does not depend on the UI's i18n
        ConfigCreated?.Invoke(filename);
        return filename;
    }

    /// <summary>打开配置目录</summary>
    /// <remarks>Opens the config directory</remarks>
    public void OpenConfigDir()
    {
        ConfigLocator.EnsureConfigDir(_baseDir);
        var dir = Core.Settings.AppPaths.ConfigDir(_baseDir);
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                {
                    UseShellExecute = true,
                });
        }
        catch (Exception err)
        {
            Error?.Invoke("Failed to open config dir: " + err.Message);
        }
    }

    /// <summary>
    /// IPC 边界独立校验（note 0-127、key 可被 VK 表解析），写入后重新应用该配置并广播 midiLog
    ///
    /// Validation at the IPC boundary is independent (note 0-127, and the key must be resolvable by the VK table)
    /// Once written it re-applies that config and broadcasts midiLog
    /// </summary>
    public void AddMapping(string note, string key, string? filename, string? configPath)
    {
        if (note.Length == 0 || key.Length == 0)
        {
            Error?.Invoke("addMapping requires note and key");
            return;
        }

        var noteNum = JsNumber.ToNumber(note);
        if (!JsNumber.IsInteger(noteNum) || noteNum < 0 || noteNum > 127)
        {
            Error?.Invoke("MIDI note must be 0-127");
            return;
        }
        if (BindingParser.ParseBinding(key, note) is null)
        {
            Error?.Invoke("Invalid key name: " + key);
            return;
        }

        var configFilename = string.IsNullOrEmpty(filename) ? ConfigLoader.DefaultConfigFileName(_baseDir) : filename;
        if (!configFilename.EndsWith(".json", StringComparison.Ordinal))
        {
            configFilename += ".json";
        }
        var targetPath = string.IsNullOrEmpty(configPath)
            ? Path.Combine(Core.Settings.AppPaths.ConfigDir(_baseDir), configFilename)
            : configPath;

        var noteStr = ((int)noteNum).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ConfigEditor.AddMappingToConfig(_baseDir, targetPath, noteStr, key))
        {
            ApplyConfig(targetPath, persist: false);
            MappingAdded?.Invoke(noteStr, key);
        }
        else
        {
            Error?.Invoke("Failed to save mapping to config file");
        }
    }

    /// <summary>删除当前配置中的映射</summary>
    /// <remarks>Deletes a mapping from the active config</remarks>
    public void DeleteMapping(string note)
    {
        var configPath = CurrentConfigPath;
        if (note.Length == 0 || configPath is null)
        {
            Error?.Invoke("deleteMapping requires an active config and note");
            return;
        }
        if (ConfigEditor.DeleteMappingFromConfig(_baseDir, configPath, note))
        {
            ApplyConfig(configPath, persist: false);
            MappingDeleted?.Invoke(note);
        }
        else
        {
            Error?.Invoke("Failed to delete mapping from config file");
        }
    }

    /// <summary>
    /// 当前配置里某个音符的映射；该音符未绑定、或尚无配置加载完成时返回 null
    /// 界面据此判断「移除映射」是否可用：让按钮置灰，比点下去再报「没有可移除的映射」更加直观
    /// 读的是 _lastConfigLoaded 这份快照，与 CurrentConfigPath 一样不加锁
    /// 该字段整体替换，而记录本身不可变，因此读到的永远是某一次完整加载的结果，不会是半个状态
    ///
    /// The mapping for one note in the current config; null when the note is unbound or no config has finished loading yet
    /// The UI uses this to decide whether "Remove mapping" is available
    /// Greying the button out is clearer than letting the click report "there is no mapping to remove"
    /// The read is of the _lastConfigLoaded snapshot and, like CurrentConfigPath, it takes no lock
    /// That field is replaced as a whole and the record itself is immutable
    /// So a read sees the result of one complete load rather than half a state
    /// </summary>
    public MappingEntry? FindMapping(byte note)
        => _lastConfigLoaded is { } info && info.Mapping.TryGetValue(note, out var entry) ? entry : null;

    /// <summary>
    /// 加载配置 -> 释放已按住的按键 -> 换表 -> 按需持久化 ->广播 configLoaded
    /// 失败返回 null（由调用方广播错误）
    ///
    /// Load the config -> release the held keys -> swap the table -> persist if needed -> broadcast configLoaded
    /// Returns null on failure (the caller broadcasts the error)
    /// </summary>
    private MappingConfig? ApplyConfig(string? configPath, bool persist)
    {
        var configResult = ConfigLoader.LoadConfig(_baseDir, SilentOptions with { ConfigPath = configPath });
        if (configResult is null)
        {
            return null;
        }

        List<MidiAction> releaseActions;
        lock (_gate)
        {
            releaseActions = _stateMachine.ReleaseAllKeys();
        }
        EnqueueActions(releaseActions);

        if (persist)
        {
            ConfigLocator.SaveLastConfigPath(_baseDir, configResult.ConfigPath);
        }
        lock (_gate)
        {
            InstallConfig(configResult);
        }
        // 记录加载时的文件签名：热更新监视用它区分"应用自身写入"与"外部编辑"
        //
        // Records the file signature at load time
        // The hot-reload watcher uses it to tell "a write by the app itself" from "an external edit"
        _lastLoadedSignature = ConfigSignature(configResult.ConfigPath);
        EmitConfigLoaded(configResult);
        return configResult;
    }

    private void InstallConfig(MappingConfig config)
    {
        _stateMachine.NoteMap.Clear();
        foreach (var (note, vkCodes) in config.NoteMap)
        {
            _stateMachine.NoteMap[note] = vkCodes;
        }
        CurrentConfigPath = config.ConfigPath;
    }

    private void EmitConfigLoaded(MappingConfig config)
    {
        var mapping = new SortedDictionary<byte, MappingEntry>();
        foreach (var (note, vkCodes) in config.NoteMap)
        {
            mapping[note] = new MappingEntry(
                string.Join("+", vkCodes.Select(VirtualKeyTable.LabelFor)),
                vkCodes.Length > 1);
        }
        // 显示名就是文件名去掉扩展名，与配置下拉框里那一项完全一致
        //
        // The display name is the file name without its extension, matching the entry in the config drop-down exactly
        var filename = Path.GetFileName(config.ConfigPath);
        _lastConfigLoaded = new ConfigLoadedInfo(
            config.ConfigPath,
            filename,
            Path.GetFileNameWithoutExtension(filename),
            config.NoteMap.Count,
            mapping);
        ConfigLoaded?.Invoke(_lastConfigLoaded);
    }

    // ------------------------------------------------------------------ update & misc

    /// <summary>
    /// 当前的更新网络设置
    /// 代理在**每次检查时**读取，因此用户在设置页改完立刻生效，不需要重启应用
    ///
    /// The current update network settings
    /// The proxy is read on **every check**
    /// So a change made on the settings page takes effect immediately without restarting the app
    /// </summary>
    public UpdateOptions UpdateOptions =>
        new(Core.Settings.AppStorage.GetUpdateProxy(_baseDir));

    /// <summary>
    /// 启动时静默更新检查，开发构建直接返回
    /// 这里是**唯一**会为更新访问 GitHub 的入口，判定放在这里，任何调用方都漏不掉（界面另有一层，见 AppServices.UpdatesEnabled）
    ///
    /// The silent update check at startup; a development build returns immediately
    /// This is the **only** entry point that reaches GitHub for updates
    /// The decision lives here and no caller can miss it
    /// The UI has a second layer, see AppServices.UpdatesEnabled
    /// </summary>
    public async Task CheckForUpdatesAsync(string currentVersion)
    {
        if (!AppServices.UpdatesEnabled)
        {
            return;
        }

        var (info, _) = await UpdateChecker
            .CheckForUpdatesDetailedAsync(currentVersion, UpdateOptions)
            .ConfigureAwait(false);
        if (info is not null)
        {
            _lastUpdate = info;
            UpdateAvailable?.Invoke(info);
        }
    }

    /// <summary>向（重新创建的）页面重放最近一次 updateAvailable</summary>
    /// <remarks>Replays the most recent updateAvailable to a (recreated) page</remarks>
    public void ReemitUpdateAvailable()
    {
        if (_lastUpdate is not null)
        {
            UpdateAvailable?.Invoke(_lastUpdate);
        }
    }

    // ------------------------------------------------------------------ config hot reload

    private void StartConfigWatcher()
    {
        if (_configWatcher is not null)
        {
            return;
        }
        try
        {
            var watcher = new FileSystemWatcher(Core.Settings.AppPaths.ConfigDir(_baseDir), "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnConfigFileChanged;
            watcher.Created += OnConfigFileChanged;
            watcher.Renamed += OnConfigFileChanged;
            watcher.Deleted += OnConfigFileDeleted;
            watcher.Error += OnConfigWatcherError;
            _configWatcher = watcher;
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.config]: hot reload watcher failed: " + err.Message);
        }
    }

    private void OnConfigWatcherError(object? sender, ErrorEventArgs e)
    {
        // 缓冲区溢出等异常情况：强制重载一次当前配置兜底
        //
        // Abnormal situations such as a buffer overflow: force one reload of the current config as a fallback
        try
        {
            HotReloadConfig(force: true);
        }
        catch
        {
            // ignore
        }
    }

    private void OnConfigFileChanged(object? sender, FileSystemEventArgs e)
    {
        if (!string.Equals(Path.GetExtension(e.FullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        // 防抖：编辑器保存通常连发多个事件，等 400ms 静默后再重载
        //
        // Debounce: saving in an editor usually fires several events in a row, so wait for 400 ms of silence before reloading
        _configReloadDebounce?.Dispose();
        _configReloadDebounce = new System.Threading.Timer(
            _ => HotReloadConfig(force: false), null, TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    private void OnConfigFileDeleted(object? sender, FileSystemEventArgs e)
    {
        if (!string.Equals(Path.GetExtension(e.FullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        // 与 Changed 共用同一个防抖计时器：重命名/删除常常连发多个事件
        //
        // Shares the same debounce timer as Changed: a rename or delete often fires several events
        _configReloadDebounce?.Dispose();
        _configReloadDebounce = new System.Threading.Timer(
            _ => OnConfigFileRemoved(), null, TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
    }

    private void OnConfigFileRemoved()
    {
        try
        {
            // 配置文件被外部删除/改名后，下拉列表必须立即刷新
            // 否则界面里还会列出一个已经不存在的文件
            // 否则界面里还会列出一个已经不存在的文件
            //
            // After a config file is deleted or renamed externally the drop-down has to refresh immediately
            // Otherwise the UI would keep listing a file that no longer exists
            ListConfigs();

            var path = CurrentConfigPath;
            if (path is null || File.Exists(path))
            {
                // 未被删除的是当前配置：无需其它处理
                //
                // A file that was not deleted is the current config: no further handling is needed
                return;
            }

            // 当前配置被外部删除：保留内存中已加载的映射继续运行，不静默改变按键行为
            // 只让签名失效，这样文件被重新创建（或界面再次保存映射）时能被监视器/持久化重新捕获
            //
            // The current config was deleted externally
            // The mappings already loaded in memory are kept so key behaviour does not change silently
            // Only the signature is invalidated
            // So that recreating the file (or saving a mapping from the UI again) is picked up by the watcher or by persistence
            _lastLoadedSignature = null;
        }
        catch
        {
            // ignore
        }
    }

    private static string ConfigSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "missing";
        }
        catch
        {
            return "missing";
        }
    }

    private void HotReloadConfig(bool force)
    {
        try
        {
            var path = CurrentConfigPath;
            if (path is null)
            {
                return;
            }
            // 应用自身的写入（界面增删映射）已经加载过：签名一致则跳过，避免重复广播
            //
            // A write by the app itself (adding or deleting a mapping in the UI) has already been loaded
            // An identical signature is skipped to avoid a duplicate broadcast
            if (!force && string.Equals(ConfigSignature(path), _lastLoadedSignature, StringComparison.Ordinal))
            {
                return;
            }
            ApplyConfig(path, persist: false);
        }
        catch
        {
            // 文件被编辑器占用的瞬时状态：忽略，下次保存再重载
            //
            // A transient state in which an editor holds the file: ignore it and reload on the next save
        }
    }

    /// <summary>用系统默认浏览器打开 http(s) URL</summary>
    /// <remarks>Opens an http(s) URL in the system default browser</remarks>
    public void OpenUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap] Failed to open browser: " + err.Message);
        }
    }
}
