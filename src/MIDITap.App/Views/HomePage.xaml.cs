// HomePage.cs — 设备选择 + 实时音符盘可视化（中间的矩阵见 Controls/NoteGrid.cs）
// 应用默认就在监听（只要有设备，后端就会启动），因此选中设备等于热切换端口
// 设备消失则回退到第一台可用的，音符事件直接驱动显示
// 扩展功能可用时，下拉框多一个"MIDIBridge"哨兵项（见 OnPortsListed）
//
// HomePage.cs — device selection plus the live note board (the matrix in the middle is Controls/NoteGrid.cs)
// The app listens by default (as long as a device exists the backend starts)
// So selecting a device is a hot port switch
// A device that disappears falls back to the first available one
// Note events drive the display directly
// When the extension is usable the drop-down gains one "remote playing" sentinel entry (see OnPortsListed)

using System.Collections.ObjectModel;
using MIDITap.App.Dialogs;
using MIDITap.App.Helpers;
using MIDITap.App.Services;
using MIDITap.Core.Midi;
using MIDITap.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Views;

/// <summary>实时触发 chip：一个音符及其目标键位</summary>
/// <remarks>A live trigger chip: one note and the target key it maps to</remarks>
public sealed record ActiveNoteChip(byte Note, string Text);


public sealed partial class HomePage : Page
{
    private readonly ObservableCollection<MidiPortInfo> _devices = new();
    private readonly ObservableCollection<ActiveNoteChip> _activeNotes = new();
    // 实时日志的呈现（订阅、筛选、封顶、渲染、滚动）全部在 LogFeed 里，本页的上限是 200 条
    //
    // Showing the live log — subscribing, filtering, capping, rendering, scrolling — lives entirely in LogFeed
    // This page caps it at 200 lines
    private readonly LogFeed _feed;
    private int _selectedPortIndex = -1;
    // 程序化更新设备列表时抑制 SelectionChanged
    // Clear/重选会同步触发事件，否则每次刷新都会丢选中、监听中还会误触发重启
    // Suppresses SelectionChanged while the device list is updated programmatically
    // Clear and reselecting raise the event synchronously
    // Otherwise every refresh would lose the selection
    // A restart would also be triggered while listening
    private bool _suppressDeviceSelection;

    public HomePage()
    {
        InitializeComponent();
        DeviceBox.ItemsSource = _devices;
        ActiveNotesItems.ItemsSource = _activeNotes;
        // 日志的订阅与渲染交给 LogFeed（见该类型）：本页只交出文本块与滚动容器，并给它一个上限
        //
        // Subscription and rendering are LogFeed's job (see that type)
        // This page hands over its text block and scroll host, and gives it a cap
        // inset 传 0：这一栏没有卡片，日志直接排在栏里，页面内边距已经是它到边缘的距离
        // Passing 0: this column has no card, the log is laid out straight in it
        // The page padding is already its distance to the edge
        _feed = new LogFeed(RecentLogText, RecentLogScroll, capacity: 200, inset: 0);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshTexts();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var backend = AppServices.Backend;
        backend.PortsListed += OnPortsListed;
        backend.Started += OnStarted;
        backend.Stopped += OnStopped;
        backend.NoteOn += OnNoteOn;
        backend.NoteOff += OnNoteOff;
        backend.HeldNotesRestored += OnHeldNotesRestored;
        backend.ConfigLoaded += OnConfigLoaded;
        backend.ConfigStateRestored += OnConfigLoaded; // 状态重放走同一处理器
        AppServices.I18n.LanguageChanged += RefreshTexts;
        NoteBoard.NoteClicked += OnNoteBoardNoteClicked;

        // 页面按导航重建，会错过启动时的广播：重新拉取设备列表并重放最近一次 configLoaded
        //
        // The page is rebuilt on navigation and misses the broadcasts made at startup
        // Re-fetch the port list and replay the most recent configLoaded
        AppServices.Backend.ListPorts();
        AppServices.Backend.ReemitConfigLoaded();
        // 仍被按住的音符同样要重放：站在别的页面时按下的那些，这一页没有任何来源知道
        //
        // The notes still held are replayed as well
        // Presses made while another page was shown have no other way of reaching this one
        AppServices.Backend.ReemitHeldNotes();

        // Attach 按当前筛选先渲染一次（含启动期那几条）并跟随后续变化与筛选
        //
        // Attach renders once under the current filter (including the startup lines)
        // It then follows later entries and filter changes
        _feed.Attach();
        RefreshTexts();
        // 页面按导航重建：若创建于主题切换之后，画笔已是新主题
        // 这里再同步一次已持有的激活态（ClearActive 之外的情况）
        // The page is rebuilt on navigation
        // When it was created after a theme switch the brushes are already the new theme's
        // So the active state it already holds is synced once more (the case outside ClearActive)
        NoteBoard.ApplyTheme();
    }


    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var backend = AppServices.Backend;
        backend.PortsListed -= OnPortsListed;
        backend.Started -= OnStarted;
        backend.Stopped -= OnStopped;
        backend.NoteOn -= OnNoteOn;
        backend.NoteOff -= OnNoteOff;
        backend.HeldNotesRestored -= OnHeldNotesRestored;
        backend.ConfigLoaded -= OnConfigLoaded;
        backend.ConfigStateRestored -= OnConfigLoaded;
        AppServices.I18n.LanguageChanged -= RefreshTexts;
        _feed.Detach();
        NoteBoard.NoteClicked -= OnNoteBoardNoteClicked;
    }

    // 点击某个格子 -> 打开该音符的映射编辑器（README 承诺的流程）
    // Clicking a cell opens the mapping editor for that note (the flow the README promises)
    private async void OnNoteBoardNoteClicked(byte note)
    {
        var configPath = AppServices.Backend.CurrentConfigPath;
        var dialog = new MappingEditorDialog(
            configPath is null ? null : System.IO.Path.GetFileName(configPath),
            configPath,
            note,
            allowRemove: true)
        {
            XamlRoot = XamlRoot,
        };
        _ = await dialog.ShowAsync();
    }

    // ------------------------------------------------------------------ backend events

    private void OnPortsListed(IReadOnlyList<MidiPortInfo> ports)
    {
        AppServices.RunOnUi(() =>
        {
            _suppressDeviceSelection = true;
            try
            {
                _devices.Clear();
                foreach (var port in ports)
                {
                    _devices.Add(port);
                }
                AppendBridgeEntry();
                ResolveDeviceSelection();
                SyncDeviceSelection();
                AppServices.Backend.SelectedPort = _selectedPortIndex;
            }
            finally
            {
                _suppressDeviceSelection = false;
            }
            UpdateDevicePlaceholder();
        });
    }

    /// <summary>
    /// 决定下拉框该选中哪一项
    /// 顺序：远程源在用时选哨兵项；否则优先后端真正在听的端口，其次用户此前的选择，最后第一台设备
    /// 端口编号在设备增删后会重排，因此每次都用 Index 重新匹配而不是记住位置
    ///
    /// Decides which item the drop-down selects
    /// The order: the sentinel entry while the remote source is in use; otherwise the port the backend is actually
    /// monitoring, then the user's earlier choice, then the first device
    /// Port numbers are reordered when devices are added or removed, so Index is matched afresh rather than a
    /// position being remembered
    /// </summary>
    private void ResolveDeviceSelection()
    {
        var hasBridgeEntry = _devices.Any(d => Core.Bridge.BridgeServers.IsServerPort(d.Index));

        // 远程源正在用：下拉框必须显示"正在用的那一台"，否则它会报一台其实没在被监听的设备
        //
        // The remote source is in use: the drop-down has to show the server actually in use, or it would name a device
        // that is not being listened to
        if (AppServices.Backend.ActiveSource == MidiSource.Bridge && hasBridgeEntry)
        {
            var servers = AppServices.Bridge.Servers;
            var currentId = Core.Bridge.BridgeServerStore.ResolveCurrent(AppServices.BaseDir).Server?.Id;
            var index = servers.ToList().FindIndex(s => string.Equals(s.Id, currentId, StringComparison.Ordinal));
            _selectedPortIndex = Core.Bridge.BridgeServers.SourcePortFor(index >= 0 ? index : 0);
            return;
        }

        var active = AppServices.Backend.ActivePortIndex;
        if (active is int listening && _devices.Any(d => d.Index == listening))
        {
            _selectedPortIndex = listening;
            return;
        }

        // 此前选的是远程项，但它已不在列表里（扩展被删或配置不全）
        // 停在这个索引上会让下拉框空白，而应用其实在监听某台设备
        //
        // The earlier choice was a remote entry that is no longer in the list (the extension was removed, or the
        // config is incomplete)
        // Staying on that index would leave the drop-down blank while the app is in fact monitoring some device
        if (Core.Bridge.BridgeServers.IsServerPort(_selectedPortIndex) || !_devices.Any(d => d.Index == _selectedPortIndex))
        {
            _selectedPortIndex = -1;
        }

        if (_selectedPortIndex < 0)
        {
            var firstDevice = _devices.FirstOrDefault(IsPhysicalDevice);
            _selectedPortIndex = firstDevice?.Index ?? -1;
        }
    }

    /// <summary>
    /// 追加下拉框的固定项：一台物理设备都没有时先补一个空行，最后一项是"MIDIBridge"
    /// "该不该有空行、MIDIBridge 该写成什么样"由 <see cref="MIDITap.Core.Bridge.BridgePicker.Describe"/> 判定，
    /// 这里只把结论落成列表项，页面本身不推导规则
    ///
    /// MIDIBridge 排在最后：WinMM 端口按下标顺序先入集合，这个哨兵项最后 Add，
    /// 本文件没有排序调用，因此有其他设备时它也在底部
    ///
    /// 未安装时条目照样出现：下拉框是功能的唯一入口，条目消失就等于把入口藏了，
    /// 用户既不知道该功能存在，也找不到下载的地方
    /// 选中这一项才决定下一步（见 <see cref="PickBridgeAsync"/>）
    ///
    /// 协议不被支持时条目仍然出现，但带一句说明、且选中会被拒：
    /// 让条目消失等于告诉用户"这个功能没有了"，而事实是"装着但这一版对不上"，
    /// 后者该引导他去重装，前者会让他去找下载入口，两者的下一步动作不同
    ///
    /// Appends the drop-down's fixed entries: an empty row when there is no physical device, then "MIDIBridge" last
    /// Whether the empty row belongs, and how MIDIBridge reads, is <see cref="MIDITap.Core.Bridge.BridgePicker.Describe"/>'s
    /// verdict; this method only turns it into list items, so the page derives no rule of its own
    ///
    /// MIDIBridge sits last: the WinMM ports enter the collection in index order and this sentinel is appended last,
    /// and nothing in this file sorts, so it also sits at the bottom when other devices exist
    ///
    /// The entry appears even before the component is installed: the drop-down is the feature's only way in, and
    /// hiding the entry would hide that way in, leaving the user neither aware of the feature nor able to find a
    /// download. Picking it is what decides the next step (see <see cref="PickBridgeAsync"/>)
    ///
    /// An unusable protocol keeps the entry visible but labelled, and selecting it is refused: hiding the entry says
    /// "this feature is gone", while the truth is "installed, but this build does not match", and the latter should
    /// send the user to a reinstall rather than hunting for a download
    /// </summary>
    private void AppendBridgeEntry()
    {
        // 一次求值兼顾两件事：装没装，以及协议对不对得上
        // BridgeUnusableReason 已经在内部按"未装 → 协议 → 配置"的顺序判定过，这里不重算
        //
        // One evaluation answers both questions — installed or not, and whether the protocol matches
        // BridgeUnusableReason already decides in the order "not installed, then protocol, then config", so
        // nothing is recomputed here
        var reason = AppServices.Bridge.BridgeUnusableReason;
        var installed = reason != BridgeUnusableReason.NotInstalled;
        var protocolSupported = reason != BridgeUnusableReason.ProtocolUnsupported;
        var plan = MIDITap.Core.Bridge.BridgePicker.Describe(_devices.Count(IsPhysicalDevice), installed, protocolSupported);

        if (plan.ShowEmptyRow)
        {
            // 空行没有名字：留白即"这里没有设备"，不写占位文字，那会看起来像一台叫"未知"的设备
            //
            // The empty row carries no name: the blank itself says "there is no device here", whereas placeholder
            // text would read like a device called "unknown"
            _devices.Add(new MidiPortInfo(MidiSourcePort.EmptyRow, string.Empty));
        }

        var baseName = AppServices.I18n.T("devices.bridge");
        if (plan.BridgeLabel == MIDITap.Core.Bridge.BridgePickerLabel.Unavailable)
        {
            // 协议不可用时把原因写进条目名：下拉框的条目只能显示一段文字，
            // 没有副标题的位置，因此说明与名字合成一项
            //
            // While the protocol is unusable the reason goes into the entry's own text: a drop-down item carries one
            // string and has no room for a subtitle, so the explanation is merged with the name
            baseName = AppServices.I18n.T("devices.bridge.unavailable", ("name", baseName));
        }

        // 每台服务器各占一项：用户从这里直接选"连哪一台"，与设置页里切换选中项是同一件事
        // 没有服务器时只留一项不带编号的内置名称，作为"还没配置"的入口
        // 一台物理端口恰好也叫 MIDIBridge 时，[内置] 前缀把两者区分开
        //
        // Every server gets its own item: the user picks which one to connect straight here, and that is the same
        // operation as switching the selection in the settings page
        // With no server there is one item carrying the built-in name without an index, the entry for "not configured
        // yet"
        // When a physical port happens to be called MIDIBridge as well, the [内置] prefix is what tells them apart
        var servers = AppServices.Bridge.Servers;
        if (servers.Count == 0)
        {
            _devices.Add(new MidiPortInfo(Core.Bridge.BridgeServers.SourcePortFor(0), baseName));
            return;
        }

        for (var i = 0; i < servers.Count; i++)
        {
            var marker = Core.Bridge.BridgeServers.ShortLabel(servers[i].Name, i, servers.Count);
            var label = marker.Length > 0 ? $"{baseName} · {marker}" : baseName;
            _devices.Add(new MidiPortInfo(Core.Bridge.BridgeServers.SourcePortFor(i), label));
        }
    }

    /// <summary>
    /// 列表项是不是一台真实设备
    /// WinMM 的端口号从 0 开始，两个哨兵项（空行 -3、MIDIBridge -2）都是负数
    ///
    /// Whether a list item is a real device
    /// WinMM port numbers start at 0, and both sentinels (the empty row at -3 and MIDIBridge at -2) are negative
    /// </summary>
    private static bool IsPhysicalDevice(MidiPortInfo item) => item.Index >= 0;

    private void OnStarted(int port, string portName) => AppServices.RunOnUi(() =>
    {
        // 远程源在用时本地端口的 Started 不改选中项：那一项属于远程
        // 竞争窗口：切到远程的同一瞬间，一次物理启动的 Started 可能还在队列里
        //
        // While the remote source is in use, a local port's Started does not change the selection: that item belongs
        // to the remote side
        // Race window: at the same instant the remote source is picked, a physical start's Started may still be in
        // the queue
        if (AppServices.Backend.ActiveSource == MidiSource.Bridge)
        {
            return;
        }
        // 自动启动/热切换可能由后端选定端口：同步下拉框选中项
        // Auto-start or a hot swap can let the backend pick the port: sync the drop-down's selected item
        if (_selectedPortIndex != port)
        {
            _selectedPortIndex = port;
            _suppressDeviceSelection = true;
            try
            {
                SyncDeviceSelection();
            }
            finally
            {
                _suppressDeviceSelection = false;
            }
        }
    });

    private void OnStopped() => AppServices.RunOnUi(ClearActiveNotes);

    /// <summary>chip 文本：音名 + 目标键位，未绑定显示占位文案
    /// A chip's text: the note name plus the target key, or the placeholder when unbound</summary>
    private static string NoteChipText(byte note, string? key) =>
        $"{NoteNames.Name(note)} → {key ?? AppServices.I18n.T("monitor.notes.unbound")}";

    private void OnNoteOn(byte note, byte velocity, string? key) => AppServices.RunOnUi(() =>
    {
        NoteBoard.SetNoteActive(note, true);
        var text = NoteChipText(note, key);
        var existing = _activeNotes.FirstOrDefault(r => r.Note == note);
        if (existing is not null)
        {
            _activeNotes[_activeNotes.IndexOf(existing)] = existing with { Text = text };
        }
        else
        {
            _activeNotes.Add(new ActiveNoteChip(note, text));
        }
        RenderActiveNotes();
    });

    private void OnNoteOff(byte note) => AppServices.RunOnUi(() =>
    {
        NoteBoard.SetNoteActive(note, false);
        var existing = _activeNotes.FirstOrDefault(r => r.Note == note);
        if (existing is not null)
        {
            _activeNotes.Remove(existing);
        }
        RenderActiveNotes();
    });

    // 页面按导航重建，站在别的页面时按下的音符它收不到 NoteOn
    // 加载时把这批音符补回来，否则它们要等到抬起才第一次出现，而抬起只负责移除
    //
    // The page is rebuilt on navigation and receives no NoteOn for presses made while another page was shown
    // They are filled in on load; otherwise they first appear on release, and the release only removes them
    private void OnHeldNotesRestored(IReadOnlyList<HeldNote> held) => AppServices.RunOnUi(() =>
    {
        foreach (var note in held)
        {
            // 重放可能与一次真实 NoteOn 竞争，同一音符已经在了就跳过
            //
            // The replay can race a genuine NoteOn, so a note already present is skipped
            if (_activeNotes.Any(r => r.Note == note.Note))
            {
                continue;
            }
            NoteBoard.SetNoteActive(note.Note, true);
            _activeNotes.Add(new ActiveNoteChip(note.Note, NoteChipText(note.Note, note.KeyLabel)));
        }
        RenderActiveNotes();
    });

    private void OnConfigLoaded(ConfigLoadedInfo info) => AppServices.RunOnUi(() =>
    {
        ClearActiveNotes();
        // 音符盘只关心"哪个音符显示什么键位"，不需要类型，因此在调用点投影出标签字典
        //
        // The note board only needs note -> label; the type is irrelevant to it, so project here
        NoteBoard.UpdateMapping(info.Mapping.ToDictionary(kv => kv.Key, kv => kv.Value.KeyLabel));
        // 把当前配置摊在 Home 页上：用户不必切到映射页就能确认"现在用的是哪套、有几个映射"
        //
        // The current config is laid out on the Home page
        // The user can confirm which config is in use and how many mappings it has without switching to the mapping page
        ConfigNameText.Text = info.Name;
        ConfigCountText.Text = AppServices.I18n.T("home.configCount", ("count", info.NoteCount.ToString()));
        ConfigCountText.Visibility = Visibility.Visible;
    });



    // ------------------------------------------------------------------ interactions

    private async void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceSelection)
        {
            return;
        }
        var prevIndex = _selectedPortIndex;
        _selectedPortIndex = DeviceBox.SelectedIndex >= 0 ? _devices[DeviceBox.SelectedIndex].Index : -1;

        // 空行只是"一台设备都没有"时给列表留的位置，不是一台设备
        // 选中它没有含义，也不会改变正在听的来源，因此直接退回
        //
        // The empty row only holds a place in the list while there is no device; it is not a device
        // Picking it means nothing and changes no listening source, so the selection rolls back
        if (_selectedPortIndex == MidiSourcePort.EmptyRow)
        {
            RollBackDeviceSelection(prevIndex);
            return;
        }

        // 远程哨兵项不是端口号，不能写进 SelectedPort：那个字段的含义是"当前选中的 WinMM 端口"
        // 写进负数之后 CaptureNote 之类的调用会拿它去开设备
        //
        // The remote sentinel is not a port number and must not go into SelectedPort, whose meaning is "the WinMM
        // port currently chosen"
        // Writing a negative number there would let calls such as CaptureNote try to open it as a device
        var isBridgeItem = Core.Bridge.BridgeServers.IsServerPort(_selectedPortIndex);
        if (!isBridgeItem)
        {
            AppServices.Backend.SelectedPort = _selectedPortIndex;
        }

        if (_selectedPortIndex == prevIndex)
        {
            return;
        }

        ClearActiveNotes();

        if (isBridgeItem)
        {
            await PickBridgeAsync(prevIndex, Core.Bridge.BridgeServers.IndexForSourcePort(_selectedPortIndex));
            return;
        }

        if (_selectedPortIndex >= 0)
        {
            // 回到本机设备：先停掉远程会话，再热切到新端口
            // 顺序不能反：Backend.Start 也会摘掉远程通路，但上游进程那时还在跑，
            // 它随时可能把帧送回来
            //
            // Back to a local device: stop the remote session first, then hot-swap to the new port
            // The order matters: Backend.Start detaches the remote route too, yet the upstream process would still
            // be running at that point and could deliver frames at any moment
            AppServices.Bridge.Stop();
            AppServices.Backend.SelectPhysicalSource();
            AppStorage.SaveBridgeSource(AppServices.BaseDir, "Physical");
            AppServices.Backend.Start(_selectedPortIndex, AppServices.Backend.CurrentConfigPath);
        }
    }

    /// <summary>
    /// 用户在下拉框里选中 MIDIBridge 之后该做什么
    /// 三种"接不上去"的情况各有各的下一步：没装给安装入口、协议对不上指向重装、地址没填指向设置
    ///
    /// 能接上去时：切换来源、持久化选择，再拉起上游进程
    /// 持久化放在这里而不是等启动流程，用户下次开局就该回到同一个来源
    ///
    /// What to do once MIDIBridge is picked in the drop-down
    /// The three cases that cannot connect each have their own next step: no install means an install entry, a
    /// mismatched protocol points at a reinstall, and a missing address points at the settings
    ///
    /// When it can connect: switch the source, persist the choice, then start the upstream process
    /// Persisting here rather than leaving it to the startup sequence means the next launch returns to the same source
    /// </summary>
    private async Task PickBridgeAsync(int prevIndex, int serverIndex)
    {
        // 先把下拉框选中的那一台设为当前服务器：下面几处"不可用"的判断都是针对它做的，
        // 判完再退回的话用户看到的仍然是他点的那一项
        //
        // The picked server becomes the current one first: the "unusable" verdicts below are made about it, and
        // rolling back afterwards would leave the drop-down on the item the user clicked
        var servers = AppServices.Bridge.Servers;
        if (serverIndex >= 0 && serverIndex < servers.Count)
        {
            AppServices.Bridge.SelectServer(servers[serverIndex].Id);
        }

        var reason = AppServices.Bridge.BridgeUnusableReason;

        // 没装：先问要不要装，再谈别的
        // 这一步必须在协议判断之前：未安装时协议号是空的，得不出"不支持"的结论
        //
        // Not installed: ask whether to install before anything else
        // This has to come before the protocol check: a missing install has no protocol number, so no
        // "unsupported" verdict can be reached
        if (reason == BridgeUnusableReason.NotInstalled)
        {
            // 先退回原来的端口：对话框与下载都要花时间，期间下拉框不该停在一个尚未生效的来源上
            //
            // The previous port is restored first: the dialog and the download both take time, and the drop-down
            // should not sit on a source that has not taken effect in the meantime
            RollBackDeviceSelection(prevIndex);
            var dialog = new BridgeInstallDialog { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            var outcome = await BridgeInstallFlow.RunAsync(AppServices.Bridge, XamlRoot);
            if (outcome is { Ok: true })
            {
                // 装好之后重算列表：协议探测与条目文案都由这次重建刷新
                //
                // The list is recomputed once installed: that rebuild refreshes the protocol probe and the entry text
                AppServices.Backend.ListPorts();
            }
            return;
        }

        // 已装但协议不被本 Tap 支持：接上去也消费不了一帧，停在"正在连接"比直接说清楚更糟
        // 已经停在远程源上时不拦：那是一次正在用的会话，不该因为重装期间协议探测的变化被切断
        //
        // Installed with a protocol this Tap does not support: connecting would consume no frame, and sitting at
        // "connecting" is worse than saying so
        // A session already on the remote source is not blocked: it is in use, and a protocol probe changing during
        // a reinstall should not cut it off
        if (reason == BridgeUnusableReason.ProtocolUnsupported
            && AppServices.Backend.ActiveSource != MidiSource.Bridge)
        {
            ToastService.Show(
                AppServices.I18n.T("devices.bridge.unavailable.toast"),
                severity: Core.Notifications.ToastSeverity.Warning);
            RollBackDeviceSelection(prevIndex);
            return;
        }

        // 已装但从它读不出任何信息（损坏）：连上去只会立刻失败，先让用户看到"需要重装"这句话
        // 放在协议判断之后：损坏时协议号也是空的，得不出"不支持"的结论，两者不会同时命中
        //
        // Installed, yet nothing can be read from it (damaged): connecting would fail at once, so the user is told a
        // reinstall is needed instead
        // It comes after the protocol check: a damaged build has no protocol number either, so no "unsupported"
        // verdict is possible and the two never fire together
        if (reason == BridgeUnusableReason.Damaged)
        {
            ToastService.Show(
                AppServices.I18n.T("settings.bridge.damaged"),
                severity: Core.Notifications.ToastSeverity.Warning);
            RollBackDeviceSelection(prevIndex);
            return;
        }

        // 已装且协议对得上，但服务器地址或端口没填：拉起上游进程只会立刻失败并弹一条"请填写地址"，
        // 而用户此刻就在下拉框边上，直接告诉他去哪儿填更省一步
        //
        // Installed with a matching protocol, yet the server address or port is missing: starting the upstream
        // process would fail at once with a "fill the address in" toast, and the user is standing right next to the
        // drop-down, so saying where to fill it in saves a step
        if (reason == BridgeUnusableReason.NotConfigured)
        {
            ToastService.Show(
                AppServices.I18n.T("bridge.failed.notConfigured"),
                severity: Core.Notifications.ToastSeverity.Warning);
            RollBackDeviceSelection(prevIndex);
            return;
        }

        // 用 ConnectServer 而不是"写选中键 + StartAsync"：后者在会话已在运行时直接返回，
        // 换了服务器却还连着上一台，界面看着切了、实际没切
        //
        // ConnectServer is used rather than "write the selection key + StartAsync": the latter returns at once while a
        // session is running, leaving the previous server connected while the UI looks switched
        AppServices.Bridge.ConnectServer(servers[serverIndex].Id);
    }

    /// <summary>
    /// 收起下拉框并重算列表：连接状态变化后让条目与状态文字跟上
    ///
    /// Closes the drop-down and recomputes the list, so items and the state text follow the connection change
    /// </summary>
    private void RefreshDeviceList()
    {
        AppServices.Backend.ListPorts();
    }

    /// <summary>
    /// 把下拉框退回选择前的端口
    /// SelectionChanged 已经发生过，不撤回的话界面会显示一个并未生效的来源
    ///
    /// Puts the drop-down back on the port that was selected before
    /// SelectionChanged has already fired, and without this the UI would display a source that did not take effect
    /// </summary>
    private void RollBackDeviceSelection(int prevIndex)
    {
        _selectedPortIndex = prevIndex;

        // 选中键也要退回去：上面的检查可能已经把它写成了刚点的那台，
        // 只把下拉框退回原位会让"界面上选中的"与"配置里选中的"变成两台
        //
        // The selection key is rolled back as well: the checks above may already have written the freshly clicked
        // server into it, and restoring only the drop-down would leave "selected on screen" and "selected in config"
        // naming two different servers
        var servers = AppServices.Bridge.Servers;
        var prevServerIndex = Core.Bridge.BridgeServers.IndexForSourcePort(prevIndex);
        if (prevServerIndex >= 0 && prevServerIndex < servers.Count)
        {
            Core.Bridge.BridgeServerStore.Select(AppServices.BaseDir, servers[prevServerIndex].Id);
        }

        _suppressDeviceSelection = true;
        try
        {
            SyncDeviceSelection();
        }
        finally
        {
            _suppressDeviceSelection = false;
        }
    }

    // ------------------------------------------------------------------ render helpers

    private void ClearActiveNotes()
    {
        NoteBoard.ClearActive();
        _activeNotes.Clear();
        RenderActiveNotes();
    }

    /// <summary>
    /// 只控制"暂无"提示的显隐：chip 本身由 ItemsControl 绑定集合自动增删
    /// 排序在 OnNoteOn 插入时保证（按音符编号升序），无需每次重建文本
    /// </summary>
    /// <remarks>
    /// Controls only the visibility of the "no notes" hint
    /// The chips themselves are added and removed by the ItemsControl data binding
    /// Their sorting is guaranteed when OnNoteOn inserts them (ascending by note number)
    /// So the text need not be rebuilt every time
    /// </remarks>
    private void RenderActiveNotes()
    {
        var empty = _activeNotes.Count == 0;
        ActiveNotesEmpty.Text = empty ? AppServices.I18n.T("monitor.notes.empty") : string.Empty;
        ActiveNotesEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // 把当前触发的键位汇总给无障碍层（同时也是 UIA 能观察到的读点）
        // 按音符编号升序，与视觉上 chip 的顺序一致
        //
        // The active key labels are summarised for the accessibility layer (this is also the read point UIA can observe)
        // Sorted ascending by note number, matching the visual order of the chips
        var summary = string.Join(", ",
            _activeNotes.OrderBy(r => r.Note).Select(r => r.Text));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ActiveNotesItems, summary);
    }

    private void SyncDeviceSelection()
    {
        var index = _devices.ToList().FindIndex(d => d.Index == _selectedPortIndex);
        DeviceBox.SelectedIndex = index; // -1 时清除选择

        // 把当前设备名交给无障碍层：屏幕阅读器要能读出"现在听的是哪台设备"
        // 下拉框本身没有可见标签（设备条上只有一个图标），不设这一项它的可访问名就是空的
        // 这也是 UIA 能观察到的读点，端到端测试因此可以直接读出在听哪个端口，不必从日志文字里猜
        //
        // The current device name is handed to the accessibility layer
        // A screen reader has to be able to say which device is being listened to
        // The drop-down has no visible label (the device bar carries only an icon), so without this its accessible name is empty
        // It is also the read point UIA can observe, so the end-to-end test reads the port rather than guessing it from log text
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            DeviceBox,
            index >= 0 ? _devices[index].Name : AppServices.I18n.T("devices.empty"));
    }

    private void UpdateDevicePlaceholder()
    {
        // 占位文本只在一台物理设备都没有时出现：列表里虽然有"MIDIBridge"这一项，
        // 但那不是设备，此时说"没有找到 MIDI 设备"正好解释了上面那个空行
        //
        // The placeholder appears only when there is no physical device at all: the list does carry the
        // "MIDIBridge" entry, but that is not a device, and "no MIDI device found" is exactly what explains the
        // blank row above it
        var deviceCount = _devices.Count(IsPhysicalDevice);
        DeviceBox.PlaceholderText = deviceCount == 0 ? AppServices.I18n.T("devices.empty") : string.Empty;
        // 设备条右侧的状态文字：把"有没有设备/是否在听"直接说清楚，省得用户看状态胶囊
        // 只在多台设备时显示数量：0 台由 PlaceholderText 表达，1 台无需赘述
        // 计数只数真实设备：远程项是功能入口，不是一台设备，把它数进去会得到"2 devices"却只有一台琴
        //
        // The status text on the right of the device bar states whether a device exists and whether the app is listening to it
        // So the user need not read the status pill
        // The count is shown only with more than one device: zero devices is expressed by PlaceholderText, and one needs no remark
        // Only real devices are counted: the remote entry is a feature entry, not a device, and counting it would
        // report "2 devices" for a single piano
        DeviceStateText.Text = AppServices.I18n.T("devices.count", ("count", deviceCount.ToString()));
        DeviceStateText.Visibility = deviceCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 对比度主题变化后重绘音符盘配色
    /// NoteBoard 由 x:Name 生成为私有字段，因此对外只暴露这一个重绘入口
    /// 不把控件本身公开出去
    ///
    /// Repaints the note board for a contrast-theme change
    /// The x:Name field is private, so only this entry point is exposed rather than the control itself
    /// </summary>
    public void ApplyNoteBoardTheme() => NoteBoard.ApplyTheme();

    public void RefreshTexts()
    {
        Func<string, string> t = AppServices.I18n.T;
        ConfigLabel.Text = t("home.currentConfig");
        NotesLabel.Text = t("monitor.notes.label");
        LogLabel.Text = t("monitor.log.label");
        RenderActiveNotes();
        UpdateDevicePlaceholder();
    }
}
