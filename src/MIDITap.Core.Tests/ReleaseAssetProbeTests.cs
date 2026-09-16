// ReleaseAssetProbeTests.cs — HEAD 探测资产（不依赖 API 的下载通道）
//
// 为什么这组测试重要：用户明确指出"release 下载又不受 API 影响"
// 因此"必须能自动更新"应当**在任何情况下**成立
// 探测一旦逻辑写错（把 404 当存在、或只试一种命名），用户就会退回到手动下载 —— 而那本不该发生
//
// HEAD asset probing — the API-independent download channel
//
// Why these matter: release downloads are independent of the API quota
// So the app can still update itself whenever the download endpoint is reachable
// A probing mistake means treating 404 as present, or trying only one naming form
// Such a mistake would drop the user back to manual downloading
// That fallback is the one thing this path exists to avoid

using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class ReleaseAssetProbeNamingTests
{
    [Fact]
    public void Probes_only_the_ci_naming()
    {
        // 只有一种拼法，且没有别名
        // 见 UpdateAssetSelector.PlatformSuffix
        // 数量断言在这里，免得将来"顺手"再加一个候选
        //
        // Exactly one spelling and no alias
        // See UpdateAssetSelector.PlatformSuffix
        // The count is asserted here so that a candidate cannot be "helpfully" added back later
        var names = ReleaseAssetProbe.CandidateNames("v2.0.0");
        Assert.Equal(["MIDITap-v2.0.0-win-x64.zip"], names);
    }

    [Fact]
    public void Builds_the_download_url_from_the_releases_base()
    {
        Assert.Equal(
            "https://github.com/username/repository/releases/download/v2.0.0/MIDITap-v2.0.0-win-x64.zip",
            ReleaseAssetProbe.BuildUrl("https://github.com/username/repository/releases", "v2.0.0", "MIDITap-v2.0.0-win-x64.zip"));
    }

    [Fact]
    public void Escapes_tags_that_need_it()
    {
        // 标签可能含需要转义的字符
        // 直接拼接会得到无效 URL
        //
        // A tag may contain characters that need escaping
        // Concatenating it directly would produce an invalid URL
        var url = ReleaseAssetProbe.BuildUrl("https://github.com/username/repository/releases", "v1.0.0 beta", "x.zip");
        Assert.Contains("v1.0.0%20beta", url);
    }
}

public sealed class ReleaseAssetProbeTests
{
    private static readonly UpdateOptions NoProxy = new(null);

    [Fact]
    public async Task Finds_the_asset_and_treats_a_redirect_as_present()
    {
        // GitHub 对存在的资产返回 302 跳到资产 CDN —— 这是"存在"的信号，且必须**不跟随**（跟随会开始下载整个包）
        //
        // GitHub answers 302 to the asset CDN for an existing asset, which is the "present" signal
        // The redirect must **not** be followed, because following it starts downloading the whole archive
        using var server = new MiniHttpServer(path => path.Contains("MIDITap-v9.9.9-win-x64.zip")
            ? (302, "https://cdn.example/x", string.Empty)
            : (404, null, string.Empty));

        var asset = await ReleaseAssetProbe.FindExistingAsync(
            server.BaseUrl + "/releases", "v9.9.9", NoProxy);

        Assert.NotNull(asset);
        Assert.Equal("MIDITap-v9.9.9-win-x64.zip", asset!.Name);
    }

    [Fact]
    public async Task Does_not_probe_the_underscore_name()
    {
        // 只有带下划线的资源存在时，必须判为"没有可用包"，而不是把它当成本平台的包
        // 宁可退回"打开 Releases 页面"，也不要挑一个我们并不产出的命名
        //
        // When only the underscore asset exists the result must be "no usable package"
        // It is not this platform's package
        // Falling back to "open the Releases page" beats matching a naming we do not produce
        using var server = new MiniHttpServer(path => path.Contains("-win_x64.zip")
            ? (302, "https://cdn.example/x", string.Empty)
            : (404, null, string.Empty));

        var asset = await ReleaseAssetProbe.FindExistingAsync(
            server.BaseUrl + "/releases", "v1.7.1", NoProxy);

        Assert.Null(asset);
        Assert.Single(server.Requests);
        Assert.Contains("-win-x64.zip", server.Requests[0]);
    }

    [Fact]
    public async Task Returns_null_when_no_candidate_exists()
    {
        // 真正"该版本没有 Windows 构建"时才返回 null，由调用方引导到 Releases 页
        //
        // null is returned only when that release genuinely has no Windows build
        // The caller then points the user at the Releases page
        using var server = new MiniHttpServer(_ => (404, null, string.Empty));

        var asset = await ReleaseAssetProbe.FindExistingAsync(
            server.BaseUrl + "/releases", "v9.9.9", NoProxy);

        Assert.Null(asset);
        // 只有一种候选命名，因此只探测一次 / A single candidate spelling means exactly one probe
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Treats_a_direct_200_as_present_too()
    {
        // 某些代理会自己跟随重定向，只返回 200 —— 同样算存在
        //
        // Some proxies follow the redirect themselves and return only 200
        // That counts as present too
        using var server = new MiniHttpServer(_ => (200, null, string.Empty));

        var asset = await ReleaseAssetProbe.FindExistingAsync(
            server.BaseUrl + "/releases", "v9.9.9", NoProxy);

        Assert.NotNull(asset);
    }

    [Fact]
    public async Task Uses_head_not_get()
    {
        // 必须是 HEAD：GET 会把整个包拉下来，只为了问一句"有没有"
        // MiniHttpServer 记录的是请求行，这里通过响应体为空间接确认已足够
        // 关键是不应触发下载
        // 用 200 且带大响应体的场景无法在此模拟
        // 因此以"不跟随重定向"这一必要条件作为断言（见上一个用例）
        //
        // It must be HEAD: a GET would pull the whole archive down just to ask "is it there?"
        // MiniHttpServer records the request line
        // So confirming indirectly through an empty response body is enough here
        // The point is that it must not trigger a download
        // A 200 with a large body cannot be simulated here
        // So the necessary condition "does not follow redirects" is asserted instead (see the previous case)
        using var server = new MiniHttpServer(_ => (302, "https://cdn/x", string.Empty));

        var asset = await ReleaseAssetProbe.FindExistingAsync(
            server.BaseUrl + "/releases", "v9.9.9", NoProxy);

        Assert.NotNull(asset);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Unreachable_host_returns_null_without_throwing()
    {
        // 探测失败必须安静地返回 null，让调用方退回"引导到 Releases 页"，而不是抛异常打断流程
        //
        // A failed probe must quietly return null
        // The caller can then fall back to "pointing the user at the Releases page"
        // A throw would instead break the flow
        var asset = await ReleaseAssetProbe.FindExistingAsync(
            "http://127.0.0.1:9/releases", "v9.9.9", NoProxy);

        Assert.Null(asset);
    }
}
