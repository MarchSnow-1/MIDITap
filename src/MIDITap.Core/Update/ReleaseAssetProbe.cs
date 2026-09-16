// ReleaseAssetProbe.cs — 用 HEAD 探测发布资产是否存在，**完全不依赖 API**
//
// 为什么需要它：下载端点 github.com/<o>/<r>/releases/download/<tag>/<file> 与 api.github.com 是**两条独立通道**
// 实测响应里没有 x-ratelimit-* 头，所以下载本身从不受 API 配额影响
// 既然如此，"拿不到资产列表就只能让用户手动下载"就是个**假限制**
// 资产名由我们自己的 CI 命名约定决定，构造出来再用 HEAD 验证即可
// 实测：存在返回 302 跳到资产 CDN，不存在返回 404，信号干净
//
// 保留 expanded_assets 作为首选（它给出权威列表），本模块是它不可用时的后备
//
// Probes for a release asset with HEAD, depending on the API in no way at all
//
// Why this exists: the download endpoint and api.github.com are INDEPENDENT channels
// The download endpoint is github.com/<o>/<r>/releases/download/<tag>/<file>
// Measured: no x-ratelimit-* headers on the former, so downloading never spends the API quota
// Given that, "no asset list means the user must download by hand" is a FALSE limitation
// The file name follows our own CI convention, so it can be constructed and then verified with HEAD
// Measured: 302 to the asset CDN when present, 404 when absent — a clean signal
//
// expanded_assets remains the first choice, since it yields the authoritative list
// This is the fallback for when it is unavailable

using System.Net.Http;

namespace MIDITap.Core.Update;

public static class ReleaseAssetProbe
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 按命名约定构造候选文件名，顺序即优先级
    /// 目前只有一种拼法，即 CI 产出的连字符形式
    /// 下划线写法不作为候选（理由见 UpdateAssetSelector.PlatformSuffix）
    /// 仍然返回列表：探测本来就是"依次尝试候选"的写法，将来多一种命名不必改结构
    ///
    /// Candidate file names per the naming convention, in priority order
    /// There is currently one spelling, the hyphenated form the CI produces
    /// The underscore form is not a candidate (see UpdateAssetSelector.PlatformSuffix)
    /// It still returns a list because the probe is written to try candidates in turn
    /// A second naming variant later therefore needs no structural change
    /// </summary>
    public static IReadOnlyList<string> CandidateNames(string tag)
        => [$"MIDITap-{tag}-win-x64.zip"];

    /// <summary>构造下载地址 / Builds the download URL</summary>
    public static string BuildUrl(string releasesPageBase, string tag, string fileName)
        => $"{releasesPageBase.TrimEnd('/')}/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}";

    /// <summary>
    /// 逐个探测候选名，返回第一个存在的资产；都不存在返回 null
    /// 用 HEAD（不下载内容，只问"有没有"），并把重定向视为"存在"
    /// GitHub 对存在的资产返回 302 跳到资产 CDN，这正是最强的存在信号
    ///
    /// Probes each candidate and returns the first that exists, or null
    /// Uses HEAD (no body) and treats a redirect as "present"
    /// GitHub answers 302 to the asset CDN for an existing asset, which is the strongest possible signal
    /// </summary>
    public static async Task<UpdateAsset?> FindExistingAsync(
        string releasesPageBase, string tag, UpdateOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                // 不跟随重定向：要的就是 302 本身（跟随反而会去下载整个包的内容）
                //
                // Do NOT follow redirects: the 302 itself is the signal
                // Following it would start fetching the whole archive
                AllowAutoRedirect = false,
            };
            var proxy = options.BuildProxy();
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = RequestTimeout };

            foreach (var name in CandidateNames(tag))
            {
                var url = BuildUrl(releasesPageBase, tag, name);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, url);
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 MIDITap-updater");

                    using var response = await client.SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);

                    // 302/301 = 资产存在（会跳到 CDN）；200 也视为存在（某些代理会自己跟随）
                    //
                    // 302/301 means the asset exists, since it redirects to the CDN
                    // 200 also counts, because some proxies follow the redirect themselves
                    var status = (int)response.StatusCode;
                    if (status is 200 or 301 or 302 or 303 or 307 or 308)
                    {
                        return new UpdateAsset(name, url, 0);
                    }
                }
                catch (Exception err)
                {
                    // 单个候选探测失败就试下一个：不同命名之间互不影响
                    //
                    // A failed probe for one candidate just moves on to the next
                    // The naming forms are independent of each other
                    Console.Error.WriteLine(
                        "[miditap.updater]: Probe failed for " + name + ": " + err.Message);
                }
            }
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.updater]: Asset probing failed: " + err.Message);
        }
        return null;
    }
}
