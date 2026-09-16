// UpdateStager.cs — "下载 -> 校验 -> 解压暂存"整条链路
// 为什么放在 Core：它包含**校验和验证**这一安全关键步骤
// 放在 Core 后，本地服务器 + 真实 zip 就能把整条链路（含失败分支）测到
//
// The whole "download -> verify -> extract into staging" chain
// Why this lives in Core: it contains the checksum verification step
// That step is security-critical
// In Core, a local server plus a real zip exercises the entire chain including its failure branches

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
    /// 下载 -> 校验 -> 解压到 <paramref name="stagingDirectory"/>
    /// <paramref name="packagePath"/> 是包在暂存区旁的落盘位置（失败时会被清掉）
    ///
    /// Download, verify, then extract into the staging directory
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
            // 每次都从干净的暂存区开始
            // "混装的更新"是最难排查的坏状态
            //
            // Every run starts from a clean staging directory
            // A half-mixed update is the hardest bad state to diagnose
            CleanStaging(stagingDirectory, packagePath);

            // ---- 1) 下载 / Download ----
            var downloaded = await UpdateDownloader
                .DownloadAsync(asset.DownloadUrl, packagePath, options, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!downloaded)
            {
                return new StageResult(false, StageFailure.Download);
            }

            // ---- 2) 校验（下载之后、解压之前）/ Verify (after download, before extract) ----
            // 顺序很关键：**先校验再解压**
            // 若先解压，坏包的内容已经落到磁盘上，而随后的安装步骤只看得见"文件都在"，校验就形同虚设
            // 摘要来自资产信息本身（API 的 digest，或网页路径 expanded_assets 里的同一值）
            // 因此**不需要额外下载校验和文件**，也不会多一次可能失败的请求
            //
            // Order matters: verify BEFORE extracting
            // Extracting first would put the bad package's contents on disk
            // The install step would then only see "the files are there", making verification pointless
            // The digest comes with the asset information (the API's digest, or the web path's expanded_assets)
            // There is therefore no separate checksum download and no extra request that could fail
            var checksum = UpdateChecksum.VerifyDigest(packagePath, asset.Sha256);

            if (!checksum.CanInstall)
            {
                Console.Error.WriteLine(
                    "[miditap.updater]: Checksum " + checksum.Verdict + "; discarding the package.");
                // 丢弃坏包：绝不能留下一个"看起来可用"的文件等着被安装
                //
                // The bad package is discarded
                // A file that looks usable must never be left behind for installation
                DeleteQuietly(packagePath);
                return new StageResult(
                    false,
                    StageFailure.ChecksumMismatch,
                    checksum,
                    "expected=" + checksum.Expected + " actual=" + checksum.Actual);
            }

            // ---- 3) 解压 / Extract ----
            var extracted = UpdateExtractor.Extract(packagePath, stagingDirectory);
            if (!extracted.Ok)
            {
                return new StageResult(false, StageFailure.Extract, checksum, extracted.Error);
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
                    false, StageFailure.MissingRequiredContent, checksum, string.Join(", ", missing));
            }

            return new StageResult(true, StageFailure.None, checksum, null, extracted.FilesWritten);
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

    /// <summary>清掉暂存目录与已下载的包 / Clears the staging directory and the downloaded package</summary>
    public static void CleanStaging(string stagingDirectory, string packagePath)
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
        DeleteQuietly(packagePath);
        DeleteQuietly(packagePath + ".part");
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
