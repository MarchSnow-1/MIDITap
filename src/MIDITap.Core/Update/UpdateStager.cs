// UpdateStager.cs — "下载 -> 校验 -> 落地暂存"整条链路
// 为什么放在 Core：它包含**校验和验证**这一安全关键步骤
// 放在 Core 后，本地服务器 + 真实文件就能把整条链路（含失败分支）测到
//
// 两条落地的路子
//   便携路线：解压到暂存目录，随后由辅助脚本逐文件替换
//   安装器路线：不解压，只确认那个 exe 确实是本应用的安装包，随后静默运行它
// 两条路共用"下载 + 校验"这一段，因此它被抽成一个私有步骤
//
// The whole "download -> verify -> stage" chain
// Why this lives in Core: it contains the checksum verification step
// That step is security-critical
// In Core, a local server plus real files exercises the entire chain including its failure branches
//
// Two ways to stage
//   The portable route extracts into the staging directory, and the helper script then replaces files one by one
//   The installer route does not extract: it confirms the exe really is this app's installer, and runs it silently
// Both share the download and verification step, so that step is factored out

using System.Net.Http;

namespace MIDITap.Core.Update;

/// <summary>暂存结果 / The staging result</summary>
public sealed record StageResult(
    bool Ok,
    StageFailure Failure = StageFailure.None,
    ChecksumResult? Checksum = null,
    string? Detail = null,
    int FilesWritten = 0);

/// <summary>暂存失败的原因 / Why staging failed</summary>
public enum StageFailure
{
    None,
    /// <summary>下载失败（网络、404、中断） / The download failed (network, 404, interruption)</summary>
    Download,
    /// <summary>
    /// **校验和不匹配 —— 包不可信，已丢弃。**
    ///
    /// **Checksum mismatch — the package is untrusted and has been discarded.**
    /// </summary>
    ChecksumMismatch,
    /// <summary>
    /// 解压失败（坏包、zip-slip、无法写入）
    ///
    /// Extraction failed (corrupt archive, zip-slip, cannot write)
    /// </summary>
    Extract,
    /// <summary>
    /// 解压成功，但内容**不是本应用的构建**（缺少必需文件）
    /// 这种情况必须拒绝安装：否则辅助脚本会把应用目录替换成一堆无关文件
    /// 结果是"更新完应用再也起不来"
    /// 远端最新发布是旧技术栈的 v1.7.1（Node.js 版，含 package.json / resources.neu，没有 scripts/apply-update.ps1）
    ///
    /// Extraction succeeded but the contents are NOT a build of this app (required files missing)
    /// Installing that would let the helper replace the app directory with unrelated files
    /// The app would then be unable to start
    /// The newest published release was v1.7.1, the old Node.js build
    /// That build carries package.json / resources.neu, and no scripts/apply-update.ps1
    /// </summary>
    MissingRequiredContent,
    /// <summary>
    /// 下载到的 exe **没有通过身份校验**，因此拒绝运行
    /// 两种触发方式
    ///   * 版本资源不是本应用（见 InstallerIdentity）
    ///   * 资产信息里没有摘要，无从校验，而运行一个未校验的可执行文件比解压一个未校验的 zip 危险得多
    ///
    /// The downloaded exe FAILED its identity check, so it is refused
    /// Two ways to reach it
    ///   The version resource is not this app's (see InstallerIdentity)
    ///   The asset carries no digest, so nothing can be verified, and running an unverified executable is far
    ///   riskier than extracting an unverified archive
    /// </summary>
    NotOurInstaller,
    /// <summary>用户取消 / Cancelled by the user</summary>
    Cancelled,
}

public static class UpdateStager
{
    /// <summary>
    /// 解压后必须存在的文件（相对路径）
    /// 这两个缺任何一个，安装下去都不会有好结果
    ///   * MIDITap.exe —— 没有它就不是本应用
    ///   * scripts/apply-update.ps1 —— 没有它，用户点「重启并更新」后辅助脚本起不来，应用退出却没人接手替换，表现为"点了更新，程序就没了"
    ///
    /// Files that must exist after extraction. Missing either makes installation useless
    /// Without the executable it is not this app
    /// Without the helper script, "restart & update" leaves the app exited with nobody to do the replacing
    /// The user then clicks update and the program simply vanishes
    /// </summary>
    /// <summary>
    /// 辅助脚本在包内的相对路径
    /// 应用侧定位脚本时（UpdateService.LaunchApplyAndExit）也用它，免得同一个路径在两处各写一遍、改名时漏掉一处
    ///
    /// Relative path of the helper script inside the package
    /// The app side uses it too when it locates the script (UpdateService.LaunchApplyAndExit)
    /// The path is therefore not written out twice and then missed in one place when it changes
    /// </summary>
    public static readonly string ApplyUpdateScriptRelativePath =
        Path.Combine("scripts", "apply-update.ps1");

    public static IReadOnlyList<string> RequiredRelativePaths { get; } =
        ["MIDITap.exe", ApplyUpdateScriptRelativePath];

    /// <summary>
    /// 安装包在更新工作目录里的文件名
    /// 与便携路线的 package.zip 分开命名，读日志时一眼能看出走的是哪条路
    ///
    /// The file name of the installer inside the update work directory
    /// Kept apart from the portable route's package.zip so a log line says at a glance which route ran
    /// </summary>
    public const string InstallerFileName = "setup.exe";

    /// <summary>
    /// 便携路线：下载 -> 校验 -> 解压到 <paramref name="stagingDirectory"/>
    /// <paramref name="packagePath"/> 是包在暂存区旁的落盘位置（失败时会被清掉）
    ///
    /// The portable route: download, verify, then extract into the staging directory
    /// </summary>
    public static async Task<StageResult> PrepareAsync(
        UpdateAsset asset,
        UpdateOptions options,
        string stagingDirectory,
        string packagePath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 解压出来的内容每次都从干净状态开始
            // "混装的更新"是最难排查的坏状态
            // 但已下载的包**保留**，它可能是上一轮中断时留下的、仍然完好的那一份
            // 是否复用它由下面按摘要决定，摘要对不上就删掉
            //
            // The extracted content always starts clean
            // A half-mixed update is the hardest bad state to diagnose
            // The downloaded package is KEPT though, since it may be a still-intact one left by an interrupted round
            // Whether it is reused is decided by the shared step below, and a mismatch deletes it
            ClearExtractedContent(stagingDirectory);

            var acquired = await AcquireAsync(asset, options, packagePath, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!acquired.Ok)
            {
                return acquired;
            }

            // ---- 3) 解压 / Extract ----
            var extracted = UpdateExtractor.Extract(packagePath, stagingDirectory);
            if (!extracted.Ok)
            {
                return new StageResult(false, StageFailure.Extract, acquired.Checksum, extracted.Error);
            }

            // ---- 4) 内容校验：解压出来的必须**确实是本应用** / Content check: what was extracted must really be this app ----
            // 解压正常、文件齐全，但里面根本没有 MIDITap.exe 与自更新脚本
            // 若照此安装，辅助脚本会把应用目录替换成一堆无关文件，应用再也起不来
            // 摘要校验管不到这种情况 —— 包的摘要是对的，只是它不是我们的包
            //
            // Content check: what was extracted must actually BE this app
            // The newest release was the old Node.js v1.7.1, which extracted fine and was complete
            // It contained no MIDITap.exe and no self-update script, so installing it would leave the app unable to start
            // A checksum cannot catch this: the digest is correct, the package simply is not ours
            var missing = RequiredRelativePaths
                .Where(relative => !File.Exists(Path.Combine(stagingDirectory, relative)))
                .ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine(
                    "[miditap.updater]: Staged content is not a MIDITap build; missing: " +
                    string.Join(", ", missing));
                CleanStaging(stagingDirectory, packagePath);
                return new StageResult(
                    false, StageFailure.MissingRequiredContent, acquired.Checksum, string.Join(", ", missing));
            }

            return new StageResult(true, StageFailure.None, acquired.Checksum, null, extracted.FilesWritten);
        }
        catch (OperationCanceledException)
        {
            CleanStaging(stagingDirectory, packagePath);
            return new StageResult(false, StageFailure.Cancelled);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.updater]: Staging failed: " + err.Message);
            return new StageResult(false, StageFailure.Extract, null, err.Message);
        }
    }

    /// <summary>
    /// 安装器路线：下载 -> 校验 -> **身份校验**，不解压
    /// 落地到 <paramref name="installerPath"/>，随后由界面层静默运行它
    ///
    /// 为什么必须有摘要：这一步之后要**执行**那个文件
    /// 便携路线在摘要缺失时仍允许安装（历史发布没有摘要文件），因为最坏情况是解压出一堆无关文件，应用起不来但不会执行任何东西
    /// 运行一个来路不明的可执行文件不是同一量级的风险，因此这里更严
    ///
    /// The installer route: download, verify, then check the file's identity, with no extraction
    /// It lands at <paramref name="installerPath"/> and the UI layer later runs it silently
    ///
    /// Why a digest is mandatory here: the step after this one EXECUTES the file
    /// The portable route still allows installation without a digest (historical releases carry none), because
    /// the worst case there is extracting unrelated files: the app fails to start, but nothing is executed
    /// Running an unverified executable is not the same order of risk, so this route is stricter
    ///
    /// <paramref name="verifyInstaller"/> 默认是 InstallerIdentity.Verify，测试可注入替身（理由见方法内注释）
    /// <paramref name="verifyInstaller"/> defaults to InstallerIdentity.Verify; tests may inject a stand-in,
    /// as the comment inside explains
    /// </summary>
    public static async Task<StageResult> PrepareInstallerAsync(
        UpdateAsset asset,
        UpdateOptions options,
        string installerPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, InstallerIdentityResult>? verifyInstaller = null)
    {
        try
        {
            // 没有摘要就不下载：几十 MB 下完也无法校验，白费流量
            //
            // Without a digest nothing is downloaded: tens of MB would arrive unverifiable, wasting the transfer
            if (asset.Sha256 is null)
            {
                Console.Error.WriteLine(
                    "[miditap.updater]: No digest for the installer asset; refusing to run it.");
                return new StageResult(false, StageFailure.NotOurInstaller, null, "no digest to verify against");
            }

            var acquired = await AcquireAsync(asset, options, installerPath, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!acquired.Ok)
            {
                return acquired;
            }

            // 校验器可注入：一份能通过身份校验的**真实**安装包有几十 MB，不能作为测试数据
            // 与 MiniHttpServer（网络）和 UpdateEndpoints（地址）是同一类做法
            // 身份判定本身的规则由 InstallerIdentityTests 单独覆盖
            //
            // The verifier is injectable: a REAL installer that passes the identity check is tens of MB, so it
            // cannot serve as test data
            // This is the same kind of seam as MiniHttpServer for the network and UpdateEndpoints for addresses
            // The identity rules themselves are covered separately by InstallerIdentityTests
            var verify = verifyInstaller ?? InstallerIdentity.Verify;
            var identity = verify(installerPath);
            if (!identity.Ok)
            {
                Console.Error.WriteLine(
                    "[miditap.updater]: The downloaded exe is not our installer: " + identity.Reason);
                // 拒绝之后必须删掉：留一个"看起来可用"的可执行文件等着被运行，正是这一步要防的事
                //
                // The file is deleted after a refusal: leaving a plausible-looking executable for someone to run
                // later is exactly what this step guards against
                DeleteQuietly(installerPath);
                return new StageResult(false, StageFailure.NotOurInstaller, acquired.Checksum, identity.Reason);
            }

            return new StageResult(true, StageFailure.None, acquired.Checksum);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(installerPath);
            return new StageResult(false, StageFailure.Cancelled);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.updater]: Installer staging failed: " + err.Message);
            return new StageResult(false, StageFailure.Extract, null, err.Message);
        }
    }

    /// <summary>
    /// 两条路共用的第一步：复用本地已有的包，否则下载，然后校验
    /// 返回 Ok=false 时调用方直接把它交出去即可
    ///
    /// The shared first step: reuse the package already on disk, otherwise download, then verify
    /// When Ok=false the caller simply hands the result back
    /// </summary>
    private static async Task<StageResult> AcquireAsync(
        UpdateAsset asset,
        UpdateOptions options,
        string packagePath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        DeleteQuietly(packagePath + ".part");

        // ---- 0) 本地已有的包能否直接复用 / Can the package already on disk be reused -------
        // 先看本地是否已经有一份完好的包，能省掉一次几十 MB 的下载
        // 判断依据只有摘要：**没有摘要时无从校验**，那份文件就不能信任
        // 注意 ChecksumVerdict.NotProvided 的 CanInstall 是 true（历史版本没有摘要文件）
        // 那是"允许安装"，不等于"可以复用本地文件"
        // 复用必须先能校验，因此这里单独判断 Sha256 是否存在
        //
        // Check whether an intact package is already on disk, which saves a download of tens of MB
        // The digest is the only basis: without one the file cannot be verified and must not be trusted
        // Note that CanInstall is true for ChecksumVerdict.NotProvided (historical releases carry no digest)
        // That means "installation is allowed", not "a local file may be reused"
        // Reuse requires verification, so the presence of Sha256 is checked separately here
        var reused = false;
        if (asset.Sha256 is not null && File.Exists(packagePath))
        {
            var local = UpdateChecksum.VerifyDigest(packagePath, asset.Sha256);
            if (local.Verdict == ChecksumVerdict.Match)
            {
                reused = true;
                Console.Error.WriteLine(
                    "[miditap.updater]: Reusing the package already on disk; the digest matches.");
                // 复用时不经过下载，进度在这里补一次，否则界面会停在 0%
                //
                // The download is skipped, so progress is reported here
                // Without it the UI would sit at 0%
                progress?.Report(new DownloadProgress(asset.Size, asset.Size));
            }
            else
            {
                Console.Error.WriteLine(
                    "[miditap.updater]: Discarding the package already on disk; digest " + local.Verdict + ".");
                DeleteQuietly(packagePath);
            }
        }

        // ---- 1) 下载 / Download ----
        if (!reused)
        {
            var downloaded = await UpdateDownloader
                .DownloadAsync(asset.DownloadUrl, packagePath, options, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!downloaded)
            {
                return new StageResult(false, StageFailure.Download);
            }
        }

        // ---- 2) 校验（下载之后、落地之前）/ Verify (after download, before anything else) ----
        // 顺序很关键：**先校验再解压，先校验再运行**
        // 若先解压，坏包的内容已经落到磁盘上，而随后的安装步骤只看得见"文件都在"，校验就形同虚设
        // 摘要来自资产信息本身（API 的 digest，或网页路径 expanded_assets 里的同一值）
        // 因此**不需要额外下载校验和文件**，也不会多一次可能失败的请求
        //
        // Order matters: verify BEFORE extracting, and before running anything
        // Extracting first would put the bad package's contents on disk
        // The install step would then only see "the files are there", making verification pointless
        // The digest comes with the asset information (the API's digest, or the web path's expanded_assets)
        // There is therefore no separate checksum download and no extra request that could fail
        var checksum = UpdateChecksum.VerifyDigest(packagePath, asset.Sha256);

        if (!checksum.CanInstall)
        {
            Console.Error.WriteLine(
                "[miditap.updater]: Checksum " + checksum.Verdict + "; discarding the package.");
            // 丢弃坏包：绝不能留下一个"看起来可用"的文件等着被安装或运行
            //
            // The bad package is discarded
            // A file that looks usable must never be left behind for installation or execution
            DeleteQuietly(packagePath);
            return new StageResult(
                false,
                StageFailure.ChecksumMismatch,
                checksum,
                "expected=" + checksum.Expected + " actual=" + checksum.Actual);
        }

        return new StageResult(true, StageFailure.None, checksum);
    }

    /// <summary>清掉暂存目录与已下载的包 / Clears the staging directory and the downloaded package</summary>
    public static void CleanStaging(string stagingDirectory, string packagePath)
    {
        ClearExtractedContent(stagingDirectory);
        DeleteQuietly(packagePath);
        DeleteQuietly(packagePath + ".part");
    }

    /// <summary>
    /// 只清解压出来的内容，保留已下载的包
    /// 下载前用它而不是 CleanStaging：那份包可能就是上一轮留下的、仍然完好的那一份
    ///
    /// Clears only the extracted content and keeps the downloaded package
    /// Used before a download instead of CleanStaging, because that package may be a still-intact one from an earlier round
    /// </summary>
    private static void ClearExtractedContent(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch
        {
            // 清理失败：下一轮会重建目录
            //
            // A failed cleanup is fine: the next run rebuilds the directory
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略 / ignore
        }
    }
}
