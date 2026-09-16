// UpdateApplyReportTests.cs — 更新失败 id 的解析：认得出、也扛得住畸形输入
//
// Parsing the update-failure id: it recognises the real thing and survives malformed input

using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class UpdateApplyReportTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Round_trips_an_exit_code(int exitCode)
    {
        var argument = UpdateApplyReport.Format(exitCode);
        var parsed = UpdateApplyReport.Parse(["MIDITap.exe", argument]);
        Assert.Equal(exitCode, parsed);
    }

    [Fact]
    public void Returns_null_when_the_argument_is_absent()
        => Assert.Null(UpdateApplyReport.Parse(["MIDITap.exe"]));

    [Fact]
    public void Ignores_unrelated_arguments()
        => Assert.Null(UpdateApplyReport.Parse(["MIDITap.exe", "--verbose", "-x", "update-failed=5"]));

    [Theory]
    // 畸形值一律当作"没有传"：应用要能照常启动，而不是因为一个坏参数起不来
    // Malformed values count as absent: the app must still start rather than fail on a bad argument
    [InlineData("--update-failed=")]
    [InlineData("--update-failed=abc")]
    [InlineData("--update-failed=-5")]
    [InlineData("--update-failed=+5")]
    [InlineData("--update-failed=5.0")]
    [InlineData("--update-failed= 5")]
    [InlineData("--update-failed=5 ")]
    [InlineData("--update-failed=0x5")]
    [InlineData("--update-faileded=5")]
    [InlineData("--update-failed=99999999999999999999")]
    public void Returns_null_for_malformed_values(string argument)
        => Assert.Null(UpdateApplyReport.Parse([argument]));

    [Fact]
    public void Takes_the_first_matching_argument()
    {
        // 理论上只会传一个；真出现多个时取第一个，行为确定即可
        // Only one is ever passed in practice; with several, taking the first keeps the behaviour defined
        var parsed = UpdateApplyReport.Parse([UpdateApplyReport.Format(5), UpdateApplyReport.Format(6)]);
        Assert.Equal(5, parsed);
    }

    [Theory]
    [InlineData(UpdateApplyReport.StagingMissing, "update.apply.staging")]
    [InlineData(UpdateApplyReport.RolledBack, "update.apply.rolledBack")]
    [InlineData(UpdateApplyReport.UnexpectedError, "update.apply.error")]
    // 认不出的码走通用键：0（成功，本不该带参数）、脚本自己的超时码、以及任何将来新增/未知的码
    // An unrecognised code uses the generic key
    // That covers 0 (success, which should not carry the argument)
    // It also covers the script's own timeout code, and any code added or invented later
    [InlineData(0, "update.apply.other")]
    [InlineData(UpdateApplyReport.NotExited, "update.apply.other")]
    [InlineData(99, "update.apply.other")]
    [InlineData(-1, "update.apply.other")]
    public void Maps_an_exit_code_to_a_message_key(int exitCode, string expected)
        => Assert.Equal(expected, UpdateApplyReport.MessageKey(exitCode));
}
