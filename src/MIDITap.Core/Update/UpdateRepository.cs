// UpdateRepository.cs — 仓库地址的**唯一来源**
// 为什么需要它：改仓库名（或做 fork）不必在多个文件里找
// **运行时地址全部由这里派生**，改这一处即可
// 注意这里**只放运行时用到的地址**
// README/CHANGELOG 里的徽章与提交链接仍写死仓库名
// 那是文档的正常做法（它们描述的就是这个仓库本身），不属于配置
//
// The SINGLE source of truth for the repository address
// Why: renaming the repository (or forking) needs no hunting through several files
// All runtime addresses are derived from here
// Only runtime addresses live here
// Badges and commit links in README/CHANGELOG still spell the repository out
// That is normal for documentation, since they describe this very repository, and is not configuration

namespace MIDITap.Core.Update;

/// <summary>
/// 被检查与下载的发布仓库。改这里即可整体切换
///
/// The release repository checked and downloaded from; changing it switches everything at once
/// </summary>
public static class UpdateRepository
{
    /// <summary>GitHub 主机 / The GitHub host</summary>
    public const string Host = "https://github.com";

    /// <summary>
    /// API 主机。与 Host **配额独立**（实测网页路径无 x-ratelimit-* 头）
    ///
    /// The API host. Its quota is INDEPENDENT of Host (measured: no x-ratelimit-* headers on the web path)
    /// </summary>
    public const string ApiHost = "https://api.github.com";

    /// <summary>组织/用户 / Organisation or user</summary>
    public const string Owner = "MarchSnow-1";

    /// <summary>仓库名 / Repository name</summary>
    public const string Name = "MIDITap";

    /// <summary>owner/name，用于拼接路径 / owner/name, used to build paths</summary>
    public const string Slug = Owner + "/" + Name;

    /// <summary>
    /// 仓库主页（"GitHub 仓库"按钮用）
    ///
    /// Repository home page, used by the "GitHub repository" button
    /// </summary>
    public const string RepositoryUrl = Host + "/" + Slug;

    /// <summary>
    /// Release 列表根地址（下载地址与资源片段都从这里拼）
    ///
    /// Root address of the release list, from which download URLs and the asset fragment are built
    /// </summary>
    public const string ReleasesBase = Host + "/" + Slug + "/releases";

    /// <summary>
    /// "最新发布"的网页地址。**不跟随重定向**时它的 302 会给出标签
    /// 这是 API 不可用时的版本来源
    ///
    /// The web address of the latest release; without following redirects its 302 yields the tag
    /// That tag is where the version comes from when the API is unavailable
    /// </summary>
    public const string LatestReleaseUrl = ReleasesBase + "/latest";

    /// <summary>
    /// 最新发布的 API 地址（信息最全：含资产大小与 SHA-256）
    ///
    /// API address of the latest release — the most complete source, carrying asset sizes and SHA-256
    /// </summary>
    public const string LatestReleaseApiUrl = ApiHost + "/repos/" + Slug + "/releases/latest";
}
