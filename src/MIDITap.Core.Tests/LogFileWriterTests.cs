// LogFileWriterTests.cs — 日志落盘与轮转
//
// Covers the log-to-file writer and the preference that enables it

using MIDITap.Core.Logging;
using MIDITap.Core.Settings;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class LogFileWriterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-logtests-" + Guid.NewGuid().ToString("N"));

    public LogFileWriterTests() => Directory.CreateDirectory(_dir);

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
            // 清理失败不影响测试结论 / A failed cleanup does not affect the test result
        }
    }

    private string LogPath => Path.Combine(_dir, "logs", "miditap.log");

    // ------------------------------------------------------------------ 基本写入 / Basic writing

    [Fact]
    public void Lines_are_written_in_order_and_directory_is_created()
    {
        // 目录尚不存在：写入器应自行创建（用户第一次开启时 .storage/logs 还不存在）
        //
        // The directory does not exist yet: the writer should create it itself
        // .storage/logs does not exist yet the first time the user turns the switch on
        using (var writer = new LogFileWriter(LogPath))
        {
            writer.Append("first");
            writer.Append("second");
            writer.Append("third");
        }

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(["first", "second", "third"], lines);
    }

    [Fact]
    public void File_has_no_byte_order_mark()
    {
        // 带 BOM 会让日志开头出现不可见字节，给工具解析与人肉查看添乱
        //
        // A BOM would put invisible bytes at the start of the log
        // That gets in the way of both tool parsing and eyeballing
        using (var writer = new LogFileWriter(LogPath))
        {
            writer.Append("hello");
        }

        var bytes = File.ReadAllBytes(LogPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.StartsWith("hello", File.ReadAllText(LogPath));
    }

    [Fact]
    public void Burst_is_fully_flushed_on_dispose()
    {
        // 退出时不能丢尾巴 —— 最后几条往往正是崩溃前最要紧的线索
        //
        // The tail must not be lost on exit
        // The last few lines are often exactly the clue that matters most before a crash
        const int count = 500;
        using (var writer = new LogFileWriter(LogPath))
        {
            for (var i = 0; i < count; i++)
            {
                writer.Append("line-" + i);
            }
        }

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(count, lines.Length);
        Assert.Equal("line-0", lines[0]);
        Assert.Equal("line-499", lines[count - 1]);
    }

    [Fact]
    public void Append_after_dispose_is_a_silent_no_op()
    {
        var writer = new LogFileWriter(LogPath);
        writer.Append("before");
        writer.Dispose();

        // 关闭开关后仍可能有在途的日志事件：绝不能因此抛异常打断应用
        //
        // Log events may still be in flight after the switch is turned off
        // This must never throw and interrupt the app
        writer.Append("after");
        writer.Dispose();

        Assert.Equal(["before"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Append_is_never_blocked_by_a_missing_path()
    {
        // 路径非法（含非法字符）时：Append 不应抛出，应用照常运行
        //
        // With an illegal path (containing invalid characters) Append must not throw
        // The app must keep running
        var writer = new LogFileWriter(Path.Combine(_dir, "bad\0name.log"));
        writer.Append("x");
        writer.Dispose();
    }

    // ------------------------------------------------------------------ 体积上限轮转
    //
    // Rotation at the size cap

    [Fact]
    public void File_rotates_to_backup_once_the_cap_is_exceeded()
    {
        // 每条 "0123456789" + 换行 = 12 字节；上限 40 字节
        //
        // Each "0123456789" + newline = 12 bytes; the cap is 40 bytes
        using (var writer = new LogFileWriter(LogPath, maxBytes: 40))
        {
            for (var i = 0; i < 10; i++)
            {
                writer.Append("0123456789");
            }
        }

        Assert.True(File.Exists(LogPath), "主日志文件应存在");
        Assert.True(File.Exists(LogPath + ".1"), "超过上限后应产生 .1 备份");
    }

    [Fact]
    public void Rotation_keeps_at_most_one_backup_so_disk_use_is_bounded()
    {
        // 不封顶的日志会无限增长；轮转必须只保留 .1，占用上限约为 2 x maxBytes
        //
        // An uncapped log grows without bound
        // Rotation must keep only .1, so usage is capped at about 2 x maxBytes
        using (var writer = new LogFileWriter(LogPath, maxBytes: 24))
        {
            for (var i = 0; i < 40; i++)
            {
                writer.Append("0123456789");
            }
        }

        Assert.True(File.Exists(LogPath));
        Assert.True(File.Exists(LogPath + ".1"));
        Assert.False(File.Exists(LogPath + ".2"), "只应保留一个备份");

        // 主文件在轮转后必然小于上限；备份也不应无界增长
        //
        // After rotation the main file is necessarily below the cap
        // The backup must not grow without bound either
        Assert.True(new FileInfo(LogPath).Length <= 24, "主文件应小于上限");
        Assert.True(new FileInfo(LogPath + ".1").Length <= 48, "备份约为一个上限的量级");
    }

    [Fact]
    public void No_rotation_before_the_cap_is_reached()
    {
        using (var writer = new LogFileWriter(LogPath, maxBytes: 10_000))
        {
            writer.Append("small");
        }

        Assert.True(File.Exists(LogPath));
        Assert.False(File.Exists(LogPath + ".1"), "未达上限不应轮转");
    }

    // ------------------------------------------------------------------ 偏好开关 / Preference switch

    [Fact]
    public void Log_to_file_defaults_to_off()
    {
        // 默认关闭是刻意的：未经要求就往磁盘写日志属于多余副作用
        //
        // Off by default is deliberate: writing logs to disk unasked is a needless side effect
        Assert.False(AppStorage.GetLogToFile(_dir));
    }

    [Fact]
    public void Log_to_file_preference_round_trips()
    {
        Assert.True(AppStorage.SaveLogToFile(_dir, true));
        Assert.True(AppStorage.GetLogToFile(_dir));

        Assert.True(AppStorage.SaveLogToFile(_dir, false));
        Assert.False(AppStorage.GetLogToFile(_dir));
    }

    [Fact]
    public void Invalid_log_to_file_value_is_treated_as_off()
    {
        // 文件被手改坏时不能意外打开日志落盘 / A hand-broken file must not accidentally turn log-to-disk on
        Directory.CreateDirectory(Path.Combine(_dir, ".storage"));
        File.WriteAllText(Path.Combine(_dir, ".storage", AppStorage.LogToFileStorageKey), "yes");

        Assert.False(AppStorage.GetLogToFile(_dir));
    }

    [Fact]
    public void Log_to_file_preference_creates_storage_directory()
    {
        Assert.False(Directory.Exists(Path.Combine(_dir, ".storage")));
        AppStorage.SaveLogToFile(_dir, true);
        Assert.True(File.Exists(Path.Combine(_dir, ".storage", AppStorage.LogToFileStorageKey)));
    }
}
