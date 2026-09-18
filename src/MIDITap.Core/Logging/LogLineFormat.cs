// LogLineFormat.cs — 日志行的文本格式，写入与读取共用这一处
//
// 为什么值得单独一个类型：这个格式原本在两处各写了一遍
// LogPersistence 写落盘文件，LogExporter 写导出文件，两边的字符串必须一模一样
// 一旦其中一处改了（比如时间戳去掉毫秒），导出的文件就与落盘的文件格式不同
// 而按级别筛选导出时，读取方也必须知道 [level] 在什么位置 —— 那是第三处
// 因此格式、以及从一行里取级别，都收在这里
//
// 行形如：2026-09-18 15:30:45.123 +09:00 [info] 按键按下: 60 · 力度 100 → A
//
// LogLineFormat.cs — the text format of a log line, shared by the writer and the reader
//
// Why a type of its own: the format used to be written out twice
// LogPersistence writes the on-disk file and LogExporter writes the exported one, and the two strings must match
// Change one of them (dropping milliseconds, say) and the exported file no longer matches the on-disk one
// Filtering by level while exporting then needs a third copy, because the reader has to know where [level] sits
// The format, and reading the level back out of a line, therefore live here
//
// A line reads: 2026-09-18 15:30:45.123 +09:00 [info] note on: 60 velocity 100 -> A

using System.Globalization;

namespace MIDITap.Core.Logging;

/// <summary>日志行的格式 / The format of a log line</summary>
public static class LogLineFormat
{
    /// <summary>
    /// 时间戳格式：带毫秒与时区偏移
    /// 跨时区回看日志、或把两次事件的先后对齐时，这两项都是必需的
    ///
    /// The timestamp format: milliseconds plus the UTC offset
    /// Both are needed when reading a log from another timezone or lining two events up
    /// </summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    /// <summary>把一条日志拼成一行文本 / Builds one line of text from a log entry</summary>
    public static string Format(DateTimeOffset time, string level, string message)
        => time.ToString(TimeFormat, CultureInfo.InvariantCulture) + " [" + level + "] " + message;

    /// <summary>
    /// 从一行里取出级别，取不到时返回 null
    /// 返回 null 的是**结构性行**（会话头、截断标记等），它们没有级别，因此不该被级别筛选丢掉
    ///
    /// Reads the level out of a line, returning null when there is none
    /// A null means a **structural line** (a session header, the truncation marker and the like)
    /// Those carry no level and must not be dropped by a level filter
    /// </summary>
    public static string? LevelOf(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        // 时间戳里没有方括号，因此第一个 [ 就是级别标记的起点
        //
        // The timestamp contains no square bracket, so the first [ opens the level marker
        var open = line.IndexOf('[');
        if (open < 0)
        {
            return null;
        }

        var close = line.IndexOf(']', open + 1);
        return close > open + 1 ? line[(open + 1)..close] : null;
    }
}
