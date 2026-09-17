// StagedUpdateTests.cs — “上次下载好却没重启的更新”能否在下次启动时恢复
//
// 为什么值得测：这条路径只在**正式构建**里才走得到（开发构建没有更新流程）
// 而它又在用户真实的使用方式里很常见 —— 下载完先不重启、关掉窗口、第二天再开
// 判断错了的后果是要么白丢一个已下载的包，要么提供一个根本装不上的更新
//
// StagedUpdateTests.cs — whether an update downloaded but never restarted can be restored on the next launch
//
// Why these tests matter: this path is reachable only in a **release** build (a development build has no update flow),
// yet it matches a very ordinary way of using the app: download, do not restart, close the window, come back tomorrow
// Getting the decision wrong either throws away a downloaded package for nothing
// or offers an update that cannot possibly install

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

    /// <summary>铺一份“解压完成”的暂存目录 / Lays down a staging directory that looks fully extracted</summary>
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
        StagedUpdate.Mark(_dir, "v2.1.0");
        WriteCompletePayload();

        var result = StagedUpdate.Restore(_dir, "2.0.0");

        Assert.Equal(StagedRestoreVerdict.Ready, result.Verdict);
        Assert.Equal("v2.1.0", result.Version);
    }

    [Fact]
    public void A_missing_payload_is_not_restorable_and_clears_the_record()
    {
        // 必需文件不在 = 上次解压没做完（或用户手工清过）
        // 此时若还提供“重启并更新”，点下去必然失败 —— 不如如实说清并清掉记录
        //
        // A missing required file means the extraction never finished (or the user cleaned up)
        // Offering "restart and update" in that state can only fail, so the record is dropped and the reason is reported
        StagedUpdate.Mark(_dir, "v2.1.0");
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
        StagedUpdate.Mark(_dir, "v2.0.0");
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
        StagedUpdate.Mark(_dir, "v2.1.0");
        Directory.CreateDirectory(StagedUpdate.StagingDir(_dir));

        Assert.Equal(StagedRestoreVerdict.Incomplete, StagedUpdate.Restore(_dir, "2.0.0").Verdict);
        Assert.Equal(StagedRestoreVerdict.None, StagedUpdate.Restore(_dir, "2.0.0").Verdict);
    }

    [Fact]
    public void Clearing_leaves_nothing_behind()
    {
        StagedUpdate.Mark(_dir, "v2.1.0");
        StagedUpdate.Clear(_dir);

        Assert.Equal(StagedRestoreVerdict.None, StagedUpdate.Restore(_dir, "2.0.0").Verdict);
    }

    [Fact]
    public void The_record_survives_a_round_trip_through_storage()
    {
        StagedUpdate.Mark(_dir, "v9.9.9");

        Assert.Equal("v9.9.9", AppStorage.GetStagedUpdate(_dir));
    }
}
