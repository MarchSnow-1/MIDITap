// StagedUpdateTests.cs — "上次下载好却没重启的更新"能否在下次启动时恢复
//
// 为什么值得测：这条路径只在**正式构建**里才走得到（开发构建没有更新流程）
// 而它又在用户真实的使用方式里很常见 —— 下载完先不重启、关掉窗口、第二天再开
// 判断错了的后果是要么白丢一个已下载的包，要么提供一个根本装不上的更新
//
// 两条路线的凭据不同，因此形态也必须一起核对：便携看解压出来的必需文件，安装器看那个 setup.exe
// 只记版本号的话，重启后无从判断该核对哪一样
//
// StagedUpdateTests.cs — whether an update downloaded but never restarted can be restored on the next launch
//
// Why these tests matter: this path is reachable only in a **release** build (a development build has no update flow),
// yet it matches a very ordinary way of using the app: download, do not restart, close the window, come back tomorrow
// Getting the decision wrong either throws away a downloaded package for nothing
// or offers an update that cannot possibly install
//
// The two routes check different evidence, so the shape has to be checked as well: the portable route looks for
// the extracted required files, the installer route for that setup.exe
// With only a version number, a relaunch cannot tell which one to check

using MIDITap.Core.Settings;
using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class StagedUpdateTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-staged-" + Guid.NewGuid().ToString("N"));

    public StagedUpdateTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>铺一份"解压完成"的暂存目录 / Lays down a staging directory that looks fully extracted</summary>
    private void WriteCompletePayload()
    {
        var staging = StagedUpdate.StagingDir(_dir);
        foreach (var relative in UpdateStager.RequiredRelativePaths)
        {
            var full = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }

    /// <summary>铺一份"安装包已下载"的文件 / Lays down an installer that looks downloaded</summary>
    private void WriteInstaller()
    {
        var path = StagedUpdate.InstallerPath(_dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "MZ");
    }

    [Fact]
    public void No_record_means_nothing_to_restore()
    {
        var result = StagedUpdate.Restore(_dir, "2.0.0");

        Assert.Equal(StagedRestoreVerdict.None, result.Verdict);
        Assert.Null(result.Version);
    }

    [Fact]
    public void A_complete_payload_for_another_version_is_restorable()
    {
        // 正常路径：下载完 v2.1.0 却没重启，下次启动应当能直接装
        //
        // The ordinary path: v2.1.0 was downloaded but never restarted, so the next launch can install it straight away
        StagedUpdate.Mark(_dir, "v2.1.0", UpdatePackageKind.Portable);
        WriteCompletePayload();

        var result = StagedUpdate.Restore(_dir, "2.0.0");

        Assert.Equal(StagedRestoreVerdict.Ready, result.Verdict);
        Assert.Equal("v2.1.0", result.Version);
        Assert.Equal(UpdatePackageKind.Portable, result.Kind);
    }

    [Fact]
    public void A_missing_payload_is_not_restorable_and_clears_the_record()
    {
        // 必需文件不在 = 上次解压没做完（或用户手工清过）
        // 此时若还提供"重启并更新"，点下去必然失败 —— 不如如实说清并清掉记录
        //
        // A missing required file means the extraction never finished (or the user cleaned up)
        // Offering "restart and update" in that state can only fail, so the record is dropped and the reason is reported
        StagedUpdate.Mark(_dir, "v2.1.0", UpdatePackageKind.Portable);
        // 故意只建目录、不放必需文件 / Deliberately creates the directory without the required files
        Directory.CreateDirectory(StagedUpdate.StagingDir(_dir));

        var result = StagedUpdate.Restore(_dir, "2.0.0");

        Assert.Equal(StagedRestoreVerdict.Incomplete, result.Verdict);
        // 版本号仍要带出来：界面才能说清是哪个版本被丢弃了
        //
        // The version still comes back, so the UI can name which one was discarded
        Assert.Equal("v2.1.0", result.Version);
        Assert.Equal(string.Empty, AppStorage.GetStagedUpdate(_dir));
    }

    [Fact]
    public void An_already_installed_version_clears_the_record()
    {
        // 上次已经装上了，只是记录没来得及清：不能再弹一次更新，同时把记录抹掉
        //
        // The update did get installed last time and only the record survived: no prompt, and the record is cleared
        StagedUpdate.Mark(_dir, "v2.0.0", UpdatePackageKind.Portable);
        WriteCompletePayload();

        var result = StagedUpdate.Restore(_dir, "v2.0.0");

        Assert.Equal(StagedRestoreVerdict.AlreadyInstalled, result.Verdict);
        Assert.Equal(string.Empty, AppStorage.GetStagedUpdate(_dir));
    }

    [Fact]
    public void A_stale_record_is_not_restored_twice()
    {
        // 清掉之后第二次启动不能再判成可恢复：否则用户会反复看到一个装不上的更新
        //
        // Once cleared, a second launch must not judge it restorable again
        // Otherwise the user keeps seeing an update that cannot be installed
        StagedUpdate.Mark(_dir, "v2.1.0", UpdatePackageKind.Portable);
        Directory.CreateDirectory(StagedUpdate.StagingDir(_dir));

        Assert.Equal(StagedRestoreVerdict.Incomplete, StagedUpdate.Restore(_dir, "2.0.0").Verdict);
        Assert.Equal(StagedRestoreVerdict.None, StagedUpdate.Restore(_dir, "2.0.0").Verdict);
    }

    [Fact]
    public void Clearing_leaves_nothing_behind()
    {
        StagedUpdate.Mark(_dir, "v2.1.0", UpdatePackageKind.Portable);
        StagedUpdate.Clear(_dir);

        Assert.Equal(StagedRestoreVerdict.None, StagedUpdate.Restore(_dir, "2.0.0").Verdict);
    }

    [Fact]
    public void The_record_survives_a_round_trip_through_storage()
    {
        StagedUpdate.Mark(_dir, "v9.9.9", UpdatePackageKind.Portable);

        Assert.Equal("v9.9.9", AppStorage.GetStagedUpdate(_dir));
    }

    // ================================================================ 形态 / The shape

    [Fact]
    public void An_installer_record_is_restored_from_the_installer_file()
    {
        // 安装器路线的凭据只有那一个 exe，因此**不需要**解压目录
        // 若这里仍去要解压出来的必需文件，安装器路线就永远恢复不出来
        //
        // The installer route's only evidence is that one exe, so no staging directory is needed
        // Were this to still demand the extracted files, the installer route could never be restored
        StagedUpdate.Mark(_dir, "v2.1.0", UpdatePackageKind.Installer);
        WriteInstaller();

        var result = StagedUpdate.Restore(_dir, "2.0.0");

        Assert.Equal(StagedRestoreVerdict.Ready, result.Verdict);
        Assert.Equal(UpdatePackageKind.Installer, result.Kind);
    }

    [Fact]
    public void An_installer_record_without_its_file_is_incomplete()
    {
        StagedUpdate.Mark(_dir, "v2.1.0", UpdatePackageKind.Installer);

        var result = StagedUpdate.Restore(_dir, "2.0.0");

        Assert.Equal(StagedRestoreVerdict.Incomplete, result.Verdict);
        Assert.Equal(UpdatePackageKind.Installer, result.Kind);
    }

    [Fact]
    public void A_portable_record_ignores_a_leftover_installer_file()
    {
        // 形态必须真的被核对：便携记录不能因为目录里恰好有个 setup.exe 就算完整
        //
        // The shape really has to be checked: a portable record must not count as complete merely because a
        // setup.exe happens to be lying around
        StagedUpdate.Mark(_dir, "v2.1.0", UpdatePackageKind.Portable);
        WriteInstaller();

        Assert.Equal(StagedRestoreVerdict.Incomplete, StagedUpdate.Restore(_dir, "2.0.0").Verdict);
    }

    [Fact]
    public void A_record_without_a_shape_is_treated_as_portable()
    {
        // 向后兼容：旧版本只写版本号
        // 按便携路线处理是保守的一侧，也让升级上来的用户不会白丢一个已下载的包
        //
        // Backwards compatibility: an older version recorded only the version number
        // Treating that as the portable route is the cautious side, and it keeps an upgrading user from losing a
        // downloaded package for nothing
        AppStorage.SaveStagedUpdate(_dir, "v2.1.0", string.Empty);
        WriteCompletePayload();

        var result = StagedUpdate.Restore(_dir, "2.0.0");

        Assert.Equal(StagedRestoreVerdict.Ready, result.Verdict);
        Assert.Equal(UpdatePackageKind.Portable, result.Kind);
    }

    [Fact]
    public void The_shape_survives_a_round_trip_through_storage()
    {
        StagedUpdate.Mark(_dir, "v9.9.9", UpdatePackageKind.Installer);

        Assert.Equal(StagedUpdate.InstallerKindValue, AppStorage.GetStagedUpdateKind(_dir));
    }

    [Theory]
    [InlineData(UpdatePackageKind.Portable, StagedUpdate.PortableKindValue)]
    [InlineData(UpdatePackageKind.Installer, StagedUpdate.InstallerKindValue)]
    public void The_shape_and_its_stored_spelling_round_trip(UpdatePackageKind kind, string stored)
    {
        // 存储用的是固定的拼写而不是枚举序号：序号会随枚举重排而改变含义
        //
        // The stored form is a fixed spelling rather than the enum's numeric value, since a reordering of the enum
        // would silently change the meaning
        Assert.Equal(stored, StagedUpdate.KindValue(kind));
        Assert.Equal(kind, StagedUpdate.ParseKind(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-else")]
    public void An_unrecognised_spelling_is_treated_as_portable(string? stored)
    {
        Assert.Equal(UpdatePackageKind.Portable, StagedUpdate.ParseKind(stored));
    }
}
