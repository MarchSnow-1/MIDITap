// ReleasePageChecker.cs — 经**网页**检查最新版本（不消耗 API 配额）
// 用途：GitHub API 返回 403（匿名配额 60/小时用尽）时的回退路径
// 实测确认该路径的响应里没有 x-ratelimit-* 头，即不受同一配额约束
//
// Checks the latest release through the WEB page, consuming no API quota
// Used as the fallback when the GitHub API returns 403 (the 60/hour anonymous quota is exhausted)
// Measured: responses on this path carry no x-ratelimit-* headers, i.e. a different budget

using System.Net.Http;

namespace MIDITap.Core.Update;

public static class ReleasePageChecker
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    /// <summary>取最新发布的版本标签；失败返回 null / Fetches the latest release's tag; null on failure</summary>
    public static async Task<string?> GetLatestTagAsync(
        string latestUrl, UpdateOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                // **不跟随重定向**：要的就是 302 里的 Location
                //
                // Do NOT follow redirects: the Location header of the 302 is the point
                AllowAutoRedirect = false,
            };
            var proxy = options.BuildProxy();
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = RequestTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, latestUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 MIDITap-updater");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // 302/301 才有 Location；有些代理会把它变成 200 并自己跟随，因此两种都看
            //
            // Only 301/302 carry a Location, but a proxy may follow it itself and return 200
            // Both are therefore inspected
            var location = response.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location) && response.RequestMessage?.RequestUri is { } finalUri)
            {
                location = finalUri.ToString();
            }
            return ReleasePageParser.ParseTagFromUrl(location);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.updater]: Release page tag lookup failed: " + err.Message);
            return null;
        }
    }

    /// <summary>
    /// 取某个标签下的资源列表（网页片段）；失败返回空列表
    ///
    /// Fetches the asset list for a tag (the web fragment); an empty list on failure
    /// </summary>
    public static async Task<IReadOnlyList<UpdateAsset>> GetAssetsAsync(
        string pageBaseUrl, string tag, UpdateOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = pageBaseUrl.TrimEnd('/') + "/expanded_assets/" + Uri.EscapeDataString(tag);
            using var handler = new HttpClientHandler();
            var proxy = options.BuildProxy();
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = RequestTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 带浏览器 UA：该片段是网页自身的接口，浏览器标识更稳妥
            //
            // A browser UA: this fragment is the web UI's own endpoint, so a browser identity is safer
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 MIDITap-updater");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    "[miditap.updater]: Asset list request returned " + (int)response.StatusCode);
                return [];
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReleasePageParser.ParseAssets(html);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.updater]: Asset list request failed: " + err.Message);
            return [];
        }
    }
}
