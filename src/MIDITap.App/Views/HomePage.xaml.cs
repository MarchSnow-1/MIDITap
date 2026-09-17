// HomePage.cs — 设备选择 + 实时音符盘可视化（中间的矩阵见 Controls/NoteGrid.cs）
// 应用默认就在监听（只要有设备，后端就会启动），因此选中设备等于热切换端口
// 设备消失则回退到第一台可用的，音符事件直接驱动显示
//
// HomePage.cs — device selection plus the live note board (the matrix in the middle is Controls/NoteGrid.cs)
// The app listens by default (as long as a device exists the backend starts)
// So selecting a device is a hot port switch
// A device that disappears falls back to the first available one
// Note events drive the display directly

using System.Collections.ObjectModel;
using MIDITap.App.Dialogs;
using MIDITap.App.Helpers;
using MIDITap.App.Services;
using MIDITap.Core.Update;
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
    // 用户已在浮窗上点过操作（或关闭）的更新版本：重放不再弹出
    // The update version the user has already acted on (or closed) in the toast: a replay does not show it again
    private string? _dismissedUpdateLatest;

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
        backend.ConfigLoaded += OnConfigLoaded;
        backend.ConfigStateRestored += OnConfigLoaded; // 状态重放走同一处理器
        backend.UpdateAvailable += OnUpdateAvailable;
        AppServices.I18n.LanguageChanged += RefreshTexts;
        NoteBoard.NoteClicked += OnNoteBoardNoteClicked;

        // 页面按导航重建，会错过启动时的广播：重新拉取设备列表并重放最近一次 configLoaded / updateAvailable
        // The page is rebuilt on navigation and misses the broadcasts made at startup
        // Re-fetch the port list and replay the most recent configLoaded / updateAvailable
        AppServices.Backend.ListPorts();
        AppServices.Backend.ReemitConfigLoaded();
        AppServices.Backend.ReemitUpdateAvailable();

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
        backend.ConfigLoaded -= OnConfigLoaded;
        backend.ConfigStateRestored -= OnConfigLoaded;
        backend.UpdateAvailable -= OnUpdateAvailable;
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

                // 没有启停按钮，下拉框必须如实反映"当前到底在听哪个端口"
                // 监听中的端口优先
                // 其次是用户此前选中的端口（若仍存在）
                // 最后回退到第一台设备
                // 端口编号在设备增删后会重排，所以每次都用 Index 重新匹配
                //
                // There is no start/stop button
                // So the drop-down must faithfully reflect "which port is actually being listened to"
                // The port being listened to wins
                // Then the port the user picked earlier (if it still exists)
                // The first device is the fallback
                // Port numbers are reordered when devices are added or removed, so Index is matched afresh every time
                var active = AppServices.Backend.ActivePortIndex;
                if (active is int listening && _devices.Any(d => d.Index == listening))
                {
                    _selectedPortIndex = listening;
                }
                else if (_selectedPortIndex >= 0 && !_devices.Any(d => d.Index == _selectedPortIndex))
                {
                    _selectedPortIndex = -1;
                }
                if (_selectedPortIndex < 0 && _devices.Count > 0)
                {
                    _selectedPortIndex = _devices[0].Index;
                }
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

    private void OnStarted(int port, string portName) => AppServices.RunOnUi(() =>
    {
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

    private void OnNoteOn(byte note, byte velocity, string? key) => AppServices.RunOnUi(() =>
    {
        NoteBoard.SetNoteActive(note, true);
        var text = $"{NoteNames.Name(note)} → {key ?? AppServices.I18n.T("monitor.notes.unbound")}";
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

    private void OnUpdateAvailable(UpdateInfo info) => AppServices.RunOnUi(() =>
    {
        // 用户已经忽略过同一版本时不再弹出
        // It is not shown once the user has already dismissed the same version
        if (info.Latest == _dismissedUpdateLatest)
        {
            return;
        }

        // 右上角浮窗，带操作按钮 => 不自动消失，等用户点"GitHub Releases"或关闭
        // The toast in the top-right corner carries an action button
        // So it does not disappear on its own and waits for the user to click "GitHub Releases" or close it
        ToastService.Show(
            AppServices.I18n.T("update.available", ("latest", info.Latest), ("current", info.Current)),
            severity: Core.Notifications.ToastSeverity.Info,
            actionLabel: AppServices.I18n.T("update.releasesBtn"),
            action: () =>
            {
                // 用户点过操作后不再重复提示同一版本
                // The same version is not prompted again once the user has acted on it
                _dismissedUpdateLatest = info.Latest;
                AppServices.Backend.OpenUrl(Core.Update.UpdateChecker.ReleasesUrl);
            });
    });


    // ------------------------------------------------------------------ interactions

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceSelection)
        {
            return;
        }
        var prevIndex = _selectedPortIndex;
        _selectedPortIndex = DeviceBox.SelectedIndex >= 0 ? _devices[DeviceBox.SelectedIndex].Index : -1;
        AppServices.Backend.SelectedPort = _selectedPortIndex;

        // 切换设备 = 立即热切换到新端口并抬起所有按键
        // Switching devices hot-swaps to the new port immediately and releases every key
        if (_selectedPortIndex != prevIndex && _selectedPortIndex >= 0)
        {
            ClearActiveNotes();
            AppServices.Backend.Start(_selectedPortIndex, AppServices.Backend.CurrentConfigPath);
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
        DeviceBox.PlaceholderText = _devices.Count == 0 ? AppServices.I18n.T("devices.empty") : string.Empty;
        // 设备条右侧的状态文字：把"有没有设备/是否在听"直接说清楚，省得用户看状态胶囊
        // 只在多台设备时显示数量：0 台由 PlaceholderText 表达，1 台无需赘述
        //
        // The status text on the right of the device bar states whether a device exists and whether the app is listening to it
        // So the user need not read the status pill
        // The count is shown only with more than one device: zero devices is expressed by PlaceholderText, and one needs no remark
        DeviceStateText.Text = AppServices.I18n.T("devices.count", ("count", _devices.Count.ToString()));
        DeviceStateText.Visibility = _devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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
