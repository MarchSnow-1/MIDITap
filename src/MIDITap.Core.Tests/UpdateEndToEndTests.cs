// UpdateEndToEndTests.cs — 用**本地服务器 + 真实 zip**端到端验证更新流程与各种边界
//
// 为什么必须端到端：这条链路（下载 -> SHA-256 校验 -> 解压 -> 暂存）一旦某处出错
// 后果是"把坏文件装上去"，而自更新装错的代价是应用再也起不来
// 纯函数测试覆盖不到"按字节传输是否完整""校验有没有真的发生在解压之前"这类问题
//
// 本地服务器而非真 GitHub：确定性
// 外网会限流、会抖动，而"限流"本身就是要测的场景之一
// 靠真实服务无法把它变成可重复的断言
//
// End-to-end verification of the update flow and its edge cases, using a LOCAL server and a REAL zip
//
// Why end-to-end: a mistake anywhere in this chain (download -> SHA-256 verify -> extract -> stage)
// Such a mistake means installing a broken build
// For a self-updater that means the app never starts again
// Pure-function tests cannot cover "did the bytes arrive intact"
// They also cannot cover "does verification really happen before extraction"
//
// A local server rather than real GitHub, for determinism
// The live service rate-limits and flaps, and rate limiting is itself one of the scenarios under test
// That scenario cannot be made repeatable against the real thing

using System.IO.Compression;
using System.Security.Cryptography;
using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

/// <summary>构造测试用的 zip 与摘要 / Builds the zips and digests used by the tests</summary>
internal static class ZipFixture
{
    /// <summary>打包若干条目为 zip 字节 / Packs the given entries into zip bytes</summary>
    public static byte[] Build(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// 计算字节内容的 SHA-256（小写十六进制），即 GitHub 会给出的摘要
    ///
    /// Computes the SHA-256 of the byte content (lowercase hex), which is the digest GitHub gives
    /// </summary>
    public static string Sha256(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// 一份结构正确的更新包（含程序文件、一个用户数据目录、一个 pdb）
    ///
    /// A structurally correct update package (program files, one user data directory, one pdb)
    /// </summary>
    public static byte[] NormalPackage() => Build(
        ("MIDITap/MIDITap.exe", "exe-bytes"),
        ("MIDITap/i18n/zh_CN.json", "{}"),
        ("MIDITap/scripts/apply-update.ps1", "# helper"),
        ("MIDITap/config/mapping.json", "FACTORY"),
        ("MIDITap/.storage/last_config", "X"),
        ("MIDITap/MIDITap.pdb", "symbols"));
}

public sealed class UpdateEndToEndTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-e2e-" + Guid.NewGuid().ToString("N"));

    private static readonly UpdateOptions NoProxy = new(null);

    public UpdateEndToEndTests() => Directory.CreateDirectory(_dir);

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

    private string StagingDir => Path.Combine(_dir, "staged");
    private string PackagePath => Path.Combine(_dir, "package.zip");

    private static UpdateAsset AssetFor(string baseUrl, byte[] package, string? digest)
        => new("MIDITap-v9.9.9-win-x64.zip", baseUrl + "/dl/MIDITap-v9.9.9-win-x64.zip",
            package.Length, digest);

    // ================================================================ 正常路径 / Happy path

    [Fact]
    public async Task Happy_path_stages_program_files_and_excludes_user_data()
    {
        var package = ZipFixture.NormalPackage();
        using var server = new MiniHttpServer(_ => (200, null, package));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, ZipFixture.Sha256(package)),
            NoProxy, StagingDir, PackagePath);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(ChecksumVerdict.Match, result.Checksum!.Verdict);

        // 程序文件已就位 / The program files are in place
        Assert.True(File.Exists(Path.Combine(StagingDir, "MIDITap.exe")));
        Assert.True(File.Exists(Path.Combine(StagingDir, "i18n", "zh_CN.json")));
        Assert.True(File.Exists(Path.Combine(StagingDir, "scripts", "apply-update.ps1")));

        // **用户数据与调试符号必须被排除** —— 覆盖 config 就是数据丢失
        //
        // **User data and debug symbols must be excluded** — overwriting config is data loss
        Assert.False(Directory.Exists(Path.Combine(StagingDir, "config")));
        Assert.False(Directory.Exists(Path.Combine(StagingDir, ".storage")));
        Assert.False(File.Exists(Path.Combine(StagingDir, "MIDITap.pdb")));

        // 3 个程序文件被写入（exe、i18n、脚本）；pdb、config/、.storage/ 均被排除
        //
        // 3 program files are written (exe, i18n, script); the pdb, config/ and .storage/ are excluded
        Assert.Equal(3, result.FilesWritten);
    }

    [Fact]
    public async Task Happy_path_reports_progress()
    {
        var package = ZipFixture.NormalPackage();
        using var server = new MiniHttpServer(_ => (200, null, package));

        var reports = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => reports.Add(p));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, ZipFixture.Sha256(package)),
            NoProxy, StagingDir, PackagePath, progress);

        Assert.True(result.Ok);
        // Progress<T> 的回调是异步投递的，给它一点时间落地
        //
        // The Progress<T> callback is delivered asynchronously, so give it a moment to land
        await Task.Delay(200);
        Assert.NotEmpty(reports);
        Assert.Equal(package.Length, reports[^1].Received);
    }

    // ================================================================ SHA-256 校验
    //
    // SHA-256 verification

    [Fact]
    public async Task Rejects_a_package_whose_content_does_not_match_the_digest()
    {
        // 服务器给的摘要与实际内容**不一致**（模拟被篡改或被替换的包）
        // 必须拒绝安装，并且**丢弃**已下载的文件
        // 留下一个"看起来可用"的包等着被安装，正是自更新最危险的状态
        //
        // The digest the server gives does not match the actual content (simulating a tampered or replaced package)
        // Installation must be refused and the downloaded file **discarded**
        // A package that "looks usable" waiting to be installed is the most dangerous state
        // For a self-updater that is precisely the state to avoid
        var package = ZipFixture.NormalPackage();
        var wrongDigest = ZipFixture.Sha256(ZipFixture.Build(("x", "different")));
        using var server = new MiniHttpServer(_ => (200, null, package));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, wrongDigest),
            NoProxy, StagingDir, PackagePath);

        Assert.False(result.Ok);
        Assert.Equal(StageFailure.ChecksumMismatch, result.Failure);
        Assert.Equal(ChecksumVerdict.Mismatch, result.Checksum!.Verdict);
        // 包被丢弃、没有解压出任何东西 / The package is discarded and nothing was extracted
        Assert.False(File.Exists(PackagePath), "校验失败后必须丢弃下载的包");
        Assert.False(Directory.Exists(StagingDir), "校验失败后不得解压");
    }

    [Fact]
    public async Task Rejects_a_truncated_transfer()
    {
        // 弱网截断：Content-Length 声称有 N 字节，实际只发出前一半
        // 这正是校验要拦的场景 —— 少了字节的 zip 解压会失败，或更糟：解压出残缺的程序
        //
        // A truncated transfer on a weak network: Content-Length claims N bytes
        // Only the first half is actually sent
        // This is exactly the case verification is meant to stop
        // A zip missing bytes fails to extract, or worse, extracts a mutilated program
        var package = ZipFixture.NormalPackage();
        using var server = new MiniHttpServer(_ => (200, null, package[..(package.Length / 2)]));
        server.ContentLengthOverride = package.Length;

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, ZipFixture.Sha256(package)),
            NoProxy, StagingDir, PackagePath);

        // 下载阶段就该失败（连接被提前关闭）；无论在哪一步失败，都绝不允许安装
        //
        // It should fail at the download stage already (the connection is closed early)
        // Whichever step it fails at, installation must never be allowed
        Assert.False(result.Ok);
        Assert.False(Directory.Exists(StagingDir));
    }

    [Fact]
    public async Task Proceeds_when_no_digest_is_published()
    {
        // 权衡（已在 UpdateChecksum 注释中说明）：历史版本没有摘要
        // 若一律拒绝，那些用户就再也无法升级到带摘要的版本
        // 但必须**明确记录**为 NotProvided，不能静默当作"已校验"
        //
        // The trade-off (already explained in the UpdateChecksum comments): older releases publish no digest
        // Refusing them outright would leave those users with no in-app path to a version that carries one
        // The only remaining path would be a manual download from the Releases page
        // But it must be **explicitly recorded** as NotProvided rather than silently treated as "verified"
        var package = ZipFixture.NormalPackage();
        using var server = new MiniHttpServer(_ => (200, null, package));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, null),
            NoProxy, StagingDir, PackagePath);

        Assert.True(result.Ok);
        Assert.Equal(ChecksumVerdict.NotProvided, result.Checksum!.Verdict);
    }

    [Fact]
    public async Task Rejects_a_malformed_digest()
    {
        // 摘要字段存在但内容不是 64 位十六进制（例如换了算法却仍标为 sha256）
        // 必须按"无法校验"处理并拒绝，而不是当作通过
        //
        // The digest field exists but its content is not 64 hex characters
        // One example is the algorithm being changed while the label still says sha256
        // It must be treated as "cannot verify" and refused, not counted as a pass
        var package = ZipFixture.NormalPackage();
        using var server = new MiniHttpServer(_ => (200, null, package));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, "not-a-real-digest"),
            NoProxy, StagingDir, PackagePath);

        Assert.False(result.Ok);
        Assert.Equal(StageFailure.ChecksumMismatch, result.Failure);
    }

    // ================================================================ 下载失败 / Download failures

    [Fact]
    public async Task Reports_a_download_failure_on_404()
    {
        using var server = new MiniHttpServer(_ => (404, null, Array.Empty<byte>()));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, ZipFixture.NormalPackage(), new string('a', 64)),
            NoProxy, StagingDir, PackagePath);

        Assert.False(result.Ok);
        Assert.Equal(StageFailure.Download, result.Failure);
    }

    [Fact]
    public async Task Reports_a_download_failure_when_the_host_is_unreachable()
    {
        // 端口 9 通常无人监听 / Port 9 usually has nobody listening
        var asset = new UpdateAsset("x.zip", "http://127.0.0.1:9/x.zip", 0, new string('a', 64));

        var result = await UpdateStager.PrepareAsync(
            asset, NoProxy, StagingDir, PackagePath);

        Assert.False(result.Ok);
        Assert.Equal(StageFailure.Download, result.Failure);
    }

    [Fact]
    public async Task Leaves_no_partial_file_behind_after_a_failed_download()
    {
        // .part 残留会让下一次更新困惑（甚至被误当成完整包）
        //
        // A leftover .part confuses the next update (it may even be mistaken for a complete package)
        using var server = new MiniHttpServer(_ => (404, null, Array.Empty<byte>()));

        await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, ZipFixture.NormalPackage(), new string('a', 64)),
            NoProxy, StagingDir, PackagePath);

        Assert.False(File.Exists(PackagePath));
        Assert.False(File.Exists(PackagePath + ".part"));
    }

    // ================================================================ 本地已有的包 / A package already on disk

    [Fact]
    public async Task Reuses_a_verified_local_package_without_downloading()
    {
        // 本地已经有一份完好的包时不该再下一次：那是一次几百 MB 的无谓传输
        // 复用的依据只有摘要，因此这里给的是正确摘要
        //
        // An intact package on disk must not be downloaded again, which would be a pointless transfer of hundreds of MB
        // The digest is the only basis for reuse, so the correct one is supplied here
        var package = ZipFixture.NormalPackage();
        File.WriteAllBytes(PackagePath, package);

        // 服务器只要被访问就说明复用没有生效，因此让它返回失败
        //
        // Any request at all proves the reuse did not happen, so the server is made to fail
        using var server = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, ZipFixture.Sha256(package)),
            NoProxy, StagingDir, PackagePath);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(ChecksumVerdict.Match, result.Checksum!.Verdict);

        // 一次请求都没有发出 / Not a single request was made
        Assert.Empty(server.Requests);

        // 复用的包照样要解压，并且照样排除用户数据
        //
        // A reused package is still extracted, and still excludes user data
        Assert.True(File.Exists(Path.Combine(StagingDir, "MIDITap.exe")));
        Assert.False(Directory.Exists(Path.Combine(StagingDir, "config")));
    }

    [Fact]
    public async Task Discards_a_local_package_whose_digest_does_not_match()
    {
        // 上一轮中断留下的半成品：摘要对不上就必须丢弃，否则一个坏包会反复参与判断
        //
        // A leftover from an interrupted round: a digest mismatch has to discard it
        // Otherwise the same bad file keeps taking part in the decision
        File.WriteAllBytes(PackagePath, ZipFixture.Build(("MIDITap/MIDITap.exe", "stale-bytes")));

        var package = ZipFixture.NormalPackage();
        using var server = new MiniHttpServer(_ => (200, null, package));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, ZipFixture.Sha256(package)),
            NoProxy, StagingDir, PackagePath);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(ChecksumVerdict.Match, result.Checksum!.Verdict);

        // 坏包被丢弃，重新下载了一次 / The bad file was discarded and exactly one download was made
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Does_not_reuse_a_local_package_when_no_digest_is_available()
    {
        // 没有摘要时本地文件无从校验，必须重新下载
        // 复用一份无法校验的文件等于跳过校验，而校验是自更新的安全前提
        // 注意 CanInstall 对 NotProvided 是 true，那是"允许安装"，不等于"可以复用本地文件"
        //
        // Without a digest the local file cannot be verified, so it has to be downloaded again
        // Reusing an unverifiable file would amount to skipping verification, which the self-updater depends on
        // Note CanInstall is true for NotProvided: that means "installation is allowed", not "a local file may be reused"
        var package = ZipFixture.NormalPackage();
        File.WriteAllBytes(PackagePath, package);

        using var server = new MiniHttpServer(_ => (200, null, package));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, digest: null),
            NoProxy, StagingDir, PackagePath);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(ChecksumVerdict.NotProvided, result.Checksum!.Verdict);
        Assert.Single(server.Requests);
    }

    // ================================================================ 解压边界 / Extraction edge cases

    [Fact]
    public async Task Rejects_a_zip_that_escapes_the_staging_directory()
    {
        // zip-slip：解压出来的内容随后会被执行，这是最严重的安全边界
        //
        // zip-slip: the extracted content is executed afterwards, which makes this the most serious security boundary
        var package = ZipFixture.Build(
            ("MIDITap/MIDITap.exe", "exe"),
            ("MIDITap/../../../escaped.txt", "pwned"));
        using var server = new MiniHttpServer(_ => (200, null, package));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, ZipFixture.Sha256(package)),
            NoProxy, StagingDir, PackagePath);

        Assert.False(result.Ok);
        Assert.Equal(StageFailure.Extract, result.Failure);
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public async Task Reports_a_failure_for_a_package_that_is_not_a_zip()
    {
        // 内容通过校验（摘要就是它的摘要），但不是 zip —— 例如发布流程出错传了别的东西
        // 校验通过不等于内容可用，解压必须自己再判断一次
        //
        // The content passes verification (the digest is its digest) but is not a zip
        // One instance is the release pipeline going wrong and uploading something else
        // A passing checksum does not mean usable content
        // So extraction must decide for itself one more time
        var notAZip = System.Text.Encoding.UTF8.GetBytes("this is not a zip file at all");
        using var server = new MiniHttpServer(_ => (200, null, notAZip));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, notAZip, ZipFixture.Sha256(notAZip)),
            NoProxy, StagingDir, PackagePath);

        Assert.False(result.Ok);
        Assert.Equal(StageFailure.Extract, result.Failure);
    }

    [Fact]
    public async Task Rejects_an_empty_package()
    {
        var empty = Array.Empty<byte>();
        using var server = new MiniHttpServer(_ => (200, null, empty));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, empty, ZipFixture.Sha256(empty)),
            NoProxy, StagingDir, PackagePath);

        Assert.False(result.Ok);
    }

    // ================================================================ 重复执行 / Repeated runs

    [Fact]
    public async Task A_second_attempt_does_not_mix_with_leftovers_from_the_first()
    {
        // 先放一份"上一次失败留下的垃圾"到暂存区，再跑一次成功的更新
        // 结果里不得含有旧残留 —— 半新半旧的安装是最难排查的坏状态
        //
        // First drop some "junk left behind by a previous failure" into the staging directory
        // Then run a successful update
        // The result must not contain the old leftovers
        // A half-new, half-old installation is the worst state to diagnose
        Directory.CreateDirectory(StagingDir);
        File.WriteAllText(Path.Combine(StagingDir, "leftover-from-previous-run.dll"), "stale");

        var package = ZipFixture.NormalPackage();
        using var server = new MiniHttpServer(_ => (200, null, package));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, ZipFixture.Sha256(package)),
            NoProxy, StagingDir, PackagePath);

        Assert.True(result.Ok);
        Assert.False(File.Exists(Path.Combine(StagingDir, "leftover-from-previous-run.dll")));
    }

    // ================================================================ 内容守卫 / Content guard

    [Fact]
    public async Task Refuses_a_package_that_is_not_a_miditap_build()
    {
        // 远端最新发布是旧技术栈的旧版本包（Node.js 版）
        // 它的摘要完全正确、zip 也能正常解压，但里面没有 MIDITap.exe 与自更新脚本
        // 若照此安装，辅助脚本会把应用目录换成一堆无关文件，应用再也起不来
        // 这里用该版本的真实文件结构作为反例
        //
        // The newest release was a package from the old technology stack (Node.js)
        // Its digest is perfectly correct and the zip extracts cleanly
        // But it contains no MIDITap.exe and no self-update script
        // Installing it would leave the app unable to start
        // Its real file layout is used here as the counter-example
        var nodePackage = ZipFixture.Build(
            ("resources.neu", "neutralino-bundle"),
            ("package.json", "{\"name\":\"miditap\"}"),
            ("extensions/backend/config.js", "// v1 backend"),
            ("extensions/backend/context.js", "// v1 backend"));
        using var server = new MiniHttpServer(_ => (200, null, nodePackage));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, nodePackage, ZipFixture.Sha256(nodePackage)),
            NoProxy, StagingDir, PackagePath);

        // 摘要是对的 —— 所以这一次失败**不是**校验拦下的，而是内容守卫
        //
        // The digest is correct, so this failure was **not** stopped by the checksum
        // The content guard stopped it instead
        Assert.Equal(ChecksumVerdict.Match, result.Checksum!.Verdict);
        Assert.False(result.Ok);
        Assert.Equal(StageFailure.MissingRequiredContent, result.Failure);
        // 明确报出缺了什么，便于排查 / State explicitly what is missing, to make diagnosis easier
        Assert.Contains("MIDITap.exe", result.Detail);
        // 且不留残留 / And no leftovers are left behind
        Assert.False(Directory.Exists(StagingDir));
    }

    [Fact]
    public async Task Refuses_a_package_missing_the_self_update_helper()
    {
        // 只有 exe、没有辅助脚本：安装后用户点「重启并更新」，应用退出却没人接手替换
        // 表现为"点了更新，程序就没了"
        // 必须在**点击之前**拦住
        //
        // An executable without the helper script: after restarting, the app exits with nobody to do the replacing
        // The user clicks update and the program vanishes
        // It must be caught BEFORE the click
        var package = ZipFixture.Build(("MIDITap/MIDITap.exe", "exe-only"));
        using var server = new MiniHttpServer(_ => (200, null, package));

        var result = await UpdateStager.PrepareAsync(
            AssetFor(server.BaseUrl, package, ZipFixture.Sha256(package)),
            NoProxy, StagingDir, PackagePath);

        Assert.False(result.Ok);
        Assert.Equal(StageFailure.MissingRequiredContent, result.Failure);
        Assert.Contains("apply-update.ps1", result.Detail);
    }

    [Fact]
    public void Requires_both_the_executable_and_the_helper_script()
    {
        // 守卫清单本身：列出必须存在的文件，改动它会立刻反映在测试里
        //
        // The guard list itself: it names the files that must exist
        // So a change to it shows up in the tests immediately
        Assert.Contains("MIDITap.exe", UpdateStager.RequiredRelativePaths);
        Assert.Contains(
            UpdateStager.RequiredRelativePaths,
            p => p.EndsWith("apply-update.ps1", StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================ 摘要解析 / Digest parsing

    [Theory]
    [InlineData("sha256:448d50fee6c9086f1c300adab30730a5d0822b10eb53737e7a99db0150c86e0a",
        "448d50fee6c9086f1c300adab30730a5d0822b10eb53737e7a99db0150c86e0a")]
    [InlineData("SHA256:448D50FEE6C9086F1C300ADAB30730A5D0822B10EB53737E7A99DB0150C86E0A",
        "448d50fee6c9086f1c300adab30730a5d0822b10eb53737e7a99db0150c86e0a")]
    public void Parses_the_api_digest_format(string input, string expected)
    {
        // API 的 digest 形如 "sha256:<64位>"，且大小写可能不同 —— 统一成小写再比较
        //
        // The API digest looks like "sha256:<64 hex chars>" and the case may vary
        // Normalise it to lowercase before comparing
        Assert.Equal(expected, ChecksumText.FromApiDigest(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha256:tooshort")]
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    public void Unrecognised_or_malformed_api_digests_yield_null(string? input)
    {
        // **不认识就当作没有**：换成 md5/sha512 时若仍按 sha256 比较，必然报"不匹配"
        // 那会把正常更新误判为"包不可信"
        //
        // **Unrecognised means absent**: a switch to md5/sha512 must not be compared as sha256
        // Comparing it that way would inevitably report "mismatch"
        // That would misjudge a normal update as "the package cannot be trusted"
        Assert.Null(ChecksumText.FromApiDigest(input));
    }

    [Fact]
    public void Parses_the_digest_out_of_the_web_html()
    {
        // 网页路径：expanded_assets 的 HTML 里内嵌同一个摘要（实测与 API 一致）
        //
        // The web path: the expanded_assets HTML embeds the same digest (measured identical to the API)
        const string html = """
            <li><span>sha256:448d50fee6c9086f1c300adab30730a5d0822b10eb53737e7a99db0150c86e0a</span></div>
            """;
        Assert.Equal(
            "448d50fee6c9086f1c300adab30730a5d0822b10eb53737e7a99db0150c86e0a",
            ChecksumText.FromHtml(html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>no digest here</html>")]
    [InlineData("commit 448d50fee6c908")]   // 太短，不是摘要 / Too short to be a digest
    public void Html_without_a_digest_yields_null(string? html)
    {
        Assert.Null(ChecksumText.FromHtml(html));
    }

    [Fact]
    public void A_bounded_range_picks_the_digest_inside_it()
    {
        // 片段里每个资产各带一份摘要，靠区间把"哪个摘要属于谁"定下来
        // 区间外即便有摘要也不算数，否则后面的资产会拿到前面资产的摘要
        //
        // Each asset brings its own digest, and the range decides which digest belongs to whom
        // A digest outside the range does not count, otherwise a later asset would take an earlier asset's digest
        const string first = "1111111111111111111111111111111111111111111111111111111111111111";
        const string second = "2222222222222222222222222222222222222222222222222222222222222222";
        var html = "head " + first + " middle " + second;
        var split = html.IndexOf("middle", StringComparison.Ordinal);

        Assert.Equal(first, ChecksumText.FromHtml(html, 0, split));
        Assert.Equal(second, ChecksumText.FromHtml(html, split, html.Length));
    }

    [Theory]
    [InlineData(-1, 10)]      // 起点越界 / start out of range
    [InlineData(0, 100000)]   // 终点越界 / end past the string
    [InlineData(5, 5)]        // 空区间 / empty range
    [InlineData(8, 3)]        // 起止颠倒 / reversed
    public void An_unusable_window_yields_null(int start, int end)
    {
        // 区间不成立时返回 null 而不是抛异常：这里处理的是远端内容，任何取值都可能出现
        // 下游按"未提供校验和"处理（ChecksumVerdict.NotProvided），而不是让更新流程崩掉
        //
        // An unusable range returns null rather than throwing: this handles remote content, where any value can turn up
        // Downstream treats it as "no checksum provided" (ChecksumVerdict.NotProvided) instead of failing the update flow
        Assert.Null(ChecksumText.FromHtml("0123456789", start, end));
    }

    // ================================================================ 两条路径给出同一摘要
    //
    // Both paths yield the same digest

    [Fact]
    public void Both_lookup_paths_yield_the_same_digest()
    {
        // 关键一致性：API 路径与网页路径必须得到**同一个**摘要
        // 否则用户从哪条路走会得到不同的校验结论 —— 那意味着其中一条是错的
        // 这里用 GitHub 实际返回的两种形态验证
        //
        // The crucial consistency: the API path and the web path must yield **the same** digest
        // Otherwise which route a user takes changes the verification verdict
        // That would mean one of the two is wrong
        // Both shapes GitHub actually returns are used to verify this
        const string digest = "448d50fee6c9086f1c300adab30730a5d0822b10eb53737e7a99db0150c86e0a";
        var apiForm = "sha256:" + digest;
        var htmlForm = $"<a href=\"/x\">MIDITap.zip</a><span>{digest}</span>";

        Assert.Equal(digest, ChecksumText.FromApiDigest(apiForm));
        Assert.Equal(digest, ChecksumText.FromHtml(htmlForm));
        Assert.Equal(ChecksumText.FromApiDigest(apiForm), ChecksumText.FromHtml(htmlForm));
    }
}
