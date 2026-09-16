// UpdateChannelTests.cs — 渠道判定：哪些版本号算开发构建
//
// The channel rule: which version strings count as a development build

using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class UpdateChannelTests
{
    [Theory]
    // dev.yml 的真实形状：1.7.1-dev.<run>+g<sha> / The shape dev.yml really produces
    [InlineData("1.7.1-dev.42+g0d1d487", true)]
    [InlineData("2.0.0-dev.1", true)]
    // 版本未知：拿不到自己在哪个版本，因此不参与更新
    // Unknown: nothing to compare against, so it does not take part in updates
    [InlineData("unknown", true)]
    [InlineData("UNKNOWN", true)]
    [InlineData("  unknown  ", false)] // 未 trim：真实取值不含空白（此处锁定不替调用方清理）
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, true)]
    // 正式渠道：纯版本号 / Release channel: a plain version
    [InlineData("1.7.1", false)]
    [InlineData("2.0.0", false)]
    // 已发布的预发布版仍属正式渠道：它有确定版本号，也确实发布在 Releases 上
    // A published prerelease is still a release channel: it has a definite version and is really on Releases
    [InlineData("2.0.0-beta.1", false)]
    [InlineData("2.0.0-rc.3+g9f2c1a", false)]
    // 仅带构建元数据也不是开发构建 / Build metadata alone is not a development build
    [InlineData("1.7.1+g0d1d487", false)]
    public void Classifies_versions_by_channel(string? version, bool expected)
        => Assert.Equal(expected, UpdateChannel.IsDevelopment(version));

    [Fact]
    public void Marks_the_dev_marker_as_development()
    {
        // 标记本身要是改了，dev.yml 的拼接处必须一起改 —— 这条断言让改动在这里显形
        // If the marker itself changes, the concatenation in dev.yml must change with it
        // This assertion makes that visible here
        Assert.Equal("-dev.", UpdateChannel.DevelopmentMarker);
        Assert.Equal("unknown", UpdateChannel.UnknownVersion);
    }
}
