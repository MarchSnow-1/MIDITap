// InstallKindTests.cs — 安装方式判定
//
// 为什么值得测：判定错了的两个方向后果不同，且都不会报错
//   把安装器装的当便携包：更新后注册表版本号不动，用户看到"更新了但还是旧版"
//   把便携包当安装器装的：另装一份到默认目录，用户得到两个应用
// 因此这里把边界逐一钉住，尤其是"认不出来必须算便携包"这条保守规则
//
// Why these matter: the two wrong answers differ in consequence, and neither reports an error
// Treating an installed copy as portable leaves the registry version stale, which reads as
// "it updated but still shows the old version"
// Treating a portable copy as installed installs a second copy into the default directory
// The boundaries are therefore pinned here, especially the cautious rule that anything unrecognised is portable
//
// InstallKindTests.cs — deciding how this copy of the app was installed

using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class InstallKindTests
{
    // 用临时目录拼路径而不是写死盘符：测试数据不得依赖开发机的绝对路径
    //
    // Paths are built from the temp directory rather than a hard-coded drive: fixtures must not depend on a
    // development machine's absolute path
    private static readonly string AppDir = Path.Combine(Path.GetTempPath(), "example-app");

    private static readonly string OtherDir = Path.Combine(Path.GetTempPath(), "example-other");

    [Fact]
    public void Equal_directories_mean_installed()
    {
        Assert.Equal(InstallKind.Installed, InstallKindDetector.Decide(AppDir, AppDir));
    }

    [Fact]
    public void A_trailing_separator_on_the_registry_value_still_matches()
    {
        // 这是实测的注册表值形态：InstallLocation 末尾带一个分隔符，而自身目录不带
        // 不裁掉它就会判成便携包，于是注册表版本号永远不更新
        //
        // This is the measured shape of the registry value: InstallLocation ends with a separator while the
        // app directory does not
        // Not trimming it would decide "portable", and the registry version would then never update
        var withSeparator = AppDir + Path.DirectorySeparatorChar;

        Assert.Equal(InstallKind.Installed, InstallKindDetector.Decide(AppDir, withSeparator));
        Assert.Equal(InstallKind.Installed, InstallKindDetector.Decide(withSeparator, AppDir));
    }

    [Fact]
    public void Case_differences_do_not_matter()
    {
        // Windows 路径大小写不敏感
        //
        // Windows paths are case-insensitive
        Assert.Equal(InstallKind.Installed, InstallKindDetector.Decide(AppDir, AppDir.ToUpperInvariant()));
    }

    [Fact]
    public void Alternate_separators_do_not_matter()
    {
        // 注册表里也可能写成正斜杠，规范化后是同一个位置
        //
        // The registry value may use forward slashes, which normalise to the same location
        var forward = AppDir.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.Equal(InstallKind.Installed, InstallKindDetector.Decide(AppDir, forward));
    }

    [Fact]
    public void Surrounding_whitespace_is_ignored()
    {
        Assert.Equal(InstallKind.Installed, InstallKindDetector.Decide(AppDir, "  " + AppDir + "  "));
    }

    [Fact]
    public void A_different_directory_means_portable()
    {
        Assert.Equal(InstallKind.Portable, InstallKindDetector.Decide(AppDir, OtherDir));
    }

    [Fact]
    public void A_missing_or_empty_value_means_portable()
    {
        // 没有注册表项、读不到、值缺失都走这里
        // 这是保守的一侧：认不出来就按便携包处理，宁可少做也不多做
        //
        // No registry entry, an unreadable one, and a missing value all land here
        // That is the cautious side: unrecognised counts as portable, doing less rather than more
        Assert.Equal(InstallKind.Portable, InstallKindDetector.Decide(AppDir, null));
        Assert.Equal(InstallKind.Portable, InstallKindDetector.Decide(AppDir, ""));
        Assert.Equal(InstallKind.Portable, InstallKindDetector.Decide(AppDir, "   "));
    }

    [Fact]
    public void A_missing_own_directory_means_portable()
    {
        // 连自己在哪都不知道时更不能去跑安装包
        //
        // With no idea where we are, running the installer is even less defensible
        Assert.Equal(InstallKind.Portable, InstallKindDetector.Decide(null, AppDir));
        Assert.Equal(InstallKind.Portable, InstallKindDetector.Decide("", AppDir));
    }

    [Fact]
    public void Both_missing_means_portable()
    {
        Assert.Equal(InstallKind.Portable, InstallKindDetector.Decide(null, null));
    }

    [Fact]
    public void A_relative_value_does_not_match_an_absolute_directory()
    {
        // 注册表里存相对路径本身就是异常，此时解析出来的位置与自身目录不同
        // 判成便携包是对的：那条记录不可信
        //
        // A relative path in the registry is itself abnormal, and it resolves somewhere other than our own
        // directory
        // Deciding "portable" is correct there: the record cannot be trusted
        Assert.Equal(InstallKind.Portable, InstallKindDetector.Decide(AppDir, "example-app"));
    }
}
