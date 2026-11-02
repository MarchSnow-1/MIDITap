// BridgeReleaseInfo.cs — 取一条上游发布的更新日志，供更新弹窗展示
//
// 更新日志只存在于 GitHub API 的 body 字段里，网页路径与清单都拿不到
// "取日志"失败不影响安装，版本比较、平台筛选、协议门禁全部留在 BridgeUpdateChecker
// 请求经由 UpdateChecker.ReadLatestReleaseAsync：超时、代理、403 配额头与字段解析都已在那一处封装
//
// BridgeReleaseInfo.cs — fetches one upstream release's notes for the update dialog
//
// The release notes exist only in the GitHub API's body field, and neither the web path nor the manifest carries
// them
// A failed fetch does not affect the install; version comparison, platform selection and the protocol gate stay in
// BridgeUpdateChecker
// The request goes through UpdateChecker.ReadLatestReleaseAsync, which already wraps the timeout, the proxy, the 403
// quota headers and the field parsing

using MIDITap.Core.Update;

namespace MIDITap.Core.Bridge;

/// <summary>
/// 一次更新日志读取的结论
/// <see cref="Ok"/> 为 false 表示日志拿不到，此时 <see cref="Notes"/> 为 null 而 <see cref="ReleaseUrl"/> 仍然可用
/// 界面据此说明原因并把用户送到发布页，而不是留下一个空的日志区
///
/// The outcome of one notes read
/// A false <see cref="Ok"/> means the notes could not be fetched, and <see cref="Notes"/> is null while
/// <see cref="ReleaseUrl"/> still resolves
/// The UI explains why and sends the user to the release page rather than leaving an empty notes area
/// </summary>
/// <param name="Notes">更新日志的 markdown 原文；拿不到时为 null / The raw markdown of the notes; null when unavailable</param>
/// <param name="ReleaseUrl">发布页地址，无论是否取到日志都有值 / The release page address, present whether or not the notes were read</param>
/// <param name="Ok">日志是否取到 / Whether the notes were fetched</param>
/// <param name="Failure">失败的可归因原因，与更新检查同一套分类 / The categorised failure, the same classification the update check uses</param>
/// <param name="RateLimit">403 时带回来的配额，供界面说明何时可重试 / The quota returned with a 403, letting the UI say when to retry</param>
/// <param name="Detail">异常文本，仅供日志 / The exception text, for the log alone</param>
public sealed record BridgeReleaseNotes(
    string? Notes,
    string ReleaseUrl,
    bool Ok,
    UpdateFailure Failure = UpdateFailure.None,
    RateLimitInfo? RateLimit = null,
    string? Detail = null);

public static class BridgeReleaseInfo
{
    /// <summary>用真实端点读取更新日志 / Reads the notes through the real endpoint</summary>
    public static Task<BridgeReleaseNotes> FetchAsync(
        string? tag, string? releaseUrl, UpdateOptions options, CancellationToken cancellationToken = default)
        => FetchAsync(tag, releaseUrl, options, BridgeRepository.LatestReleaseApiUrl, cancellationToken);

    /// <summary>
    /// 用可覆盖的 API 地址读取更新日志（供测试注入本地服务器）
    /// <paramref name="releaseUrl"/> 来自清单的 releaseUrl 字段；它缺失时按 tag 拼出发布页地址
    /// tag 也缺失时退回 Releases 列表
    ///
    /// Reads the notes through an overridable API address (tests inject a local server)
    /// <paramref name="releaseUrl"/> comes from the manifest's releaseUrl field; when it is missing the page address
    /// is built from the tag
    /// With no tag either it falls back to the release list
    /// </summary>
    public static async Task<BridgeReleaseNotes> FetchAsync(
        string? tag,
        string? releaseUrl,
        UpdateOptions options,
        string apiUrl,
        CancellationToken cancellationToken = default)
    {
        var pageUrl = !string.IsNullOrWhiteSpace(releaseUrl)
            ? releaseUrl!
            : string.IsNullOrWhiteSpace(tag)
                ? BridgeRepository.ReleasesBase
                : BridgeRepository.ReleasePageUrl(tag!);

        var read = await UpdateChecker
            .ReadLatestReleaseAsync(apiUrl, options, cancellationToken)
            .ConfigureAwait(false);

        if (!read.Ok)
        {
            // 失败不是错误：日志只是"这次装的是什么"的说明，取不到时安装照样可以进行
            //
            // A failure is not an error: the notes only describe what is being installed, and the install can go
            // ahead without them
            return new BridgeReleaseNotes(
                null, pageUrl, Ok: false, Failure: read.Failure, RateLimit: read.RateLimit, Detail: read.Detail);
        }

        return new BridgeReleaseNotes(read.Notes, pageUrl, Ok: true);
    }
}
