// BridgeUpdateChecker.cs — MIDIBridge 扩展的「已装版本探测 + 上游最新发布检查」
//
// 与 UpdateChecker 的分工：网络与解析复用 UpdateChecker.ReadLatestReleaseAsync
// （请求头、8 秒超时、403 配额头、tag_name/body/assets[].digest 字段解析都在那里）
// 本文件只做扩展功能自己的事：资产筛选用 BridgeAssetSelector（上游命名），比较对象是已装的上游版本
// 网页回退走 Update.ReleasePageChecker（302 取标签 + expanded_assets 取资产），它按 URL 参数化，两个仓库共用
// expanded_assets 挑不到资产时，用 ReleaseAssetProbe.BuildUrl 拼下载地址，只重写"上游候选名 + HEAD"这一小段
//
// 检查只读、安装才落盘，因此本文件不触碰正在运行的上游进程：替换动作由界面在上游进程停止后发起，
// 被锁定的 exe 由 BridgeInstaller 返回 ReplaceFailed，旧文件保持原样
//
// 已装版本取 `midibridge-client -v` 的输出而不读 exe 版本资源：上游是 Go 程序，版本由构建时 -ldflags 注入，
// Windows 版本资源里没有该值
// 上游 main.go 在解析参数后、初始化日志与加载配置之前处理 -v 并退出，因此探测不产生 data/config.json、
// 不写日志、不连接服务器；输出形如 "midibridge-client <version>"，本地 `go build` 的 dev 构建输出 "midibridge-client dev"
//
// 协议版本用 `--ipc-protocol` 单独探测（v1.3.1 起）：它只打印协议版本单行，取值不依赖行序
// 程序版本回答"要不要升级"，协议版本回答"能不能对接"，两者由不同函数分开探测
//
// BridgeUpdateChecker.cs — the extension's installed-version probe and upstream latest-release check
//
// Its division of labour with UpdateChecker: networking and parsing reuse UpdateChecker.ReadLatestReleaseAsync
// (request headers, the 8-second timeout, the 403 quota headers, and the tag_name/body/assets[].digest field parsing
// all live there)
// This file does only the extension's own work: asset selection via BridgeAssetSelector (upstream naming), comparing
// against the installed upstream version
// The web fallback walks Update.ReleasePageChecker (a 302 for the tag plus expanded_assets for the assets), which is
// parameterised by URL, so both repositories share it
// When expanded_assets yields no asset, ReleaseAssetProbe.BuildUrl builds the download URL and only the "upstream
// candidate name plus HEAD" part is rewritten here
//
// Checking only reads and installing writes to disk, so this file never touches a running upstream process: the UI
// starts the replacement once the upstream has stopped, and BridgeInstaller returns ReplaceFailed for a locked exe,
// leaving the previous file in place
//
// The installed version comes from `midibridge-client -v` rather than the exe's version resource: the upstream is a Go
// program whose version is injected at build time via -ldflags, and the Windows version resource does not carry it
// The upstream main.go handles -v after argument parsing and before initialising logging or loading the config, then
// exits, so a probe generates no data/config.json, writes no log and connects to no server; the output reads
// "midibridge-client <version>", and a dev build from a local `go build` prints "midibridge-client dev"
//
// The protocol version is probed separately with `--ipc-protocol` (from v1.3.1): it prints the protocol version alone
// on one line, so the value does not depend on line order
// The program version answers "should this be upgraded" while the protocol answers "can the two sides talk", and two
// functions probe them independently

using System.Diagnostics;
using System.Net.Http;
using MIDITap.Core.Update;
using Semver;

namespace MIDITap.Core.Bridge;

/// <summary>
/// 一次清单检查的结论
/// <see cref="Info"/> 为 null 表示清单取不到或解析失败，此时 <see cref="Detail"/> 说明原因
/// <see cref="NotComparable"/> 表示检查成功但给不出"要不要更新"的结论（已装版本是 dev 或解析不出）
///
/// The outcome of one manifest check
/// A null <see cref="Info"/> means the manifest could not be fetched or parsed, and <see cref="Detail"/> says why
/// <see cref="NotComparable"/> means the check succeeded but no update verdict is possible (the installed version is dev or unparsable)
/// </summary>
public sealed record BridgeManifestCheckOutcome(
    BridgeManifestCheckInfo? Info,
    bool Ok,
    bool UpdateAvailable = false,
    bool NotComparable = false,
    string? Detail = null)
{
    /// <summary>远端版本标识（清单原样，例如 v1.3.1） / The remote version as the manifest spells it, for example v1.3.1</summary>
    public string? LatestVersion => Info?.Manifest.Version;

    /// <summary>本平台可安装的产物；null 表示该发布没有本平台的构建 / This platform's artifact; null when the release has no build for it</summary>
    public BridgeManifestArtifact? Artifact => Info?.Artifact;
}

/// <summary>
/// 清单检查成功时的内容：整份清单加上已经挑好的本平台产物
/// 把"挑好的产物"一并交出去，界面就不必再重复一遍平台匹配
///
/// What a successful manifest check yields: the whole manifest plus this platform's artifact already selected
/// Handing over the selected artifact keeps the UI from repeating the platform match
/// </summary>
public sealed record BridgeManifestCheckInfo(BridgeManifest Manifest, BridgeManifestArtifact? Artifact)
{
    /// <summary>
    /// 协议是否与本构建兼容：只看主版本
    /// 主版本相同即视为可对接（上游文档 §6：同主版本向下兼容）；不同则不兼容
    /// 解析不出主版本时 schema 为 0，与任何真实主版本都不等，因此按不兼容处理，是安全的一侧
    ///
    /// Whether the protocol is compatible with this build, judged by the major alone
    /// An equal major counts as compatible (upstream documentation §6: the same major is backward compatible); otherwise
    /// it is incompatible. An unparsable major yields schema 0, which equals no real major and therefore counts as
    /// incompatible, the safe side
    /// </summary>
    public bool ProtocolCompatible => Manifest.IpcProtocolSchema == BridgeProtocolContract.ExpectedSchema;
}

/// <summary>
/// 本扩展这一侧能对接的协议契约
/// <see cref="ExpectedSchema"/> 是 IPC 协议的主版本：1 对应协议文档的 1.0.0
/// 主版本变化意味着握手帧或帧头可能已变，因此整条消费链路都要按新协议重新验证
///
/// The protocol contract this extension side can talk
/// <see cref="ExpectedSchema"/> is the IPC protocol's major: 1 corresponds to protocol 1.0.0
/// A changed major means the handshake or the frame header may have changed, so the whole consumption chain needs
/// re-validating against the new protocol
/// </summary>
public static class BridgeProtocolContract
{
    /// <summary>本构建实现的协议主版本 / The protocol major this build implements</summary>
    public const int ExpectedSchema = 1;
}

/// <summary>
/// 已装版本探测的结论；<see cref="Version"/> 是输出里的原样 token（含 v 前缀或 dev），供界面直接展示
///
/// The installed-version probe verdict; <see cref="Version"/> is the token as printed (v prefix or dev included),
/// ready for the UI to display
/// </summary>
/// <param name="Version">解析出的版本 token；无输出或格式不符为 null / The parsed version token; null when there is no output or the format does not match</param>
/// <param name="Parsed">是否解析出可比较的发布版本号；dev 构建为 false / Whether a comparable release version was parsed; false for dev builds</param>
/// <param name="Detail">未解析成功时的原因，供日志 / Why parsing failed, for the log</param>
public sealed record BridgeInstalledVersion(string? Version, bool Parsed, string? Detail)
{
    /// <summary>能否参与"要不要更新"的比较 / Whether this value can take part in an update comparison</summary>
    public bool Comparable => Parsed;
}

/// <summary>
/// 已装上游的协议版本探测结论；<see cref="Protocol"/> 是输出原样 token（例如 1.0.0），供界面直接展示
/// <see cref="Schema"/> 是主版本的整数形式，解析不出时为 0
/// 0 与任何真实主版本都不相等，因此 <see cref="BridgeCompatibilityPolicy"/> 会把它判为"未知"而不是"不支持"
///
/// The protocol-version probe verdict for the installed upstream; <see cref="Protocol"/> is the token as printed
/// (1.0.0 for example), ready for the UI to display. <see cref="Schema"/> is the major as an integer, or 0 when
/// it cannot be parsed. Zero equals no real major, so <see cref="BridgeCompatibilityPolicy"/> reads it as
/// unknown rather than unsupported
/// </summary>
/// <param name="Protocol">解析出的协议版本 token；无输出或格式不符为 null / The parsed protocol token; null when there is no output or the format does not match</param>
/// <param name="Parsed">是否解析出可用的协议版本 / Whether a usable protocol version was parsed</param>
/// <param name="Detail">未解析成功时的原因，供日志 / Why parsing failed, for the log</param>
public sealed record BridgeInstalledProtocol(string? Protocol, bool Parsed, string? Detail)
{
    /// <summary>能否参与"能不能对接"的判断 / Whether this value can take part in a compatibility verdict</summary>
    public bool Comparable => Parsed;

    /// <summary>
    /// 协议主版本的整数形式；未解析成功时为 0
    /// 复用 BridgeManifestParser.MajorOf，与清单侧的主版本语义保持一处定义
    ///
    /// The protocol major as an integer, or 0 when nothing was parsed
    /// BridgeManifestParser.MajorOf is reused so the manifest side and this side share one definition
    /// </summary>
    public int Schema => Parsed ? BridgeManifestParser.MajorOf(Protocol) : 0;
}

/// <summary>
/// 扩展功能更新检查使用的三个端点；端点可覆盖，测试注入本地服务器，从而**确定性地**验证回退顺序
///
/// The three endpoints of the extension's update check; they are overridable so tests inject local servers and
/// verify the fallback order DETERMINISTICALLY
/// </summary>
public sealed record BridgeUpdateEndpoints(
    string ApiUrl,
    string LatestPageUrl,
    string ReleasesPageBase)
{
    /// <summary>真实端点（来自 BridgeRepository）/ The real endpoints, from BridgeRepository</summary>
    public static readonly BridgeUpdateEndpoints Default =
        new(BridgeRepository.LatestReleaseApiUrl, BridgeRepository.LatestReleaseUrl, BridgeRepository.ReleasesBase);
}

/// <summary>
/// 扩展功能的"有更新"信息
/// 与 UpdateInfo 的差异：没有发布页面 URL 之外的 MIDITap 概念，资产按上游命名挑选
/// Asset 可为 null（资产列表没拿到，或该发布没有 Windows 构建），界面据此退回"打开 Releases 页面"
///
/// The extension's "an update exists" information
/// Differences from UpdateInfo: none of MIDITap's own concepts beyond the release page URL, and the asset is picked
/// by upstream naming. Asset may be null (the asset list was unavailable, or the release has no Windows build), and
/// the UI then falls back to opening the Releases page
/// </summary>
public sealed record BridgeUpdateInfo(
    string Latest,
    UpdateAsset? Asset,
    string? Notes,
    bool ViaFallback,
    RateLimitInfo? RateLimit)
{
    /// <summary>
    /// 资产是否带摘要。没有摘要时 BridgeInstaller 会拒绝安装（落地后要执行该文件），界面可据此提示
    ///
    /// Whether the asset carries a digest. Without one BridgeInstaller refuses the install (the file would be
    /// executed after landing), and the UI can explain that up front
    /// </summary>
    public bool DigestAvailable => Asset?.Sha256 is not null;
}

/// <summary>
/// 一次扩展更新检查的结论
/// NotComparable=true 表示检查成功但给不出结论（已装版本是 dev 或解析不出），界面按"版本未知"展示
///
/// The verdict of one extension update check
/// NotComparable=true means the check succeeded but has no verdict (the installed version is dev or does not parse),
/// and the UI presents that as "version unknown"
/// </summary>
public sealed record BridgeUpdateCheckOutcome(
    BridgeUpdateInfo? Info,
    bool Ok,
    UpdateFailure Failure = UpdateFailure.None,
    int HttpStatus = 0,
    string? Detail = null,
    RateLimitInfo? RateLimit = null,
    bool NotComparable = false);

public static class BridgeUpdateChecker
{
    /// <summary>
    /// 探测子进程的超时。-v 应当瞬间退出；给它 10 秒是留出杀毒软件扫描 exe 的余量，
    /// 超时后杀掉子进程并按"不可比较"处理，而不是无限等
    ///
    /// The probe subprocess timeout. -v should exit instantly; ten seconds leaves room for antivirus scanning the exe,
    /// and on timeout the subprocess is killed and the result treated as not comparable, rather than blocking the caller
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>上游 -v 输出的程序名 / The program name in the upstream -v output</summary>
    public const string ProbeProgramName = "midibridge-client";

    /// <summary>
    /// 运行 <paramref name="exePath"/> -v 并解析出版本
    /// 不抛异常：exe 缺失、启动失败、超时、输出不符都返回 Parsed=false
    /// 两侧 stdout/stderr 都要重定向并读空：只排一侧会让管道缓冲占满从而卡住子进程
    /// （与 BridgeService 消费上游输出时是同一条约束）
    ///
    /// Runs <paramref name="exePath"/> with -v and parses the version
    /// Nothing is thrown: a missing exe, a failed start, a timeout and a mismatched output all return Parsed=false
    /// Both stdout and stderr are redirected and drained: draining only one side lets the pipe buffer fill up and
    /// stall the subprocess (the same constraint BridgeService has while consuming the upstream's output)
    /// </summary>
    public static async Task<BridgeInstalledVersion> InstalledVersionAsync(
        string exePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return new BridgeInstalledVersion(null, false, "the executable is missing");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                // -v 与 --version 上游都认；用短形式，输出按它自己的约定解析
                //
                // The upstream accepts -v and --version alike; the short form is used and the output is parsed by its
                // own convention
                Arguments = "-v",
                UseShellExecute = false,
                CreateNoWindow = true,
                // 工作目录锚到 exe 所在目录：-v 不读配置（在加载配置前就退出），
                // 锚定使子进程的输出不依赖调用方的工作目录
                //
                // The working directory is anchored to the exe's own directory; -v reads no config (it exits before
                // that), and anchoring keeps the subprocess output independent of the caller's working directory
                WorkingDirectory = Path.GetDirectoryName(exePath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时或用户取消：把子进程杀掉再走"不可比较"，避免留下一个挂着的进程
                //
                // Timeout or user cancellation: the subprocess is killed before reporting not comparable, so no hung
                // process is left behind
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程已退出时 Kill 会抛，忽略 / Kill throws when the process has already exited; ignored
                }
                return new BridgeInstalledVersion(
                    null, false, cancellationToken.IsCancellationRequested
                        ? "the version probe was cancelled"
                        : "the version probe timed out");
            }

            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new BridgeInstalledVersion(
                    null, false, "the version probe exited with code " + process.ExitCode);
            }
            return ParseVersionOutput(output);
        }
        catch (Exception err)
        {
            // 启动失败（权限、被拦截）等：探测失败不算安装损坏，界面按"版本未知"展示
            //
            // A failed start (permissions, blocked by policy) and the like: a failed probe does not mean a broken
            // install, and the UI shows "version unknown"
            return new BridgeInstalledVersion(null, false, err.Message);
        }
    }

    /// <summary>
    /// 解析 -v 的标准输出（纯函数，独立出来供测试）
    /// 取**第一个非空行**：v1.3.1 起 -v 打印两行（第二行是 "ipc-protocol 1.0.0"），第一行是程序名加版本
    /// 按行取首个非空行既可容忍尾部空行，也只依赖第一行
    /// 期望形如 "midibridge-client v1.3.0"；token 数不符或程序名不对都按解析失败处理
    /// "dev"（大小写不限）解析为版本字符串但 Parsed=false，即"可展示、不可比较"
    ///
    /// Parses the -v standard output (a pure function, split out for tests)
    /// The FIRST non-empty line is taken: from v1.3.1 on, -v prints two lines (the second is "ipc-protocol 1.0.0"),
    /// and the first is the program name plus version
    /// Taking the first non-empty line tolerates trailing blank lines and depends on the first line alone. The
    /// expected shape is "midibridge-client v1.3.0"; a different token count or program name counts as a parse failure
    /// "dev" (case-insensitive) parses into the version string with Parsed=false, i.e. displayable but not comparable
    /// </summary>
    public static BridgeInstalledVersion ParseVersionOutput(string? output)
    {
        var line = output?
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (line is null)
        {
            return new BridgeInstalledVersion(null, false, "the probe produced no output");
        }

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 2
            || !string.Equals(tokens[0], ProbeProgramName, StringComparison.OrdinalIgnoreCase))
        {
            return new BridgeInstalledVersion(
                null, false, "the probe output does not match the expected format: " + line);
        }

        var version = tokens[1];
        return string.Equals(version, "dev", StringComparison.OrdinalIgnoreCase)
            ? new BridgeInstalledVersion(version, false, "a development build reports no release version")
            : new BridgeInstalledVersion(version, true, null);
    }

    /// <summary>
    /// 运行 <paramref name="exePath"/> --ipc-protocol 并解析出协议版本
    ///
    /// 为什么用 --ipc-protocol 而不是读 -v 的第二行：
    ///   上游 v1.3.1 起 -v 打印两行，第二行是 "ipc-protocol 1.0.0"，解析它要求该行存在且前缀不变
    ///   --ipc-protocol 只打印协议版本单行（上游 main.go 的约定），取值不依赖行序
    ///   两者读的是同一个常量 IPCOProtoVersion，因此给出同一个协议版本
    ///
    /// 与 InstalledVersionAsync 同样的边界：不抛异常，两侧管道都排空，超时后杀掉子进程
    ///
    /// Runs <paramref name="exePath"/> with --ipc-protocol and parses the protocol version
    ///
    /// Why --ipc-protocol rather than the second line of -v:
    ///   From upstream v1.3.1, -v prints two lines whose second is "ipc-protocol 1.0.0"; parsing it requires that
    ///   line to exist with its prefix unchanged
    ///   --ipc-protocol prints the protocol version alone on one line (the upstream main.go convention), so the value
    ///   does not depend on line order
    ///   Both read the same IPCOProtoVersion constant and therefore report the same protocol version
    ///
    /// The same boundaries as InstalledVersionAsync: nothing is thrown, both pipes are drained, and the subprocess is
    /// killed after a timeout
    /// </summary>
    public static async Task<BridgeInstalledProtocol> ProbeProtocolAsync(
        string exePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return new BridgeInstalledProtocol(null, false, "the executable is missing");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                // --ipc-protocol 与 -v 一样在上游加载配置之前就处理并退出，因此探测不产生副作用
                //
                // Like -v, --ipc-protocol is handled before the upstream loads its config and then exits, so the probe
                // has no side effect
                Arguments = "--ipc-protocol",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时或用户取消：先杀掉子进程，避免留下一个挂着的进程
                //
                // Timeout or user cancellation: the subprocess is killed first, so no hung process is left behind
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程已退出时 Kill 会抛，忽略 / Kill throws when the process has already exited; ignored
                }
                return new BridgeInstalledProtocol(
                    null, false, cancellationToken.IsCancellationRequested
                        ? "the protocol probe was cancelled"
                        : "the protocol probe timed out");
            }

            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new BridgeInstalledProtocol(
                    null, false, "the protocol probe exited with code " + process.ExitCode);
            }
            return ParseProtocolOutput(output);
        }
        catch (Exception err)
        {
            // 启动失败（权限、被拦截）等：探测失败不影响已装版本的可用性，界面按"协议未知"展示
            //
            // A failed start (permissions, blocked by policy) and the like: a failed probe does not affect the
            // installed version's usability, and the UI shows "protocol unknown"
            return new BridgeInstalledProtocol(null, false, err.Message);
        }
    }

    /// <summary>
    /// 解析 --ipc-protocol 的标准输出（纯函数，独立出来供测试）
    /// 取**第一个非空行**，因此前后空行与首尾空白都不影响结果
    /// 期望是一段语义化版本号（例如 1.0.0）：主版本解析不出来就按失败处理，
    /// 因为"读到了字符串但读不出主版本"与"没读到"对兼容判断是同一个结论，分开报告只会让调用方多一个分支
    /// dev 构建的 --ipc-protocol 仍打印真实的协议常量（上游两者取自同一个 IPCOProtoVersion），
    /// 因此这里**不**把 dev 当特例：它是否能解析取决于拿到的字符串本身
    ///
    /// Parses the --ipc-protocol standard output (a pure function, split out for tests)
    /// The FIRST non-empty line is taken, so leading and trailing blank lines and surrounding whitespace change nothing
    /// A semantic version (1.0.0 for example) is expected: a value whose major cannot be parsed counts as a failure,
    /// because "a string arrived but its major cannot be read" and "nothing arrived" lead to the same compatibility
    /// verdict, and reporting them apart would only give the caller another branch
    /// A dev build still prints the real protocol constant for --ipc-protocol (the upstream takes both from one
    /// IPCOProtoVersion), so dev is NOT special-cased here: whether it parses depends on the string itself
    /// </summary>
    public static BridgeInstalledProtocol ParseProtocolOutput(string? output)
    {
        var line = output?
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (line is null)
        {
            return new BridgeInstalledProtocol(null, false, "the protocol probe produced no output");
        }

        // 上游用 fmt.Println(IPCOProtoVersion) 打印，因此合法输出是单个 token
        // 多个 token 说明这不是那一行（例如有人把 -v 的多行输出误喂进来），按格式不符处理
        //
        // The upstream prints with fmt.Println(IPCOProtoVersion), so a valid output is a single token
        // Several tokens mean this is not that line (feeding -v's multi-line output in by mistake, for instance) and
        // count as a format mismatch
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 1)
        {
            return new BridgeInstalledProtocol(
                null, false, "the protocol probe output is not a single token: " + line);
        }

        // 主版本决定能否对接，读不出主版本就没有可用结论
        // 这里不校验次版本与补丁号：上游文档 §6 只承诺同主版本向下兼容
        //
        // The major decides whether the two sides can talk, and without it there is no usable verdict
        // Minor and patch are not validated, since §6 of the upstream documentation promises compatibility within one
        // major alone
        var protocol = tokens[0];
        if (BridgeManifestParser.MajorOf(protocol) <= 0)
        {
            return new BridgeInstalledProtocol(
                null, false, "the protocol probe output carries no major version: " + protocol);
        }
        return new BridgeInstalledProtocol(protocol, true, null);
    }

    /// <summary>检查上游是否有新版本（默认端点：BridgeRepository）/ Checks the upstream for a newer version (default endpoints)</summary>
    public static Task<BridgeUpdateCheckOutcome> CheckAsync(
        string? installedVersion, UpdateOptions options, CancellationToken cancellationToken = default,
        bool forInstall = false)
        => CheckAsync(installedVersion, options, BridgeUpdateEndpoints.Default, cancellationToken, forInstall);

    /// <summary>
    /// 带端点覆盖的检查（供测试注入本地服务器）
    /// 顺序：API 优先 → 任何 API 失败回退网页 → 两条路都失败上报 API 的原因
    /// 已装版本不可比较时返回成功 + NotComparable=true，**不触发**回退：
    /// API 已经给出答案，回退只该为"拿不到答案"服务
    ///
    /// <paramref name="forInstall"/> 为 true 时不与已装版本比较：安装要的是"这个发布的本平台产物"，
    /// 装没装过、装的是 dev 还是旧版都与选产物无关；此时 <paramref name="installedVersion"/> 可以传 null
    ///
    /// The check with endpoint overrides (tests inject local servers)
    /// Order: API first, any API failure falls back to the web page, and a double failure reports the API's reason
    /// A not-comparable installed version returns success with NotComparable=true and does NOT trigger the fallback:
    /// the API already produced an answer, and the fallback exists for "no answer could be obtained"
    ///
    /// With <paramref name="forInstall"/> set, the installed version is not compared: installing wants this release's
    /// artifact for this platform, and whether an older build or a dev build is already there does not enter that
    /// choice; <paramref name="installedVersion"/> may be null then
    /// </summary>
    public static async Task<BridgeUpdateCheckOutcome> CheckAsync(
        string? installedVersion, UpdateOptions options, BridgeUpdateEndpoints endpoints,
        CancellationToken cancellationToken = default, bool forInstall = false)
    {
        // 1) API 优先 / API first
        var read = await UpdateChecker
            .ReadLatestReleaseAsync(endpoints.ApiUrl, options, cancellationToken)
            .ConfigureAwait(false);
        if (read.Ok)
        {
            return Judge(
                read.Tag, read.Assets, installedVersion, viaFallback: false, read.Notes, read.RateLimit,
                forInstall);
        }

        // 2) API 失败 -> 网页路径 / API failed -> the web page
        var viaPage = await CheckViaReleasePageAsync(
            endpoints, options, read.RateLimit, installedVersion, cancellationToken, forInstall).ConfigureAwait(false);
        if (viaPage is not null)
        {
            Console.Error.WriteLine(
                "[miditap.bridge]: API failed (" + read.Failure + "); succeeded via the release page.");
            return viaPage;
        }

        // 3) 两条路都失败 -> 上报 API 的原因（带 HTTP 状态码与限流重置时间，比网页路径的异常更具说明性）
        //
        // 3) Both paths failed -> report the API's reason, which carries the HTTP status and the rate-limit reset
        //    time and is more informative than the web path's exception
        return new BridgeUpdateCheckOutcome(
            null, false, read.Failure, read.HttpStatus, read.Detail, read.RateLimit);
    }

    /// <summary>
    /// 优先读发布清单的检查
    ///
    /// 为什么清单优先，而不是继续走 Releases API：
    ///   1) 一次请求给出全部所需：版本、平台产物、大小与 SHA-256，不必再"查 API 再猜文件名"
    ///   2) 走的是 releases/latest/download 这个**下载**端点，不消耗 api.github.com 的配额
    ///   3) 清单是上游的结构化契约（见其 docs/version-manifest_zh-CN.md），字段含义有文档可依
    ///   4) 清单里的 ipcProtocol / ipcProtocolSchema 直接回答了"能不能对接"，这是 API 路径给不出的
    ///
    /// 清单取不到时**不**在本方法内回退到 API：回退由调用方决定，因为两者失败语义不同
    /// 本方法返回 Ok=false + 原因，调用方据此决定提示还是回退
    ///
    /// The check that reads the release manifest first
    ///
    /// Why the manifest comes first instead of the Releases API:
    ///   1) One request yields everything needed: the version, the platform artifact, its size and SHA-256, removing the
    ///      "query the API then guess the file name" steps
    ///   2) It uses the releases/latest/download DOWNLOAD endpoint, which spends no api.github.com quota
    ///   3) The manifest is a structured contract the upstream commits to (see its docs/version-manifest_zh-CN.md),
    ///      with documented field meanings
    ///   4) Its ipcProtocol / ipcProtocolSchema answer "can the two sides talk", which the API path cannot
    ///
    /// A manifest that cannot be fetched does NOT fall back to the API inside this method: the fallback belongs to the
    /// caller, since the two failures read differently
    /// This method returns Ok=false with a reason, and the caller decides between a message and a fallback
    /// </summary>
    public static Task<BridgeManifestCheckOutcome> CheckManifestAsync(
        string? installedVersion, UpdateOptions options, CancellationToken cancellationToken = default,
        bool forInstall = false)
        => CheckManifestAsync(
            installedVersion, options, BridgeRepository.LatestManifestUrl, cancellationToken, forInstall);

    /// <summary>
    /// 带清单地址覆盖的检查（供测试注入本地服务器）
    /// 端点可覆盖，使回退与解析能在本地服务器上测到，不必访问公网
    ///
    /// <paramref name="forInstall"/> 为 true 时只挑产物、不与已装版本比较：
    /// 首次安装时磁盘上还没有上游，已装版本读出来是 unknown，比较只会得出"不可比较"，
    /// 而"这个发布有没有本平台的产物"与装没装过无关
    ///
    /// The check with an overridable manifest address (tests inject a local server)
    /// The endpoint is overridable so the fallback and the parsing are testable against a local server without
    /// touching the public internet
    ///
    /// With <paramref name="forInstall"/> set, only the artifact is picked and the installed version is not compared:
    /// a first install has no upstream on disk, so the installed version reads as unknown and the comparison can only
    /// yield "not comparable"; whether this release carries a build for this platform does not depend on what is
    /// already installed
    /// </summary>
    public static async Task<BridgeManifestCheckOutcome> CheckManifestAsync(
        string? installedVersion, UpdateOptions options, string manifestUrl,
        CancellationToken cancellationToken = default, bool forInstall = false)
    {
        var fetch = await FetchManifestAsync(manifestUrl, options, cancellationToken).ConfigureAwait(false);
        if (fetch.Manifest is null)
        {
            return new BridgeManifestCheckOutcome(null, Ok: false, Detail: fetch.Error);
        }

        var manifest = fetch.Manifest;

        // 平台产物缺失（平台不在该发布里、或 dev 构建没有稳定地址）时仍算检查成功：
        // 远端确实有一个版本，只是这个平台装不了。把它与"网络失败"混为一类会让界面说错话
        //
        // A missing platform artifact (the platform is absent from that release, or a dev build has no stable address)
        // still counts as a successful check: the remote does have a version, this platform simply cannot install it
        // Folding that into "network failure" would make the UI say the wrong thing
        var artifact = BridgeInstaller.SelectInstallable(manifest, BridgePlatform.WindowsAmd64);

        if (forInstall)
        {
            return new BridgeManifestCheckOutcome(
                new BridgeManifestCheckInfo(manifest, artifact), Ok: true, UpdateAvailable: true);
        }

        var comparison = CompareLatest(manifest.Version, installedVersion);
        if (comparison.NotComparable)
        {
            return new BridgeManifestCheckOutcome(
                new BridgeManifestCheckInfo(manifest, artifact), Ok: true, NotComparable: true,
                Detail: comparison.Detail);
        }
        if (comparison.Precedence <= 0)
        {
            // 已是最新：仍然把清单交出去，界面可用它显示远端版本与协议版本
            //
            // Already up to date: the manifest is still handed over so the UI can show the remote version and protocol
            return new BridgeManifestCheckOutcome(
                new BridgeManifestCheckInfo(manifest, artifact), Ok: true, UpdateAvailable: false);
        }
        return new BridgeManifestCheckOutcome(
            new BridgeManifestCheckInfo(manifest, artifact), Ok: true, UpdateAvailable: true);
    }

    /// <summary>
    /// 取清单文本并解析
    /// 体积上限在读取时判：先按上限截断读取，避免"下载一个巨大文件再解析"
    /// 这里只负责网络，解析交给 BridgeManifestParser
    ///
    /// Fetches the manifest text and parses it
    /// The size bound is enforced while reading, so "download a huge file then parse it" cannot happen
    /// This method owns the network alone and hands parsing to BridgeManifestParser
    /// </summary>
    public static async Task<BridgeManifestParseResult> FetchManifestAsync(
        string manifestUrl, UpdateOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            using var handler = new HttpClientHandler();
            var proxy = options.BuildProxy();
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = ManifestTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
            request.Headers.UserAgent.ParseAdd("MIDITap-updater/1.0");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new BridgeManifestParseResult(
                    null, "the manifest request returned status " + (int)response.StatusCode);
            }

            await using var stream = await response
                .Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var buffer = new char[BridgeManifestParser.MaxManifestBytes];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read >= buffer.Length)
            {
                return new BridgeManifestParseResult(null, "the manifest is larger than the accepted bound");
            }
            return BridgeManifestParser.Parse(new string(buffer, 0, read));
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.bridge]: Manifest fetch failed: " + err.Message);
            return new BridgeManifestParseResult(null, err.Message);
        }
    }

    /// <summary>清单请求的超时。与检查同量级：清单只有几 KB / The manifest timeout; the file is only a few KB</summary>
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 对"最新 tag + 资产列表 + 已装版本"给出结论
    /// 比较用 Core 已依赖的 Semver 库（SemVersionStyles.Any 接受带 v 前缀的真实 tag），不手写规则
    /// 判为"不可比较"（tag 或已装版本解析不出）时返回成功结果而不是失败：这与 UpdateChecker 的既有契约一致
    /// —— 版本解析失败走"无更新"路径，不算检查失败
    ///
    /// Reaches a verdict for "the latest tag plus the asset list plus the installed version"
    /// Comparison uses the Semver library Core already depends on (SemVersionStyles.Any accepts real tags with the v
    /// prefix), with no hand-written rules. A NOT-COMPARABLE verdict (the tag or the installed version does not parse)
    /// returns a successful result rather than a failure, matching UpdateChecker's existing contract: an unparsable
    /// version takes the "no update" path and does not count as a failed check
    /// </summary>
    private static BridgeUpdateCheckOutcome Judge(
        string? tag,
        IReadOnlyList<UpdateAsset> assets,
        string? installedVersion,
        bool viaFallback,
        string? notes,
        RateLimitInfo? rateLimit,
        bool forInstall = false)
    {
        // 安装不比较版本：远端有这个 tag 就够了，产物挑不挑得到是另一回事
        //
        // Installing does not compare versions: the tag existing is enough, and whether an artifact is found is a
        // separate matter
        if (forInstall)
        {
            return tag is null
                ? new BridgeUpdateCheckOutcome(
                    null, Ok: true, Detail: "the release carries no tag", RateLimit: rateLimit, NotComparable: true)
                : new BridgeUpdateCheckOutcome(
                    new BridgeUpdateInfo(
                        tag, BridgeAssetSelector.SelectFromApiAssets(assets), notes, viaFallback, rateLimit),
                    Ok: true,
                    RateLimit: rateLimit);
        }

        var comparison = CompareLatest(tag, installedVersion);
        if (comparison.NotComparable)
        {
            return new BridgeUpdateCheckOutcome(
                null, Ok: true, Detail: comparison.Detail, RateLimit: rateLimit, NotComparable: true);
        }
        if (comparison.Precedence <= 0)
        {
            // 已是最新 / Already up to date
            return new BridgeUpdateCheckOutcome(null, Ok: true, RateLimit: rateLimit);
        }
        return new BridgeUpdateCheckOutcome(
            new BridgeUpdateInfo(tag!, BridgeAssetSelector.SelectFromApiAssets(assets), notes, viaFallback, rateLimit),
            Ok: true,
            RateLimit: rateLimit);
    }

    /// <summary>
    /// 比较的公共部分，网页路径与 API 路径共用，保证两条路的判定一致
    ///
    /// The shared part of the comparison, used by both the web and the API path, so the two routes judge alike
    /// </summary>
    private static (int Precedence, bool NotComparable, string? Detail) CompareLatest(
        string? tag, string? installedVersion)
    {
        if (tag is null)
        {
            return (0, true, "the release carries no tag");
        }
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return (0, true, "the installed version is unknown");
        }
        if (!SemVersion.TryParse(tag, SemVersionStyles.Any, out var latest)
            || !SemVersion.TryParse(installedVersion, SemVersionStyles.Any, out var current))
        {
            return (0, true, "either version does not parse as semver");
        }
        return (latest.ComparePrecedenceTo(current), false, null);
    }

    /// <summary>
    /// 网页回退：302 取标签，仍有可能有更新时才取 expanded_assets（省一次请求）
    /// 返回 null 表示网页路径也失败（调用方按 API 的原因上报）
    ///
    /// The web fallback: the 302 yields the tag, and expanded_assets is fetched only when an update is still possible
    /// (saving one request). Null means the web path failed too (the caller reports the API's reason)
    /// </summary>
    private static async Task<BridgeUpdateCheckOutcome?> CheckViaReleasePageAsync(
        BridgeUpdateEndpoints endpoints,
        UpdateOptions options,
        RateLimitInfo? rateLimit,
        string? installedVersion,
        CancellationToken cancellationToken,
        bool forInstall = false)
    {
        try
        {
            var tag = await ReleasePageChecker
                .GetLatestTagAsync(endpoints.LatestPageUrl, options, cancellationToken)
                .ConfigureAwait(false);
            if (tag is null)
            {
                return null;
            }

            // 与 API 路径同一套比较：不可比较或已是最新都不必再取资产列表
            // 安装时跳过比较，标签到手就去取资产列表
            //
            // The same comparison as the API path: neither a not-comparable verdict nor "up to date" needs the asset
            // list fetched
            // Installing skips the comparison, so the asset list is fetched as soon as the tag is in hand
            if (!forInstall)
            {
                var comparison = CompareLatest(tag, installedVersion);
                if (comparison.NotComparable)
                {
                    return new BridgeUpdateCheckOutcome(
                        null, Ok: true, Detail: comparison.Detail, RateLimit: rateLimit, NotComparable: true);
                }
                if (comparison.Precedence <= 0)
                {
                    return new BridgeUpdateCheckOutcome(null, Ok: true, RateLimit: rateLimit);
                }
            }

            var assets = await ReleasePageChecker
                .GetAssetsAsync(endpoints.ReleasesPageBase, tag, options, cancellationToken)
                .ConfigureAwait(false);

            // 网页路径拿不到发布说明 markdown（expanded_assets 只有资源列表），Notes 按没有处理
            //
            // The web path cannot obtain the release notes markdown (expanded_assets carries the asset list only),
            // so Notes is treated as absent
            return new BridgeUpdateCheckOutcome(
                new BridgeUpdateInfo(
                    tag, await ResolveAssetAsync(endpoints, tag, assets, options, cancellationToken)
                        .ConfigureAwait(false),
                    null, ViaFallback: true, rateLimit),
                Ok: true,
                RateLimit: rateLimit);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.bridge]: Release page fallback failed: " + err.Message);
            return null;
        }
    }

    /// <summary>
    /// 资产解析的最后一道：先按 expanded_assets 的真实列表挑，挑不到再用 HEAD 探测候选名
    /// 下载地址由 ReleaseAssetProbe.BuildUrl 拼出，本方法只负责"上游候选名 + HEAD"这一小段
    ///
    /// 探测的语义与 ReleaseAssetProbe 一致：HEAD 不取内容，302/301/200 都视为"存在"
    /// 找不到返回 null，界面据此退回"打开 Releases 页面"
    ///
    /// The last resort for asset resolution: pick from the real expanded_assets list first, and fall back to probing
    /// the candidate name with HEAD
    /// The download URL comes from ReleaseAssetProbe.BuildUrl, and this method owns only the "upstream candidate name
    /// plus HEAD" part
    ///
    /// The probe semantics match ReleaseAssetProbe: HEAD fetches no content, and 302/301/200 all count as "present"
    /// Null means not found, and the UI then falls back to opening the Releases page
    /// </summary>
    private static async Task<UpdateAsset?> ResolveAssetAsync(
        BridgeUpdateEndpoints endpoints,
        string tag,
        IReadOnlyList<UpdateAsset> assets,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        var selected = BridgeAssetSelector.SelectFromApiAssets(assets);
        if (selected is not null)
        {
            return selected;
        }
        return await ProbeAssetAsync(endpoints.ReleasesPageBase, tag, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 用 HEAD 探测上游资产是否存在，完全不依赖 API 配额（下载端点与 api 主机是两条独立通道）
    /// 任何异常都返回 null：探测失败与"资产不存在"对调用方是同一个结果
    ///
    /// Probes whether the upstream asset exists with HEAD, depending on the API quota in no way (the download endpoint
    /// and the API host are independent channels)
    /// Any exception returns null: to the caller a failed probe and an absent asset are the same outcome
    /// </summary>
    private static async Task<UpdateAsset?> ProbeAssetAsync(
        string releasesPageBase,
        string tag,
        UpdateOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var name = BridgeAssetSelector.CandidateName(tag, BridgePlatform.WindowsAmd64);
            var url = ReleaseAssetProbe.BuildUrl(releasesPageBase, tag, name);

            using var handler = new System.Net.Http.HttpClientHandler
            {
                // 不跟随重定向：要的就是 302 本身（跟随会去取整个资产）
                //
                // Do NOT follow redirects: the 302 itself is the signal (following it would fetch the whole asset)
                AllowAutoRedirect = false,
            };
            var proxy = options.BuildProxy();
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            using var client = new System.Net.Http.HttpClient(handler) { Timeout = ProbeTimeout };

            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 MIDITap-updater");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 200 or 301 or 302 or 303 or 307 or 308)
            {
                // 大小在 HEAD 里拿不到，按 0 交给下载流程（它不依赖该值）
                // 摘要同样拿不到：下游 BridgeInstaller 会因无摘要拒绝安装，界面据此提示，这是有意的保守
                //
                // The size is unavailable from HEAD and reported as 0, which the download flow does not depend on
                // The digest is unavailable too: BridgeInstaller then refuses the install for lack of one, and the UI
                // says so — deliberately on the cautious side
                return new UpdateAsset(name, url, 0);
            }
            Console.Error.WriteLine("[miditap.bridge]: Asset probe returned status " + status + " for " + name);
            return null;
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.bridge]: Asset probe failed: " + err.Message);
            return null;
        }
    }
}
