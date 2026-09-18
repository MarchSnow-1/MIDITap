// LogFileWriterTests.cs — 日志落盘、体积上限与截断
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

    // ------------------------------------------------------------------ 体积上限与截断 / Size cap and truncation
    //
    // Appending stops at the size cap

    [Fact]
    public void Appending_stops_once_the_cap_is_reached()
    {
        // 每条 0123456789 加换行是 12 字节，上限 40 字节
        // 写满 36 字节后下一条会超过上限，于是写一行标记并停手
        //
        // Each 0123456789 plus a newline is 12 bytes, and the cap is 40 bytes
        // After 36 bytes the next line would pass the cap, so one marker is written and appending stops
        using (var writer = new LogFileWriter(LogPath, maxBytes: 40))
        {
            for (var i = 0; i < 10; i++)
            {
                writer.Append("0123456789");
            }
        }

        // 断言放在 Dispose 之后：写入由后台线程完成，Dispose 会等队列排空
        // 在 using 块内断言就是与那个线程赛跑，截断标记可能还没写下去
        //
        // The assertions come after Dispose: the writing happens on a background thread and Dispose waits for
        // the queue to drain
        // Asserting inside the using block races that thread, so the marker may not be written yet
        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(LogFileWriter.TruncationMarker, lines[^1]);
        Assert.True(lines.Length < 10, "达到上限后不应把 10 条全部写入");
    }

    [Fact]
    public void The_file_never_grows_past_the_cap_by_more_than_one_marker()
    {
        // 上限的意义是占用有界：超出上限的只有那一行标记
        // 为什么不再轮转成 .1：每个会话已是独立文件，.log.1 不在命名规则里，清理与归档都认不出它
        //
        // The point of the cap is bounded usage: the only thing past it is that single marker line
        // Why rotation to .1 is gone: every session is already its own file, and .log.1 is not one of the
        // name shapes, so cleanup and archiving could not recognise it
        using (var writer = new LogFileWriter(LogPath, maxBytes: 24))
        {
            for (var i = 0; i < 400; i++)
            {
                writer.Append("0123456789");
            }
        }

        var markerBytes = System.Text.Encoding.UTF8.GetByteCount(LogFileWriter.TruncationMarker)
            + System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine);

        Assert.True(
            new FileInfo(LogPath).Length <= 24 + markerBytes,
            "文件最多只超出上限一行标记的长度");
        Assert.Equal(LogFileWriter.TruncationMarker, File.ReadAllLines(LogPath)[^1]);

        // 不再产生 .1 备份
        //
        // No .1 backup is produced any more
        Assert.False(File.Exists(LogPath + ".1"), "不再轮转，因此不应出现 .1");
    }

    [Fact]
    public void Nothing_is_truncated_before_the_cap_is_reached()
    {
        using (var writer = new LogFileWriter(LogPath, maxBytes: 10_000))
        {
            writer.Append("small");
        }

        Assert.Equal(["small"], File.ReadAllLines(LogPath));
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
