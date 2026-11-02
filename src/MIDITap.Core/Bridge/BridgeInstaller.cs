// BridgeInstaller.cs — MIDIBridge 扩展上游程序的「下载 -> 校验 -> 落地」整条链路
//
// 上游从 v1.3.1 起把产物改成**裸二进制**（见其 .github/workflows/build.yml 的 "Ship bare platform binaries instead of archives"）
// 因此这条路不再解压：下载到的文件本身就是可执行的 .exe
// zip 形态的发布不由本类处理：那种发布的清单缺 url/平台条目，在选择阶段就报"该发布没有可用产物"
//
// 为什么复用 Update 下的组件而不是自己再写一遍：
//   UpdateDownloader（流式下载 + .part + 取消 + 代理）与 UpdateChecksum（SHA-256）
//   这两件事在自更新里已经实现过，截断与坏包的处理就在这里复用
//   UpdateExtractor 不再需要——裸二进制没有归档可解，也就没有 zip-slip 这个面
//
// 为什么无摘要就拒绝：
//   落地之后这个文件会被 MIDITap 直接执行（BridgeService 启动它）
//   运行一个校验不了的文件，与解压一个校验不了的 zip 不是同一量级的风险
//   上游清单里每条产物都带 sha256，缺失说明清单本身不对，因此拒绝而不是放行
//
// 下载目录为什么在 AppPaths.UpdateDir 下的临时区：.update 是更新流程的工作区，从不含用户数据
// 上游二进制十几 MB，因此**没有**照搬自更新的「复用本地已校验包」那条路：
// 那条路要按摘要先核对本地文件，省下的是一次几百 MB 的下载；这里省下的十几 MB，不值得多一条分支
//
// 为什么落地时统一改名为 midibridge-client.exe：
//   上游资产名带版本与平台（MIDIBridge-Client-<tag>-windows-amd64.exe），名字随版本浮动
//   运行时定位（BridgeLayout.ExePath）只认约定名，所以落地前统一改成它
//   名字本身定义在 AppPaths.BridgeExecutableFileName，本类与 BridgeLayout 消费同一个常量
//
// 代理同样生效：下载经由 UpdateDownloader，代理在那里按 UpdateOptions.BuildProxy 构造
// 本文件不做自己的网络配置
//
// BridgeInstaller.cs — the whole "download -> verify -> land" chain for the extension's upstream program
//
// From v1.3.1 the upstream ships BARE BINARIES (see its build.yml: "Ship bare platform binaries instead of archives")
// This path therefore extracts nothing: the downloaded file is itself the executable
// A release shipped as a zip is not handled here; such a release carries no url or platform entry in the manifest,
// so it is rejected at the selection stage as "no build for this platform"
//
// Why the Update components are reused rather than written again:
//   UpdateDownloader (streamed download, .part, cancellation, proxy) and UpdateChecksum (SHA-256)
//   Both already exist for the self-updater, and the truncation and corrupt-file handling is reused here
//   UpdateExtractor is no longer needed: a bare binary has no archive, and therefore no zip-slip surface
//
// Why a missing digest is refused outright:
//   After landing, MIDITap executes this file itself (BridgeService starts it)
//   Running an unverifiable file is not the same order of risk as extracting an unverifiable zip
//   Every artifact in the upstream manifest carries a sha256, so its absence means the manifest itself is wrong, which is
//   refused rather than waved through
//
// Why the download lands in a temp area under AppPaths.UpdateDir: .update is the update work area and holds no user data
// The upstream binary is tens of MB, so the self-updater's "reuse a verified local package" path is NOT carried over:
// that path verifies the local file against the digest first, which pays off for a multi-hundred-MB download but buys
// tens of MB here — not worth a branch of its own
//
// Why the landed file is renamed to midibridge-client.exe:
//   The upstream asset name carries a version and platform (MIDIBridge-Client-<tag>-windows-amd64.exe) and floats with
//   the tag. Runtime lookup (BridgeLayout.ExePath) only knows the agreed name, so it is renamed before landing
//   The name itself lives in AppPaths.BridgeExecutableFileName, and both this class and BridgeLayout consume it
//
// The proxy applies too: the download goes through UpdateDownloader, which builds it from UpdateOptions.BuildProxy
// This file does no networking of its own

using MIDITap.Core.Settings;
using MIDITap.Core.Update;

namespace MIDITap.Core.Bridge;

/// <summary>安装失败的原因 / Why the install failed</summary>
public enum BridgeInstallFailure
{
    /// <summary>成功 / Success</summary>
    None,

    /// <summary>
    /// 上游资产没带摘要：落地后要执行该文件，因此拒绝
    ///
    /// The upstream asset carries no digest: the file would be executed after landing, so it is refused
    /// </summary>
    NoDigest,

    /// <summary>下载失败（网络、404、中断） / The download failed (network, 404, interruption)</summary>
    Download,

    /// <summary>
    /// 校验不匹配 —— 文件不可信，已丢弃
    ///
    /// Verification mismatch — the file is untrusted and has been discarded
    /// </summary>
    ChecksumMismatch,

    /// <summary>
    /// 落地失败：上游 exe 正在运行（被锁定）或磁盘不可写，旧文件因此保持原样
    ///
    /// Landing failed: the upstream exe is running (locked) or the disk is not writable, so the previous file stays as it was
    /// </summary>
    ReplaceFailed,

    /// <summary>用户取消 / Cancelled by the user</summary>
    Cancelled,

    /// <summary>意外异常 / An unexpected exception</summary>
    Unexpected,
}

/// <summary>
/// 一次安装的结论；<see cref="Detail"/> 供日志排查，界面按 <see cref="Failure"/> 取本地化文案
/// <see cref="Version"/> 是落地该产物的清单版本（例如 v1.3.1），供界面直接显示而无需再探测
///
/// The outcome of one install; <see cref="Detail"/> is for the log, the UI localises from <see cref="Failure"/>
/// <see cref="Version"/> is the manifest version that supplied the artifact (for example v1.3.1), so the UI can show it
/// without probing the binary again
/// </summary>
public sealed record BridgeInstallResult(
    bool Ok,
    BridgeInstallFailure Failure = BridgeInstallFailure.None,
    ChecksumResult? Checksum = null,
    string? Detail = null,
    string? Version = null);

public static class BridgeInstaller
{
    /// <summary>
    /// .update 下的工作子目录名：下载中的文件放这里，与自更新链路的文件隔开
    ///
    /// The work sub-directory under .update holding the file being downloaded, kept apart from the
    /// self-update chain's files
    /// </summary>
    public const string WorkDirectoryName = "bridge";

    /// <summary>
    /// 下载中的文件在工作目录里的名字
    /// 扩展名保持 .exe：上游产物本身就是可执行文件，按原名保留便于排查时直接看文件头
    ///
    /// The name of the file being downloaded inside the work directory
    /// The .exe extension is kept: the upstream artifact is itself an executable, and keeping the real name makes the
    /// file header readable during diagnosis
    /// </summary>
    public const string DownloadFileName = "bridge-client.exe";

    /// <summary>落地时使用的约定文件名，取 AppPaths 的定义 / The agreed landing name, from AppPaths</summary>
    private static string ClientExecutableName => AppPaths.BridgeExecutableFileName;

    /// <summary>
    /// 下载清单条目指向的裸二进制 -> 校验 -> 改名落地
    /// 失败时磁盘上不留下"看起来可用"的文件：校验不符删文件、落地失败时旧文件保持原样
    ///
    /// Downloads the bare binary a manifest entry points at, verifies it, then renames it onto the final path
    /// A failure leaves nothing that "looks usable" behind: a verification mismatch deletes the file, and a failed
    /// rename leaves the previous file untouched
    /// </summary>
    /// <param name="artifact">清单里选中的那一条 / The selected manifest entry</param>
    /// <param name="manifestVersion">该清单的版本标识，写入结果供界面显示 / The manifest's version, recorded for the UI</param>
    /// <param name="downloadUrl">实际下载地址：优先用条目自带的 url / The address to download from, preferring the entry's own url</param>
    public static async Task<BridgeInstallResult> InstallAsync(
        BridgeManifestArtifact artifact,
        string manifestVersion,
        string downloadUrl,
        UpdateOptions options,
        string baseDir,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workDir = Path.Combine(AppPaths.UpdateDir(baseDir), WorkDirectoryName);
        var downloadPath = Path.Combine(workDir, DownloadFileName);

        try
        {
            // ---- 0) 无摘要就不下载：下完也无法校验，而落地后要执行它 ----
            //
            // ---- 0) Without a digest nothing is downloaded: it could not be verified, and it would be executed ----
            if (artifact.Sha256 is null)
            {
                Console.Error.WriteLine(
                    "[miditap.bridge]: No digest for the bridge artifact; refusing to install it.");
                return new BridgeInstallResult(
                    false, BridgeInstallFailure.NoDigest, null, "no digest to verify against", manifestVersion);
            }

            Directory.CreateDirectory(workDir);

            // ---- 1) 下载（代理在 UpdateDownloader 里按 options.BuildProxy 生效） ----
            //
            // ---- 1) Download (the proxy is applied inside UpdateDownloader via options.BuildProxy) ----
            DeleteQuietly(downloadPath + ".part");
            var downloaded = await UpdateDownloader
                .DownloadAsync(downloadUrl, downloadPath, options, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!downloaded)
            {
                return new BridgeInstallResult(false, BridgeInstallFailure.Download, null, null, manifestVersion);
            }

            // ---- 2) 校验（先于落地：坏文件不允许先出现在运行路径上） ----
            //
            // ---- 2) Verify (before landing: a bad file must not reach the run path first) ----
            var checksum = UpdateChecksum.VerifyDigest(downloadPath, artifact.Sha256);
            if (!checksum.CanInstall)
            {
                Console.Error.WriteLine(
                    "[miditap.bridge]: Checksum " + checksum.Verdict + "; discarding the artifact.");
                DeleteQuietly(downloadPath);
                DeleteQuietly(downloadPath + ".part");
                return new BridgeInstallResult(
                    false,
                    BridgeInstallFailure.ChecksumMismatch,
                    checksum,
                    "expected=" + checksum.Expected + " actual=" + checksum.Actual,
                    manifestVersion);
            }

            // 清单还带了 size：摘要对得上时它只是补充信息，对不上说明清单与实际文件不同源
            // 这里不用它做判定（摘要已经足够强），只在日志里留一条，便于事后判断清单与资产是否同版本
            //
            // The manifest also carries a size: with the digest matching it is supplementary, and a mismatch would mean
            // the manifest and the file come from different sources
            // It is not used as a decision (the digest is already strong), only logged, so afterwards one can tell whether
            // the manifest and the asset were the same version
            if (artifact.Size > 0 && new FileInfo(downloadPath).Length != artifact.Size)
            {
                Console.Error.WriteLine(
                    "[miditap.bridge]: Artifact size differs from the manifest; the digest still matched.");
            }

            // ---- 3) 改名落地 ----
            //
            // ---- 3) Rename and land ----
            var targetPath = Path.Combine(AppPaths.BridgeDir(baseDir), ClientExecutableName);
            try
            {
                Directory.CreateDirectory(AppPaths.BridgeDir(baseDir));
                // overwrite: true 而不是"先删再移"：目标被占用（上游正在运行）时替换失败、旧文件保持原样，
                // 不会出现"旧的已删、新的没落地"的空档
                //
                // overwrite: true instead of "delete then move": with the target held (the upstream is running) the
                // replace fails and the previous file stays, so there is no gap where the old one is gone and the new
                // one has not landed
                File.Move(downloadPath, targetPath, overwrite: true);
            }
            catch (Exception err)
            {
                Console.Error.WriteLine(
                    "[miditap.bridge]: Could not put the client executable in place: " + err.Message);
                return new BridgeInstallResult(
                    false, BridgeInstallFailure.ReplaceFailed, checksum, err.Message, manifestVersion);
            }

            // 记下自己落地的文件名：启动时按这个名字找，目录里被放进别的 exe 也不改变启动目标
            // 写失败不改变安装结果：记录只用来让下次启动更确定，缺了它仍会回落到约定名
            //
            // Records the file name landed here: starting looks it up by this name, so another exe dropped into the
            // directory does not change the start target
            // A failed write does not change the outcome: the record only makes the next start deterministic, and
            // without it the conventional name still applies
            if (!AppStorage.SaveBridgeExecutableName(baseDir, ClientExecutableName))
            {
                Console.Error.WriteLine(
                    "[miditap.bridge]: Could not record the installed executable name.");
            }

            // 落地成功后清掉工作目录里剩下的东西（下载失败时留着供排查）
            //
            // Once landed, the rest of the work area is cleared (a failed download keeps it for diagnosis)
            DeleteQuietly(downloadPath + ".part");
            return new BridgeInstallResult(true, BridgeInstallFailure.None, checksum, null, manifestVersion);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(downloadPath + ".part");
            return new BridgeInstallResult(false, BridgeInstallFailure.Cancelled, null, null, manifestVersion);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.bridge]: Bridge install failed: " + err.Message);
            DeleteQuietly(downloadPath + ".part");
            return new BridgeInstallResult(
                false, BridgeInstallFailure.Unexpected, null, err.Message, manifestVersion);
        }
    }

    /// <summary>
    /// 读取清单 JSON 文本到解析结果；网络由调用方负责，本方法只做解析
    ///
    /// Reads manifest JSON text into a parse result; the caller owns the network, this method only parses
    /// </summary>
    public static BridgeManifestParseResult ParseManifest(string? json) => BridgeManifestParser.Parse(json);

    /// <summary>
    /// 从一份清单里取出本平台可安装的产物
    /// 返回 null 表示该发布没有可用条目（平台缺失、dev 构建没有 url、或清单本身不完整）
    /// 这里把"能不能装"的判断集中成一处，界面与更新检查都调用它，不各自组合条件
    ///
    /// Picks the installable artifact for this platform out of a manifest
    /// Null means the release has no usable entry (missing platform, a dev build with no url, or an incomplete manifest)
    /// The "can this be installed" decision lives here alone, called by both the UI and the update check rather than
    /// recomposed at each site
    /// </summary>
    public static BridgeManifestArtifact? SelectInstallable(BridgeManifest manifest, BridgePlatform platform)
    {
        if (!manifest.IsRelease)
        {
            // dev 构建的产物只存在于 Actions run，没有稳定地址，装不了
            //
            // Dev builds exist only as Actions run artifacts with no stable address, so they cannot be installed
            return null;
        }

        var artifact = BridgePlatformMatcher.Select(manifest.Artifacts, platform);
        if (artifact is null)
        {
            return null;
        }

        // url 为空时按 tag + 文件名兜底：清单里 url 与 filename 属于同一条目，
        // 拼出来的地址与 url 指向同一个资源
        //
        // A blank url falls back to tag plus filename: url and filename belong to the same entry, so the rebuilt address
        // points at the same resource
        if (string.IsNullOrWhiteSpace(artifact.Url))
        {
            return artifact with { Url = BridgeRepository.ArtifactUrl(manifest.Version, artifact.FileName) };
        }
        return artifact;
    }

    /// <summary>
    /// 解析 exe 自带版本之外的另一条来源：清单里声明的版本
    /// 本方法只做语义化整理，不判定兼容性
    ///
    /// Turns the version the manifest declares into a normalised form
    /// This method only normalises; it judges no compatibility
    /// </summary>
    public static string? NormaliseManifestVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }
        var text = version.Trim();
        return text.StartsWith('v') ? text[1..] : text;
    }

    /// <summary>
    /// 从清单 JSON 里取出版本号，供"只想知道最新版是多少"的场景使用
    /// 解析失败返回 null，调用方按"未知"处理
    ///
    /// Extracts the version from manifest JSON for callers that only ask "what is the latest version"
    /// Null on a parse failure, which the caller treats as unknown
    /// </summary>
    public static string? VersionFromManifestJson(string? json)
        => NormaliseManifestVersion(BridgeManifestParser.Parse(json).Manifest?.Version);

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
            // 清理失败不改变结果 / A failed cleanup does not change the outcome
        }
    }

}
