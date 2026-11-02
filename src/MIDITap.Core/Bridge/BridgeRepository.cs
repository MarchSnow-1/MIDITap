// BridgeRepository.cs — 上游 MIDIBridge Client 发布仓库地址的唯一来源（下载到 .bridge/ 的扩展功能）
// 运行时地址全部由这里拼出，上游换仓库时只改这个文件
//
// BridgeRepository.cs — the single source of truth for the upstream MIDIBridge Client release repository
// MIDITap downloads that binary into .bridge/, and every runtime address is built from here

namespace MIDITap.Core.Bridge;

/// <summary>发布仓库的地址 / The release repository addresses</summary>
public static class BridgeRepository
{
    public const string Host = "https://github.com";

    /// <summary>
    /// API 主机。与 Host **配额独立**（网页路径无 x-ratelimit-* 头），理由同 UpdateRepository
    ///
    /// The API host. Its quota is INDEPENDENT of Host (the web path carries no x-ratelimit-* headers)
    /// The reasoning is the same as for UpdateRepository
    /// </summary>
    public const string ApiHost = "https://api.github.com";

    public const string Owner = "MarchSnow-1";

    public const string Name = "midibridge-client";

    public const string Slug = Owner + "/" + Name;

    /// <summary>设置页"了解上游"入口用的仓库主页 / The repository home page behind the settings page's "about the upstream" entry</summary>
    public const string RepositoryUrl = Host + "/" + Slug;

    public const string ReleasesBase = Host + "/" + Slug + "/releases";

    /// <summary>
    /// "最新发布"的网页地址。**不跟随重定向**时它的 302 会给出标签
    /// 这是 API 不可用时的版本来源（做法与 Update 流程一致）
    ///
    /// The web address of the latest release; without following redirects its 302 yields the tag
    /// That tag is where the version comes from when the API is unavailable
    /// The approach matches the update flow
    /// </summary>
    public const string LatestReleaseUrl = ReleasesBase + "/latest";

    /// <summary>最新发布的 API 地址，唯一携带资产大小与 SHA-256 摘要的来源 / API address of the latest release, the only source carrying asset sizes and SHA-256 digests</summary>
    public const string LatestReleaseApiUrl = ApiHost + "/repos/" + Slug + "/releases/latest";

    /// <summary>
    /// 发布清单的文件名，上游每次发布都会附带一份（见其 docs/version-manifest_zh-CN.md）
    ///
    /// The manifest file name, attached to every upstream release (see its docs/version-manifest_zh-CN.md)
    /// </summary>
    public const string ManifestFileName = "version.json";

    // 两个清单地址的路径顺序不能互换
    // GitHub 的 latest 是 **release 段的别名**，因此它排在 download 之前：releases/latest/download/<file>
    // 写成 releases/download/latest/<file> 会被当作 tag 名为 latest 的发布，返回 404
    //
    // The two manifest URLs cannot swap their path segments
    // On GitHub, latest is an alias for the RELEASE segment, so it precedes download: releases/latest/download/<file>
    // Written as releases/download/latest/<file> it is read as a release tagged "latest" and returns 404

    /// <summary>
    /// 最新正式版的清单地址。**恒定地址，不随版本变化**
    /// latest 只会指向非 prerelease、非 draft 的发布，因此这里拿到的始终是正式版
    /// 走的是下载端点而不是 api.github.com，因此不消耗 API 配额
    ///
    /// Manifest address of the latest stable release; a CONSTANT address that does not change with versions
    /// latest only points at a release that is neither a prerelease nor a draft, so this always yields a stable one
    /// It uses the download endpoint rather than api.github.com, so it spends no API quota
    /// </summary>
    public const string LatestManifestUrl = ReleasesBase + "/latest/download/" + ManifestFileName;

    /// <summary>
    /// 指定 tag 的清单地址，供"按已知版本取清单"与测试使用
    /// tag 带 v 前缀（例如 v1.3.1），与上游发布标签的形式一致
    ///
    /// Manifest address for a given tag, used to fetch a known version's manifest and by tests
    /// The tag carries the v prefix (for example v1.3.1), matching the upstream release tags
    /// </summary>
    public static string ManifestUrl(string tag)
        => ReleasesBase + "/download/" + Uri.EscapeDataString(tag) + "/" + ManifestFileName;

    /// <summary>
    /// 指定 tag 的发布页地址，供用户在浏览器里看完整更新日志
    /// GitHub 的发布页形状是 releases/tag/&lt;tag&gt;，与下载地址的 releases/download/&lt;tag&gt; 只差中间一段
    ///
    /// The release page address for a given tag, so the user can read the full notes in a browser
    /// GitHub's release pages read releases/tag/&lt;tag&gt;, which differs from the download address
    /// releases/download/&lt;tag&gt; in that one segment alone
    /// </summary>
    public static string ReleasePageUrl(string tag) => ReleasesBase + "/tag/" + Uri.EscapeDataString(tag);

    /// <summary>
    /// 按清单条目里的 filename 拼下载地址，仅在条目自身没有 url 时作为兜底
    /// 正常情况下应当直接用清单的 url 字段：它锁定具体 tag，与清单必然同版本
    ///
    /// Builds a download address from an artifact's filename, used only as a fallback when the entry carries no url
    /// Normally the manifest's url field is used directly: it pins a concrete tag and is therefore the same version as the manifest
    /// </summary>
    public static string ArtifactUrl(string tag, string fileName)
        => ReleasesBase + "/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(fileName);
}
