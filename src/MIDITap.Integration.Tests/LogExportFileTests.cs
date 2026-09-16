// LogExportFileTests.cs — 导出功能的**文件系统**行为
//
// 与 Core.Tests 里的 LogFilterAndExportTests 分工：那边在临时目录里验证逻辑与格式
// 这边验证“真正落盘之后”的东西 —— 用户实际会遇到的场景
//   * 导出到含中文与空格的路径（zip 的编码假设在这种路径上最容易出问题）
//   * 连点两次导出时，第二次是否正确覆盖而不是堆成一堆
//   * 保存对话框里手滑选中了一个**目录**时，是否干净失败而非留下半成品
//   * 没有任何日志就导出（刚启动）时，得到的仍是一个有效归档而不是 0 字节文件
// 这些都属于“单测里用临时路径跑不出来”的情况
//
// File-system behaviour of the export feature
//
// Split of duties with LogFilterAndExportTests in Core.Tests
// That one verifies logic and format in a temporary directory
// This one verifies what happens once files actually land on disk
// Those are the cases a user hits
//   * A path containing non-ASCII characters and spaces
//     An archive's encoding assumptions are most likely to break there
//   * Exporting twice, where the second run must overwrite rather than pile up
//   * Targeting a path that is already a directory (a slip in a save dialog)
//     That must fail cleanly instead of leaving a half-written product
//   * Exporting before any log exists
//     That must still yield a valid archive rather than a zero-byte file

using System.IO.Compression;
using System.Text;
using MIDITap.Core.Logging;
using Xunit;

namespace MIDITap.Integration.Tests;

public sealed class LogExportFileTests : IDisposable
{
    // 中文 + 空格 + 较深嵌套：真实用户的目录往往就是这样
    // Non-ASCII characters plus spaces and deeper nesting
    // Real user directories tend to look like this
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MIDITap 导出测试 " + Guid.NewGuid().ToString("N")[..8],
        "logs");

    private static readonly DateTimeOffset Fixed = new(2026, 9, 14, 15, 20, 30, TimeSpan.FromHours(8));

    public LogExportFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // 连上一级一起删：_root 的父目录是本测试自己建的
            // Delete one level up too: the parent of _root belongs to this test
            Directory.Delete(Directory.GetParent(_root)!.FullName, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
            // A failed cleanup does not change the conclusion
        }
    }

    private static List<LogExportEntry> SampleEntries() =>
    [
        new(Fixed, LogLevels.Info, "配置已加载: 默认配置"),
        new(Fixed.AddSeconds(1), LogLevels.Warn, "按键未映射: 60"),
        new(Fixed.AddSeconds(2), LogLevels.Error, "设备已断开"),
    ];

    /// <summary>把 zip 里某个条目的文本读出来</summary>
    /// <remarks>Reads one entry from the zip as text</remarks>
    private static string ReadEntry(string zipPath, string entryName)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return string.Empty;
        }
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Exports_into_a_path_with_spaces_and_non_ascii_characters()
    {
        // 路径含中文与空格时 zip 仍应能写入并读回
        // 归档 API 对这类路径的编码假设比普通文件写入更强
        // 这也是最容易出问题的地方
        //
        // With spaces and non-ASCII characters in the path the zip must still write and read back
        // The archive APIs make stronger encoding assumptions there than plain file writes do
        var zip = Path.Combine(_root, LogExporter.DefaultFileName(Fixed));

        var result = LogExporter.Export(zip, SampleEntries(), "version=1.7.1" + "\n" + "lang=zh_CN", Fixed);

        Assert.Equal(3, result.EntryCount);
        Assert.True(File.Exists(zip), "zip 未生成 / the zip was not created");

        // 中文日志内容必须原样保留（UTF-8 往返）
        // The Chinese log text must survive the round trip (UTF-8)
        var log = ReadEntry(zip, LogExporter.LogEntryName);
        Assert.Contains("配置已加载: 默认配置", log);
        Assert.Contains("设备已断开", log);
        Assert.Contains("[warn]", log);
    }

    [Fact]
    public void Exporting_twice_over_the_same_file_replaces_it_rather_than_appending()
    {
        // 用户连点两次导出：第二次必须**替换**第一次的内容
        // 若变成追加，文件里会同时出现新旧两段
        // 而打开 zip 的人无从判断哪段是这次的
        //
        // The user clicks export twice: the second run must REPLACE the first
        // Appending would leave both runs in one file
        // Whoever opens the zip cannot tell which part is current
        var zip = Path.Combine(_root, "repeat.zip");

        LogExporter.Export(zip, SampleEntries(), "first", Fixed);
        var second = LogExporter.Export(
            zip,
            [new LogExportEntry(Fixed.AddHours(1), LogLevels.Info, "第二次导出")],
            "second",
            Fixed.AddHours(1));

        Assert.Equal(1, second.EntryCount);
        var log = ReadEntry(zip, LogExporter.LogEntryName);
        Assert.Contains("第二次导出", log);
        Assert.DoesNotContain("配置已加载", log);
    }

    [Fact]
    public void Export_creates_missing_directories_under_the_target()
    {
        // 首次导出到一个还不存在的子目录：应自动创建，而不是抛异常让用户自己去建
        //
        // Exporting into a subdirectory that does not exist yet must create it
        // It must not fail and make the user create it by hand
        var zip = Path.Combine(_root, "深层", "再深一层", "archive.zip");

        LogExporter.Export(zip, SampleEntries(), "env", Fixed);

        Assert.True(File.Exists(zip));
        Assert.True(Directory.Exists(Path.GetDirectoryName(zip)));
    }

    [Fact]
    public void Export_to_a_path_that_is_a_directory_fails_without_leaving_anything()
    {
        // 用户在保存对话框里选中了一个**目录**：导出必须失败，且不能留下 .tmp 或半个 zip
        // 半成品比直接失败更糟 —— 它看起来像一个完整的导出，用户会把它发给别人
        //
        // The user picks a DIRECTORY in the save dialog
        // The export must fail and leave neither a .tmp file nor a partial zip
        // A half-product is worse than a failure, because it looks complete
        // The user then hands it to somebody else
        var occupied = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(occupied);

        Assert.ThrowsAny<Exception>(() =>
            LogExporter.Export(occupied, SampleEntries(), "env", Fixed));

        Assert.True(Directory.Exists(occupied), "原有目录被破坏了 / the existing directory was damaged");
        Assert.False(File.Exists(occupied + ".tmp"), "留下了临时文件 / a temp file was left behind");
        Assert.False(File.Exists(occupied + LogExporter.Extension), "留下了半个 zip / a partial zip was left");
    }

    [Fact]
    public void Export_with_the_default_filter_still_produces_a_readable_archive()
    {
        // 组合验证：界面默认筛选（不含 debug）产出的集合，走完整导出后仍是可打开的 zip
        // 且环境摘要带上了版本信息 —— 这是用户唯一能附在 issue 里的东西
        //
        // Combined check: the set produced by the UI's default filter (no debug) is exported in full
        // The result is still a readable archive
        // It carries the environment summary with the version
        // That is the one thing a user can attach to an issue
        var zip = Path.Combine(_root, "default-filter.zip");
        var visible = SampleEntries()
            .Where(e => new LogFilter().Matches(e.Level))
            .ToList();

        LogExporter.Export(zip, visible, "version=1.7.1" + "\n" + "lang=zh_CN", Fixed);

        using var archive = ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry(LogExporter.LogEntryName));
        Assert.NotNull(archive.GetEntry(LogExporter.EnvironmentEntryName));

        var environment = ReadEntry(zip, LogExporter.EnvironmentEntryName);
        Assert.Contains("version=1.7.1", environment);
        Assert.Contains("lang=zh_CN", environment);
    }

    [Fact]
    public void An_empty_log_export_is_still_a_valid_archive_with_the_environment()
    {
        // 用户没有任何日志就点了导出（刚启动程序）：仍应得到一个**有效**的 zip，里面至少有环境摘要
        // 生成 0 字节文件会让收到附件的人以为文件坏了
        //
        // The user exports before any log exists (a fresh launch)
        // The result must still be a VALID zip carrying at least the environment summary
        // A zero-byte file would look like a broken attachment
        var zip = Path.Combine(_root, "empty.zip");

        LogExporter.Export(zip, [], "version=1.7.1", Fixed);

        Assert.True(new FileInfo(zip).Length > 0, "空日志导出了 0 字节文件 / a zero-byte file was produced");
        using var archive = ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry(LogExporter.EnvironmentEntryName));
        Assert.Contains("version=1.7.1", ReadEntry(zip, LogExporter.EnvironmentEntryName));
    }
}
