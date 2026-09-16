// MappingsPage.cs — 配置管理 + 映射列表编辑
// 配置下拉框（点即加载）、刷新、打开配置目录、重命名，以及带逐行删除的映射列表
// 新增流程会弹出 MappingEditorDialog（先捕获音符，再捕获单键或组合键）
//
// MappingsPage.cs — config management + mapping list editor
// It covers the config dropdown (click to load), refresh, open config folder and rename
// It also covers the mapping list with per-row delete
// The add flow opens MappingEditorDialog (note capture + single/combo key capture)

using System.Collections.ObjectModel;
using MIDITap.App.Dialogs;
using MIDITap.App.Helpers;
using MIDITap.App.Services;
using MIDITap.Core.Config;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MIDITap.App.Views;

public sealed record MappingRow(byte Note, string NoteDisplay, bool IsCombo, string KeyLabel)
{
    public string TypeLabel => AppServices.I18n.T(IsCombo ? "edit.combo" : "edit.key");

    public string DeleteTip => AppServices.I18n.T("mappings.delete");

    public string NoteNumber => Note.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed partial class MappingsPage : Page
{
    private readonly ObservableCollection<ConfigFileInfo> _configs = new();
    private readonly ObservableCollection<MappingRow> _mappings = new();
    private string? _currentConfigFilename;
    private string? _currentConfigPath;

    // 当前选中的配置文件读不出来时记下它的名字，界面据此挂常驻提示并禁用写操作
    // null 表示当前配置是好的
    //
    // The name of the selected config file when it cannot be read
    // The UI shows a standing hint and disables the write actions from it
    // Null means the current config is fine
    private string? _corruptFilename;

    public MappingsPage()
    {
        InitializeComponent();
        ConfigBox.ItemsSource = _configs;
        MappingList.ItemsSource = _mappings;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshTexts();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var backend = AppServices.Backend;
        backend.ConfigList += OnConfigList;
        backend.ConfigLoaded += OnConfigLoaded;
        backend.ConfigStateRestored += OnConfigLoaded; // 状态重放走同一处理器 / State replay goes through the same handler
        backend.ConfigRenamed += OnConfigRenamed;
        backend.ConfigInvalid += OnConfigInvalid;
        AppServices.I18n.LanguageChanged += RefreshTexts;

        RefreshTexts();
        AppServices.RunOnUi(() =>
        {
            // 页面按导航重建：重新拉取配置列表并重放最近一次 configLoaded，否则重命名/新增映射会拿不到当前配置
            //
            // The page is rebuilt on navigation
            // Re-fetch the config list and replay the last configLoaded
            // Otherwise renaming or adding a mapping would find no current config
            backend.ListConfigs();
            backend.ReemitConfigLoaded();
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var backend = AppServices.Backend;
        backend.ConfigList -= OnConfigList;
        backend.ConfigLoaded -= OnConfigLoaded;
        backend.ConfigStateRestored -= OnConfigLoaded;
        backend.ConfigRenamed -= OnConfigRenamed;
        backend.ConfigInvalid -= OnConfigInvalid;
        AppServices.I18n.LanguageChanged -= RefreshTexts;
    }

    // ------------------------------------------------------------------ backend events

    private void OnConfigList(IReadOnlyList<ConfigFileInfo> configs) => AppServices.RunOnUi(() =>
    {
        _configs.Clear();
        foreach (var config in configs)
        {
            _configs.Add(config);
        }

        // 保持选中项：当前配置仍在列表中则选中它
        //
        // Keep the selection: the current config is selected again when it is still in the list
        var index = _currentConfigFilename is null
            ? -1
            : _configs.ToList().FindIndex(c => c.Filename == _currentConfigFilename);
        ConfigBox.SelectedIndex = index;
        UpdateListVisibility();
    });

    private void OnConfigLoaded(ConfigLoadedInfo info) => AppServices.RunOnUi(() =>
    {
        _currentConfigFilename = info.Filename;
        _currentConfigPath = info.Path;

        _mappings.Clear();
        foreach (var (note, entry) in info.Mapping)
        {
            _mappings.Add(new MappingRow(
                note,
                NoteNames.Name(note),
                entry.IsCombo,
                entry.KeyLabel));
        }

        // 这一份读得出来，就把上一次的损坏标记清掉
        // 否则用户修好文件、点刷新之后，界面还会一直挂着那条提示
        //
        // This one parsed, so the previous corruption flag is cleared
        // Otherwise the hint would stay up after the user fixed the file and hit Refresh
        _corruptFilename = null;

        var index = _configs.ToList().FindIndex(c => c.Filename == info.Filename);
        ConfigBox.SelectedIndex = index;
        UpdateListVisibility();
    });

    /// <summary>
    /// 改名后把当前配置指到新路径
    /// 后续的增删映射都按 _currentConfigPath 落盘，不同步更新就会写到一个已经不存在的文件名上
    ///
    /// Points the current config at its new path after a rename
    /// Later add/delete writes go to _currentConfigPath, so without this they would target a name that no longer exists
    /// </summary>
    private void OnConfigRenamed(string oldFilename, string newPath) => AppServices.RunOnUi(() =>
    {
        if (!string.Equals(_currentConfigFilename, oldFilename, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _currentConfigFilename = Path.GetFileName(newPath);
        _currentConfigPath = newPath;
        UpdateListVisibility();
    });

    /// <summary>
    /// 选中的配置文件读不出来：挂常驻提示并禁用写入
    /// 复用空态那块面板，只换图标与文案
    ///
    /// The selected config file cannot be read: show a standing hint and disable the write actions
    /// It reuses the empty-state panel and only swaps the icon and the copy
    /// </summary>
    private void OnConfigInvalid(string path, string filename) => AppServices.RunOnUi(() =>
    {
        _currentConfigFilename = filename;
        _currentConfigPath = path;
        _corruptFilename = filename;
        _mappings.Clear();

        // 下拉框仍然停在这个文件上：用户点的就是它，选中项跳走只会更让人困惑
        //
        // The drop-down stays on this file: the user clicked it, and moving the selection away would only confuse
        var index = _configs.ToList().FindIndex(c => c.Filename == filename);
        ConfigBox.SelectedIndex = index;

        AppServices.Log.Warn(AppServices.I18n.T("log.configInvalid", ("name", filename)));
        UpdateListVisibility();
    });

    /// <summary>
    /// 把 Core 的文件名校验结果翻成界面文案，交给对话框在关闭前显示
    /// 判定留在 Core（那里才看得见 validateConfigName 的全部规则），文案留在这里（Core 不依赖 i18n）
    ///
    /// Turns Core's file-name verdict into UI copy for the dialog to show before closing
    /// The check stays in Core, which is where the whole set of rules is visible, and the wording stays here (Core does not depend on i18n)
    /// </summary>
    private static Func<string, string?> NameValidator(string? excludeFilename = null) => typedName =>
        AppServices.Backend.ValidateConfigName(typedName, excludeFilename) switch
        {
            ConfigNameError.None => null,
            ConfigNameError.Empty => AppServices.I18n.T("config.nameError.empty"),
            ConfigNameError.InvalidCharacters => AppServices.I18n.T("config.nameError.invalid"),
            ConfigNameError.ReservedName => AppServices.I18n.T("config.nameError.reserved"),
            ConfigNameError.TrailingDotOrSpace => AppServices.I18n.T("config.nameError.trailing"),
            ConfigNameError.TooLong => AppServices.I18n.T("config.nameError.tooLong"),
            ConfigNameError.AlreadyExists => AppServices.I18n.T("config.nameError.exists"),
            // 枚举以后若加了新成员，这里兜底成通用的一句，而不是放行
            //
            // If the enum gains a member later, this falls back to a generic line rather than letting it through
            _ => AppServices.I18n.T("config.nameError.invalid"),
        };

    // ------------------------------------------------------------------ interactions

    private void OnConfigSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ConfigBox.SelectedItem is not ConfigFileInfo config)
        {
            return;
        }
        // 下拉即加载 / Selecting loads it
        if (config.Filename != _currentConfigFilename)
        {
            AppServices.Backend.LoadConfig(config.Path);
        }
    }

    private void OnRefreshConfigs(object sender, RoutedEventArgs e) => AppServices.Backend.ListConfigs();

    private void OnBrowse(object sender, RoutedEventArgs e) => AppServices.Backend.OpenConfigDir();

    /// <summary>点击笔形按钮：弹出对话框修改当前选中配置的名称 / Pen button click: opens a dialog that renames the currently selected config</summary>
    private async void OnRenameConfig(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentConfigFilename))
        {
            AppServices.Log.Warn(AppServices.I18n.T("log.warnEmptyName"));
            return;
        }

        var dialog = new TextInputDialog(
            AppServices.I18n.T("config.rename.title"),
            AppServices.I18n.T("config.name"),
            // 预填当前文件名（不含 .json），用户不必自己回忆原名
            //
            // Pre-filled with the current file name without .json, so the user need not recall it
            Path.GetFileNameWithoutExtension(_currentConfigFilename),
            AppServices.I18n.T("config.rename"),
            // 排除自身：改成自己原来的名字不该被当成冲突
            //
            // Excludes the file itself: renaming it to its own name is not a clash
            NameValidator(_currentConfigFilename))
        {
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            AppServices.Backend.RenameConfig(_currentConfigFilename, dialog.Value);
        }
    }

    /// <summary>点击加号：新建配置文件并立即切换过去 / Plus click: creates a config file and switches to it straight away</summary>
    private async void OnNewConfig(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog(
            AppServices.I18n.T("config.new.title"),
            AppServices.I18n.T("config.new.placeholder"),
            string.Empty,
            AppServices.I18n.T("config.new"),
            NameValidator())
        {
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            AppServices.Backend.CreateConfig(dialog.Value);
        }
    }

    private async void OnAddMapping(object sender, RoutedEventArgs e)
    {
        var dialog = new MappingEditorDialog(_currentConfigFilename, _currentConfigPath)
        {
            XamlRoot = XamlRoot,
        };
        _ = await dialog.ShowAsync();
    }

    private void OnDeleteMappingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MappingRow row })
        {
            AppServices.Backend.DeleteMapping(row.Note.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // ------------------------------------------------------------------ render helpers

    private void UpdateListVisibility()
    {
        // 空态是"引导下一步"而不是"一行提示"：图标 + 说明居中显示
        // 同时把列表与表头一起隐藏，避免出现"有表头无数据"的空表格
        //
        // 配置读不出来时复用同一块面板，只换图标与文案
        // 位置与无映射时一致，用户不必再学第二套界面
        //
        // The empty state guides the next step rather than being a one-line hint
        // The icon and the copy are centred
        // The list and the header row are hidden together, so no table shows a header with no data
        //
        // An unreadable config reuses the same panel and only swaps the icon and the copy
        // It appears in the same place as the no-mappings case, so there is no second interface to learn
        var corrupt = _corruptFilename is not null;
        var showPanel = corrupt || _mappings.Count == 0;

        EmptyPanel.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;
        MappingList.Visibility = showPanel ? Visibility.Collapsed : Visibility.Visible;
        HeaderRow.Visibility = showPanel ? Visibility.Collapsed : Visibility.Visible;

        // 警告三角对应"出了状况"，与无映射时的音符图标区分开
        //
        // A warning triangle says something went wrong, telling it apart from the note icon of the no-mappings case
        EmptyIcon.Glyph = corrupt ? "\uE7BA" : "\uE8A5";
        EmptyHint.Text = AppServices.I18n.T(corrupt ? "mappings.corrupt" : "mappings.empty");

        // 读不出来的配置不能写入：新增映射要去改一个连解析都过不了的文件
        //
        // An unreadable config must not be written to: adding a mapping would edit a file that cannot even be parsed
        AddMappingBtn.IsEnabled = !corrupt;
    }

    public void RefreshTexts()
    {
        Func<string, string> t = AppServices.I18n.T;
        ConfigBox.PlaceholderText = t("config.label");
        // 图标按钮用 tooltip 说明用途：视觉上无文字，但功能必须可发现
        //
        // Icon buttons explain their purpose through a tooltip
        // With no visible text, the function has to stay discoverable
        ToolTipService.SetToolTip(ConfigRefreshBtn, t("config.refresh"));
        ToolTipService.SetToolTip(BrowseBtn, t("config.browse"));
        ToolTipService.SetToolTip(RenameConfigBtn, t("config.rename"));
        ToolTipService.SetToolTip(NewConfigBtn, t("config.new"));
        NameLabel.Text = t("mappings.current");
        AddMappingBtn.Content = t("mappings.add");
        ColNoteHeader.Text = t("mappings.col.note");
        ColNoteNumberHeader.Text = t("mappings.col.noteNumber");
        ColTypeHeader.Text = t("mappings.col.type");
        ColKeyHeader.Text = t("mappings.col.key");

        var rows = _mappings.ToList();
        _mappings.Clear();
        foreach (var row in rows)
        {
            _mappings.Add(row with { });
        }

        // 空态那一句（无映射 / 读不出来）也随语言走，统一在这里重绘
        //
        // The empty-state line (no mappings or unreadable) follows the language too, so it is repainted here
        UpdateListVisibility();
    }
}
