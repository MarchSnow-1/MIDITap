// LogFilterAndExportTests.cs — 日志级别筛选与导出打包
//
// 这一组用例覆盖三件事，每件都是"错了会静默出错"的地方
//   1) 默认**不含** debug，而"全部"**含** debug
//      两个方向的默认值搞反了不会报错，只会让界面或导出的内容悄悄不对
//   2) 未知级别归入 info
//      归一化写错会让某些条目永远不被任何筛选命中，表现为"日志少了"
//   3) 导出失败时不留半成品 —— 留下一个看似完整的 zip 比直接失败更糟
//
// These cover three things that fail silently when wrong
// The default excludes debug while "all" includes it
// Swapping the two would not raise an error, it would just quietly show or export the wrong thing
// An unknown level folds into info
// A broken normalization makes entries match no filter at all, which reads as "the log lost lines"
// A failed export leaves no half-product
// A zip that looks complete is worse than an outright failure

using System.IO.Compression;
using MIDITap.Core.Logging;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class LogFilterTests
{
    [Fact]
    public void Default_shows_info_warn_and_error_but_not_debug()
    {
        // debug 是排查用的，默认显示会在弹奏时以每秒数十条的速度把真正要看的内容挤出缓冲区
        //
        // Debug is for triage
        // Showing it by default would push what one actually needs out of the buffer
        // That happens at dozens of lines per second while playing
        var filter = new LogFilter();

        Assert.True(filter.Matches(LogLevels.Info));
        Assert.True(filter.Matches(LogLevels.Warn));
        Assert.True(filter.Matches(LogLevels.Error));
        Assert.False(filter.Matches(LogLevels.Debug));
        Assert.False(filter.IsAll);
    }

    [Fact]
    public void Select_all_includes_debug()
    {
        var filter = new LogFilter();
        filter.SelectAll();

        Assert.True(filter.Matches(LogLevels.Debug));
        Assert.True(filter.IsAll);
        Assert.Equal(LogLevels.All.Count, filter.Enabled.Count);
    }

    [Fact]
    public void Select_defaults_returns_to_the_no_debug_state()
    {
        var filter = new LogFilter();
        filter.SelectAll();
        filter.SelectDefaults();

        Assert.False(filter.Matches(LogLevels.Debug));
        Assert.True(filter.Matches(LogLevels.Info));
    }

    [Fact]
    public void Turning_off_the_last_level_falls_back_to_all_rather_than_showing_nothing()
    {
        // 一个级别都不选会让界面空白且无法解释；宁可回到全选
        //
        // Selecting nothing leaves a blank, unexplainable list; falling back to all is better
        var filter = new LogFilter();
        filter.SetEnabled(LogLevels.Info, false);
        filter.SetEnabled(LogLevels.Warn, false);
        filter.SetEnabled(LogLevels.Error, false);

        Assert.True(filter.IsAll);
        Assert.True(filter.Matches(LogLevels.Debug));
    }

    [Fact]
    public void Unknown_levels_follow_info_so_a_new_level_is_never_silently_hidden()
    {
        var filter = new LogFilter();

        // 未知级别按 info 处理：显示得略粗，好过静默隐藏
        //
        // An unknown level counts as info: showing it a little coarsely beats hiding it silently
        Assert.True(filter.Matches("trace"));
        Assert.Equal(filter.Matches(LogLevels.Info), filter.Matches("trace"));

        filter.SetEnabled(LogLevels.Info, false);
        Assert.False(filter.Matches("trace"));
    }

    [Fact]
    public void Changed_fires_only_when_the_selection_actually_changes()
    {
        // 两页都订阅这个事件；无变化也触发会让主页在每次点击时白白重建列表
        //
        // Both pages subscribe; firing on a no-op would make Home rebuild its list for nothing
        var filter = new LogFilter();
        var count = 0;
        filter.Changed += () => count++;

        filter.SetEnabled(LogLevels.Info, true);   // 已经是选中的
        Assert.Equal(0, count);

        filter.SetEnabled(LogLevels.Debug, true);
        Assert.Equal(1, count);

        filter.SelectAll();                        // 此时已全选
        Assert.Equal(1, count);

        filter.SelectDefaults();
        Assert.Equal(2, count);
    }
}

public sealed class LogExporterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-export-" + Guid.NewGuid().ToString("N"));

    public LogExporterTests() => Directory.CreateDirectory(_dir);

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

    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 14, 15, 20, 30, TimeSpan.FromHours(8));

    private string ZipPath => Path.Combine(_dir, "out", "miditap-logs.zip");

    private static LogExportEntry Entry(int second, string level, string message)
        => new(FixedNow.AddSeconds(second), level, message);

    private string ReadEntry(string entryName)
    {
        using var archive = ZipFile.OpenRead(ZipPath);
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void Export_packs_the_log_and_the_environment_summary()
    {
        // 环境摘要与日志同等重要：单发日志时用户得手抄版本、语言、系统，抄漏的往往正是要用的那项
        //
        // The environment summary matters as much as the log
        // With a bare log the user has to copy the version, language and system by hand
        // The one they omit is usually the one needed
        var result = LogExporter.Export(
            ZipPath,
            [Entry(0, LogLevels.Info, "hello")],
            "version=1.7.1\nlang=zh_CN\n",
            FixedNow);

        Assert.Equal(ZipPath, result.ZipPath);
        Assert.Equal(1, result.EntryCount);
        Assert.True(File.Exists(ZipPath));

        using var archive = ZipFile.OpenRead(ZipPath);
        Assert.NotNull(archive.GetEntry(LogExporter.LogEntryName));
        Assert.NotNull(archive.GetEntry(LogExporter.EnvironmentEntryName));

        Assert.Contains("lang=zh_CN", ReadEntry(LogExporter.EnvironmentEntryName));
    }

    [Fact]
    public void Export_creates_the_target_directory()
    {
        // 目标目录通常还不存在（用户第一次导出） / The target directory usually does not exist yet
        var nested = Path.Combine(_dir, "a", "b", "logs.zip");
        LogExporter.Export(nested, [], "env", FixedNow);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Export_keeps_line_order_and_level_tags()
    {
        var text = ExportedLog([Entry(0, LogLevels.Info, "first"), Entry(1, LogLevels.Warn, "second")]);

        // **必须同时按 CRLF 与 LF 切**：日志用 Environment.NewLine 写盘（在 Windows 上是 CRLF）
        // 只切 '\n' 会在每行末尾留下一个 '\r'
        // 于是 EndsWith("...[info] first") 永远不成立
        // Split on CRLF AND LF: the log is written with Environment.NewLine (CRLF on Windows)
        // Splitting on '\n' alone leaves a '\r' at the end of every line
        // The EndsWith assertions can never hold
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // 第一行是自述行，之后按原顺序排列 / A self-describing header, then the lines in order
        Assert.StartsWith("===== MIDITap log export", lines[0]);
        Assert.Contains("entries 2", lines[0]);
        Assert.EndsWith("[info] first", lines[1]);
        Assert.EndsWith("[warn] second", lines[2]);
    }

    [Fact]
    public void Export_timestamps_are_millisecond_and_offset_exact_with_a_fixed_format()
    {
        // 落盘格式逐字一致：跨时区回看、对齐两次事件时毫秒与偏移都是必需的
        // 固定用 InvariantCulture —— 部分区域设置会把数字换成非拉丁字形，那样的时间戳没法解析
        //
        // Character-identical to the on-disk format
        // Milliseconds and the offset are both needed when reading from another timezone or ordering events
        // InvariantCulture is pinned because some locales replace the digits with non-Latin glyphs
        // That leaves an unparseable timestamp
        var text = ExportedLog([Entry(0, LogLevels.Info, "x")]);
        Assert.Contains("2026-09-14 15:20:30.000 +08:00 [info] x", text);
    }

    [Fact]
    public void Export_with_the_default_filter_omits_debug_lines()
    {
        // 组合起来验一次：界面默认不显示 debug，但**导出默认包含** debug
        // 这里验证前者，因为"导出漏了 debug"与"导出多了 debug"是相反的两个缺陷，必须各自钉住
        //
        // Composed check: the UI hides debug by default while an export includes it by default
        // This pins the former
        // The two are opposite defects and each needs its own test
        var filter = new LogFilter();
        var all =
            new List<LogExportEntry>
            {
                Entry(0, LogLevels.Debug, "internal detail"),
                Entry(1, LogLevels.Info, "visible"),
            };
        var visible = all.Where(e => filter.Matches(e.Level)).ToList();

        LogExporter.Export(ZipPath, visible, "env", FixedNow);
        var text = ReadEntry(LogExporter.LogEntryName);

        Assert.Contains("visible", text);
        Assert.DoesNotContain("internal detail", text);
        Assert.Contains("entries 1", text);
    }

    [Fact]
    public void Exporting_every_selected_level_includes_debug()
    {
        // 导出对话框默认勾选全部四个级别，因此这条路径必须真的把 debug 带上
        //
        // The export dialog checks all four levels by default, so this path must really carry debug
        var filter = new LogFilter();
        filter.SelectAll();

        Assert.True(filter.Matches(LogLevels.Debug));
        Assert.Equal(LogLevels.All.Count, filter.Enabled.Count);
    }

    [Fact]
    public void Export_overwrites_an_existing_zip()
    {
        // 用户选一个已存在的文件名就是要覆盖；留下旧内容会让人以为导出没生效
        //
        // Picking an existing file name means overwrite
        // Keeping the old contents would make it look as if the export did nothing
        LogExporter.Export(ZipPath, [Entry(0, LogLevels.Info, "old")], "env", FixedNow);
        LogExporter.Export(ZipPath, [Entry(0, LogLevels.Info, "new")], "env", FixedNow);

        var text = ReadEntry(LogExporter.LogEntryName);
        Assert.Contains("new", text);
        Assert.DoesNotContain("old", text);
    }

    [Fact]
    public void Export_leaves_no_temp_file_behind()
    {
        LogExporter.Export(ZipPath, [Entry(0, LogLevels.Info, "x")], "env", FixedNow);
        Assert.False(File.Exists(ZipPath + ".tmp"), "不应留下临时文件");
    }

    [Fact]
    public void Failed_export_leaves_no_zip_at_all()
    {
        // 失败必须"什么都不留"，而不是留一个看起来正常的半成品
        // 用户会把半成品当完整日志发出去，而缺的恰好是末尾 —— 最要紧的那几条
        //
        // A failure must leave nothing rather than a plausible-looking partial zip
        // The user would send it as a complete log while the missing part is the tail — the lines that matter most
        // 目标是一个**目录**，File.Move 必定失败
        // The target is a DIRECTORY, so File.Move must fail
        var asDirectory = Path.Combine(_dir, "occupied");
        Directory.CreateDirectory(asDirectory);

        Assert.ThrowsAny<Exception>(() =>
            LogExporter.Export(asDirectory, [Entry(0, LogLevels.Info, "x")], "env", FixedNow));

        Assert.True(Directory.Exists(asDirectory), "原有目录不应被破坏");
        Assert.False(File.Exists(asDirectory + ".tmp"), "失败后不应留下临时文件");
    }

    [Fact]
    public void Export_of_an_empty_log_still_carries_the_environment()
    {
        // 一条日志都没有时仍要能导出：环境摘要本身就是最少量的可用信息
        //
        // An empty log must still export
        // The environment summary is by itself the minimum useful information
        var result = LogExporter.Export(ZipPath, [], "version=1.7.1", FixedNow);

        Assert.Equal(0, result.EntryCount);
        Assert.Contains("version=1.7.1", ReadEntry(LogExporter.EnvironmentEntryName));
    }

    [Fact]
    public void Default_file_name_carries_a_second_resolution_timestamp()
    {
        // 覆盖用户刚导出的那份是不可接受的，因此文件名必须能区分连续两次导出
        //
        // Overwriting the copy the user just made is unacceptable
        // So the name must distinguish two consecutive exports
        Assert.Equal("miditap-logs-20260914-152030.zip", LogExporter.DefaultFileName(FixedNow));
    }

    private string ExportedLog(IReadOnlyList<LogExportEntry> entries)
    {
        LogExporter.Export(ZipPath, entries, "env", FixedNow);
        return ReadEntry(LogExporter.LogEntryName);
    }
}
