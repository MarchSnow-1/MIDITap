// BridgeManifestTests.cs — 发布清单的解析与平台匹配
//
// 清单是从 v1.3.1 起判断"装不装"的唯一依据：
// 解析错一个字段的后果是选错产物或拿错摘要，而摘要一旦拿错，落地就会被拒绝，
// 用户看到的是"安装失败"，真正的原因却在解析这一层
//
// BridgeManifestTests.cs — parsing the release manifest and matching it to a platform
//
// From v1.3.1 the manifest is the only basis for "install or not":
// getting one field wrong picks the wrong artifact or the wrong digest, and a wrong digest makes the landing refuse,
// so the user sees "install failed" while the real cause sits in this parsing layer

using MIDITap.Core.Bridge;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class BridgeManifestTests
{
    /// <summary>一份结构完整的正式发布清单（两个平台）/ A structurally complete release manifest with two platforms</summary>
    private const string ReleaseJson = """
        {
          "client": "midibridge-client",
          "channel": "release",
          "version": "v9.9.9",
          "versionSemver": "9.9.9",
          "ipcProtocol": "1.0.0",
          "ipcProtocolSchema": 1,
          "commit": "0000000000000000000000000000000000000000",
          "commitShort": "0000000",
          "builtAt": 1700000000,
          "source": "https://example.com/username/repository",
          "releaseUrl": "https://example.com/username/repository/releases/tag/v9.9.9",
          "changelogUrl": "https://example.com/username/repository/blob/v9.9.9/CHANGELOG.md",
          "artifacts": [
            {
              "platform": "windows-amd64",
              "os": "windows",
              "arch": "amd64",
              "filename": "MIDIBridge-Client-v9.9.9-windows-amd64.exe",
              "size": 14412647,
              "sha256": "483000c9fab10950e3c81a6fa4e5e5d8dabc0322691e43bc77e9da494090a842",
              "url": "https://example.com/username/repository/releases/download/v9.9.9/MIDIBridge-Client-v9.9.9-windows-amd64.exe"
            },
            {
              "platform": "linux-armv7",
              "os": "linux",
              "arch": "armv7",
              "filename": "MIDIBridge-Client-v9.9.9-linux-armv7",
              "size": 9779420,
              "sha256": "96fe2399b48d3b154b7ebe2d2d0c4a5be1ff75cfde46a3832e110d153244a537",
              "url": "https://example.com/username/repository/releases/download/v9.9.9/MIDIBridge-Client-v9.9.9-linux-armv7"
            }
          ],
          "warnings": []
        }
        """;

    // ================================================================ 解析 / Parsing

    [Fact]
    public void Parses_the_top_level_fields()
    {
        var result = BridgeManifestParser.Parse(ReleaseJson);

        Assert.True(result.Ok);
        var manifest = result.Manifest!;
        Assert.Equal("midibridge-client", manifest.Client);
        Assert.Equal("release", manifest.Channel);
        Assert.Equal("v9.9.9", manifest.Version);
        // versionSemver 去掉 v 前缀，供语义化比较
        //
        // versionSemver has its v prefix removed, for semantic comparison
        Assert.Equal("9.9.9", manifest.VersionSemver);
        Assert.Equal("1.0.0", manifest.IpcProtocol);
        Assert.Equal(1, manifest.IpcProtocolSchema);
        Assert.True(manifest.IsRelease);
        Assert.Empty(manifest.Warnings);
    }

    [Fact]
    public void Parses_every_artifact_field()
    {
        var manifest = BridgeManifestParser.Parse(ReleaseJson).Manifest!;

        Assert.Equal(2, manifest.Artifacts.Count);
        var windows = manifest.Artifacts[0];
        Assert.Equal("windows-amd64", windows.Platform);
        Assert.Equal("windows", windows.Os);
        Assert.Equal("amd64", windows.Arch);
        Assert.Equal("MIDIBridge-Client-v9.9.9-windows-amd64.exe", windows.FileName);
        Assert.Equal(14412647, windows.Size);
        Assert.Equal(64, windows.Sha256!.Length);
        Assert.StartsWith("https://", windows.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_armv7_as_a_distinct_arch()
    {
        // 上游把 32 位 ARM 写成 armv7，而 Go 的 GOARCH 是 arm
        // 解析层要原样保留，匹配层才有机会按 armv7 精确比对
        //
        // The upstream writes 32-bit ARM as armv7 while Go's GOARCH is arm
        // The parser keeps it verbatim so the matcher can compare armv7 exactly
        var manifest = BridgeManifestParser.Parse(ReleaseJson).Manifest!;

        Assert.Equal("armv7", manifest.Artifacts[1].Arch);
    }

    [Fact]
    public void Rejects_a_manifest_for_another_program()
    {
        // client 是"这份清单属于谁"的自证字段
        // 装别的程序的二进制正是本扩展最不该发生的错误
        //
        // client identifies whose manifest this is
        // Installing another program's binary is exactly what must not happen here
        var json = ReleaseJson.Replace("\"client\": \"midibridge-client\"", "\"client\": \"another-program\"");

        var result = BridgeManifestParser.Parse(json);

        Assert.False(result.Ok);
        Assert.Null(result.Manifest);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Rejects_invalid_json()
    {
        Assert.False(BridgeManifestParser.Parse("{ not json").Ok);
        Assert.False(BridgeManifestParser.Parse("[]").Ok);
        Assert.False(BridgeManifestParser.Parse("").Ok);
        Assert.False(BridgeManifestParser.Parse(null).Ok);
    }

    [Fact]
    public void Rejects_a_manifest_beyond_the_size_bound()
    {
        // 先按上限挡住，避免"下载一个巨大文件再解析"
        //
        // The bound stops "download a huge file then parse it"
        var huge = new string(' ', BridgeManifestParser.MaxManifestBytes + 1);

        Assert.False(BridgeManifestParser.Parse(huge).Ok);
    }

    [Fact]
    public void Falls_back_to_the_version_when_versionSemver_is_absent()
    {
        // versionSemver 是可选的整理值；缺失时从 version 去掉 v 前缀，不该让整份清单作废
        //
        // versionSemver is an optional normalised value; when absent it is derived from version without the v prefix,
        // and that must not discard the whole manifest
        var json = ReleaseJson.Replace("\"versionSemver\": \"9.9.9\",", string.Empty);

        var manifest = BridgeManifestParser.Parse(json).Manifest!;

        Assert.Equal("9.9.9", manifest.VersionSemver);
    }

    [Fact]
    public void Derives_the_schema_from_the_protocol_when_it_is_absent()
    {
        // ipcProtocolSchema 是协议主版本的整数形式
        // 清单没写它时从 ipcProtocol 推出主版本，而不是留 0 —— 0 会被判为不兼容
        //
        // ipcProtocolSchema is the protocol major as an integer
        // When the manifest omits it the major is derived from ipcProtocol rather than left at zero, which would count
        // as incompatible
        var json = ReleaseJson.Replace("\"ipcProtocolSchema\": 1,", string.Empty);

        Assert.Equal(1, BridgeManifestParser.Parse(json).Manifest!.IpcProtocolSchema);
    }

    [Fact]
    public void Reports_the_value_that_cannot_be_parsed_as_major()
    {
        // 解析不出的主版本返回 0：与任何真实主版本都不等，因此按不兼容处理，是安全的一侧
        //
        // An unparsable major yields zero, which equals no real major and therefore counts as incompatible, the safe side
        Assert.Equal(0, BridgeManifestParser.MajorOf("not-a-version"));
        Assert.Equal(0, BridgeManifestParser.MajorOf(string.Empty));
        Assert.Equal(0, BridgeManifestParser.MajorOf(null));
        Assert.Equal(2, BridgeManifestParser.MajorOf("2.30.77"));
        Assert.Equal(1, BridgeManifestParser.MajorOf("1.0.0"));
    }

    [Fact]
    public void Keeps_warnings_so_a_partial_release_is_visible()
    {
        // warnings 非空说明该次构建缺平台或产物名无法识别，产物可能不完整
        // 解析层原样保留，界面据此提示，而不是当作"一切正常"
        //
        // Non-empty warnings mean a platform is missing or an artifact name was unrecognised, so the release may be
        // incomplete. The parser keeps them verbatim for the UI to surface rather than treating it as "all fine"
        var json = ReleaseJson.Replace(
            "\"warnings\": []", "\"warnings\": [\"missing expected platform: darwin-arm64\"]");

        var manifest = BridgeManifestParser.Parse(json).Manifest!;

        Assert.Single(manifest.Warnings);
        Assert.Contains("darwin-arm64", manifest.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Skips_artifacts_without_a_file_name()
    {
        // 没有文件名的条目无法下载，跳过即可；它不该让整份清单失败
        //
        // An entry with no file name cannot be downloaded, so it is skipped; it must not fail the whole manifest
        var json = ReleaseJson.Replace(
            "\"filename\": \"MIDIBridge-Client-v9.9.9-linux-armv7\",", string.Empty);

        var manifest = BridgeManifestParser.Parse(json).Manifest!;

        Assert.Single(manifest.Artifacts);
    }

    // ================================================================ 平台匹配 / Platform matching

    [Fact]
    public void Matches_the_windows_build_by_os_and_arch()
    {
        var manifest = BridgeManifestParser.Parse(ReleaseJson).Manifest!;

        var selected = BridgePlatformMatcher.Select(manifest.Artifacts, BridgePlatform.WindowsAmd64);

        Assert.NotNull(selected);
        Assert.Equal("windows", selected!.Os);
    }

    [Fact]
    public void Does_not_match_a_blank_os_or_arch()
    {
        // 上游遇到无法识别的产物名会留空 os 与 arch，那种条目不该被选中
        //
        // The upstream leaves os and arch blank for unrecognised artifact names, and such an entry must not be selected
        var blank = new BridgeManifestArtifact(
            "windows-amd64", string.Empty, string.Empty, "x.exe", 1, "a", "https://example.com/x");

        Assert.Null(BridgePlatformMatcher.Select([blank], BridgePlatform.WindowsAmd64));
    }

    [Fact]
    public void Protocol_compatibility_is_decided_by_the_major()
    {
        // 主版本相同即视为可对接（上游文档 §6：同主版本向下兼容）
        // 主版本不同则不兼容：握手帧或帧头可能已变
        //
        // An equal major counts as compatible (upstream documentation §6: the same major is backward compatible)
        // A different major is incompatible: the handshake or frame header may have changed
        Assert.True(Compatible(1).ProtocolCompatible);
        Assert.False(Compatible(2).ProtocolCompatible);
        Assert.False(Compatible(0).ProtocolCompatible);
    }

    /// <summary>用给定主版本造一份检查结果 / Builds a check result with the given major</summary>
    private static BridgeManifestCheckInfo Compatible(int schema)
    {
        var manifest = new BridgeManifest(
            "midibridge-client", "release", "v9.9.9", "9.9.9", "1.0.0", schema, null, [], []);
        return new BridgeManifestCheckInfo(manifest, null);
    }
}
