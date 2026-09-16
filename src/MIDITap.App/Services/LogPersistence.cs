// LogPersistence.cs — 活动日志落盘开关（在设置页切换）
//
// 默认**关闭**：未经要求就往磁盘写日志是多余副作用，且日志行可能包含设备名与按键内容
// 用户显式开启后，每条活动日志都会以文本行追加到 .storage/logs/miditap.log
// 由 LogFileWriter 负责后台写入与体积轮转（不在此重复实现）
//
// LogPersistence.cs — the switch that writes the activity log to disk (toggled on the settings page)
//
// **Off** by default: writing a log to disk without being asked is a side effect the user did not request
// Log lines can also contain device names and key presses
// Once the user turns it on explicitly, every activity entry is appended as one text line to .storage/logs/miditap.log
// LogFileWriter handles the background writing and the rotation by size (not reimplemented here)


using MIDITap.Core.Logging;
using MIDITap.Core.Settings;

namespace MIDITap.App.Services;

public static class LogPersistence
{
    private static LogFileWriter? _writer;

    /// <summary>当前是否正在把日志写入文件 / Whether the log is currently being written to a file</summary>
    public static bool Enabled => _writer is not null;

    /// <summary>日志文件路径（与其它可变文件一致，位于 exe 旁的 .storage/ 内） / Path of the log file; like the other writable files it lives in .storage/ next to the exe</summary>
    public static string FilePath(string baseDir)
        => AppPaths.LogFilePath(baseDir);

    /// <summary>启动时按已保存的偏好决定是否开启 / Decides at startup whether to enable it, following the saved preference</summary>
    public static void Load(string baseDir)
    {
        if (AppStorage.GetLogToFile(baseDir))
        {
            Enable(baseDir);
        }
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
        var directory = Path.GetDirectoryName(FilePath(baseDir));
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }
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
            _writer = new LogFileWriter(FilePath(baseDir));
        }
        catch
        {
            // 无法创建写入器（路径非法等）：保持关闭，不影响应用运行
            //
            // The writer cannot be created (an invalid path, say): logging stays off, and the app keeps running
            _writer = null;
            return;
        }

        AppServices.Log.Added += OnEntryAdded;
        // 会话头：排查问题时最先要看的几项（版本、时间、界面语言）
        //
        // Session header: the first things worth knowing when triaging
        _writer.Append(
            $"===== MIDITap {AppServices.AppVersion} | " +
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | lang={AppServices.I18n.Current} =====");
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

    // 时间戳带毫秒与时区偏移：跨时区回看日志、或需要对齐两次事件时，这两项都是必需的
    //
    // Timestamps carry milliseconds and the UTC offset
    // Both are needed when reading a log from another timezone or lining two events up
    private static void OnEntryAdded(LogEntry entry)
        => _writer?.Append($"{entry.Time:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.Level}] {entry.Message}");
}
