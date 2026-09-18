// LogHousekeepingTests.cs — 启动时的日志整理
//
// 这一组用例覆盖三件事，每一件错了都会**静默丢日志**
//   1) 归档的顺序 —— 必须先把包写成、改名成功，最后才删源文件
//      顺序反了的话，中途失败就等于把日志删了
//   2) 今天必须跳过 —— 它的会话各自独立，那正是「多次启动不混在一起」这条要求
//   3) 保留期的边界 —— 多删一天，用户就少一天可以回溯的历史
//
// These cover three things that each lose log lines silently when wrong
// The order of archiving: write the package, rename it, and only then remove the sources
// Reversing that order means a failure midway simply deletes the log
// Today must be skipped, because its sessions stay separate, which is the launches-must-not-be-mixed rule
// The retention boundary: one day too many removed costs the user a day of history

using System.Formats.Tar;
using System.IO.Compression;
using MIDITap.Core.Logging;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class LogHousekeepingTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 9, 18);
    private static readonly DateOnly Yesterday = new(2026, 9, 17);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-housekeeping-" + Guid.NewGuid().ToString("N"));

    public LogHousekeepingTests() => Directory.CreateDirectory(_dir);

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

    private string WriteSession(DateOnly date, int session, string text)
    {
        var path = Path.Combine(_dir, LogFileNames.SessionName(date, session));
        File.WriteAllText(path, text);
        return path;
    }

    private static List<(string Name, string Text)> ReadDayArchive(string path)
    {
        var entries = new List<(string, string)>();
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            using var reader = new StreamReader(entry.DataStream!);
            entries.Add((entry.Name, reader.ReadToEnd()));
        }
        return entries;
    }

    // ---------------- 旧格式清理 / Legacy cleanup ----------------

    [Fact]
    public void RemoveLegacyFiles_deletes_both_legacy_shapes_and_counts_them()
    {
        // 旧格式只由上一版产生，新命名不会再写出它们，但留着会让日志目录有两种解释
        //
        // Only the previous version produced the legacy shapes and the new naming never writes them
        // Leaving them means the log directory has two possible readings
        var plain = Path.Combine(_dir, "miditap.log");
        var rotated = Path.Combine(_dir, "miditap.log.1");
        File.WriteAllText(plain, "legacy");
        File.WriteAllText(rotated, "legacy rotated");

        var session = WriteSession(Today, 1, "current session");
        var foreign = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(foreign, "not ours");

        Assert.Equal(2, LogHousekeeping.RemoveLegacyFiles(_dir));

        Assert.False(File.Exists(plain));
        Assert.False(File.Exists(rotated));

        // 自己的会话文件与别人的文件都不许动
        //
        // Neither our own session file nor somebody else's may be touched
        Assert.True(File.Exists(session));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void RemoveLegacyFiles_returns_zero_for_a_missing_directory()
        => Assert.Equal(0, LogHousekeeping.RemoveLegacyFiles(Path.Combine(_dir, "absent")));

    // ---------------- 整天归档 / Whole-day archiving ----------------

    [Fact]
    public void ArchiveEarlierDays_packs_the_earlier_day_and_removes_its_sources()
    {
        // 今天的会话必须各自独立，那正是"多次启动不混在一起"这条要求
        //
        // Today's sessions have to stay separate, which is the launches-must-not-be-mixed rule
        var first = WriteSession(Yesterday, 1, "yesterday one");
        var second = WriteSession(Yesterday, 2, "yesterday two");
        var today = WriteSession(Today, 1, "today");

        Assert.Equal(1, LogHousekeeping.ArchiveEarlierDays(_dir, Today));

        var archive = Path.Combine(_dir, LogFileNames.DayArchiveName(Yesterday));
        Assert.True(File.Exists(archive));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(today));

        var entries = ReadDayArchive(archive);
        Assert.Equal(2, entries.Count);
        Assert.Equal(LogFileNames.SessionName(Yesterday, 1), entries[0].Name);
        Assert.Equal("yesterday one", entries[0].Text);
        Assert.Equal(LogFileNames.SessionName(Yesterday, 2), entries[1].Name);
        Assert.Equal("yesterday two", entries[1].Text);
    }

    [Fact]
    public void ArchiveEarlierDays_stores_a_session_verbatim()
    {
        // 归档里装的是明文原文，因此解开就能直接读，不必再解一层
        // 这里用一段带时间戳与中文的文本，顺带钉住编码没有被动过
        //
        // The archive holds the plain text as it is, so extracting gives readable content with no second step
        // The text carries a timestamp and Chinese characters, which also pins the encoding
        var text = "2026-09-17 15:30:45.123 +09:00 [info] note on: 60 velocity 100 -> f13 按键按下" + Environment.NewLine;
        WriteSession(Yesterday, 1, text);

        Assert.Equal(1, LogHousekeeping.ArchiveEarlierDays(_dir, Today));

        var entry = Assert.Single(ReadDayArchive(Path.Combine(_dir, LogFileNames.DayArchiveName(Yesterday))));
        Assert.Equal(text, entry.Text);
    }

    [Fact]
    public void ArchiveEarlierDays_orders_entries_by_session_number()
    {
        // 条目名是解压后可直接辨认的明文名字，顺序按编号升序，读起来才是时间顺序
        // 这里故意先创建 2 号，证明顺序来自排序而不是目录枚举顺序
        //
        // The entry names are the identifiable plain-text names, ordered by number so the archive reads chronologically
        // Number 2 is created first on purpose, proving the order comes from sorting rather than enumeration
        WriteSession(Yesterday, 2, "second");
        WriteSession(Yesterday, 1, "first");

        Assert.Equal(1, LogHousekeeping.ArchiveEarlierDays(_dir, Today));

        var entries = ReadDayArchive(Path.Combine(_dir, LogFileNames.DayArchiveName(Yesterday)));
        Assert.Equal(
            new[] { LogFileNames.SessionName(Yesterday, 1), LogFileNames.SessionName(Yesterday, 2) },
            entries.Select(entry => entry.Name).ToList());
    }

    [Fact]
    public void ArchiveEarlierDays_leaves_today_untouched()
    {
        // 没跨天就一个字节都不动：今天的会话各自独立，内容与名字都要保持原样
        //
        // Nothing is touched before the day has passed: today's sessions stay separate,
        // with both their names and their contents unchanged
        var first = WriteSession(Today, 1, "today one");
        var second = WriteSession(Today, 2, "today two");

        Assert.Equal(0, LogHousekeeping.ArchiveEarlierDays(_dir, Today));

        Assert.Equal("today one", File.ReadAllText(first));
        Assert.Equal("today two", File.ReadAllText(second));
        Assert.False(File.Exists(Path.Combine(_dir, LogFileNames.DayArchiveName(Today))));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void ArchiveEarlierDays_does_not_touch_a_compressed_session_name()
    {
        // 会话级的 .gz 从未发布过，也不是现在的命名，因此它算「不认识的文件」
        // 不认识的既不入归档也不删除，交回给用户自己处理
        //
        // A session-level .gz never shipped and is not part of the current naming, so it counts as unrecognised
        // An unrecognised file is neither archived nor deleted, and is left for the user to deal with
        var compressed = Path.Combine(_dir, LogFileNames.SessionName(Yesterday, 9) + ".gz");
        File.WriteAllText(compressed, "leftover from a build that never shipped");
        WriteSession(Yesterday, 1, "plain");

        Assert.Equal(1, LogHousekeeping.ArchiveEarlierDays(_dir, Today));

        Assert.True(File.Exists(compressed));
        Assert.Single(ReadDayArchive(Path.Combine(_dir, LogFileNames.DayArchiveName(Yesterday))));
    }

    [Fact]
    public void ArchiveEarlierDays_is_idempotent()
    {
        // 每次启动都会跑，因此重复执行不能产生第二个归档，也不能删掉刚建好的那个
        //
        // It runs on every launch, so a second run must neither add another archive nor remove the one just made
        WriteSession(Yesterday, 1, "yesterday");

        Assert.Equal(1, LogHousekeeping.ArchiveEarlierDays(_dir, Today));
        Assert.Equal(0, LogHousekeeping.ArchiveEarlierDays(_dir, Today));
        Assert.True(File.Exists(Path.Combine(_dir, LogFileNames.DayArchiveName(Yesterday))));
    }

    [Fact]
    public void ArchiveEarlierDays_writes_one_archive_per_day()
    {
        WriteSession(new DateOnly(2026, 9, 16), 1, "two days ago");
        WriteSession(Yesterday, 1, "yesterday");

        Assert.Equal(2, LogHousekeeping.ArchiveEarlierDays(_dir, Today));

        Assert.True(File.Exists(Path.Combine(_dir, LogFileNames.DayArchiveName(new DateOnly(2026, 9, 16)))));
        Assert.True(File.Exists(Path.Combine(_dir, LogFileNames.DayArchiveName(Yesterday))));
    }

    [Fact]
    public void ArchiveEarlierDays_removes_a_leftover_temp_file()
    {
        var temp = Path.Combine(_dir, LogFileNames.DayArchiveName(Yesterday) + LogHousekeeping.TempSuffix);
        File.WriteAllText(temp, "half written");
        WriteSession(Yesterday, 1, "yesterday");

        Assert.Equal(1, LogHousekeeping.ArchiveEarlierDays(_dir, Today));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void ArchiveEarlierDays_returns_zero_for_a_missing_directory()
        => Assert.Equal(0, LogHousekeeping.ArchiveEarlierDays(Path.Combine(_dir, "absent"), Today));

    // ---------------- 过期清理 / Expiry ----------------

    [Fact]
    public void RemoveExpiredArchives_keeps_the_window_including_today()
    {
        // keepDays 为 7 时，今天与之前 6 天都留下，也就是最早保留 today-6
        //
        // With keepDays of 7, today and the six days before it stay, so the oldest kept day is today-6
        var justExpired = Today.AddDays(-7);
        var oldestKept = Today.AddDays(-6);

        var expired = Path.Combine(_dir, LogFileNames.DayArchiveName(justExpired));
        var kept = Path.Combine(_dir, LogFileNames.DayArchiveName(oldestKept));
        var recent = Path.Combine(_dir, LogFileNames.DayArchiveName(Yesterday));
        File.WriteAllText(expired, "old");
        File.WriteAllText(kept, "boundary");
        File.WriteAllText(recent, "recent");

        Assert.Equal(1, LogHousekeeping.RemoveExpiredArchives(_dir, Today, LogHousekeeping.RetentionDays));

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void RemoveExpiredArchives_leaves_session_files_alone()
    {
        // 只删整天归档：属于今天的会话必须留着
        // 更早日期的会话已由 ArchiveEarlierDays 收进归档，不是本方法的职责
        //
        // Only whole-day archives are removed: today's sessions must stay
        // An earlier day's sessions were already collected by ArchiveEarlierDays and are not this method's concern
        var session = WriteSession(Today, 1, "today");
        var oldSession = WriteSession(new DateOnly(2026, 1, 1), 1, "long ago");

        Assert.Equal(0, LogHousekeeping.RemoveExpiredArchives(_dir, Today, LogHousekeeping.RetentionDays));

        Assert.True(File.Exists(session));
        Assert.True(File.Exists(oldSession));
    }

    [Fact]
    public void RemoveExpiredArchives_returns_zero_when_the_window_is_below_one_day()
    {
        // keepDays 小于 1 是一个无意义的窗口，此时宁可什么都不做
        //
        // A keepDays below 1 is a meaningless window, and doing nothing is the safer reading of it
        var archive = Path.Combine(_dir, LogFileNames.DayArchiveName(new DateOnly(2026, 1, 1)));
        File.WriteAllText(archive, "old");

        Assert.Equal(0, LogHousekeeping.RemoveExpiredArchives(_dir, Today, 0));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void RemoveExpiredArchives_removes_a_leftover_temp_file()
    {
        var temp = Path.Combine(_dir, LogFileNames.DayArchiveName(Yesterday) + LogHousekeeping.TempSuffix);
        File.WriteAllText(temp, "half written");

        Assert.Equal(0, LogHousekeeping.RemoveExpiredArchives(_dir, Today, LogHousekeeping.RetentionDays));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void RemoveExpiredArchives_returns_zero_for_a_missing_directory()
        => Assert.Equal(
            0,
            LogHousekeeping.RemoveExpiredArchives(
                Path.Combine(_dir, "absent"), Today, LogHousekeeping.RetentionDays));
}
