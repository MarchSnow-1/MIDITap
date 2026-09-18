// LogFileNamesTests.cs — 日志文件的命名、解析与编号
//
// 为什么值得测：解析认错会让清理与归档动到不属于自己的文件
// 反过来，认不出来会让文件被放着不管，日志目录越积越多
// 编号撞车则会让两个实例写进同一个文件，把两份日志混在一起
//
// Why these tests matter: a parse that misidentifies a file lets cleanup and archiving touch something that is not ours
// The opposite mistake leaves files alone forever and the log directory grows without bound
// A number collision puts two instances into one file and mixes two logs together
//
// LogFileNamesTests.cs — log file naming, parsing and numbering

using MIDITap.Core.Logging;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class LogFileNamesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-lognames-" + Guid.NewGuid().ToString("N"));

    public LogFileNamesTests() => Directory.CreateDirectory(_dir);

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
            // 清理失败不影响结论 / A failed cleanup does not change the conclusion
        }
    }

    private static readonly DateOnly Day = new(2026, 9, 18);

    [Fact]
    public void Builds_both_names()
    {
        Assert.Equal("miditap-2026-09-18-3.log", LogFileNames.SessionName(Day, 3));
        Assert.Equal("miditap-2026-09-18.tar.gz", LogFileNames.DayArchiveName(Day));
    }

    [Fact]
    public void Parses_a_session()
    {
        var info = LogFileNames.Parse("miditap-2026-09-18-3.log");

        Assert.Equal(LogFileShape.Session, info.Shape);
        Assert.Equal(Day, info.Date);
        Assert.Equal(3, info.Session);
    }

    [Fact]
    public void Parses_a_day_archive()
    {
        var info = LogFileNames.Parse("miditap-2026-09-17.tar.gz");

        Assert.Equal(LogFileShape.DayArchive, info.Shape);
        Assert.Equal(new DateOnly(2026, 9, 17), info.Date);
        Assert.Null(info.Session);
    }

    [Theory]
    [InlineData("miditap.log")]
    [InlineData("miditap.log.1")]
    public void Recognises_the_legacy_shapes(string name)
    {
        Assert.Equal(LogFileShape.Legacy, LogFileNames.Parse(name).Shape);
    }

    [Theory]
    [InlineData("other.log")]
    [InlineData("miditap-2026-09-18.log")]
    [InlineData("miditap-2026-09-18-0.log")]
    [InlineData("miditap-2026-09-18--1.log")]
    [InlineData("miditap-2026-13-01-1.log")]
    [InlineData("miditap-20260918-1.log")]
    [InlineData("miditap-2026-09-18-1.txt")]
    [InlineData("miditap-.log")]
    // 会话级的 .gz 从未发布过，也不是现在的命名，因此认不出来
    // 认不出来就等于「不是我们的文件」，清理与归档都不会碰它
    //
    // A session-level .gz never shipped and is not part of the current naming, so it is not recognised
    // Unrecognised means "not our file", and neither cleanup nor archiving will touch it
    [InlineData("miditap-2026-09-18-1.log.gz")]
    [InlineData("")]
    public void Refuses_anything_it_cannot_place(string name)
    {
        // 认不出来的一律 Foreign：清理与归档据此跳过，不会误删别人的文件
        //
        // Anything unrecognised counts as Foreign, so cleanup and archiving skip it rather than deleting somebody else's file
        Assert.Equal(LogFileShape.Foreign, LogFileNames.Parse(name).Shape);
    }

    [Fact]
    public void Numbers_start_at_one_and_follow_the_files_on_disk()
    {
        Assert.Equal(1, LogFileNames.NextSessionNumber(_dir, Day));

        File.WriteAllText(Path.Combine(_dir, LogFileNames.SessionName(Day, 1)), "a");
        Assert.Equal(2, LogFileNames.NextSessionNumber(_dir, Day));

        File.WriteAllText(Path.Combine(_dir, LogFileNames.SessionName(Day, 2)), "b");
        Assert.Equal(3, LogFileNames.NextSessionNumber(_dir, Day));
    }

    [Fact]
    public void Numbers_are_counted_per_day()
    {
        // 换一天就从 1 重新开始，编号的语义是"当天第几次"
        //
        // A different day starts again at 1: the number means "which launch of that day"
        File.WriteAllText(Path.Combine(_dir, LogFileNames.SessionName(Day, 7)), "a");

        Assert.Equal(8, LogFileNames.NextSessionNumber(_dir, Day));
        Assert.Equal(1, LogFileNames.NextSessionNumber(_dir, Day.AddDays(1)));
    }

    [Fact]
    public void A_day_archive_does_not_advance_the_session_number()
    {
        // 归档不占编号：它代表一整天，不是某一次启动
        //
        // An archive takes no number: it stands for a whole day rather than one launch
        File.WriteAllText(Path.Combine(_dir, LogFileNames.DayArchiveName(Day)), "a");

        Assert.Equal(1, LogFileNames.NextSessionNumber(_dir, Day));
    }

    [Fact]
    public void Reserving_creates_the_file_and_moves_forward()
    {
        var (firstPath, first) = LogFileNames.ReserveSessionFile(_dir, Day);
        var (secondPath, second) = LogFileNames.ReserveSessionFile(_dir, Day);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
        Assert.NotEqual(firstPath, secondPath);
    }

    [Fact]
    public void Reserving_skips_a_number_that_is_already_taken()
    {
        // 模拟"另一个实例先抢到了同一个编号"：它已经在盘上，这里必须往后换一个
        // CreateNew 是这一步的保证，因此两个实例不会写进同一个文件
        //
        // Simulates another instance having taken the same number: the file is already there, so the next is used
        // CreateNew is what guarantees this, so two instances cannot write into one file
        File.WriteAllText(Path.Combine(_dir, LogFileNames.SessionName(Day, 1)), "taken");

        var (path, session) = LogFileNames.ReserveSessionFile(_dir, Day);

        Assert.Equal(2, session);
        Assert.Equal(LogFileNames.SessionName(Day, 2), Path.GetFileName(path));
        // 已存在的那一份没有被覆盖
        //
        // The existing file was not overwritten
        Assert.Equal("taken", File.ReadAllText(Path.Combine(_dir, LogFileNames.SessionName(Day, 1))));
    }

    [Fact]
    public void Reserving_creates_the_directory()
    {
        var nested = Path.Combine(_dir, "logs", "deeper");

        var (path, _) = LogFileNames.ReserveSessionFile(nested, Day);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Names_round_trip_through_parse()
    {
        // 生成与解析必须互为逆运算，否则归档会产生自己认不出来的名字
        //
        // Building and parsing have to be inverses, or archiving produces names nothing can recognise
        var name = LogFileNames.SessionName(Day, 1);
        var info = LogFileNames.Parse(name);
        Assert.Equal(LogFileShape.Session, info.Shape);
        Assert.Equal(Day, info.Date);
        Assert.Equal(1, info.Session);

        var archive = LogFileNames.Parse(LogFileNames.DayArchiveName(Day));
        Assert.Equal(LogFileShape.DayArchive, archive.Shape);
        Assert.Equal(Day, archive.Date);
    }
}
