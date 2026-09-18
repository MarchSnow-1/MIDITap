// LogExportFromSourcesTests.cs — 从落盘的日志文件导出
//
// 这一组用例覆盖三件事，每一件错了都会让用户收到一份"看起来对但缺内容"的日志
//   1) 来源的排序 —— 归档属于更早的日期，会话属于今天，顺序错了读起来就不是时间顺序
//   2) 级别筛选 —— 没有级别的结构性行（会话头、截断标记）必须始终保留
//      它们是这份日志的上下文，被筛掉之后没人知道日志从哪里开始、为什么结束
//   3) 压缩与归档的来源必须先解开再写进导出
//      直接拷字节会把 .gz 的内容塞进一个文本文件，用户拿到的是乱码
//
// These cover three things that each hand the user a log which looks right but is missing content
// The order of the sources: an archive belongs to an earlier day and sessions to today,
// and a wrong order does not read chronologically
// Level filtering: a structural line with no level, such as a session header or the truncation marker, must always survive
// Those lines are the context of the log, and dropping them leaves nobody able to tell where it begins or why it ends
// A compressed or archived source must be decompressed before it goes into the export
// Copying the bytes straight through would put .gz content into a text file

using System.Formats.Tar;
using System.IO.Compression;
using MIDITap.Core.Logging;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class LogExportFromSourcesTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 18, 15, 30, 45, TimeSpan.FromHours(9));

    private static readonly DateOnly Today = new(2026, 9, 18);
    private static readonly DateOnly Yesterday = new(2026, 9, 17);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-export-sources-" + Guid.NewGuid().ToString("N"));

    public LogExportFromSourcesTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(LogDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响测试结论 / A failed cleanup does not affect the result
        }
    }

    private string LogDirectory => Path.Combine(_dir, "logs");

    private string ZipPath => Path.Combine(_dir, "out", "miditap-logs.zip");

    private static string Line(string level, string message) => LogLineFormat.Format(FixedNow, level, message);

    private string WriteSession(DateOnly date, int session, string text)
    {
        var path = Path.Combine(LogDirectory, LogFileNames.SessionName(date, session));
        File.WriteAllText(path, text);
        return path;
    }

    private string WriteDayArchive(DateOnly date, params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(LogDirectory, LogFileNames.DayArchiveName(date));
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, text) in entries)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(bytes),
                });
            }
        }
        return path;
    }

    private string ReadZipEntry(string name)
    {
        using var archive = ZipFile.OpenRead(ZipPath);
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    // **必须同时按 CRLF 与 LF 归一**：导出用 Environment.NewLine 写盘（Windows 上是 CRLF）
    // 只替换 LF 会在每行末尾留下一个 CR，断言里写的换行就永远对不上
    // Both CRLF and LF are normalized: the export writes Environment.NewLine (CRLF on Windows)
    // Replacing only LF leaves a CR at the end of every line, so the line breaks in the assertions never match
    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private string ExportedLog() => Normalize(ReadZipEntry(LogExporter.LogEntryName));

    // ---------------- 来源排序 / Source ordering ----------------

    [Fact]
    public void ListExportSources_returns_empty_for_a_missing_directory()
        => Assert.Empty(LogExporter.ListExportSources(Path.Combine(_dir, "absent")));

    [Fact]
    public void ListExportSources_orders_by_date_then_session_number()
    {
        // 会话文件只属于今天，归档只属于更早的日期，两者不会落在同一天
        // 这里仍按（日期，编号）排序，读起来才是时间顺序
        // 故意先创建 2 号，证明顺序来自排序而不是目录枚举顺序
        //
        // Session files belong to today and archives to earlier days, so the two never share a date
        // They are still ordered by date then number, so the result reads chronologically
        // Number 2 is created first on purpose, proving the order comes from sorting rather than enumeration
        var second = WriteSession(Today, 2, "second");
        var first = WriteSession(Today, 1, "first");

        Assert.Equal(new[] { first, second }, LogExporter.ListExportSources(LogDirectory));
    }

    [Fact]
    public void ListExportSources_places_an_earlier_day_before_a_later_one()
    {
        var today = WriteSession(Today, 1, "today");
        var archive = WriteDayArchive(Yesterday, (LogFileNames.SessionName(Yesterday, 1), "yesterday"));

        Assert.Equal(new[] { archive, today }, LogExporter.ListExportSources(LogDirectory));
    }

    [Fact]
    public void ListExportSources_ignores_files_that_are_not_ours()
    {
        // 认不出来的文件不列出来：导出别人的文件既无用也不该发生
        //
        // Unrecognised files are not listed: exporting somebody else's file is both useless and wrong
        File.WriteAllText(Path.Combine(LogDirectory, "notes.txt"), "not ours");
        File.WriteAllText(Path.Combine(LogDirectory, "miditap.log"), "legacy");
        // 会话级的 .gz 从未发布过，也不是现在的命名，因此不进导出列表
        //
        // A session-level .gz never shipped and is not part of the current naming, so it is not listed for export
        File.WriteAllText(Path.Combine(LogDirectory, LogFileNames.SessionName(Today, 9) + ".gz"), "leftover");
        var session = WriteSession(Today, 1, "ours");

        Assert.Equal(new[] { session }, LogExporter.ListExportSources(LogDirectory));
    }

    // ---------------- 导出内容 / Export content ----------------

    [Fact]
    public void ExportFromSources_copies_a_plain_session()
    {
        var path = WriteSession(Today, 1, Line(LogLevels.Info, "note on: 60") + Environment.NewLine);

        var result = LogExporter.ExportFromSources(ZipPath, [path], "env", FixedNow);

        Assert.Equal(1, result.EntryCount);
        Assert.Contains("note on: 60", ExportedLog());
    }

    [Fact]
    public void ExportFromSources_expands_a_day_archive_with_a_blank_line_between_sessions()
    {
        var archive = WriteDayArchive(
            Yesterday,
            (LogFileNames.SessionName(Yesterday, 1), "first entry" + Environment.NewLine),
            (LogFileNames.SessionName(Yesterday, 2), "second entry" + Environment.NewLine));

        var result = LogExporter.ExportFromSources(ZipPath, [archive], "env", FixedNow);

        Assert.Equal(2, result.EntryCount);

        // 条目之间留一空行，解开后一眼能看出会话的分界
        //
        // A blank line between entries makes the session boundary visible at a glance
        Assert.Contains("first entry\n\nsecond entry", ExportedLog());
    }

    [Fact]
    public void ExportFromSources_keeps_structural_lines_when_filtering_by_level()
    {
        // 会话头与截断标记没有级别，被筛掉之后没人知道这份日志从哪里开始、为什么结束
        //
        // The session header and the truncation marker carry no level
        // Dropping them leaves nobody able to tell where this log begins or why it ends
        var text =
            "===== MIDITap session 1 =====" + Environment.NewLine +
            Line(LogLevels.Debug, "detail") + Environment.NewLine +
            Line(LogLevels.Info, "visible") + Environment.NewLine +
            LogFileWriter.TruncationMarker + Environment.NewLine;
        var path = WriteSession(Today, 1, text);

        var result = LogExporter.ExportFromSources(ZipPath, [path], "env", FixedNow, [LogLevels.Info]);

        var exported = ExportedLog();
        Assert.Contains("===== MIDITap session 1 =====", exported);
        Assert.Contains(LogFileWriter.TruncationMarker, exported);
        Assert.Contains("visible", exported);
        Assert.DoesNotContain("detail", exported);

        // 首行自述行不计入，写入的是会话头、visible 与截断标记三行
        //
        // The self-describing first line is not counted
        // Three lines were written: the session header, visible and the truncation marker
        Assert.Equal(3, result.EntryCount);
    }

    [Fact]
    public void ExportFromSources_writes_every_line_when_no_levels_are_given()
    {
        var text =
            Line(LogLevels.Debug, "detail") + Environment.NewLine +
            Line(LogLevels.Error, "bad") + Environment.NewLine;
        var path = WriteSession(Today, 1, text);

        var result = LogExporter.ExportFromSources(ZipPath, [path], "env", FixedNow);

        Assert.Equal(2, result.EntryCount);
        Assert.Contains("detail", ExportedLog());
        Assert.Contains("bad", ExportedLog());
    }

    [Fact]
    public void ExportFromSources_matches_levels_case_insensitively()
    {
        // 级别名的大小写不该决定一条日志是否被导出
        //
        // The case of a level name must not decide whether a line is exported
        var path = WriteSession(Today, 1, Line(LogLevels.Info, "visible") + Environment.NewLine);

        var result = LogExporter.ExportFromSources(ZipPath, [path], "env", FixedNow, ["INFO"]);

        Assert.Equal(1, result.EntryCount);
    }

    [Fact]
    public void ExportFromSources_headers_with_the_source_count()
    {
        // 首行自述行让收到 zip 的人判断这份日志是否完整
        //
        // The self-describing first line lets whoever receives the zip judge whether the log looks complete
        var first = WriteSession(Today, 1, "a" + Environment.NewLine);
        var second = WriteSession(Today, 2, "b" + Environment.NewLine);

        LogExporter.ExportFromSources(ZipPath, [first, second], "env", FixedNow);

        Assert.StartsWith(
            "===== MIDITap log export | exported 2026-09-18 15:30:45.000 +09:00 | sources 2 =====",
            ExportedLog());
    }

    [Fact]
    public void ExportFromSources_writes_the_environment_summary()
    {
        var path = WriteSession(Today, 1, "x" + Environment.NewLine);

        LogExporter.ExportFromSources(ZipPath, [path], "version=1.7.1", FixedNow);

        Assert.Contains("version=1.7.1", ReadZipEntry(LogExporter.EnvironmentEntryName));
    }

    [Fact]
    public void ExportFromSources_creates_the_target_directory()
    {
        var path = WriteSession(Today, 1, "x" + Environment.NewLine);

        LogExporter.ExportFromSources(ZipPath, [path], "env", FixedNow);

        Assert.True(File.Exists(ZipPath));
    }

    [Fact]
    public void ExportFromSources_overwrites_an_existing_zip()
    {
        var path = WriteSession(Today, 1, "new" + Environment.NewLine);

        LogExporter.ExportFromSources(ZipPath, [path], "env", FixedNow);
        LogExporter.ExportFromSources(ZipPath, [path], "env", FixedNow);

        Assert.Contains("new", ExportedLog());
    }

    [Fact]
    public void ExportFromSources_leaves_no_temp_file_behind()
    {
        // 失败时目标路径必须不存在，而不是留一个看起来正常的半成品
        //
        // On failure the target path must not exist, rather than holding a plausible-looking half-product
        var path = WriteSession(Today, 1, "x" + Environment.NewLine);

        LogExporter.ExportFromSources(ZipPath, [path], "env", FixedNow);

        Assert.False(File.Exists(ZipPath + ".tmp"));
    }

    [Fact]
    public void ExportFromSources_reports_zero_lines_for_an_empty_source()
    {
        // 一条日志都没有时仍要能导出：环境摘要本身就是最少量的可用信息
        //
        // An empty source must still export: the environment summary is by itself the minimum useful information
        var path = WriteSession(Today, 1, string.Empty);

        var result = LogExporter.ExportFromSources(ZipPath, [path], "version=1.7.1", FixedNow);

        Assert.Equal(0, result.EntryCount);
        Assert.Contains("version=1.7.1", ReadZipEntry(LogExporter.EnvironmentEntryName));
    }
}
