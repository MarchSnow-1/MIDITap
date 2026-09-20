// StagedUpdateTests.cs — "上次下载好却没重启的更新"能否在下次启动时恢复
//
// 为什么值得测：这条路径只在**正式构建**里才走得到（开发构建没有更新流程）
// 而它又在用户真实的使用方式里很常见 —— 下载完先不重启、关掉窗口、第二天再开
// 判断错了的后果是要么白丢一个已下载的包，要么提供一个根本装不上的更新
//
// 两条路线的凭据不同，因此形态也必须一起核对：便携看解压出来的必需文件，安装器看那个 setup.exe
// 只记版本号的话，重启后无从判断该核对哪一样
//
// 版本号有**两种形式**，两者都必须在用例里出现：
//   记录里写的是远端标签（v2.0.1，带 v），而核对时传进来的是程序集版本（2.0.1，不带 v）
//   只测"两边都带 v"这一种组合，正是漏掉"更新成功后每次启动又提示一遍"那个缺陷的原因
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
//
// A version comes in TWO forms and both must appear in these tests:
//   the record holds the remote tag (v2.0.1, with the v) while the check passes in the assembly version (2.0.1, without it)
//   Testing only the "both carry a v" combination is exactly why the defect where every launch re-offered an
//   already-installed update went unnoticed

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

    /// <summary>按形态铺出对应的凭据 / Lays down the evidence the given shape looks for</summary>
    private void WriteEvidence(UpdatePackageKind kind)
    {
        if (kind == UpdatePackageKind.Installer) { WriteInstaller(); } else { WriteCompletePayload(); }
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

    // ================================================================ 版本形式 / The two forms of a version

    [Fact]
    public void An_already_installed_version_clears_the_record()
    {
        // 上次已经装上了，只是记录没来得及清：不能再弹一次更新，同时把记录抹掉
        // 这里刻意用实机上的真实组合：记录来自远端标签（v2.0.1，带 v），而 Restore 拿到的是程序集版本（2.0.1，不带 v）
        // 两者形式不同，按文字比较永远不相等，于是记录清不掉、每次启动都把同一个版本再提示一遍
        //
        // The update did get installed last time and only the record survived: no prompt, and the record is cleared
        // The real-machine combination is used on purpose: the record comes from the remote tag (v2.0.1, with the v)
        // while Restore receives the assembly version (2.0.1, without it)
        // The two forms differ, so a text comparison never matches, the record is never cleared, and every launch
        // offers the same version again
        StagedUpdate.Mark(_dir, "v2.0.1", UpdatePackageKind.Portable);
        WriteCompletePayload();

        var result = StagedUpdate.Restore(_dir, "2.0.1");

        Assert.Equal(StagedRestoreVerdict.AlreadyInstalled, result.Verdict);
        Assert.Equal(string.Empty, AppStorage.GetStagedUpdate(_dir));
    }

    [Theory]
    [InlineData(UpdatePackageKind.Portable)]
    [InlineData(UpdatePackageKind.Installer)]
    public void The_tag_form_and_the_assembly_form_name_the_same_version(UpdatePackageKind kind)
    {
        // 两条路线都要按版本比较：安装器路线是实机复现出该缺陷的那一条，便携路线此前也有同一处形式差
        //
        // Both routes have to compare versions: the installer route is the one where the defect was reproduced on a
        // real machine, and the portable route carried the same difference in form
        StagedUpdate.Mark(_dir, "v2.0.1", kind);
        WriteEvidence(kind);

        var result = StagedUpdate.Restore(_dir, "2.0.1");

        Assert.Equal(StagedRestoreVerdict.AlreadyInstalled, result.Verdict);
        Assert.Equal(kind, result.Kind);
    }

    [Fact]
    public void A_record_older_than_the_running_version_is_superseded()
    {
        // 记录停在旧版本：此时若还恢复出"可恢复的更新"，提示会把版本往回退（当前 2.0.1 / 最新 2.0.0）
        //
        // A record left at an older version: restoring it would propose moving the version backwards
        // (current 2.0.1, latest 2.0.0)
        StagedUpdate.Mark(_dir, "v2.0.0", UpdatePackageKind.Portable);
        WriteCompletePayload();

        var result = StagedUpdate.Restore(_dir, "2.0.1");

        Assert.Equal(StagedRestoreVerdict.Superseded, result.Verdict);
        Assert.Equal("v2.0.0", result.Version);
        Assert.Equal(string.Empty, AppStorage.GetStagedUpdate(_dir));
    }

    [Fact]
    public void A_prerelease_record_is_older_than_its_release()
    {
        // 预发布版排在正式版之前：记录 v2.0.0-beta.1 而当前已是 2.0.0 时，这条记录同样过期
        // 按文字比较会认为两者不同，于是恢复出一个把正式版换回 beta 的提示
        //
        // A prerelease precedes its release, so a record of v2.0.0-beta.1 against a running 2.0.0 is stale as well
        // A text comparison would call the two different and restore a prompt that swaps the release back for a beta
        StagedUpdate.Mark(_dir, "v2.0.0-beta.1", UpdatePackageKind.Portable);
        WriteCompletePayload();

        Assert.Equal(StagedRestoreVerdict.Superseded, StagedUpdate.Restore(_dir, "2.0.0").Verdict);
    }

    [Theory]
    [InlineData("garbage", "2.0.1")]
    [InlineData("2.0.1", "garbage")]
    [InlineData("nightly", "2.0.1")]
    [InlineData("v", "2.0.1")]
    public void A_version_that_does_not_parse_never_counts_as_already_installed(string recorded, string current)
    {
        // 解析不了时不能当成"同一个版本"：那会把用户已经下好的更新包白白清掉
        // 文字完全相同才算相等，其余一律继续按磁盘凭据判断，那才是保守的一侧
        //
        // An unparsable value must not count as "the same version", or a downloaded package would be discarded
        // Only an exact text match counts as equal; anything else still goes through the disk evidence,
        // which is the cautious side
        StagedUpdate.Mark(_dir, recorded, UpdatePackageKind.Portable);
        WriteCompletePayload();

        Assert.Equal(StagedRestoreVerdict.Ready, StagedUpdate.Restore(_dir, current).Verdict);
    }

    [Fact]
    public void A_loose_form_is_still_read_as_a_version()
    {
        // SemVersionStyles.Any 接受简写（v2.0 读作 2.0.0），因此它比当前版本旧，记录同样过期
        // 这条不是附带的：宽松解析是有意选的，真实标签就带 v 前缀，而写死成严格解析会把这些标签全判成读不懂
        //
        // SemVersionStyles.Any accepts the short form (v2.0 reads as 2.0.0), so it is older than the running version
        // and the record is stale in the same way
        // This is not incidental: the lenient style is deliberate, since real tags carry the v prefix and a strict
        // style would call every one of them unreadable
        StagedUpdate.Mark(_dir, "v2.0", UpdatePackageKind.Portable);
        WriteCompletePayload();

        Assert.Equal(StagedRestoreVerdict.Superseded, StagedUpdate.Restore(_dir, "2.0.1").Verdict);
    }

    [Fact]
    public void Identical_unparsable_text_still_counts_as_already_installed()
    {
        // 两侧文字完全一样时仍按"已装上"处理：不解析也得能收敛，否则记录会一直留着
        //
        // Identical text on both sides still counts as already installed: without parsing it must still converge,
        // or the record would stay behind for good
        StagedUpdate.Mark(_dir, "garbage", UpdatePackageKind.Portable);
        WriteCompletePayload();

        Assert.Equal(StagedRestoreVerdict.AlreadyInstalled, StagedUpdate.Restore(_dir, "garbage").Verdict);
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

    // ================================================================ 落地内容的清理 / Discarding landed content

    /// <summary>铺出一次更新落地后会留下的全部内容 / Lays down everything a landed update leaves behind</summary>
    private void WriteLandedContent()
    {
        WriteInstaller();
        WriteCompletePayload();
        var update = AppPaths.UpdateDir(_dir);
        File.WriteAllText(Path.Combine(update, UpdateStager.PortablePackageFileName), "zip");
        File.WriteAllText(Path.Combine(update, UpdateStager.PortablePackageFileName + ".part"), "partial");

        // 便携辅助脚本的备份与它自己的日志：实测成功后一个成功更新会留下 539 个文件 / 230 MB
        // 它们也在更新工作目录之下，因此同样该被丢弃
        //
        // The portable helper's backup and its own log: measured, one successful update left 539 files and 230 MB
        // They live under the same update working directory, so they go too
        Directory.CreateDirectory(Path.Combine(update, "backup", "Assets"));
        File.WriteAllText(Path.Combine(update, "backup", "MIDITap.exe"), "old");
        File.WriteAllText(Path.Combine(update, "backup", "Assets", "appIcon.ico"), "old");
        File.WriteAllText(Path.Combine(update, "apply.log"), "helper log");
    }

    [Fact]
    public void Discarding_a_landed_update_removes_the_whole_working_directory()
    {
        // 更新装上之后再留着这些文件只是占地方，而"占地方"会一直被忽略下去
        // 实测两处：安装器路线成功后 .update 里的 setup.exe 有 62 MB；便携路线成功后 .update\backup 有 230 MB
        // 两处此前都没有任何一步去删
        //
        // Once the update is installed these files only take up room, and that goes unnoticed
        // Measured twice: after a successful installer-route update the setup.exe in .update was 62 MB, and after a
        // successful portable-route update .update\backup held 230 MB
        // Neither had any step deleting it
        var update = AppPaths.UpdateDir(_dir);
        WriteLandedContent();

        UpdateStager.DiscardStagedContent(_dir);

        // 断言整个目录不在，而不是逐条断言：逐条列举正是漏掉 backup/ 的原因
        //
        // The assertion is that the DIRECTORY is gone rather than an itemised list
        // An itemised list is exactly how backup/ was missed
        Assert.False(Directory.Exists(update), "the update working directory should be gone");
    }

    [Fact]
    public void Discarding_nothing_is_quiet()
    {
        // 启动时每次都会走到这里，而绝大多数启动根本没有落地内容
        // 目录不存在就抛异常会让应用在启动阶段失败
        //
        // Every launch reaches this point, and most launches have nothing landed
        // Throwing on a missing directory would fail the app during startup
        UpdateStager.DiscardStagedContent(_dir);

        Assert.True(Directory.Exists(_dir));
    }
}