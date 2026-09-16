// ReleasePageParser.cs — 从**网页**（而非 API）提取版本与资源信息
// 为什么需要它：api.github.com 对匿名请求限流为每出口 IP 每小时 60 次，用尽后返回 403
// 此时用户会看到"检查更新失败"，而其实完全有别的路可走
// github.com 的网页走的是另一套（实测响应里没有 x-ratelimit-* 头），因此可以复用
//   * GET https://github.com/<owner>/<repo>/releases/latest
//     不跟随重定向时返回 302，Location 指向 .../releases/tag/<tag>，版本号就在里面
//   * GET https://github.com/<owner>/<repo>/releases/expanded_assets/<tag>
//     这是网页懒加载"Assets"列表用的片段端点，返回含真实下载链接的 HTML
// 这**不是**抓取式的脆弱实现：解析的是 GitHub 自己使用的稳定 URL 结构
// 实测证据：v1.7.1 的真实资源名是 MIDITap-v1.7.1-win_x64.zip（下划线）
// 而当前 CI 产出 MIDITap-<tag>-win-x64.zip（连字符）
// 说明"按命名约定猜文件名"必然出错，必须读取真实列表，本模块正是为此
//
// Extracts version and asset information from the GitHub WEB page rather than the API
//
// Why this exists: api.github.com rate-limits anonymous requests to 60/hour per egress IP
// It then returns 403, so the user sees "update check failed" while a good alternative exists
// The github.com web page is a different budget (measured: no x-ratelimit-* headers), so it can be reused
//   * GET https://github.com/<owner>/<repo>/releases/latest — the redirect carries the tag
//   * GET https://github.com/<owner>/<repo>/releases/expanded_assets/<tag>
//     This is the fragment endpoint the web UI itself lazy-loads to list assets
//
// This is NOT fragile scraping: it parses stable URL structures GitHub itself uses
// Measured evidence: v1.7.1's real asset is MIDITap-v1.7.1-win_x64.zip (underscore)
// The current CI produces MIDITap-<tag>-win-x64.zip (hyphen)
// Guessing the file name from a convention therefore breaks, and reading the real list is required

namespace MIDITap.Core.Update;

public static class ReleasePageParser
{
    /// <summary>网页资源链接所在的主机 / Host of the web asset links</summary>
    public const string WebHost = "https://github.com";

    /// <summary>
    /// 网页资源链接的固定片段，用于把相对路径转成绝对路径
    ///
    /// The fixed fragment of a web asset link, used to turn a relative path into an absolute URL
    /// </summary>
    private const string DownloadPathMarker = "/releases/download/";

    /// <summary>
    /// 从重定向目标或最终 URL 中解析版本标签
    /// 例：https://github.com/username/repository/releases/tag/v1.7.1?x=1  ->  v1.7.1
    /// 解析失败返回 null（例如 /releases/latest 没有任何发布时会被重定向到 /releases 本身）
    ///
    /// Parses the tag out of a redirect target or final URL
    /// Returns null when there is no tag, e.g. a repository with no releases redirects to /releases itself
    /// </summary>
    public static string? ParseTagFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // 去掉查询与片段：Location 可能带 ?expanded=true 之类
        //
        // Query and fragment are dropped: the Location may carry something like ?expanded=true
        var text = url.Trim();
        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        const string marker = "/releases/tag/";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var tag = text[(index + marker.Length)..].Trim().Trim('/');
        if (tag.Length == 0)
        {
            return null;
        }

        // 标签可能被 URL 编码（如含空格）
        //
        // The tag may be URL-encoded (e.g. when it contains a space)
        try
        {
            tag = Uri.UnescapeDataString(tag);
        }
        catch
        {
            // 解码失败就用原样值
            //
            // A failed decode keeps the raw value
        }
        return tag.Length > 0 ? tag : null;
    }

    /// <summary>
    /// 从 expanded_assets 的 HTML 中解析出所有发布资源
    /// 链接可能是相对路径（实测 GitHub 返回的是 "/username/repository/releases/download/..."）
    /// 因此统一补全为绝对地址
    ///
    /// Parses the release assets out of the expanded_assets HTML
    /// Links come back relative in practice, so they are completed into absolute URLs
    /// </summary>
    public static IReadOnlyList<UpdateAsset> ParseAssets(string? html)
    {
        var result = new List<UpdateAsset>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return result;
        }

        // 该片段里还嵌着 GitHub 提供的 SHA-256（实测与 API 的 assets[].digest 完全一致）
        // 因此一次请求就能同时拿到"文件名"和"完整性依据"，不必再下载 .sha256 文件
        //
        // The fragment also embeds GitHub's SHA-256, measured identical to the API's assets[].digest
        // So one request yields both the file name and the integrity value
        // No separate .sha256 download is needed
        var digest = ChecksumText.FromHtml(html);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(
                     html, "href=\"([^\"]*" + DownloadPathMarker + "[^\"]+)\"",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var href = match.Groups[1].Value.Trim();
            if (href.Length == 0)
            {
                continue;
            }

            var absolute = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : WebHost + (href.StartsWith('/') ? href : "/" + href);

            // 文件名 = 路径最后一段（可能被编码）
            //
            // File name = the last segment of the path (possibly encoded)
            var path = absolute;
            var cut = path.IndexOfAny(['?', '#']);
            if (cut >= 0)
            {
                path = path[..cut];
            }
            var slash = path.LastIndexOf('/');
            if (slash < 0 || slash == path.Length - 1)
            {
                continue;
            }
            var name = path[(slash + 1)..];
            try
            {
                name = Uri.UnescapeDataString(name);
            }
            catch
            {
                // 解码失败用原样值
                //
                // A failed decode keeps the raw value
            }

            if (name.Length == 0 || !seen.Add(absolute))
            {
                continue;
            }

            // 大小在网页上不可得（API 才有）；下载流程不依赖它，缺失时由调用方按未知处理
            //
            // The size is not available on the web page (only the API carries it)
            // The download flow does not depend on it, and the caller treats a missing value as unknown
            result.Add(new UpdateAsset(name, absolute, 0, digest));
        }
        return result;
    }
}
