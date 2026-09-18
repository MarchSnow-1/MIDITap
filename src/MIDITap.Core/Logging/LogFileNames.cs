// LogFileNames.cs — 日志文件的命名、解析与编号分配
//
// 两种名字，各自一眼可辨
//   miditap-2026-09-18-3.log    单次会话，明文
//   miditap-2026-09-17.tar.gz   整天归档，内含那天所有会话的明文
//
// 会话文件一律保持明文，不单独压缩
// 只有整天会被压缩：一天一个包，而这一天过去之前一个字节都不动它
// 因此解开归档拿到的就是可直接阅读的文本，用户不必再解一层
//
// 编号是"当天第几次启动"，只增不减，因此数字越大越晚
// 清理按**整天**进行，所以留下的每一天里编号是连续的，不会出现空洞
//
// 为什么解析也放在这里：归档、清理、导出三处都要从文件名反推日期与编号
// 各写一遍匹配规则的话，改命名时必然漏掉一处，而漏掉的那处会把文件当成"不认识"而放着不管
//
// LogFileNames.cs — naming, parsing and session-number allocation for log files
//
// Two shapes, each recognisable at a glance
//   miditap-2026-09-18-3.log    one session, plain text
//   miditap-2026-09-17.tar.gz   a whole-day archive holding that day's sessions as plain text
//
// A session file always stays plain text and is never compressed on its own
// Only whole days are compressed: one package per day, and nothing touches that day until it has passed
// Extracting an archive therefore yields text that can be read directly, with no second step
//
// The number is "which launch of that day this was", and it only grows, so a larger number is later
// Cleanup removes whole days, so the numbers left within any surviving day are contiguous
//
// Why parsing lives here too: archiving, cleanup and exporting all have to recover the date and number from a name
// Writing the matching rules out three times guarantees that a naming change misses one of them
// The missed one then treats the files as unrecognised and silently leaves them alone

using System.Globalization;

namespace MIDITap.Core.Logging;

/// <summary>一个日志文件的形态 / The shape of one log file</summary>
public enum LogFileShape
{
    /// <summary>不是本应用的日志文件，调用方不得改动 / Not one of this app's log files; callers must leave it alone</summary>
    Foreign,
    /// <summary>旧格式 miditap.log 与 miditap.log.1，由迁移清理 / The legacy miditap.log and .1, removed by the migration</summary>
    Legacy,
    /// <summary>单次会话，明文 / A single session, plain text</summary>
    Session,
    /// <summary>整天归档 / A whole-day archive</summary>
    DayArchive,
}

/// <summary>从文件名解析出的内容 / What a file name parses into</summary>
public sealed record LogFileNameInfo(
    LogFileShape Shape,
    DateOnly? Date = null,
    int? Session = null);

/// <summary>本应用日志文件的名字规则 / The naming rules for this app's log files</summary>
public static class LogFileNames
{
    /// <summary>所有日志文件名共有的前缀 / The prefix every log file name shares</summary>
    public const string Prefix = "miditap";

    /// <summary>会话文件的扩展名 / Extension of a session file</summary>
    public const string SessionExtension = ".log";

    /// <summary>整天归档的扩展名 / Extension of a whole-day archive</summary>
    public const string DayArchiveExtension = ".tar.gz";

    // 编号被占用时最多往后试这么多个，避免异常情况下无限循环
    //
    // How many numbers to try past a taken one, so an abnormal situation cannot loop forever
    private const int ReserveAttempts = 1000;

    /// <summary>会话文件名：miditap-2026-09-18-3.log</summary>
    public static string SessionName(DateOnly date, int session)
        => $"{Prefix}-{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{session.ToString(CultureInfo.InvariantCulture)}{SessionExtension}";

    /// <summary>整天归档文件名：miditap-2026-09-17.tar.gz</summary>
    public static string DayArchiveName(DateOnly date)
        => $"{Prefix}-{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{DayArchiveExtension}";

    /// <summary>
    /// 解析一个文件名
    /// 认不出来的一律算 Foreign，调用方因此不会误删别人的文件
    ///
    /// Parses one file name
    /// Anything unrecognised counts as Foreign, so a caller cannot delete somebody else's file by mistake
    /// </summary>
    public static LogFileNameInfo Parse(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return new LogFileNameInfo(LogFileShape.Foreign);
        }

        // 旧格式先认：miditap.log 与轮转产生的 miditap.log.1
        //
        // The legacy shapes come first: miditap.log and the miditap.log.1 left by rotation
        if (fileName.Equals(Prefix + SessionExtension, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(Prefix + SessionExtension + ".1", StringComparison.OrdinalIgnoreCase))
        {
            return new LogFileNameInfo(LogFileShape.Legacy);
        }

        if (!fileName.StartsWith(Prefix + "-", StringComparison.OrdinalIgnoreCase))
        {
            return new LogFileNameInfo(LogFileShape.Foreign);
        }

        var rest = fileName[(Prefix.Length + 1)..];

        // 整天归档：2026-09-17.tar.gz
        // 它以 .tar.gz 结尾，与会话的 .log 结尾互斥，因此两者的判断顺序不影响结果
        //
        // A whole-day archive: 2026-09-17.tar.gz
        // It ends in .tar.gz, which is mutually exclusive with a session's .log,
        // so the order of the two checks does not affect the outcome
        if (rest.EndsWith(DayArchiveExtension, StringComparison.OrdinalIgnoreCase))
        {
            var dateText = rest[..^DayArchiveExtension.Length];
            return TryParseDate(dateText, out var archiveDate)
                ? new LogFileNameInfo(LogFileShape.DayArchive, archiveDate)
                : new LogFileNameInfo(LogFileShape.Foreign);
        }

        // 会话：2026-09-18-3.log
        // 只认 .log 结尾：带别后缀的一律不算本应用的文件
        //
        // A session: 2026-09-18-3.log
        // Only the .log ending is accepted; anything else is not one of this app's files
        if (!rest.EndsWith(SessionExtension, StringComparison.OrdinalIgnoreCase))
        {
            return new LogFileNameInfo(LogFileShape.Foreign);
        }

        var body = rest[..^SessionExtension.Length];

        // 最后一个连字符把日期与编号分开：日期本身含连字符，所以取最后一个
        //
        // The last hyphen separates the date from the number
        // The date contains hyphens of its own, so the last one is the separator
        var separator = body.LastIndexOf('-');
        if (separator <= 0 || separator == body.Length - 1)
        {
            return new LogFileNameInfo(LogFileShape.Foreign);
        }

        if (!TryParseDate(body[..separator], out var sessionDate))
        {
            return new LogFileNameInfo(LogFileShape.Foreign);
        }

        if (!int.TryParse(body[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var session)
            || session <= 0)
        {
            return new LogFileNameInfo(LogFileShape.Foreign);
        }

        return new LogFileNameInfo(LogFileShape.Session, sessionDate, session);
    }

    /// <summary>
    /// 当天已经用过的最大编号加一
    ///
    /// The highest number already used that day, plus one
    /// </summary>
    public static int NextSessionNumber(string directory, DateOnly date)
    {
        var highest = 0;
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var info = Parse(Path.GetFileName(path));
                if (info.Shape == LogFileShape.Session
                    && info.Date == date
                    && info.Session is { } number
                    && number > highest)
                {
                    highest = number;
                }
            }
        }
        return highest + 1;
    }

    /// <summary>
    /// 为本次启动创建日志文件并返回它的路径与编号
    /// 用 CreateNew 原子创建：两个实例同时启动时，后者创建失败并换下一个编号，不会互相覆盖
    ///
    /// Creates this launch's log file and returns its path and number
    /// Created atomically with CreateNew: when two instances start together the second one fails and takes the next
    /// number, so neither overwrites the other
    /// </summary>
    public static (string Path, int Session) ReserveSessionFile(string directory, DateOnly date)
    {
        Directory.CreateDirectory(directory);

        var session = NextSessionNumber(directory, date);
        for (var attempt = 0; attempt < ReserveAttempts; attempt++, session++)
        {
            var path = Path.Combine(directory, SessionName(date, session));
            try
            {
                using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                return (path, session);
            }
            catch (IOException)
            {
                // 该编号已被占用，试下一个
                //
                // That number is taken, so the next one is tried
            }
        }

        throw new IOException(
            "Cannot reserve a log file name in " + directory + " after " + ReserveAttempts + " attempts");
    }

    private static bool TryParseDate(string text, out DateOnly date)
        => DateOnly.TryParseExact(
            text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
