// ReleasePageCheckerTests.cs — 经**本地服务器**确定性验证网页路径
//
// 这组测试覆盖的是真实网络代码，而不只是纯字符串函数
// 具体包括 HttpClient 的 302 处理、Location 解析、片段 HTML 解析
// 因为这条路径的存在意义就是"API 不可用时仍能工作"
// 它自己再依赖外网可用性就本末倒置了
//
// Deterministically verifies the web path against a LOCAL server
//
// These cover the real network code (HttpClient 302 handling, Location parsing, fragment HTML parsing)
// They are not just pure string helpers
// The path exists so the app works when the API does not
// So testing it via the same unreliable external network would defeat the purpose

using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class ReleasePageCheckerTests
{
    private static readonly UpdateOptions NoProxy = new(null);

    [Fact]
    public async Task Reads_the_tag_from_a_302_location_header()
    {
        // 与实测的 GitHub 行为一致：/releases/latest 返回 302，Location 指向 /releases/tag/<tag>
        //
        // Consistent with GitHub's measured behaviour: /releases/latest returns a 302
        // That 302's Location points at /releases/tag/<tag>
        using var server = new MiniHttpServer(path => path.StartsWith("/releases/latest")
            ? (302, "/releases/tag/v9.9.9", string.Empty)
            : (404, null, string.Empty));

        var tag = await ReleasePageChecker.GetLatestTagAsync(
            server.BaseUrl + "/releases/latest", NoProxy);

        Assert.Equal("v9.9.9", tag);
        Assert.Contains("/releases/latest", server.Requests);
    }

    [Fact]
    public async Task Does_not_follow_the_redirect()
    {
        // 关键行为：必须**不跟随**重定向，否则拿不到 Location —— 那正是版本号的来源
        //
        // Key behaviour: the redirect must NOT be followed
        // Otherwise the Location cannot be read, and that is exactly where the version number comes from
        var redirectTargetHit = false;
        using var server = new MiniHttpServer(path =>
        {
            if (path.StartsWith("/releases/latest"))
            {
                return (302, "/releases/tag/v9.9.9", string.Empty);
            }
            if (path.StartsWith("/releases/tag/"))
            {
                redirectTargetHit = true;
                return (200, null, "<html>release page</html>");
            }
            return (404, null, string.Empty);
        });

        var tag = await ReleasePageChecker.GetLatestTagAsync(
            server.BaseUrl + "/releases/latest", NoProxy);

        Assert.Equal("v9.9.9", tag);
        Assert.False(redirectTargetHit, "不应跟随重定向去抓发布页内容");
    }

    [Fact]
    public void Falls_back_to_the_final_url_when_a_proxy_hides_the_location()
    {
        // 有些代理会自己跟随重定向只返回 200：此时没有 Location 头
        // 必须能从 HttpClient 记录的最终 URL 里取出标签
        // 否则这类用户永远拿不到版本
        //
        // Some proxies follow the redirect themselves and return only a 200
        // There is then no Location header, so the tag must be recoverable from the final URL
        // Sync because it only exercises the parsing of that URL shape
        Assert.Equal("v3.1.4", ReleasePageParser.ParseTagFromUrl(
            "https://github.com/username/repository/releases/tag/v3.1.4"));
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_release()
    {
        // 仓库没有发布时不会跳到 /tag/...，此时必须返回 null 而不是编造版本号
        //
        // When the repository has no release, it does not redirect to /tag/...
        // Null must be returned rather than inventing a version number
        using var server = new MiniHttpServer(_ => (302, "/releases", string.Empty));

        var tag = await ReleasePageChecker.GetLatestTagAsync(
            server.BaseUrl + "/releases/latest", NoProxy);

        Assert.Null(tag);
    }

    [Fact]
    public async Task Parses_assets_from_the_fragment_html()
    {
        // 片段端点的真实形态：相对链接，指向 /releases/download/<tag>/<file>
        //
        // The real shape of the fragment endpoint: relative links pointing at /releases/download/<tag>/<file>
        const string html = """
            <div class="Box-row">
              <a href="/username/repository/releases/download/v9.9.9/MIDITap-v9.9.9-win-x64.zip" rel="nofollow">MIDITap-v9.9.9-win-x64.zip</a>
            </div>
            <div class="Box-row">
              <a href="/username/repository/releases/download/v9.9.9/MIDITap-v9.9.9-win-arm64.zip" rel="nofollow">arm64</a>
            </div>
            """;

        using var server = new MiniHttpServer(path => path.Contains("/expanded_assets/")
            ? (200, null, html)
            : (404, null, string.Empty));

        var assets = await ReleasePageChecker.GetAssetsAsync(
            server.BaseUrl + "/releases", "v9.9.9", NoProxy);

        Assert.Equal(2, assets.Count);
        Assert.Contains("/releases/expanded_assets/v9.9.9", server.Requests);

        var selected = UpdateAssetSelector.Select(assets);
        Assert.NotNull(selected);
        Assert.Equal("MIDITap-v9.9.9-win-x64.zip", selected!.Name);
    }

    [Fact]
    public async Task Asset_list_request_failure_is_not_fatal()
    {
        // 资源列表拉不到时返回空列表（调用方仍可报告"有新版本"并让用户去 Releases 页面）
        // 不能抛异常把整个检查流程打断
        //
        // When the asset list cannot be fetched, an empty list is returned
        // The caller can still report "there is a new version" and send the user to the Releases page
        // An exception must not be thrown that breaks the whole check
        using var server = new MiniHttpServer(_ => (500, null, "boom"));

        var assets = await ReleasePageChecker.GetAssetsAsync(
            server.BaseUrl + "/releases", "v9.9.9", NoProxy);

        Assert.Empty(assets);
    }

    [Fact]
    public async Task Unreachable_host_returns_null_instead_of_throwing()
    {
        // 端口无人监听：必须安静地返回 null，界面照常给出提示
        //
        // Nothing is listening on the port: null must be returned quietly and the UI still shows its message
        var tag = await ReleasePageChecker.GetLatestTagAsync(
            "http://127.0.0.1:9/releases/latest", NoProxy);

        Assert.Null(tag);
    }

    [Fact]
    public async Task Full_web_path_produces_an_update_when_a_newer_tag_exists()
    {
        // 端到端（本地）：302 -> 标签 -> 资源列表 -> 选择器挑出正确文件
        // 这一串正是 API 返回 403 时实际走的流程
        //
        // End-to-end (local): 302 -> tag -> asset list -> the selector picking the right file
        // This chain is exactly the flow actually taken when the API answers 403
        const string html = """
            <a href="/username/repository/releases/download/v2.5.0/MIDITap-v2.5.0-win-x64.zip">dl</a>
            """;

        using var server = new MiniHttpServer(path => path switch
        {
            var p when p.StartsWith("/releases/latest") => (302, "/releases/tag/v2.5.0", string.Empty),
            var p when p.Contains("/expanded_assets/") => (200, null, html),
            _ => (404, null, string.Empty),
        });

        var tag = await ReleasePageChecker.GetLatestTagAsync(
            server.BaseUrl + "/releases/latest", NoProxy);
        Assert.Equal("v2.5.0", tag);

        var assets = await ReleasePageChecker.GetAssetsAsync(
            server.BaseUrl + "/releases", tag!, NoProxy);
        var selected = UpdateAssetSelector.Select(assets);

        Assert.NotNull(selected);
        Assert.Equal("MIDITap-v2.5.0-win-x64.zip", selected!.Name);
        // 说明流程确实走了两步（先拿标签，再拿资源），而不是靠猜测文件名
        //
        // This shows the flow really took two steps (fetch the tag first, then the assets)
        // The flow does not guess at the file name
        Assert.Equal(2, server.Requests.Count);
    }
}
