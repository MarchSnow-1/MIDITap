// LogPersistence.cs — 活动日志落盘开关（在设置页切换）
//
// 默认**关闭**：未经要求就往磁盘写日志是多余副作用，且日志行可能包含设备名与按键内容
// 用户显式开启后，每次启动新建一份 .storage/logs/miditap-<日期>-<编号>.log，活动日志以文本行追加其中
// 由 LogFileWriter 负责后台写入与单会话体积上限（不在此重复实现）
// 跨天之后，那一天的各次会话会被合并成 .storage/logs/miditap-<日期>.tar.gz，见 LogHousekeeping
//
// LogPersistence.cs — the switch that writes the activity log to disk (toggled on the settings page)
//
// **Off** by default: writing a log to disk without being asked is a side effect the user did not request
// Log lines can also contain device names and key presses
// Once the user turns it on explicitly, every launch creates its own .storage/logs/miditap-<date>-<number>.log
// and activity entries are appended to it as text lines
// LogFileWriter handles the background writing and the per-session size cap (not reimplemented here)
// Once a day has passed, that day's sessions are merged into .storage/logs/miditap-<date>.tar.gz; see LogHousekeeping


using MIDITap.Core.Logging;
using MIDITap.Core.Settings;

namespace MIDITap.App.Services;

public static class LogPersistence
{
    private static LogFileWriter? _writer;

    // 本次会话的编号（当天第几次启动）
    // 它写在会话头里：文件名里的编号是给人与清理用的，头里的这一份让日志正文自己也能说清是哪一次
    //
    // This session number, meaning which launch of the day it was
    // It goes into the session header: the number in the file name serves people and cleanup,
    // while the one in the header lets the log body state which launch it belongs to
    private static int _session;

    /// <summary>当前是否正在把日志写入文件 / Whether the log is currently being written to a file</summary>
    public static bool Enabled => _writer is not null;

    /// <summary>日志目录（与其它可变文件一致，位于 exe 旁的 .storage/ 内） / The log directory; like the other writable files it lives in .storage/ next to the exe</summary>
    public static string LogDirectory(string baseDir)
        => AppPaths.LogDir(baseDir);

    /// <summary>启动时按已保存的偏好决定是否开启，并整理上一次留下的日志 / Decides at startup whether to enable it, following the saved preference, and tidies up the previous logs</summary>
    public static void Load(string baseDir)
    {
        if (AppStorage.GetLogToFile(baseDir))
        {
            Enable(baseDir);
        }

        // 整理上一次留下的日志
        // 放到后台：压缩几十上百 MB 会拖住启动，而窗口应当立刻出现
        // 落盘关闭时也要做：那些文件是本应用自己留下的，跨天之后同样应当归档，否则会一直堆着
        // 这一步只维护已有文件，不产生任何新的日志内容
        //
        // Tidies up the logs left by earlier sessions
        // On a background thread: compressing tens or hundreds of MB would hold up the start, and the window should
        // appear at once
        // It also runs when logging is off: those files were left by this app, and a day that has passed should be
        // archived all the same, or they pile up
        // This maintains existing files and produces no new log content
        _ = Task.Run(() =>
        {
            try
            {
                var directory = AppPaths.LogDir(baseDir);

                // 先清掉旧格式（miditap.log 与 miditap.log.1）：新命名不认识它们，后面的步骤都会跳过
                // 再把早于今天的每一天各自合并成整天归档；今天的不动，它的会话必须各自独立
                //
                // The legacy shapes are removed first: the new naming does not recognise them, so every later step
                // would skip them
                // Then each day earlier than today is merged into its own archive
                // Today is left alone, because its sessions have to stay separate
                var today = DateOnly.FromDateTime(DateTime.Now);
                var legacy = LogHousekeeping.RemoveLegacyFiles(directory);
                var archived = LogHousekeeping.ArchiveEarlierDays(directory, today);

                // 最后清掉超出保留期的归档：保留 7 天（含今天），没有体积上限
                //
                // Finally the archives past the retention window are removed: seven days including today,
                // with no size cap
                var removed = LogHousekeeping.RemoveExpiredArchives(
                    directory, today, LogHousekeeping.RetentionDays);

                // 结果记一条 debug："日志到底有没有在整理"正是排查时想知道的
                // LogService 只接受 UI 线程的追加，因此回到 UI 线程
                //
                // The result goes to a debug line: whether housekeeping is running at all is worth knowing when
                // diagnosing
                // LogService only accepts appends from the UI thread, so this returns to it
                AppServices.RunOnUi(() => AppServices.Log.Debug(AppServices.I18n.T(
                    "log.debug.housekeeping",
                    ("archived", archived.ToString()),
                    ("removed", removed.ToString()),
                    ("legacy", legacy.ToString()))));
            }
            catch
            {
                // 整理失败不影响应用运行，下一轮启动会再试
                //
                // A failed tidy-up does not affect the app, and the next launch tries again
            }
        });
    }

    /// <summary>
    /// 切换开关并持久化。返回偏好是否成功写入
    /// 即使写入失败（如只读介质），本次会话的开关状态仍会按用户意图改变
    /// 因此返回值只用于提示，不阻断操作
    ///
    /// Toggles the switch and persists it, returning whether the preference was written
    /// Even when the write fails (a read-only medium, say), this session's switch state still changes as the user intended
    /// So the return value only feeds a message and does not block the action
    /// </summary>
    public static bool SetEnabled(string baseDir, bool enabled)
    {
        var saved = AppStorage.SaveLogToFile(baseDir, enabled);
        if (enabled)
        {
            Enable(baseDir);
        }
        else
        {
            Disable();
        }
        return saved;
    }

    /// <summary>退出前调用：确保在途的日志行落盘 / Called before exit: makes sure in-flight log lines reach the disk</summary>
    public static void Shutdown() => Disable(writeSessionEnd: false);

    /// <summary>
    /// 在资源管理器中打开日志目录
    /// 目录不存在时先创建,否则 explorer 会拿到一个不存在的路径
    ///
    /// Opens the log folder in Explorer
    /// The folder is created first when it does not exist, otherwise explorer would be handed a path that is not there
    /// </summary>
    public static void OpenLogFolder(string baseDir)
    {
        var directory = LogDirectory(baseDir);
        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{directory}\"")
                {
                    UseShellExecute = true,
                });
        }
        catch
        {
            // 打不开目录不影响其它功能
            //
            // Failing to open the folder does not affect the rest of the app
        }
    }

    private static void Enable(string baseDir)
    {
        if (_writer is not null)
        {
            return;
        }

        try
        {
            // 每次启动都新建一个会话文件，编号是「当天第几次启动」
            // 用 CreateNew 创建：两个实例同时启动时，后者会换下一个编号，不会写进同一个文件
            //
            // Every launch creates its own session file, numbered by which launch of the day it was
            // Created with CreateNew, so two instances starting together take different numbers
            // rather than writing into one file
            var (path, session) = LogFileNames.ReserveSessionFile(
                LogDirectory(baseDir), DateOnly.FromDateTime(DateTime.Now));
            _session = session;
            _writer = new LogFileWriter(path);
        }
        catch
        {
            // 无法创建写入器（路径非法、介质只读等）：保持关闭，不影响应用运行
            //
            // The writer cannot be created (an invalid path, a read-only medium, and so on)
            // Logging stays off and the app keeps running
            _writer = null;
            return;
        }

        AppServices.Log.Added += OnEntryAdded;
        // 会话头：排查问题时最先要看的几项（版本、时间、界面语言、当天第几次启动）
        //
        // Session header: the first things worth knowing when triaging
        // The version, the time, the UI language and which launch of the day this was
        _writer.Append(
            $"===== MIDITap {AppServices.AppVersion} | " +
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | lang={AppServices.I18n.Current} | " +
            $"session {_session} =====");
        _writer.Append("logging enabled");
    }

    private static void Disable(bool writeSessionEnd = true)
    {
        if (_writer is null)
        {
            return;
        }

        AppServices.Log.Added -= OnEntryAdded;
        if (writeSessionEnd)
        {
            _writer.Append("logging disabled");
        }
        _writer.Dispose();
        _writer = null;
    }

    // 行的格式由 LogLineFormat 拥有：导出与按级别筛选都要读同一个格式
    // 这里若自己拼一遍，改格式时就会漏掉读取方
    //
    // The line format is owned by LogLineFormat: exporting and filtering by level both have to read it
    // Building it here would mean a format change misses the reader
    private static void OnEntryAdded(LogEntry entry)
        => _writer?.Append(LogLineFormat.Format(entry.Time, entry.Level, entry.Message));
}
