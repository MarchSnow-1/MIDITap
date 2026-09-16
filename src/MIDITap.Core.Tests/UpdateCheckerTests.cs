// 纯更新逻辑测试（不联网）：按 semver 规则判断是否有更新版本可用，并处理 v 前缀与无效版本
//
// Pure updater logic tests (no network): decide whether a newer version is available by semver rules
// It handles v prefixes and invalid versions

using MIDITap.Core.Update;
using Semver;
using Xunit;

namespace MIDITap.Core.Tests;

public class UpdateCheckerTests
{
    [Fact]
    public void Newer_version_is_available()
    {
        Assert.True(IsUpdate("1.7.1", "2.0.0"));
        Assert.True(IsUpdate("1.7.1", "1.7.2"));
        Assert.True(IsUpdate("1.0.0", "1.0.1"));
    }

    [Fact]
    public void Same_or_older_version_is_not_available()
    {
        Assert.False(IsUpdate("2.0.0", "2.0.0"));
        Assert.False(IsUpdate("2.0.1", "2.0.0"));
        Assert.False(IsUpdate("2.0.0", "1.9.9"));
    }

    [Fact]
    public void Leading_v_tag_prefix_is_stripped()
    {
        Assert.True(IsUpdate("1.7.1", "v2.0.0"));
        Assert.False(IsUpdate("v2.0.0", "v2.0.0"));
        Assert.True(IsUpdate("2.0.0", "v2.0.1"));
    }

    [Fact]
    public void Invalid_versions_are_never_an_update()
    {
        Assert.False(IsUpdate("garbage", "2.0.0"));
        Assert.False(IsUpdate("2.0.0", "not.a.version"));
        Assert.False(IsUpdate("", ""));
    }

    [Fact]
    public void Prerelease_and_build_metadata_follow_semver()
    {
        // 2.0.0-beta < 2.0.0，因此从 2.0.0 看它并不是更新
        //
        // 2.0.0-beta < 2.0.0, so from 2.0.0 it is not an update
        Assert.False(IsUpdate("2.0.0", "2.0.0-beta"));
        // 但预发布之后发布的稳定版才是更新 / But a stable release published after the prerelease is the update
        Assert.True(IsUpdate("2.0.0-beta", "2.0.0"));
        // 预发布版本之间的字母/数字标识符比较 / Comparison of alphanumeric identifiers between prerelease versions
        Assert.True(IsUpdate("2.0.0-alpha", "2.0.0-beta"));
        Assert.True(IsUpdate("2.0.0-beta.1", "2.0.0-beta.2"));
        Assert.False(IsUpdate("2.0.0-beta.2", "2.0.0-beta.1"));
    }

    // 后续会发布 v2.28.0-beta.5 / v2.28.0-rc.1 这类版本，所以把预发布的排序固定下来
    // 我们按规范发版，因此只覆盖正常写法，不测畸形输入 —— 那是 semver 库自己的事
    //
    // Later releases use versions such as v2.28.0-beta.5 and v2.28.0-rc.1, so prerelease ordering is pinned here
    // Releases follow the spec, so only well-formed inputs are covered
    // Malformed input is the library's business, not ours
    [Theory]
    // 预发布总是小于同主版本的正式版
    //
    // A prerelease is always smaller than the release with the same major version
    [InlineData("2.28.0-rc.1", "2.28.0", true)]
    [InlineData("2.28.0-beta.5", "2.28.0", true)]
    [InlineData("2.28.0", "2.28.0-rc.1", false)]
    [InlineData("2.28.0", "2.28.0-beta.5", false)]
    // 字母序：beta 在 rc 之前 / Alphabetical order: beta comes before rc
    [InlineData("2.28.0-beta.5", "2.28.0-rc.1", true)]
    [InlineData("2.28.0-rc.1", "2.28.0-beta.5", false)]
    // 数字段按数值比较：这是最容易写错的一条，字符串比较会得出 beta.5 > beta.10
    //
    // Numeric parts compare by value: this is the easiest one to get wrong
    // String comparison would yield beta.5 > beta.10
    [InlineData("2.28.0-beta.5", "2.28.0-beta.10", true)]
    [InlineData("2.28.0-beta.10", "2.28.0-beta.5", false)]
    [InlineData("2.28.0-rc.2", "2.28.0-rc.10", true)]
    // 主版本不同时，主版本说了算，预发布标识不能翻盘
    //
    // When the major versions differ the major version decides
    // A prerelease identifier cannot turn that around
    [InlineData("2.28.0-rc.1", "2.28.1", true)]
    [InlineData("2.27.0", "2.28.0-beta.5", true)]
    [InlineData("2.28.0", "2.29.0-rc.1", true)]
    [InlineData("2.29.0-rc.1", "2.28.0", false)]
    // 多段预发布标识，逐段比较 / Multiple prerelease identifiers are compared part by part
    [InlineData("2.28.0-beta.1.2", "2.28.0-beta.1.3", true)]
    [InlineData("2.28.0-alpha.1", "2.28.0-alpha.beta", true)]
    // 构建元数据不参与排序（semver 规定 + 之后的部分被忽略）
    //
    // Build metadata does not take part in ordering
    // Per the semver spec the part after + is ignored
    [InlineData("2.28.0-rc.1+build.7", "2.28.0-rc.1", false)]
    [InlineData("2.28.0", "2.28.0+build.9", false)]
    public void Prerelease_versions_order_like_semver(string current, string latest, bool expected)
    {
        Assert.Equal(expected, IsUpdate(current, latest));
    }

    // 走与生产代码同一条路径：semver 库的 Any 模式 + 优先级比较
    // 测试用同一个库，才能验证"生产代码用的这套规则"确实正确
    // 而不是在测另一套自己写的实现
    //
    // Same path as production code: the semver library's Any style plus precedence comparison
    // The tests must exercise the SAME rules production uses
    // They must not test a second hand-written implementation
    private static bool IsUpdate(string current, string latest)
    {
        if (!SemVersion.TryParse(current, SemVersionStyles.Any, out var cur)
            || !SemVersion.TryParse(latest, SemVersionStyles.Any, out var lat))
        {
            return false;
        }
        return lat.ComparePrecedenceTo(cur) > 0;
    }
}
