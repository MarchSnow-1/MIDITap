// LogService.cs — 界面用的活动日志，有容量上限
// 只从 UI 线程追加
// 页面直接绑定这个实时集合，因此不需要手动刷新
//
// 关于 debug 级别：它**照常记录**，只是默认不被界面显示（见 LogFilter）
// 这样做的原因是显示筛选可以随时改，而已经过去的日志无法补记
// 用户在遇到问题时把 debug 打开，需要的恰恰是之前那几十秒的内容
// 因此记录与显示必须分开
// Add 接收全部级别（至多 1000 条，超出后丢弃最旧的）
// 显示默认不含 debug，导出默认含 debug
//
// LogService.cs — the activity log used by the UI, with a capacity cap
// Appended from the UI thread only
// Pages bind to this live collection directly, so no manual refresh is needed
//
// On the debug level: it is **recorded as usual**, and only hidden from the UI by default (see LogFilter)
// The reason is that the display filter can be changed at any time
// Log lines already past cannot be recorded after the fact
// When a user runs into a problem and turns debug on, what they need is exactly those preceding tens of seconds
// Recording and display therefore have to be separate
// Add accepts every level (at most 1000 entries, dropping the oldest past that)
// The display excludes debug by default, and an export includes debug by default


using System.Collections.ObjectModel;
using MIDITap.Core.Logging;

namespace MIDITap.App.Services;

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message)
{
    public string FormattedTime => Time.ToString("HH:mm:ss");
}

public sealed class LogService
{
    private const int MaxEntries = 1000;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public event Action<LogEntry>? Added;

    public void Add(string level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        Entries.Add(entry);
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }
        Added?.Invoke(entry);
    }

    /// <summary>
    /// 排查用的详细信息。默认不在界面上显示（见 LogFilter），但会进日志文件与导出
    ///
    /// Detailed diagnostics
    /// Not shown in the UI by default (see LogFilter)
    /// It is written to the log file and to exports
    /// </summary>
    public void Debug(string message) => Add(LogLevels.Debug, message);

    public void Info(string message) => Add(LogLevels.Info, message);
    public void Warn(string message) => Add(LogLevels.Warn, message);
    public void Error(string message) => Add(LogLevels.Error, message);
}
