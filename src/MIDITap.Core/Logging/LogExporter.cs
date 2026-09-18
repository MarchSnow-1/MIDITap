// LogExporter.cs — 把一段日志与一份环境摘要打成 zip，供用户附在问题反馈里
//
// 为什么打成 zip：反馈日志时真正有用的从来不止日志本身 —— 还要知道是哪个版本、哪种界面语言、什么系统、日志落盘开没开
// 单发一个文本文件，这些信息就只能靠用户在描述里手抄，而抄漏一项往往正好是排查所需要的那项
// zip 里固定放两个文件（日志 + 环境），一次拖拽就齐全
//
// 为什么先写临时文件再改名：导出可能因为磁盘满、目标被占用、路径非法而中途失败
// 直接写目标路径时，失败会留下一个**看起来正常但内容不全**的 zip
// 用户会把它当成完整日志发出去，而缺失的部分恰恰在末尾
// 先写 .tmp 再原子改名，失败时目标路径**不存在**
// 因此不会产生误导性的半成品
// 这与自更新"校验不通过就删包"是同一类考虑
//
// 为什么时间戳用 InvariantCulture：时间格式 yyyy-MM-dd HH:mm:ss.fff zzz
// 其中的数字与符号在部分区域设置下会变成非拉丁字形（如泰语数字）
// 那样的时间戳排在日志行首，人眼与工具都难以解析
// 导出是要交给别人看的文件，因此格式固定，不跟随本机区域设置
//
// LogExporter.cs — packs a slice of the log plus an environment summary into a zip
// The user can attach that zip to a bug report
//
// Why a zip: the log alone is never enough when reporting a problem
// The version, UI language, system and whether log-to-file was on all matter too
// Sending a bare text file means the user has to copy those details into their description by hand
// The one they omit tends to be the one needed
// The zip carries two fixed files (log + environment), complete in a single drag
//
// Why a temp file renamed afterwards: an export can fail midway
// Such failures include a full disk, a locked target and an invalid path
// Writing the target directly would leave a zip that **looks fine but is truncated**
// The user would send it as a complete log, while the missing part is exactly the tail
// Writing .tmp first and renaming atomically means a failed export leaves no file at the target path
// That avoids a misleading half-product
// The same reasoning underlies the updater's "delete the package when verification fails"
//
// Why the timestamps use InvariantCulture: in some locales the format "yyyy-MM-dd HH:mm:ss.fff zzz"
// renders with non-Latin glyphs, Thai digits for instance
// Such a timestamp sits at the head of every log line, where both people and tools must parse it
// The export is a file meant to be read elsewhere, so its format is fixed
// It therefore does not follow the local locale

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace MIDITap.Core.Logging;

/// <summary>导出用的一条日志（与界面上的日志条目同构，但不依赖 WinUI，因此放在 Core）</summary>
/// <remarks>
/// One log line for export
/// Structurally identical to the UI's log entry but independent of WinUI, so it lives in Core
/// </remarks>
public sealed record LogExportEntry(DateTimeOffset Time, string Level, string Message);

/// <summary>
/// 一次导出的结果：zip 路径与写入的日志条数
///
/// The result of one export: the zip path and how many log lines it contains
/// </summary>
public sealed record LogExportResult(string ZipPath, int EntryCount);

public static class LogExporter
{
    /// <summary>zip 内日志文件的固定名（固定的好处：指导用户附日志的说明永远不用改）</summary>
    /// <remarks>
    /// The fixed name of the log file inside the zip
    /// It is fixed so the instructions telling users how to attach a log never have to change
    /// </remarks>
    public const string LogEntryName = "miditap-log.txt";

    /// <summary>zip 内环境摘要的固定名（见文件头：这些信息与日志同样必要）</summary>
    /// <remarks>
    /// The fixed name of the environment summary inside the zip
    /// See the file header: these details matter as much as the log itself
    /// </remarks>
    public const string EnvironmentEntryName = "environment.txt";

    /// <summary>扩展名（界面上的文件选择器也用它，避免两处各写一遍）</summary>
    /// <remarks>Extension, shared with the file picker so it is not spelled out in two places</remarks>
    public const string Extension = ".zip";

    // 不带 BOM：与落盘的日志文件一致
    // 开头多个不可见字节只会给工具解析添乱
    //
    // No BOM, matching the on-disk log
    // A leading invisible byte only gets in the way of tooling
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 建议的文件名：miditap-logs-20260914-152030.zip
    /// 带秒级时间戳，连续导出两次不会互相覆盖
    /// 覆盖掉用户刚导出的那份是无法接受的
    ///
    /// The suggested file name
    /// The second-resolution timestamp means two consecutive exports do not overwrite each other
    /// Clobbering the copy the user just made is not acceptable
    /// </summary>
    public static string DefaultFileName(DateTimeOffset now)
        => $"miditap-logs-{now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}{Extension}";

    /// <summary>
    /// 写出 zip
    /// 条目由调用方**先筛选**，本方法只负责打包，不关心显示哪些级别
    /// 导出与界面筛选是两件事
    /// 导出默认包含 debug，而界面默认不含
    /// 混在一起以后，谁都说不清"导出里到底有没有 debug"
    ///
    /// Writes the zip
    /// The caller filters the entries first; this method only packs
    /// Exporting and display filtering are separate concerns
    /// An export includes debug by default while the UI does not
    /// Merging them would make "does the export contain debug?" unanswerable
    /// </summary>
    public static LogExportResult Export(
        string zipPath,
        IReadOnlyList<LogExportEntry> entries,
        string environmentText,
        DateTimeOffset now)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 临时文件与目标同目录
        // 跨卷改名不是原子操作，做不到"失败即不存在"
        //
        // The temp file sits beside the target
        // A rename across volumes is not atomic and would not give the "gone on failure" guarantee
        var tempPath = zipPath + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, LogEntryName, BuildLogText(entries, now));
                WriteEntry(archive, EnvironmentEntryName, environmentText);
            }

            File.Move(tempPath, zipPath, overwrite: true);
        }
        catch
        {
            // 失败时清掉半成品，绝不在目标路径留下能误导人的 zip（见文件头）
            //
            // On failure the partial file is removed
            // That way no misleading zip is left at the target path (see the file header)
            TryDelete(tempPath);
            throw;
        }

        return new LogExportResult(zipPath, entries.Count);
    }

    /// <summary>
    /// 按时间顺序列出日志目录里可导出的来源
    /// 单次会话按（日期，编号）排序，整天归档按日期占位
    /// 两者不会落在同一天：会话文件只属于今天，归档只属于更早的日期
    ///
    /// Lists the exportable sources in the log directory in chronological order
    /// Sessions sort by date then number, and a whole-day archive takes its date slot
    /// The two never share a date: session files belong to today and archives to earlier days
    /// </summary>
    public static IReadOnlyList<string> ListExportSources(string logDirectory)
    {
        if (!Directory.Exists(logDirectory))
        {
            return [];
        }

        var ordered = new List<(DateOnly Date, int Order, string Path)>();
        foreach (var path in Directory.GetFiles(logDirectory))
        {
            var info = LogFileNames.Parse(Path.GetFileName(path));
            switch (info.Shape)
            {
                case LogFileShape.Session when info.Date is { } sessionDate:
                    ordered.Add((sessionDate, info.Session ?? 0, path));
                    break;
                case LogFileShape.DayArchive when info.Date is { } archiveDate:
                    ordered.Add((archiveDate, 0, path));
                    break;
            }
        }

        return ordered
            .OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Path)
            .ToList();
    }

    /// <summary>
    /// 从落盘的日志文件打包，边读边写，不把全文拼成一个字符串
    /// 为什么必须流式：保留期内可能有几百 MB，先拼成字符串再转 UTF-8 会同时占住两份
    /// 级别筛选在这里做：每一行都带 [level]，没有级别的结构性行（会话头、截断标记）一律保留
    ///
    /// Packs from the on-disk log files, reading and writing as it goes rather than building one big string
    /// Why streaming is required: the retention window can hold hundreds of MB, and building a string first would
    /// hold two copies at once
    /// Level filtering happens here: every log line carries [level], and structural lines without one
    /// (session headers, the truncation marker) are always kept
    /// </summary>
    public static LogExportResult ExportFromSources(
        string zipPath,
        IReadOnlyList<string> sources,
        string environmentText,
        DateTimeOffset now,
        IReadOnlyCollection<string>? levels = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var selected = levels is null ? null : new HashSet<string>(levels, StringComparer.OrdinalIgnoreCase);
        var tempPath = zipPath + ".tmp";
        var written = 0;
        try
        {
            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(LogEntryName, CompressionLevel.Optimal);
                using (var stream = entry.Open())
                using (var writer = new StreamWriter(stream, Utf8NoBom))
                {
                    writer.WriteLine(
                        "===== MIDITap log export | exported " + FormatTimestamp(now)
                        + " | sources " + sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " =====");

                    foreach (var source in sources)
                    {
                        written += CopySource(writer, source, selected);
                    }
                }

                WriteEntry(archive, EnvironmentEntryName, environmentText);
            }

            File.Move(tempPath, zipPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return new LogExportResult(zipPath, written);
    }

    /// <summary>把一个来源的内容写进导出文本，返回写入的行数 / Copies one source into the exported text, returning the lines written</summary>
    private static int CopySource(TextWriter writer, string source, HashSet<string>? levels)
    {
        var info = LogFileNames.Parse(Path.GetFileName(source));

        if (info.Shape == LogFileShape.DayArchive)
        {
            var lines = 0;
            using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.DataStream is null)
                {
                    continue;
                }

                using (var reader = new StreamReader(entry.DataStream, Utf8NoBom))
                {
                    lines += CopyLines(writer, reader, levels);
                }

                // 条目之间留一空行：解开后一眼能看出会话的分界
                //
                // A blank line between entries, so the session boundary is visible at a glance
                writer.WriteLine();
            }
            return lines;
        }

        // 会话文件一律是明文，因此这里直接按文本读
        //
        // A session file is always plain text, so it is read as text directly
        using var plain = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sessionReader = new StreamReader(plain, Utf8NoBom);
        return CopyLines(writer, sessionReader, levels);
    }

    /// <summary>
    /// 逐行搬运，并按级别筛选
    /// 没有级别的行（会话头等）始终保留：它们不是日志内容，而是这份日志的结构
    ///
    /// Copies line by line, filtering by level
    /// A line without a level (a session header, say) is always kept: it is structure rather than log content
    /// </summary>
    private static int CopyLines(TextWriter writer, TextReader reader, HashSet<string>? levels)
    {
        var written = 0;
        while (reader.ReadLine() is { } line)
        {
            if (levels is not null
                && LogLineFormat.LevelOf(line) is { } level
                && !levels.Contains(LogLevels.Normalize(level)))
            {
                continue;
            }

            writer.WriteLine(line);
            written++;
        }
        return written;
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(text);
    }

    /// <summary>
    /// 日志正文。首行是自述行：导出时间与条数
    /// 收到 zip 的人由此判断这份日志是否完整
    /// 例如"应该有几百条，只有 3 条"说明应用刚启动不久
    ///
    /// The log body. The first line describes the file: when it was exported and how many lines it holds
    /// Whoever receives the zip can judge from that whether it looks complete
    /// Three lines when hundreds were expected means the app had only just started
    /// </summary>
    private static string BuildLogText(IReadOnlyList<LogExportEntry> entries, DateTimeOffset now)
    {
        var builder = new StringBuilder();
        builder.Append("===== MIDITap log export | exported ")
            .Append(FormatTimestamp(now))
            .Append(" | entries ")
            .Append(entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine(" =====");

        // 每行格式与落盘文件完全一致（毫秒 + 时区偏移）
        // 跨时区回看、或把两次事件的先后对齐时，这两项都是必需的
        // 而导出文件正是给别人看的那一份，更不能省
        //
        // Each line matches the on-disk format exactly (milliseconds plus UTC offset)
        // Both are needed when reading the log from another timezone or ordering two events
        // The exported file is precisely the copy other people read, so they are the last thing to drop
        foreach (var entry in entries)
        {
            builder.AppendLine(LogLineFormat.Format(entry.Time, entry.Level, entry.Message));
        }

        return builder.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset time)
        => time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响失败的导出本身：调用方要看到的是原始异常
            //
            // A failed cleanup does not change the failed export
            // The caller needs the original exception, not this one
        }
    }
}
