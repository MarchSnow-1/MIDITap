// ExportLogsDialog.cs — 导出日志的对话框：选级别、选目标路径，然后交给 Core 打包
//
// 三个刻意的决定
//
// 1) **级别默认全选，含 debug**
//    与日志页默认隐藏 debug 相反，两个默认值服务于不同目的
//    看日志是日常使用（debug 是噪音）
//    导出日志是排查（debug 往往正是缺的那块）
//
// 2) **导出失败时对话框不关闭**
//    失败原因（磁盘满、目标被占用、路径非法）显示在对话框里
//    用户可以就地改选级别或换个位置重试，而不是关掉对话框、再去日志页看是哪一步错了
//    为此 PrimaryButtonClick 里先 args.Cancel = true，成功后才主动 Hide()
//
// 3) **文件选择器拿不到时退回 .storage/exports/ 并如实告知路径**
//    未打包（unpackaged）的 WinUI 应用里选择器依赖窗口句柄初始化
//    注册表/策略异常时可能直接抛错
//    此时"导不出来"是不可接受的（那正是用户需要日志的时刻）
//    退回一个确定可写的位置比失败更有用
//    前提是**必须告诉用户文件在哪**，否则等于把文件藏起来
//
// ExportLogsDialog.cs — the export dialog: pick levels and a target path, then let Core pack it
//
// Three deliberate decisions
//
// 1) **All levels are checked by default, debug included**
//    That is the opposite of the log page's default of hiding debug
//    The two defaults serve different purposes
//    Reading the log is everyday use (debug is noise)
//    Exporting it is triage (debug is usually the missing piece)
//
// 2) **A failed export keeps the dialog open**
//    The reason (disk full, target locked, invalid path) is shown inside it
//    The user can change levels or pick another location on the spot
//    That beats closing the dialog and hunting for the failure on the log page
//    PrimaryButtonClick therefore sets args.Cancel = true first
//    Hide() is called only once the export succeeded
//
// 3) **When the file picker is unavailable, fall back to .storage/exports/ and report the path**
//    In an unpackaged WinUI app the picker needs the window handle to initialize
//    It can throw outright under registry or policy problems
//    "Cannot export" is not acceptable at that point (it is precisely when the log is needed)
//    Falling back to a location known to be writable beats failing
//    The user must be **told where the file went**; otherwise it is effectively hidden

using System.IO;
using MIDITap.App.Services;
using MIDITap.Core.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MIDITap.App.Dialogs;

public sealed partial class ExportLogsDialog : ContentDialog
{
    /// <summary>导出目标：路径，以及它是否来自兜底位置（用于如实告知用户）</summary>
    /// <remarks>
    /// An export target: the path, and whether it came from the fallback location
    /// That flag exists so the user can be told honestly
    /// </remarks>
    private sealed record Target(string Path, bool IsFallback);

    private readonly int _totalEntries;

    // 级别勾选框：级别名 -> 控件。由 BuildLevelBoxes 按 Core 的 LogLevels.All 生成
    // The level check boxes: level name -> control. Built by BuildLevelBoxes from Core's LogLevels.All
    private readonly Dictionary<string, CheckBox> _levelBoxes = [];

    public ExportLogsDialog(int totalEntries)
    {
        _totalEntries = totalEntries;
        InitializeComponent();
        BuildLevelBoxes();
        RefreshTexts();
    }

    /// <summary>
    /// 按 Core 的 LogLevels.All 生成级别勾选框，**默认全部勾选（含 debug）**
    /// 级别清单只在 Core 一处
    /// 加一个级别时这里自动多一个勾选框，不必再记得改本文件的声明、文案与取值三处
    ///
    /// Builds the level check boxes from Core's LogLevels.All, **all checked by default (debug included)**
    /// The level list lives only in Core
    /// One more level produces one more box here
    /// There is no need to remember to update the declaration, the label and the value separately in this file
    /// </summary>
    private void BuildLevelBoxes()
    {
        foreach (var level in LogLevels.All)
        {
            var box = new CheckBox { IsChecked = true };
            _levelBoxes[level] = box;
            LevelBoxes.Children.Add(box);
        }
    }

    private async void OnPrimaryClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 延迟关闭：打包是异步的（要等用户选路径），而 ContentDialog 默认在处理器返回时立刻关闭
        //
        // Defer the close: packing is asynchronous (it waits for the user to choose a path)
        // ContentDialog would otherwise close the moment the handler returns
        var deferral = args.GetDeferral();
        try
        {
            // 先假定不关闭，只有成功那条路径才 Hide()
            // Assume "stay open" and close only on the success path
            args.Cancel = true;
            ErrorText.Visibility = Visibility.Collapsed;

            var now = DateTimeOffset.Now;
            var entries = CollectEntries();
            var target = await PickTargetAsync(now);
            if (target is null)
            {
                // 用户在保存对话框里取消：保持本对话框打开，不做任何事
                // The user cancelled the save dialog: keep this dialog open and do nothing
                return;
            }

            try
            {
                var result = LogExporter.Export(target.Path, entries, LogEnvironment.Compose(), now);
                var message = AppServices.I18n.T(
                    target.IsFallback ? "log.export.fallback" : "log.export.saved",
                    ("path", result.ZipPath));
                // 同时进日志与浮窗：浮窗让用户当场知道文件在哪，日志留下"什么时候导出过"的记录
                // Both a log line and a toast
                // The toast tells the user where the file is right now
                // The log records that an export happened and when
                AppServices.Log.Info(message);
                ToastService.Show(message);
                Hide();
            }
            catch (Exception ex)
            {
                // 失败留在对话框里（见文件头第 2 点）
                // A failure stays in the dialog (see decision 2 in the file header)
                ErrorText.Text = AppServices.I18n.T("log.export.failed", ("message", ex.Message));
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// 按勾选收集条目。顺序保持与日志页一致（由旧到新），因为时间是排查时唯一的排序依据
    ///
    /// Collects the entries per the checkboxes, preserving the log page's order (oldest first)
    /// Time is the only ordering a reader can rely on
    /// </summary>
    private List<LogExportEntry> CollectEntries()
    {
        // 勾选状态从生成的字典里读，而不是逐个具名控件：级别清单只有 Core 那一处
        //
        // The checked state is read from the generated dictionary
        // It is not read from named controls one by one
        // The level list has its single source in Core
        var levels = _levelBoxes
            .Where(kv => kv.Value.IsChecked == true)
            .Select(kv => kv.Key)
            .ToList();

        // 用 LogLevels.Normalize 比较而不是直接比字符串
        // 未知级别归入 info
        // 这样"勾了 info"时新增级别的条目也一并导出，不会被静默漏掉
        //
        // Comparison goes through LogLevels.Normalize rather than raw string equality
        // An unknown level folds into info
        // So checking info also exports entries of a level added later instead of silently dropping them
        var selected = levels.ToHashSet();
        return AppServices.Log.Entries
            .Where(entry => selected.Contains(LogLevels.Normalize(entry.Level)))
            .Select(entry => new LogExportEntry(entry.Time, entry.Level, entry.Message))
            .ToList();
    }

    private async Task<Target?> PickTargetAsync(DateTimeOffset now)
    {
        var suggested = Path.GetFileNameWithoutExtension(LogExporter.DefaultFileName(now));
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggested,
            };
            picker.FileTypeChoices.Add(
                AppServices.I18n.T("log.export.fileType"),
                new List<string> { LogExporter.Extension });
            // 未打包应用必须显式把选择器绑到窗口，否则它在没有任何窗口归属的状态下弹不出来
            //
            // An unpackaged app must bind the picker to a window explicitly
            // Otherwise it has no window to belong to and cannot appear
            WinRT.Interop.InitializeWithWindow.Initialize(picker, AppServices.MainWindowHandle);

            var file = await picker.PickSaveFileAsync();
            return file is null ? null : new Target(file.Path, IsFallback: false);
        }
        catch
        {
            // 见文件头第 3 点：选择器不可用时退回 .storage/exports/
            // 那是应用自己创建并拥有的目录，并如实告诉用户路径
            //
            // See decision 3: when the picker is unavailable, fall back to .storage/exports/
            // That directory is created and owned by the app
            // The path is reported to the user
            var directory = Core.Settings.AppPaths.ExportsDir(AppServices.BaseDir);
            Directory.CreateDirectory(directory);
            return new Target(Path.Combine(directory, LogExporter.DefaultFileName(now)), IsFallback: true);
        }
    }

    private void RefreshTexts()
    {
        // 单参数的键走 Func 简写，带占位符的键直接调服务（Func<string,string> 表达不了可变参数）
        //
        // Single-argument keys go through the Func shorthand while keys with placeholders call the service directly
        // A Func<string,string> cannot express the variadic overload
        Func<string, string> t = AppServices.I18n.T;
        Title = t("log.export.title");
        PrimaryButtonText = t("log.export.confirm");
        CloseButtonText = t("log.export.cancel");
        SummaryText.Text = AppServices.I18n.T(
            "log.export.summary", ("count", _totalEntries.ToString()));
        IncludeLabel.Text = t("log.export.include");
        DebugNote.Text = t("log.export.debugNote");
        // 文案键按约定从级别名拼出（log.level.<级别>），与日志页同一条约定
        //
        // The label key is derived from the level name by convention (log.level.<level>)
        // That is the same rule the log page follows
        foreach (var (level, box) in _levelBoxes)
        {
            box.Content = t("log.level." + level);
        }
    }
}
