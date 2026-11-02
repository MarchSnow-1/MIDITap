// BridgeInstallerTests.cs — 用本地服务器 + 真实字节端到端验证扩展程序的安装
//
// 端到端的理由：这条链路（下载 -> SHA-256 校验 -> 改名落地）的产物随后会被执行
// 纯函数测试覆盖不到"摘要不符时那个文件是否真的没有落到 .bridge/"这类问题
// 而"拒绝之后盘上还留着一个看起来可用的 exe"正是最危险的状态
//
// 这里造的是裸二进制而不是 zip：上游从 v1.3.1 起产物就是裸二进制（见其 build.yml）
// 解压这一步已经不存在，测试继续按 zip 写会测到一个已被删除的行为
// 夹具里的字节只是占位内容，本链路对它不做任何格式判断——它只认摘要与大小
//
// 用本地服务器而非真上游：确定性。外网会限流、会抖动，这些不是本文件要测的东西
//
// BridgeInstallerTests.cs — end-to-end verification of the extension install, using a local server and real bytes
//
// Why end-to-end: the output of this chain (download, SHA-256 verify, rename into place) is executed afterwards
// Pure-function tests cannot cover "when the digest does not match, does that file really fail to reach .bridge/"
// And "an exe that looks usable is still on disk after a refusal" is precisely the most dangerous state
//
// Why bare bytes rather than a zip: from v1.3.1 the upstream artifact is a bare binary (see its build.yml)
// The extraction step no longer exists, and a test still written around a zip would exercise a removed behaviour
// The bytes in the fixture are placeholder content; this chain makes no format judgement about them, only digest and size
//
// A local server rather than the real upstream, for determinism
// The live service rate-limits and flaps, and neither is what this file is testing

using System.Security.Cryptography;
using MIDITap.Core.Bridge;
using MIDITap.Core.Settings;
using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

/// <summary>构造扩展功能的测试字节与摘要 / Builds the bytes and digests for the extension tests</summary>
internal static class BridgeArtifactFixture
{
    /// <summary>
    /// 一份"看起来像可执行文件"的占位内容
    /// 本链路不校验格式，因此普通字节即可；用一段可读文本是为了失败时一眼看出它是夹具
    ///
    /// Placeholder content that stands in for an executable
    /// This chain validates no format, so ordinary bytes do, and readable text makes it obvious in a failure that it is a fixture
    /// </summary>
    public static byte[] Payload(string tag = "v9.9.9") =>
        System.Text.Encoding.UTF8.GetBytes("placeholder-binary-" + tag);

    /// <summary>按 GitHub 的形态计算摘要（小写十六进制）/ Computes the digest in GitHub's form (lowercase hex)</summary>
    public static string Sha256(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>上游 v1.3.1 起的产物名：裸二进制，Windows 上带 .exe / The artifact name from v1.3.1 on: a bare binary with .exe on Windows</summary>
    public const string ArtifactName = "MIDIBridge-Client-v9.9.9-windows-amd64.exe";

    /// <summary>按上游清单的形态构造一条产物 / Builds one artifact entry in the manifest's shape</summary>
    public static BridgeManifestArtifact Artifact(string baseUrl, byte[] payload, string? digest)
        => new(
            "windows-amd64",
            "windows",
            "amd64",
            ArtifactName,
            payload.Length,
            digest,
            baseUrl + "/dl/" + ArtifactName);
}

public sealed class BridgeInstallerTests : IDisposable
{
    private readonly string _baseDir =
        Directory.CreateTempSubdirectory("miditap-bridge-install-").FullName;

    private static readonly UpdateOptions NoProxy = new(null);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_baseDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论 / A failed cleanup does not change the conclusion
        }
    }

    /// <summary>
    /// 已安装程序的路径：由 BridgeLayout 给出（目录内部布局归它，AppPaths 只给目录）
    /// 用 BridgeLayout 而不是在这里自己拼，测试断言的正是"运行时定位得到的是不是安装落地的那一个"
    ///
    /// The installed program's path comes from BridgeLayout (it owns the layout inside the directory; AppPaths supplies
    /// the directory only). Using BridgeLayout rather than assembling the path here means the tests assert exactly what
    /// run-time lookup finds against what landing produced
    /// </summary>
    private string InstalledExe => BridgeLayout.ExePath(_baseDir).Path
        ?? Path.Combine(AppPaths.BridgeDir(_baseDir), AppPaths.BridgeExecutableFileName);

    private string WorkDir => Path.Combine(AppPaths.UpdateDir(_baseDir), BridgeInstaller.WorkDirectoryName);

    /// <summary>装一次，地址取自夹具 / Installs once, with the address taken from the fixture</summary>
    private Task<BridgeInstallResult> InstallAsync(
        MiniHttpServer server, byte[] payload, string? digest = null, string version = "v9.9.9")
        => BridgeInstaller.InstallAsync(
            BridgeArtifactFixture.Artifact(server.BaseUrl, payload, digest ?? BridgeArtifactFixture.Sha256(payload)),
            version,
            server.BaseUrl + "/dl/" + BridgeArtifactFixture.ArtifactName,
            NoProxy,
            _baseDir);

    // ================================================================ 正常路径 / Happy path

    [Fact]
    public async Task Installs_the_upstream_executable_under_the_conventional_name()
    {
        var payload = BridgeArtifactFixture.Payload();
        using var server = new MiniHttpServer(_ => (200, null, payload));

        var result = await InstallAsync(server, payload);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(BridgeInstallFailure.None, result.Failure);
        Assert.Equal(ChecksumVerdict.Match, result.Checksum!.Verdict);
        // 版本号取自清单（不是从文件名猜），界面可直接展示
        //
        // The version comes from the manifest rather than being guessed from the file name, so the UI can show it directly
        Assert.Equal("v9.9.9", result.Version);
        Assert.True(File.Exists(InstalledExe), "the executable should sit at the conventional name");
        Assert.Equal(payload, await File.ReadAllBytesAsync(InstalledExe));
    }

    [Fact]
    public async Task Removes_the_downloaded_copy_once_it_has_landed()
    {
        // 落地后工作目录里不该留第二份：那会让"当前装的是哪个"变得含混
        //
        // After landing no second copy may remain in the work directory, which would make "which one is installed" ambiguous
        var payload = BridgeArtifactFixture.Payload();
        using var server = new MiniHttpServer(_ => (200, null, payload));

        Assert.True((await InstallAsync(server, payload)).Ok);

        Assert.False(File.Exists(Path.Combine(WorkDir, BridgeInstaller.DownloadFileName)));
        Assert.False(File.Exists(Path.Combine(WorkDir, BridgeInstaller.DownloadFileName + ".part")));
    }

    [Fact]
    public async Task Replaces_an_existing_installation()
    {
        // 升级场景：旧文件在位时替换成功，内容是新的
        //
        // The upgrade case: with an old file present the replace succeeds and the content is the new one
        var old = BridgeArtifactFixture.Payload("v1.0.0");
        using (var first = new MiniHttpServer(_ => (200, null, old)))
        {
            Assert.True((await InstallAsync(first, old, version: "v1.0.0")).Ok);
        }

        var fresh = BridgeArtifactFixture.Payload("v9.9.9");
        using var server = new MiniHttpServer(_ => (200, null, fresh));

        // 落地名字固定，因此第二次装会覆盖第一次
        //
        // The landing name is fixed, so the second install overwrites the first
        var result = await BridgeInstaller.InstallAsync(
            BridgeArtifactFixture.Artifact(server.BaseUrl, fresh, BridgeArtifactFixture.Sha256(fresh)),
            "v9.9.9",
            server.BaseUrl + "/dl/" + BridgeArtifactFixture.ArtifactName,
            NoProxy,
            _baseDir);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(fresh, await File.ReadAllBytesAsync(InstalledExe));
    }

    // ================================================================ 拒绝路径 / Refusals

    [Fact]
    public async Task Refuses_to_download_without_a_digest()
    {
        // 无摘要时连请求都不该发出：下完也无法校验，而落地后要执行它
        //
        // Without a digest not even the request is made: it could not be verified, and it would be executed
        var payload = BridgeArtifactFixture.Payload();
        using var server = new MiniHttpServer(_ => (200, null, payload));

        // 夹具 helper 会把 null 换成真摘要，所以这里直接构造一条无摘要条目
        //
        // The fixture helper substitutes a real digest for null, so an entry without one is built directly here
        var noDigest = await BridgeInstaller.InstallAsync(
            BridgeArtifactFixture.Artifact(server.BaseUrl, payload, null),
            "v9.9.9",
            server.BaseUrl + "/dl/" + BridgeArtifactFixture.ArtifactName,
            NoProxy,
            _baseDir);

        Assert.False(noDigest.Ok);
        Assert.Equal(BridgeInstallFailure.NoDigest, noDigest.Failure);
        Assert.Empty(server.Requests);
        Assert.False(File.Exists(InstalledExe));
    }

    [Fact]
    public async Task Discards_the_file_when_the_digest_does_not_match()
    {
        var payload = BridgeArtifactFixture.Payload();
        using var server = new MiniHttpServer(_ => (200, null, payload));

        var result = await BridgeInstaller.InstallAsync(
            BridgeArtifactFixture.Artifact(server.BaseUrl, payload, new string('a', 64)),
            "v9.9.9",
            server.BaseUrl + "/dl/" + BridgeArtifactFixture.ArtifactName,
            NoProxy,
            _baseDir);

        Assert.False(result.Ok);
        Assert.Equal(BridgeInstallFailure.ChecksumMismatch, result.Failure);
        // 拒绝之后盘上不留下"看起来可用"的文件
        //
        // After a refusal no plausible-looking file remains on disk
        Assert.False(File.Exists(InstalledExe));
        Assert.False(File.Exists(Path.Combine(WorkDir, BridgeInstaller.DownloadFileName)));
        Assert.False(File.Exists(Path.Combine(WorkDir, BridgeInstaller.DownloadFileName + ".part")));
    }

    [Fact]
    public async Task Reports_a_download_failure_when_the_server_answers_with_an_error()
    {
        using var server = new MiniHttpServer(_ => (404, null, "not found"));

        var result = await BridgeInstaller.InstallAsync(
            BridgeArtifactFixture.Artifact(server.BaseUrl, [1], new string('a', 64)),
            "v9.9.9",
            server.BaseUrl + "/dl/" + BridgeArtifactFixture.ArtifactName,
            NoProxy,
            _baseDir);

        Assert.False(result.Ok);
        Assert.Equal(BridgeInstallFailure.Download, result.Failure);
        Assert.False(File.Exists(InstalledExe));
    }

    // ================================================================ 清单入口 / Manifest helpers

    [Fact]
    public async Task Selects_the_installable_artifact_for_this_platform()
    {
        var payload = BridgeArtifactFixture.Payload();
        using var server = new MiniHttpServer(_ => (200, null, payload));

        var manifest = new BridgeManifest(
            "midibridge-client", "release", "v9.9.9", "9.9.9", "1.0.0", 1, null,
            [
                new BridgeManifestArtifact("linux-amd64", "linux", "amd64", "x", 1, "a", "https://example.com/x"),
                BridgeArtifactFixture.Artifact(server.BaseUrl, payload, BridgeArtifactFixture.Sha256(payload)),
            ],
            []);

        var selected = BridgeInstaller.SelectInstallable(manifest, BridgePlatform.WindowsAmd64);

        Assert.NotNull(selected);
        Assert.Equal("windows-amd64", selected!.Platform);
        // 清单自带 url 时原样使用：它锁定具体 tag，与清单必然同版本
        //
        // When the manifest carries a url it is used verbatim: it pins a concrete tag, so it is the same version as the
        // manifest
        Assert.Contains("/dl/", selected.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Returns_null_when_the_manifest_has_no_build_for_this_platform()
    {
        var manifest = new BridgeManifest(
            "midibridge-client", "release", "v9.9.9", "9.9.9", "1.0.0", 1, null,
            [new BridgeManifestArtifact("linux-amd64", "linux", "amd64", "x", 1, "a", "https://example.com/x")],
            []);

        Assert.Null(BridgeInstaller.SelectInstallable(manifest, BridgePlatform.WindowsAmd64));
    }

    [Fact]
    public void Reads_the_version_from_manifest_json()
    {
        // 只想知道最新版是多少时不必构造整份清单
        //
        // Wanting only the latest version does not require building a whole manifest
        const string json = """
            {"client":"midibridge-client","channel":"release","version":"v9.9.9",
             "versionSemver":"9.9.9","ipcProtocol":"1.0.0","ipcProtocolSchema":1,
             "artifacts":[],"warnings":[]}
            """;

        Assert.Equal("9.9.9", BridgeInstaller.VersionFromManifestJson(json));
        Assert.Null(BridgeInstaller.VersionFromManifestJson("not json"));
        Assert.Null(BridgeInstaller.VersionFromManifestJson(null));
    }

    // ================================================================ 清理 / Cleanup

    [Fact]
    public async Task Keeps_the_work_directory_free_of_part_files_after_success()
    {
        // .part 是"下载未完成"的标记，成功落地后不留下它
        //
        // A .part file marks an unfinished download and does not survive a successful landing
        var payload = BridgeArtifactFixture.Payload();
        using var server = new MiniHttpServer(_ => (200, null, payload));

        Assert.True((await InstallAsync(server, payload)).Ok);

        var leftovers = Directory.Exists(WorkDir)
            ? Directory.GetFiles(WorkDir, "*.part")
            : [];
        Assert.Empty(leftovers);
    }
}
