// BridgeManifest.cs — 解析上游随每次发布附带的 version.json 清单
//
// 清单里有两条互不相同的版本信息，读取方要分开对待
//   versionSemver —— 程序版本，语义化，用来判断"要不要升级"
//   ipcProtocol / ipcProtocolSchema —— 协议版本与它的主版本，用来判断"能不能对接"
// ipcProtocolSchema 是协议主版本的整数形式，读取方不必再解析三段式字符串
//
// BridgeManifest.cs — parses the version.json manifest shipped with every upstream release
//
// The manifest holds two different versions, and a reader has to treat them apart
//   versionSemver: the program version, semantic, answering "should this be upgraded"
//   ipcProtocol / ipcProtocolSchema: the protocol version and its major, answering "can the two sides talk"
// ipcProtocolSchema is the protocol major as an integer, so a reader need not parse the three-part string

using System.Text.Json;
using MIDITap.Core.Update;

namespace MIDITap.Core.Bridge;

/// <summary>
/// 清单里的一个平台产物
/// <paramref name="Url"/> 为 null 表示 dev 构建没有稳定公开地址（见上游文档 dev 构建一节）
///
/// One platform artifact from the manifest
/// A null <paramref name="Url"/> means a dev build with no stable public address (see the upstream dev-build section)
/// </summary>
public sealed record BridgeManifestArtifact(
    string Platform,
    string Os,
    string Arch,
    string FileName,
    long Size,
    string? Sha256,
    string? Url);

/// <summary>
/// 已解析的发布清单
/// <paramref name="Warnings"/> 非空表示该次构建有平台缺失或产物名无法识别，产物可能不完整
///
/// A parsed release manifest
/// A non-empty <paramref name="Warnings"/> means a platform was missing or an artifact name was unrecognised, so the release may be incomplete
/// </summary>
public sealed record BridgeManifest(
    string Client,
    string Channel,
    string Version,
    string VersionSemver,
    string IpcProtocol,
    int IpcProtocolSchema,
    string? ReleaseUrl,
    IReadOnlyList<BridgeManifestArtifact> Artifacts,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// 正式发布才可安装；dev 构建的产物只存在于 Actions run 里，没有稳定地址
    ///
    /// Only a release build is installable; a dev build's artifacts exist only as Actions run artifacts with no stable address
    /// </summary>
    public bool IsRelease => string.Equals(Channel, "release", StringComparison.OrdinalIgnoreCase);
}

/// <summary>清单解析的结果：成功时带清单，失败时带可归因的原因</summary>
/// <remarks>The parse outcome: the manifest on success, a categorised reason on failure</remarks>
public sealed record BridgeManifestParseResult(BridgeManifest? Manifest, string? Error)
{
    /// <summary>是否解析成功 / Whether the parse succeeded</summary>
    public bool Ok => Manifest is not null && Error is null;
}

public static class BridgeManifestParser
{
    /// <summary>
    /// 清单里 client 字段的固定值，用于确认这份 JSON 确实属于本扩展依赖的程序
    /// 上游文档 §3 声明该字段固定为它
    ///
    /// The fixed value of the client field, confirming this JSON belongs to the program the extension depends on
    /// The upstream documentation declares it fixed to this value
    /// </summary>
    public const string ExpectedClient = "midibridge-client";

    /// <summary>
    /// 清单体积上限
    /// 真实清单约数 KB，给到 1 MiB 已有充裕余量
    /// 设上限是为了让"下载到一个巨大文件再解析"这条路在读取阶段就被挡住
    ///
    /// An upper bound on the manifest size
    /// A real manifest is a few KB, so 1 MiB leaves ample headroom
    /// The bound stops "download a huge file, then parse it" at the reading stage
    /// </summary>
    public const int MaxManifestBytes = 1 << 20;

    /// <summary>
    /// 解析清单文本
    /// 任何结构问题都返回带原因的失败结果，而不是抛异常
    ///
    /// Parses the manifest text
    /// Any structural problem yields a failure result carrying a reason rather than an exception
    /// </summary>
    public static BridgeManifestParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Fail("the manifest is empty");
        }

        if (json.Length > MaxManifestBytes)
        {
            return Fail("the manifest is larger than the accepted bound");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException err)
        {
            return Fail("the manifest is not valid JSON: " + err.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail("the manifest is not a JSON object");
            }

            // client 是"这份清单属于哪个程序"的自证字段
            // 缺失或不符都按失败处理：拿别的程序的清单去装二进制会装上错误的程序
            //
            // client identifies which program the manifest belongs to
            // Missing or mismatched both fail: installing a binary from another program's manifest puts the wrong program in place
            var client = ReadString(root, "client");
            if (!string.Equals(client, ExpectedClient, StringComparison.Ordinal))
            {
                return Fail("the manifest is not for " + ExpectedClient);
            }

            var channel = ReadString(root, "channel") ?? string.Empty;
            var version = ReadString(root, "version");
            if (string.IsNullOrWhiteSpace(version))
            {
                return Fail("the manifest carries no version");
            }

            // versionSemver 缺失时退回 version 去掉 v 前缀，避免因为一个可选字段漏写就整份作废
            //
            // A missing versionSemver falls back to version without its v prefix, so one omitted optional field does not discard the whole manifest
            var versionSemver = ReadString(root, "versionSemver");
            if (string.IsNullOrWhiteSpace(versionSemver))
            {
                versionSemver = version.StartsWith('v') ? version[1..] : version;
            }

            var ipcProtocol = ReadString(root, "ipcProtocol") ?? string.Empty;
            var schema = ReadInt(root, "ipcProtocolSchema") ?? MajorOf(ipcProtocol);

            var artifacts = new List<BridgeManifestArtifact>();
            if (root.TryGetProperty("artifacts", out var artifactArray)
                && artifactArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in artifactArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var fileName = ReadString(item, "filename");
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }
                    artifacts.Add(new BridgeManifestArtifact(
                        ReadString(item, "platform") ?? string.Empty,
                        ReadString(item, "os") ?? string.Empty,
                        ReadString(item, "arch") ?? string.Empty,
                        fileName,
                        ReadLong(item, "size") ?? 0,
                        ReadString(item, "sha256"),
                        ReadString(item, "url")));
                }
            }

            var warnings = new List<string>();
            if (root.TryGetProperty("warnings", out var warningArray)
                && warningArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in warningArray.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    {
                        warnings.Add(text);
                    }
                }
            }

            return new BridgeManifestParseResult(
                new BridgeManifest(
                    client!,
                    channel,
                    version,
                    versionSemver,
                    ipcProtocol,
                    schema,
                    ReadString(root, "releaseUrl"),
                    artifacts,
                    warnings),
                null);
        }
    }

    /// <summary>
    /// 取语义化版本字符串的主版本号，解析不出来时返回 0
    /// 0 与任何真实主版本都不相等，因此调用方按"不兼容"处理，是安全的一侧
    ///
    /// Returns the major of a semantic version string, or 0 when it cannot be parsed
    /// Zero equals no real major, so the caller treats it as incompatible, which is the safe side
    /// </summary>
    public static int MajorOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return 0;
        }
        var head = version.Split('.', 2)[0].Trim();
        return int.TryParse(head, out var major) ? major : 0;
    }

    private static BridgeManifestParseResult Fail(string error) => new(null, error);

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;

    private static long? ReadLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
                ? parsed
                : null;
}
