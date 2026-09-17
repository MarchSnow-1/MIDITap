// UpdateChecker.cs — 向 GitHub Releases API 查询最新版本
// 任何失败（网络、非 200、解析错误）都被吞掉并回报 null
// 调用方因此永远不需要 try/catch
// 请求固定带 User-Agent / Accept 头，超时为 8 秒
//
// 除版本号外还发现发布资产与发布说明，并支持可选的代理（用于"点击更新"）
//
// UpdateChecker.cs — queries the GitHub Releases API for the latest version
// Every failure (network, non-200, parse error) is swallowed and reported as null
// Callers therefore never need try/catch
// Requests always carry the User-Agent / Accept headers and a fixed 8s timeout
// Beyond the version number it discovers release assets and release notes, and supports an optional proxy (for click-to-update)

using System.Collections.Concurrent;
using System.Text.Json;
using Semver;

namespace MIDITap.Core.Update;

/// <summary>
/// 更新信息：新版本号、当前版本、Release 页面地址、可下载资产、发布说明、是否走了网页回退
/// 资产可能为 null（尚未上传完成），此时调用方退回"打开 Releases 页面"
/// 发布说明是**原始 markdown**，只有 API 路径拿得到
/// 因此回退路径下 Notes 为 null 且 <paramref name="ViaFallback"/> 为 true
/// 界面据此说明"预览不可用、可到发布页查看"，而不是留下一个空白的更新内容区
///
/// Update information: the new version, current version, release page URL, the downloadable asset,
/// the release notes, and whether the web fallback was used
/// The asset may be null, e.g. when it has not been uploaded yet
/// The caller then falls back to opening the Releases page
/// The notes are RAW markdown, available on the API path alone
/// The fallback path therefore carries null notes and sets <paramref name="ViaFallback"/> to true
/// The UI uses that to say "the preview is unavailable, view it on the release page"
/// rather than leaving an empty notes area
/// </summary>
public sealed record UpdateInfo(
    string Latest,
    string Current,
    string Url,
    UpdateAsset? Asset = null,
    string? Notes = null,
    bool ViaFallback = false);

/// <summary>
/// 检查失败的**可归因原因**。为什么要分类而不是只给一个 bool
/// 用户完全无法判断是网络不通、需要代理，还是 GitHub 限流
/// 想解决也无从下手
/// 分类后界面可以给出下一步该做什么
///
/// A categorised failure reason. A bare boolean was unhelpful
/// That left the user unable to tell a dead network from a missing proxy from GitHub rate limiting
/// With no idea what to do next
/// </summary>
public enum UpdateFailure
{
    None,
    /// <summary>
    /// 网络层失败（连不上、DNS、超时、TLS、代理不可用等）
    ///
    /// A network-layer failure (cannot connect, DNS, timeout, TLS, unusable proxy, and so on)
    /// </summary>
    Network,
    /// <summary>
    /// 服务器有响应但状态码非 200（如 403 限流、404、5xx）
    ///
    /// The server answered, but with a status other than 200 (e.g. 403 rate limit, 404, 5xx)
    /// </summary>
    Http,
    /// <summary>响应无法解析为预期的 JSON / The response cannot be parsed as the expected JSON</summary>
    Parse,
    /// <summary>其它未归类异常 / Any other, unclassified exception</summary>
    Unknown,
}

/// <summary>带失败原因的检查结果</summary>
/// <param name="RateLimitReset">
/// HTTP 403 且响应带 x-ratelimit-reset 时的配额重置时间（本地时间）
/// 这是 403 唯一有用的信息：告诉用户"什么时候再试"而不是"失败了"
///
/// The quota reset time when a 403 carries x-ratelimit-reset
/// This is the only useful thing to say about a 403: when to try again, not merely that it failed
/// </param>
public sealed record UpdateCheckOutcome(
    UpdateInfo? Info,
    bool Ok,
    UpdateFailure Failure = UpdateFailure.None,
    int HttpStatus = 0,
    string? Detail = null,
    DateTimeOffset? RateLimitReset = null);

public static class UpdateChecker
{
    // 三个地址**全部派生自 UpdateRepository**，改仓库只需改那一个文件
    //
    // All three are derived from UpdateRepository, so switching repositories means editing one file

    /// <summary>
    /// "最新发布"的网页地址（也是"打开 Releases 页"的目标）
    ///
    /// The web address of the latest release, and the target of "open the Releases page"
    /// </summary>
    public const string ReleasesUrl = UpdateRepository.LatestReleaseUrl;

    /// <summary>最新发布的 API 地址 / API address of the latest release</summary>
    public const string GitHubApiUrl = UpdateRepository.LatestReleaseApiUrl;

    /// <summary>
    /// Release 列表根地址：下载路径与 expanded_assets 片段都基于它
    /// 它同时是网页路径的基准地址 —— API 配额用尽（403）时改走网页，因为 github.com 的网页
    /// 与 api.github.com 走**不同的配额**（实测响应无 x-ratelimit-* 头）
    ///
    /// Root of the release list, from which both the download path and the expanded_assets fragment are built
    /// It doubles as the base address for the web path, used when the API quota is exhausted (403)
    /// That is because github.com and api.github.com draw on DIFFERENT budgets (measured: no x-ratelimit-* headers)
    /// </summary>
    public const string ReleasesPageBase = UpdateRepository.ReleasesBase;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    // HttpClient 的代理在**首次请求后不可更改**，因此不能共用一个实例再改设置
    // 按代理地址缓存：同一设置下复用连接（避免套接字 churn），换代理时才新建
    //
    // An HttpClient's proxy cannot be changed after its first request
    // A single shared instance therefore cannot be re-configured
    // Cached per proxy address: the same setting reuses the connection, avoiding socket churn
    // Only a proxy change creates a new client
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    private static HttpClient GetClient(UpdateOptions options)
    {
        var key = options.ProxyUrl?.Trim() ?? string.Empty;
        return Clients.GetOrAdd(key, _ =>
        {
            var handler = new HttpClientHandler();
            var proxy = options.BuildProxy();
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            // 代理为 null 时保持 HttpClientHandler 默认值 = 使用系统代理
            //
            // Null proxy keeps HttpClientHandler's default, which is to use the system proxy
            return new HttpClient(handler) { Timeout = RequestTimeout };
        });
    }

    /// <summary>
    /// 检查是否有可用更新（默认网络设置：跟随系统代理）
    /// 返回 { latest, current, url } 或 null（已是最新 / 检查失败）
    /// 失败静默，调用方无需 try/catch
    ///
    /// Checks whether an update is available (default network settings: follow the system proxy)
    /// Returns { latest, current, url } or null (already up to date / check failed)
    /// A failure stays silent, so the caller needs no try/catch
    /// </summary>
    public static Task<UpdateInfo?> CheckForUpdatesAsync(string currentVersion)
        => CheckForUpdatesAsync(currentVersion, UpdateOptions.Default);

    /// <summary>带网络设置的检查 / The check with explicit network settings</summary>
    public static async Task<UpdateInfo?> CheckForUpdatesAsync(string currentVersion, UpdateOptions options)
    {
        var (info, _) = await CheckForUpdatesDetailedAsync(currentVersion, options).ConfigureAwait(false);
        return info;
    }

    /// <summary>
    /// 带失败标记的检查：Ok=false 表示网络/API/解析失败（区别于"已是最新"）
    /// 供需要区分两种 null 的调用方（如设置页的手动检查）使用
    ///
    /// The check with a failure flag: Ok=false means the network, API or parse failed
    /// That is as opposed to "already up to date"
    /// For callers that must tell the two kinds of null apart, such as the manual check on the settings page
    /// </summary>
    public static Task<(UpdateInfo? Info, bool Ok)> CheckForUpdatesDetailedAsync(string currentVersion)
        => CheckForUpdatesDetailedAsync(currentVersion, UpdateOptions.Default);

    /// <summary>带网络设置的详细检查 / The detailed check with explicit network settings</summary>
    public static async Task<(UpdateInfo? Info, bool Ok)> CheckForUpdatesDetailedAsync(
        string currentVersion, UpdateOptions options)
    {
        var outcome = await CheckWithReasonAsync(currentVersion, options).ConfigureAwait(false);
        return (outcome.Info, outcome.Ok);
    }

    /// <summary>
    /// 带**失败原因**的检查。界面据此告诉用户下一步该做什么（尤其是"可能需要配置代理"）
    /// **顺序：API 优先，失败则回退到网页路径。**
    /// 回退条件刻意放宽到"API 的任何失败"，而不是只处理 403
    /// api.github.com 与 github.com 是**不同主机**，代理封锁、DNS 解析、5xx 等都可能只影响其中一个
    /// 例如某些企业代理允许网页而拦截 API 域名
    /// 只对 403 回退会白白丢掉这些场景
    ///
    /// Checks and reports WHY it failed, so the UI can tell the user what to do next
    /// In particular it can say that a proxy may be required
    /// Order: API first, falling back to the web page on failure
    /// The fallback deliberately covers ANY API failure rather than only a 403
    /// api.github.com and github.com are DIFFERENT hosts
    /// Proxy blocking, DNS resolution or a 5xx can therefore affect just one of them
    /// For example, some corporate proxies allow the website while blocking the API host
    /// Handling only 403 would needlessly lose those cases
    /// </summary>
    public static Task<UpdateCheckOutcome> CheckWithReasonAsync(
        string currentVersion, UpdateOptions options, CancellationToken cancellationToken = default)
        => CheckWithReasonAsync(currentVersion, options, UpdateEndpoints.Default, cancellationToken);

    /// <summary>
    /// 带端点覆盖的检查（供测试注入本地服务器，从而确定性地验证回退顺序）
    ///
    /// The check with endpoint overrides
    /// Tests can therefore inject a local server and verify the fallback order deterministically
    /// </summary>
    public static async Task<UpdateCheckOutcome> CheckWithReasonAsync(
        string currentVersion, UpdateOptions options, UpdateEndpoints endpoints,
        CancellationToken cancellationToken = default)
    {
        // 1) API 优先
        //
        // 1) API first
        var viaApi = await TryApiAsync(currentVersion, options, endpoints, cancellationToken)
            .ConfigureAwait(false);
        if (viaApi.Ok)
        {
            // 含"已是最新"的情形
            // 那是成功结果，不应再回退
            //
            // Includes "already up to date", which is a success and must not trigger a fallback
            return viaApi;
        }

        // 2) API 失败 -> 网页路径
        //
        // 2) API failed -> the web page path
        var viaPage = await CheckViaReleasePageAsync(currentVersion, options, endpoints, cancellationToken)
            .ConfigureAwait(false);
        if (viaPage is not null)
        {
            Console.Error.WriteLine(
                "[miditap.updater]: API failed (" + viaApi.Failure + "); succeeded via the release page.");
            return viaPage;
        }

        // 3) 两条路都失败 -> 上报 API 的失败原因（比网页路径的异常更具说明性：它带着 HTTP 状态码与限流重置时间）
        //
        // Both paths failed: report the API's reason, which is more informative
        // It carries the HTTP status and the rate-limit reset time
        return viaApi;
    }

    /// <summary>
    /// 经 API 检查，失败时返回带原因的结果而不抛异常
    ///
    /// The API check
    /// On failure it returns a result carrying the reason instead of throwing
    /// </summary>
    private static async Task<UpdateCheckOutcome> TryApiAsync(
        string currentVersion, UpdateOptions options, UpdateEndpoints endpoints,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoints.ApiUrl);
            request.Headers.UserAgent.ParseAdd("MIDITap-updater/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

            using var response = await GetClient(options).SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var status = (int)response.StatusCode;
                Console.Error.WriteLine(
                    "[miditap.updater]: Update check failed: GitHub API returned status " + status);

                // 403 几乎总是"匿名 API 配额用尽"（GitHub 对未认证请求限每小时 60 次，按出口 IP 计）
                // 响应头给出重置时间，把它解析出来交给界面 —— "什么时候能再试"远比"403"有用
                //
                // A 403 is nearly always the anonymous API quota (60 requests/hour per egress IP)
                // The header carries the reset time; surfacing it beats surfacing the status code
                DateTimeOffset? reset = null;
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    try
                    {
                        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
                            && long.TryParse(values.FirstOrDefault(), out var epoch))
                        {
                            reset = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime();
                        }
                    }
                    catch
                    {
                        // 头缺失或格式异常：不影响主流程，界面会退回通用文案
                        //
                        // A missing or malformed header does not affect the main flow
                        // The UI falls back to generic wording
                    }
                }
                // 回退到网页路径由 CheckWithReasonAsync 统一处理（这里只如实上报失败）
                //
                // The fallback to the web path is handled by CheckWithReasonAsync
                // Here we only report the failure faithfully
                return new UpdateCheckOutcome(null, false, UpdateFailure.Http, status, null, reset);
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            string? tagName;
            string? notes = null;
            UpdateAsset? asset = null;
            using (var document = JsonDocument.Parse(body))
            {
                var root = document.RootElement;
                tagName = root.TryGetProperty("tag_name", out var tag)
                    && tag.ValueKind == JsonValueKind.String
                        ? tag.GetString()
                        : null;
                // 发布说明的 markdown 原文只在 API 的 body 字段里
                // 网页路径拿不到它：release 页面与 atom 给出的都是 GitHub 渲染后的 HTML
                // 因此这里缺失或类型不符时留 null，由界面按"没有发布说明"处理，而不是当成失败
                //
                // The raw markdown of the release notes lives in the API's body field alone
                // The web path cannot obtain it, since the release page and the atom feed both carry GitHub's rendered HTML
                // A missing or mistyped value therefore stays null, and the UI treats it as "no release notes" rather than a failure
                notes = root.TryGetProperty("body", out var bodyElement)
                    && bodyElement.ValueKind == JsonValueKind.String
                        ? bodyElement.GetString()
                        : null;
                asset = ParseAsset(root);
            }

            // 用 semver 库比较，而不是自己手写。SemVersionStyles.Any 接受真实的 GitHub 标签（都带 v 前缀）
            // ComparePrecedenceTo 对应规范第 11 条的优先级比较（构建元数据不参与排序）
            //
            // Comparison uses the semver library rather than hand-written code
            // SemVersionStyles.Any accepts real GitHub tags, which all carry a v prefix
            // ComparePrecedenceTo implements the spec's rule 11 precedence comparison
            // Build metadata does not affect that ordering
            if (!SemVersion.TryParse(tagName, SemVersionStyles.Any, out var latestVersion)
                || !SemVersion.TryParse(currentVersion, SemVersionStyles.Any, out var current))
            {
                // 版本解析失败走"无更新"路径（return null），不算检查失败
                //
                // An unparsable version takes the "no update" path (return null)
                // That does not count as a failed check
                return new UpdateCheckOutcome(null, true);
            }

            if (latestVersion.ComparePrecedenceTo(current) > 0)
            {
                return new UpdateCheckOutcome(
                    new UpdateInfo(tagName!, currentVersion, ReleasesUrl, asset, notes), true);
            }
            return new UpdateCheckOutcome(null, true);
        }
        catch (Exception err)
        {
            // 保留一条日志便于排查，同时把**归类后的原因**交给调用方去说明给用户
            // 分类的意义：网络层失败与"服务器返回错误"对用户意味着完全不同的下一步
            //
            // Keep a log line for diagnosis, and hand the CALLER a categorised reason to show the user
            // A network failure and a server-side error imply different next steps
            Console.Error.WriteLine("[miditap.updater]: Update check failed: " + err.Message);
            var failure = err switch
            {
                // 超时在 HttpClient 里表现为 TaskCanceledException（非用户取消时）
                //
                // In HttpClient a timeout surfaces as TaskCanceledException when the user did not cancel
                TaskCanceledException => UpdateFailure.Network,
                HttpRequestException => UpdateFailure.Network,
                System.Text.Json.JsonException => UpdateFailure.Parse,
                _ => UpdateFailure.Unknown,
            };
            return new UpdateCheckOutcome(null, false, failure, 0, err.Message);
        }
    }

    /// <summary>
    /// 经网页检查最新版本：/releases/latest 的 302 给出标签，expanded_assets 给出真实资源列表
    /// 返回 null 表示网页路径也失败（此时调用方按原错误上报）
    ///
    /// Checks via the web page: the 302 from /releases/latest yields the tag
    /// expanded_assets yields the real asset list
    /// Null means the web path failed too, so the caller reports the original error
    /// </summary>
    private static async Task<UpdateCheckOutcome?> CheckViaReleasePageAsync(
        string currentVersion, UpdateOptions options, UpdateEndpoints endpoints,
        CancellationToken cancellationToken)
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

            if (!SemVersion.TryParse(tag, SemVersionStyles.Any, out var latestVersion)
                || !SemVersion.TryParse(currentVersion, SemVersionStyles.Any, out var current))
            {
                // 版本不可比较时按"无更新"处理，与 API 路径的既有契约一致
                //
                // An incomparable version counts as "no update"
                // That is consistent with the existing contract on the API path
                return new UpdateCheckOutcome(null, true);
            }

            if (latestVersion.ComparePrecedenceTo(current) <= 0)
            {
                // 已是最新：无需再取资源列表（省一次请求）
                //
                // Already up to date: the asset list need not be fetched, saving one request
                return new UpdateCheckOutcome(null, true);
            }

            var assets = await ReleasePageChecker
                .GetAssetsAsync(endpoints.ReleasesPageBase, tag, options, cancellationToken)
                .ConfigureAwait(false);
            var asset = UpdateAssetSelector.Select(assets);

            // 列表拿不到（端点变化、代理拦截、页面结构改动）时**不停在这里**
            // 资产名由我们自己的命名约定决定，构造候选名并用 HEAD 探测即可
            // 下载端点与 API 完全独立（实测无限流头），所以这一步能让"任何情况下都能自动更新"成立
            // 否则用户会遇到一个本不该存在的限制：能访问 github.com 却只能手动下载
            //
            // When the list is unavailable (endpoint change, proxy interception, markup change) do NOT stop here
            // The asset name follows our own convention
            // Candidates can therefore be constructed and probed with HEAD
            // The download endpoint is independent of the API (measured: no rate-limit headers)
            // That is what lets the probe path keep working while the API is rate-limited
            // Otherwise the user hits a limitation that should not exist
            // github.com is reachable, yet a manual download is required
            asset ??= await ReleaseAssetProbe
                .FindExistingAsync(endpoints.ReleasesPageBase, tag, options, cancellationToken)
                .ConfigureAwait(false);

            // 仍然为 null 只可能是"该版本确实没有 Windows 构建"，此时调用方引导到 Releases 页面
            //
            // Still null can only mean the release really has no Windows build
            // The caller then points the user at the Releases page
            //
            // 这条路径**不带发布说明**：expanded_assets 只给资源列表，release 页面与 atom 给的是渲染后的 HTML
            // 三者都不含 markdown 原文，因此 Notes 留 null，界面据此收起"更新内容"一节
            // 这是数据来源的限制，不是解析漏掉了什么
            //
            // This path carries NO release notes: expanded_assets yields the asset list only
            // The release page and the atom feed yield rendered HTML, and none of the three carries the markdown source
            // Notes therefore stays null, and the UI hides the "what's new" section accordingly
            // That is a limitation of the sources, not something the parsing missed
            return new UpdateCheckOutcome(
                new UpdateInfo(tag, currentVersion, ReleasesUrl, asset, ViaFallback: true), true);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.updater]: Release page fallback failed: " + err.Message);
            return null;
        }
    }

    /// <summary>
    /// 从 Release JSON 里取出资产列表并挑出本平台该下载的那个
    ///
    /// Takes the asset list out of the release JSON and picks the one this platform should download
    /// </summary>
    private static UpdateAsset? ParseAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<UpdateAsset>();
        foreach (var item in assets.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
            var url = item.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
            var size = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64()
                : 0L;
            // GitHub 为每个资产提供 SHA-256（实测字段存在，形如 "sha256:<64位>"）
            //
            // GitHub provides a SHA-256 per asset (the field exists in practice, as "sha256:<64 hex>")
            var digest = ChecksumText.FromApiDigest(
                item.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null);
            if (name is not null && url is not null)
            {
                list.Add(new UpdateAsset(name, url, size, digest));
            }
        }
        return UpdateAssetSelector.Select(list);
    }
}
