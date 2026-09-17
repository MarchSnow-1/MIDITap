// StagedUpdate.cs — 记录并恢复“已经下载好、只差重启”的更新
//
// 为什么需要它：用户下载完更新后可能关掉窗口、甚至直接退出程序，暂时不重启
// 若只在内存里记住这件事，下次启动就只剩“重新下载几百 MB”这一条路
// 因此记录落到 .storage，启动时再核对磁盘上那份解压结果是否真的还在
//
// 为什么这段判断在 Core 而不是界面：它是业务规则（版本比对、完整性核对），不是显示逻辑
// 放在这里可以在无桌面环境下测到
// 而界面层的对应代码只在正式构建里才走得到：开发构建根本没有更新流程，那个分支永远覆盖不到
//
// StagedUpdate.cs — records and restores an update that is downloaded and only awaits a restart
//
// Why it exists: a user may close the window after downloading, or quit the app outright, and restart later
// If that fact lived only in memory, the next launch would have nothing left but re-downloading hundreds of MB
// The record therefore goes to .storage, and startup checks that the extracted files are still on disk
//
// Why the decision lives in Core rather than the UI: it is a business rule (version comparison, completeness check),
// not display logic
// Here it can be tested without a desktop, whereas the UI-side equivalent is reachable only in a release build:
// a development build has no update flow at all, so that branch would never be covered

namespace MIDITap.Core.Update;

/// <summary>恢复暂存更新的结论 / The outcome of restoring a staged update</summary>
public enum StagedRestoreVerdict
{
    /// <summary>没有记录 / No record</summary>
    None,

    /// <summary>记录存在，但磁盘上没有完整的解压结果，记录已清除 / The record existed but the payload on disk was incomplete; it was cleared</summary>
    Incomplete,

    /// <summary>记录里的版本已经在运行，说明上次已经装上了，记录已清除 / The recorded version is already running, so it was applied; the record was cleared</summary>
    AlreadyInstalled,

    /// <summary>可以恢复成待重启状态 / Restorable as an update awaiting a restart</summary>
    Ready,
}

/// <summary>
/// 恢复的结果：结论，以及记录中的版本号（没有记录时为 null）
/// 版本号在 Incomplete 时也要带出来，界面才能说清是哪个版本不完整
///
/// The result: the verdict, plus the recorded version (null when there is no record)
/// The version is carried even for Incomplete, so the UI can name the version that turned out to be incomplete
/// </summary>
public sealed record StagedRestoreResult(StagedRestoreVerdict Verdict, string? Version = null);

public static class StagedUpdate
{
    private const string StagingDirectoryName = "new";

    /// <summary>解压产物的落盘目录 / Where the extracted payload lands</summary>
    public static string StagingDir(string baseDir)
        => Path.Combine(Settings.AppPaths.UpdateDir(baseDir), StagingDirectoryName);

    /// <summary>记下这个版本已经下载完成、等待重启 / Records that this version is downloaded and awaits a restart</summary>
    public static bool Mark(string baseDir, string version)
        => Settings.AppStorage.SaveStagedUpdate(baseDir, version);

    /// <summary>清除记录（内容已被丢弃、或已经装上了）/ Clears the record (the content was discarded, or it has been installed)</summary>
    public static void Clear(string baseDir)
        => Settings.AppStorage.SaveStagedUpdate(baseDir, string.Empty);

    /// <summary>
    /// 启动时核对记录与磁盘，判断上一次下载的更新是否还能直接安装
    /// 内容缺失或版本已经装上都算不可恢复，并顺手清掉过期的记录，避免下次再走一遍同样的判断
    ///
    /// Checks the record against the disk at startup and decides whether the last download is still installable
    /// A missing payload or an already-installed version is not restorable, and the stale record is cleared
    /// on the way out so the next launch does not repeat the same check
    /// </summary>
    public static StagedRestoreResult Restore(string baseDir, string currentVersion)
    {
        var recorded = Settings.AppStorage.GetStagedUpdate(baseDir);
        if (string.IsNullOrEmpty(recorded))
        {
            return new StagedRestoreResult(StagedRestoreVerdict.None);
        }

        // 版本与当前一致 = 上次退出前更新其实已经装上了，记录已过期
        // 这里比“是否相同”而不是“是否更新”：记录只由本类写入，而写入发生在下载成功之后，因此不会记入更旧的版本
        //
        // A version equal to the running one means the update did get applied before the last exit: the record is stale
        // The comparison is equality rather than "is newer" because only this class writes the record,
        // and it writes after a successful download, so an older version never gets recorded
        if (string.Equals(recorded, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            Clear(baseDir);
            return new StagedRestoreResult(StagedRestoreVerdict.AlreadyInstalled, recorded);
        }

        // 记录说“已就绪”，但磁盘上那份解压结果才是凭据
        // 必需文件缺失说明上次解压没有完成（或用户手工清理过），此时恢复出来只会让“重启并更新”必然失败
        //
        // The record claims "ready", but the extracted files on disk are what count
        // A missing required file means the last extraction did not finish (or the user cleaned up by hand),
        // and restoring in that state would only make "restart and update" fail for certain
        foreach (var relative in UpdateStager.RequiredRelativePaths)
        {
            if (!File.Exists(Path.Combine(StagingDir(baseDir), relative)))
            {
                Clear(baseDir);
                return new StagedRestoreResult(StagedRestoreVerdict.Incomplete, recorded);
            }
        }

        return new StagedRestoreResult(StagedRestoreVerdict.Ready, recorded);
    }
}
