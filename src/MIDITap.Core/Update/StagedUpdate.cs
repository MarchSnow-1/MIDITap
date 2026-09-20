// StagedUpdate.cs — 记录并恢复"已经下载好、只差重启"的更新
//
// 为什么需要它：用户下载完更新后可能关掉窗口、甚至直接退出程序，暂时不重启
// 若只在内存里记住这件事，下次启动就只剩"重新下载几十 MB"这一条路
// 因此记录落到 .storage，启动时再核对磁盘上那份内容是否真的还在
//
// 为什么要连**形态**一起记：两条路线要核对的凭据不同
//   便携路线：解压出来的目录里必须有 MIDITap.exe 与 scripts/apply-update.ps1
//   安装器路线：那个 setup.exe 还在不在
// 只记版本号的话，重启后无从判断该核对哪一样
// 旧版本只写了版本号，因此形态缺失时按便携路线处理 —— 那是保守的一侧
//
// 为什么版本比较交给 semver 库：记录里写的是远端标签（v2.0.1，带 v）
// 而核对时传进来的是程序集版本（2.0.1，不带 v）
// 两者形式不同却指的是同一个版本，按文字比较不会相等
// 手写"去掉 v 再比"只在这一次这一种形状上成立，换个形状（带构建元数据、带预发布后缀）又会错
//
// 为什么这段判断在 Core 而不是界面：它是业务规则（版本比对、完整性核对），不是显示逻辑
// 放在这里可以在无桌面环境下测到
// 而界面层的对应代码只在正式构建里才走得到：开发构建根本没有更新流程，那个分支永远覆盖不到
//
// StagedUpdate.cs — records and restores an update that is downloaded and only awaits a restart
//
// Why it exists: a user may close the window after downloading, or quit the app outright, and restart later
// If that fact lived only in memory, the next launch would have nothing left but re-downloading tens of MB
// The record therefore goes to .storage, and startup checks that the content is still on disk
//
// Why the SHAPE is recorded too: the two routes check different evidence
//   The portable route: the extracted directory must hold MIDITap.exe and scripts/apply-update.ps1
//   The installer route: whether that setup.exe is still there
// With only a version number, a relaunch cannot tell which of the two to check
// An older version wrote only the version, so a missing shape means the portable route, which is the cautious side
//
// Why version comparison goes to the semver library: the record holds the remote tag (v2.0.1, with the v)
// while the check passes in the assembly version (2.0.1, without it)
// The two forms differ yet name the same version, so comparing them as text never matches
// A hand-written "strip the v and compare" would hold only for this one shape and break again on another
// (build metadata, a prerelease suffix)
//
// Why the decision lives in Core rather than the UI: it is a business rule (version comparison, completeness check),
// not display logic
// Here it can be tested without a desktop, whereas the UI-side equivalent is reachable only in a release build:
// a development build has no update flow at all, so that branch would never be covered

using Semver;

namespace MIDITap.Core.Update;

/// <summary>恢复暂存更新的结论 / The outcome of restoring a staged update</summary>
public enum StagedRestoreVerdict
{
    /// <summary>没有记录 / No record</summary>
    None,

    /// <summary>记录存在，但磁盘上没有完整的落地结果，记录已清除 / The record existed but the payload on disk was incomplete; it was cleared</summary>
    Incomplete,

    /// <summary>记录里的版本已经在运行，说明上次已经装上了，记录已清除 / The recorded version is already running, so it was applied; the record was cleared</summary>
    AlreadyInstalled,

    /// <summary>记录里的版本比在跑的更旧，这条记录已经没有意义，记录已清除 / The recorded version is older than the one running, so the record is meaningless and was cleared</summary>
    Superseded,

    /// <summary>可以恢复成待重启状态 / Restorable as an update awaiting a restart</summary>
    Ready,
}

/// <summary>
/// 恢复的结果：结论、记录中的版本号（没有记录时为 null），以及记录中的形态
/// 版本号在 Incomplete 时也要带出来，界面才能说清是哪个版本不完整
/// 形态供调用方决定接下来该走哪条落地路径
///
/// The result: the verdict, the recorded version (null when there is no record), and the recorded shape
/// The version is carried even for Incomplete, so the UI can name the version that turned out to be incomplete
/// The shape tells the caller which route to take next
/// </summary>
public sealed record StagedRestoreResult(
    StagedRestoreVerdict Verdict,
    string? Version = null,
    UpdatePackageKind Kind = UpdatePackageKind.Portable);

public static class StagedUpdate
{
    private const string StagingDirectoryName = "new";

    /// <summary>形态的存储写法：便携包 / The stored spelling for the portable shape</summary>
    public const string PortableKindValue = "portable";

    /// <summary>形态的存储写法：安装包 / The stored spelling for the installer shape</summary>
    public const string InstallerKindValue = "installer";

    /// <summary>解压产物的落盘目录 / Where the extracted payload lands</summary>
    public static string StagingDir(string baseDir)
        => Path.Combine(Settings.AppPaths.UpdateDir(baseDir), StagingDirectoryName);

    /// <summary>安装包的落盘位置（只有安装器路线会用到）/ Where the installer lands (only the installer route uses it)</summary>
    public static string InstallerPath(string baseDir)
        => Path.Combine(Settings.AppPaths.UpdateDir(baseDir), UpdateStager.InstallerFileName);

    /// <summary>记下这个版本已经下载完成、等待重启，以及走的是哪条路线 / Records that this version is downloaded and awaits a restart, and which route staged it</summary>
    public static bool Mark(string baseDir, string version, UpdatePackageKind kind)
        => Settings.AppStorage.SaveStagedUpdate(baseDir, version, KindValue(kind));

    /// <summary>清除记录（内容已被丢弃、或已经装上了）/ Clears the record (the content was discarded, or it has been installed)</summary>
    public static void Clear(string baseDir)
        => Settings.AppStorage.SaveStagedUpdate(baseDir, string.Empty, string.Empty);

    /// <summary>形态与存储写法之间的转换 / Translates between the shape and its stored spelling</summary>
    public static string KindValue(UpdatePackageKind kind)
        => kind == UpdatePackageKind.Installer ? InstallerKindValue : PortableKindValue;

    /// <summary>
    /// 把存储写法还原成形态
    /// 认不出来的一律按便携路线处理：那是保守的一侧，也覆盖了旧版本只写版本号的情况
    ///
    /// Turns the stored spelling back into the shape
    /// Anything unrecognised counts as the portable route: that is the cautious side, and it also covers an
    /// older version that recorded only the version number
    /// </summary>
    public static UpdatePackageKind ParseKind(string? value)
        => string.Equals(value, InstallerKindValue, StringComparison.OrdinalIgnoreCase)
            ? UpdatePackageKind.Installer
            : UpdatePackageKind.Portable;

    /// <summary>
    /// 这个结论是否意味着记录已经被丢弃
    /// 三条结论会走到 Clear：内容缺失、版本已经装上、记录比当前更旧
    /// 记录一没，它指向的那份内容就不可能再被用上 —— 恢复与安装都以记录为入口
    /// 调用方据此决定要不要连磁盘上的内容一起清掉，否则那份内容会永久留下
    ///   Incomplete 尤其要注意：记录与内容同时被清之后，下次启动返回的是 None
    ///   那条清理路径再也不会被走到，因此必须在这一次就清掉
    ///
    /// Whether this verdict means the record has been discarded
    /// Three of them reach Clear: a missing payload, an already-installed version, and a record older than the
    /// running one
    /// Once the record is gone the content it pointed at can never be used again, since both restoring and installing
    /// enter through that record
    /// The caller uses this to decide whether to clear that content from disk as well, or it stays behind for good
    /// Incomplete especially: once the record and the content are both cleared, the next launch returns None, so that
    /// cleanup path is never reached again and it has to happen on this pass
    /// </summary>
    public static bool RecordWasCleared(StagedRestoreVerdict verdict)
        => verdict is StagedRestoreVerdict.AlreadyInstalled
            or StagedRestoreVerdict.Superseded
            or StagedRestoreVerdict.Incomplete;

    /// <summary>
    /// 启动时核对记录与磁盘，判断上一次下载的更新是否还能直接安装
    /// 内容缺失、版本已经装上、或记录比当前更旧，都算不可恢复，并顺手清掉过期的记录，避免下次再走一遍同样的判断
    ///
    /// Checks the record against the disk at startup and decides whether the last download is still installable
    /// A missing payload, an already-installed version, or a record older than the running one is not restorable,
    /// and the stale record is cleared on the way out so the next launch does not repeat the same check
    /// </summary>
    public static StagedRestoreResult Restore(string baseDir, string currentVersion)
    {
        var recorded = Settings.AppStorage.GetStagedUpdate(baseDir);
        if (string.IsNullOrEmpty(recorded))
        {
            return new StagedRestoreResult(StagedRestoreVerdict.None);
        }

        // 形态在版本比对之前就读出来：后面每一条返回都要带上它
        //
        // The shape is read before the version comparison, because every return below carries it
        var kind = ParseKind(Settings.AppStorage.GetStagedUpdateKind(baseDir));

        // 与当前版本相同 = 上次退出前更新其实已经装上了，记录已过期
        // 按版本比而不是按文字比：记录里是远端标签（v2.0.1），传进来的是程序集版本（2.0.1）
        // 见文件头对这一处形式差的说明
        //
        // Equal to the running version means the update did get applied before the last exit: the record is stale
        // Compared as versions rather than as text: the record holds the remote tag (v2.0.1) while the argument is
        // the assembly version (2.0.1); the file header explains that difference in form
        if (IsSameVersion(recorded, currentVersion))
        {
            Clear(baseDir);
            return new StagedRestoreResult(StagedRestoreVerdict.AlreadyInstalled, recorded, kind);
        }

        // 比当前更旧 = 这条记录已经没有任何意义，清掉
        // 少了这一步，一条陈旧记录会一直留到用户装上新版本，随后被当成"可恢复的更新"，
        // 弹出一个把版本往回退的提示（当前 2.1.0 / 最新 2.0.0）
        //
        // Older than the running version means the record is meaningless and is cleared
        // Without this step a stale record survives until the user installs a newer version, and is then restored
        // as an update that would move the version backwards (current 2.1.0, latest 2.0.0)
        if (IsOlderThan(recorded, currentVersion))
        {
            Clear(baseDir);
            return new StagedRestoreResult(StagedRestoreVerdict.Superseded, recorded, kind);
        }

        // 记录说"已就绪"，但磁盘上的凭据才是真的
        // 凭据按形态而异：便携路线看解压出来的必需文件，安装器路线只看那个 setup.exe 还在不在
        // 凭据缺失说明上次没有完成（或用户手工清理过），此时恢复出来只会让"重启并更新"必然失败
        //
        // The record claims "ready", but what is on disk is what counts
        // The evidence depends on the shape: the portable route looks for the extracted required files, while the
        // installer route only asks whether that setup.exe is still there
        // Missing evidence means the last run did not finish (or the user cleaned up by hand), and restoring in
        // that state would only make "restart and update" fail for certain
        var present = kind == UpdatePackageKind.Installer
            ? File.Exists(InstallerPath(baseDir))
            : UpdateStager.RequiredRelativePaths
                .All(relative => File.Exists(Path.Combine(StagingDir(baseDir), relative)));

        if (!present)
        {
            Clear(baseDir);
            return new StagedRestoreResult(StagedRestoreVerdict.Incomplete, recorded, kind);
        }

        return new StagedRestoreResult(StagedRestoreVerdict.Ready, recorded, kind);
    }

    /// <summary>
    /// 记录里的版本与当前版本是否指的是同一个版本
    /// 两侧都能解析成 semver 时按版本比，解析不了时退回忽略大小写的文字相等
    /// 退回是有意的：一条读不懂的记录不该被当成"同一个版本"，那会把用户已经下好的更新包白白清掉
    ///
    /// Whether the recorded version and the running one name the same version
    /// Both sides are compared as versions when they parse as semver, falling back to a case-insensitive text comparison
    /// The fallback is deliberate: a record that cannot be read must not count as "the same version",
    /// since that would throw away a package the user has already downloaded
    /// </summary>
    private static bool IsSameVersion(string recorded, string currentVersion)
        => Compare(recorded, currentVersion) is { } order
            ? order == 0
            : string.Equals(recorded, currentVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 记录里的版本是否比当前更旧
    /// 解析不了时返回 false，继续按磁盘凭据走：那比凭一条读不懂的记录就清掉更新更保守
    ///
    /// Whether the recorded version is older than the running one
    /// False when either side cannot be parsed, so the disk evidence still decides
    /// That is more cautious than discarding the update over a record that cannot be read
    /// </summary>
    private static bool IsOlderThan(string recorded, string currentVersion)
        => Compare(recorded, currentVersion) is { } order && order < 0;

    /// <summary>
    /// 按 semver 比较两个版本，返回负/零/正表示先后，任一侧解析不了时返回 null
    /// 用本仓库既有的 SemVersion（见 UpdateChecker 的说明），不自行解析版本号
    ///
    /// Compares two versions as semver, returning a negative, zero or positive value; null when either side fails to parse
    /// It uses the SemVersion already present in this repository (see UpdateChecker) rather than parsing versions by hand
    /// </summary>
    private static int? Compare(string left, string right)
    {
        if (!SemVersion.TryParse(left, SemVersionStyles.Any, out var parsedLeft)
            || !SemVersion.TryParse(right, SemVersionStyles.Any, out var parsedRight))
        {
            return null;
        }

        return parsedLeft.ComparePrecedenceTo(parsedRight);
    }
}
