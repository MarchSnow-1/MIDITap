// LogLineFormatTests.cs — 日志行的文本格式与级别解析
//
// 这一组用例覆盖两处"错了会静默出错"的地方
//   1) 时间戳格式 —— 写盘与导出必须逐字一致
//      两处格式不同不会有编译错误，只会让导出的文件按落盘规则解析不出来
//   2) 级别解析 —— 取不到级别时返回 null，而不是猜一个
//      猜错会让结构性行（会话头、截断标记）被级别筛选丢掉
//      丢掉的正是"这份日志从哪里开始、为什么结束"这种上下文
//
// These cover two things that fail silently when wrong
// The timestamp format must be character-identical between the on-disk file and the export
// A mismatch raises no compile error, it just leaves the exported file unparseable by the on-disk rules
// Level parsing returns null rather than guessing
// A guess would let a level filter drop structural lines such as the session header and the truncation marker
// What gets dropped is the context of where this log begins and why it ends

using System.Globalization;
using MIDITap.Core.Logging;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class LogLineFormatTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 9, 18, 15, 30, 45, 123, TimeSpan.FromHours(9));

    [Fact]
    public void Time_format_carries_milliseconds_and_the_utc_offset()
    {
        // 毫秒用来对齐两次事件的先后，偏移用来跨时区回看
        // 少任何一项，拿到日志的人都无法把它与自己的时间对上
        //
        // Milliseconds order two events, and the offset lets someone in another timezone line the log up
        // Without either one the receiver cannot place the log against their own clock
        Assert.Equal(
            "2026-09-18 15:30:45.123 +09:00",
            FixedTime.ToString(LogLineFormat.TimeFormat, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Format_joins_time_level_and_message_with_single_spaces()
    {
        Assert.Equal(
            "2026-09-18 15:30:45.123 +09:00 [info] note on: 60",
            LogLineFormat.Format(FixedTime, LogLevels.Info, "note on: 60"));
    }

    [Fact]
    public void Format_pins_the_digits_to_the_invariant_culture()
    {
        // 部分区域设置会把数字换成非拉丁字形
        // 那样的时间戳排在每一行行首，人和工具都难以解析，而导出正是交给别人看的文件
        // 在独立线程上改区域设置，避免影响同一进程里并行跑的其他用例
        //
        // Some locales render digits as non-Latin glyphs
        // Such a timestamp heads every line and is hard for both people and tools to parse,
        // and an export is exactly the file meant to be read elsewhere
        // The culture is changed on a dedicated thread so parallel test cases in this process are unaffected
        string? formatted = null;
        var thread = new Thread(() =>
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            try
            {
                formatted = LogLineFormat.Format(FixedTime, LogLevels.Info, "x");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        });
        thread.Start();
        thread.Join();

        Assert.Equal("2026-09-18 15:30:45.123 +09:00 [info] x", formatted);
    }

    [Fact]
    public void LevelOf_reads_the_level_out_of_a_formatted_line()
    {
        var line = LogLineFormat.Format(FixedTime, LogLevels.Warn, "x");
        Assert.Equal(LogLevels.Warn, LogLineFormat.LevelOf(line));
    }

    [Fact]
    public void LevelOf_returns_null_for_a_structural_line()
    {
        // 会话头与截断标记没有级别，按级别筛选时不得丢掉它们
        //
        // A session header and the truncation marker carry no level and must survive a level filter
        Assert.Null(LogLineFormat.LevelOf("===== MIDITap session 1 ====="));
        Assert.Null(LogLineFormat.LevelOf(LogFileWriter.TruncationMarker));
    }

    [Fact]
    public void LevelOf_returns_null_for_null_empty_and_bracketless_input()
    {
        Assert.Null(LogLineFormat.LevelOf(null));
        Assert.Null(LogLineFormat.LevelOf(""));
        Assert.Null(LogLineFormat.LevelOf("plain text with no marker"));
    }

    [Fact]
    public void LevelOf_returns_null_for_an_empty_or_unclosed_bracket_pair()
    {
        // 空标记与没闭合的标记都不是级别：返回它们会让筛选按一个不存在的级别比较
        //
        // An empty marker and an unclosed one are both not levels
        // Returning them would make the filter compare against a level that does not exist
        Assert.Null(LogLineFormat.LevelOf("[] message"));
        Assert.Null(LogLineFormat.LevelOf("2026-09-18 15:30:45.123 +09:00 [info"));
    }

    [Fact]
    public void LevelOf_takes_the_first_bracket_so_a_message_with_brackets_does_not_confuse_it()
    {
        // 时间戳里没有方括号，因此第一个 [ 必定是级别标记的起点
        //
        // The timestamp contains no square bracket, so the first [ is always the start of the level marker
        var line = LogLineFormat.Format(FixedTime, LogLevels.Error, "port [loopMIDI] closed");
        Assert.Equal(LogLevels.Error, LogLineFormat.LevelOf(line));
    }

    [Fact]
    public void LevelOf_reads_a_line_that_begins_with_the_marker()
    {
        Assert.Equal(LogLevels.Debug, LogLineFormat.LevelOf("[debug] bare line"));
    }

    [Theory]
    [InlineData(LogLevels.Debug)]
    [InlineData(LogLevels.Info)]
    [InlineData(LogLevels.Warn)]
    [InlineData(LogLevels.Error)]
    public void Format_and_LevelOf_round_trip_every_known_level(string level)
    {
        Assert.Equal(level, LogLineFormat.LevelOf(LogLineFormat.Format(FixedTime, level, "m")));
    }
}
