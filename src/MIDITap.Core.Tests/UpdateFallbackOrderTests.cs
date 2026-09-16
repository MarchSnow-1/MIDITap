// UpdateFallbackOrderTests.cs — 验证"API 优先，失败回退网页"这一顺序
//
// 为什么要专门测这个顺序：它是用户明确要求的行为
// 而写反了（先网页、或根本不回退）在正常网络下**看不出来**
// 两条路都会成功，只是慢一点或多一次请求
// 只有把"API 必然失败"和"网页必然成功"都构造成确定性条件，才能真正锁住顺序
//
// Verifies the "API first, fall back to the web page" ordering
//
// Why the order needs its own tests: it is explicitly requested behaviour
// Getting it wrong (web first, or no fallback at all) is INVISIBLE on a healthy network
// Both paths succeed, just a little slower
// Making "the API always fails" and "the page always succeeds" deterministic is the only way
// The order can then actually be pinned down

using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class UpdateFallbackOrderTests
{
    private static readonly UpdateOptions NoProxy = new(null);

    [Fact]
    public async Task Uses_the_api_when_it_succeeds_and_never_touches_the_web_page()
    {
        const string apiJson = """
            {"tag_name":"v9.9.9","assets":[{"name":"MIDITap-v9.9.9-win-x64.zip","browser_download_url":"https://example/dl","size":123}]}
            """;

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var endpoints = new UpdateEndpoints(
            api.BaseUrl + "/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

        var outcome = await UpdateChecker.CheckWithReasonAsync("1.0.0", NoProxy, endpoints);

        Assert.True(outcome.Ok);
        Assert.Equal("v9.9.9", outcome.Info!.Latest);
        Assert.Equal("MIDITap-v9.9.9-win-x64.zip", outcome.Info.Asset!.Name);
        // 关键断言：API 成功时**完全不访问**网页路径
        //
        // The key assertion: when the API succeeds the web path is **never touched at all**
        Assert.Empty(page.Requests);
    }

    [Fact]
    public async Task Falls_back_to_the_web_page_when_the_api_is_rate_limited()
    {
        // 实测中遇到的情形：API 返回 403（匿名配额用尽），并有 x-ratelimit-reset
        //
        // The case met in real-world testing: the API returns 403 (anonymous quota exhausted)
        // The 403 carries x-ratelimit-reset as well
        const string pageHtml = """
            <a href="/username/repository/releases/download/v9.9.9/MIDITap-v9.9.9-win-x64.zip">dl</a>
            """;

        using var api = new MiniHttpServer(_ => (403, null, "{\"message\":\"rate limit\"}"));
        // 复现真实 GitHub：403 同时带限流重置时间
        //
        // Reproduces real GitHub: the 403 carries the rate-limit reset time as well
        api.ExtraHeaders["x-ratelimit-reset"] = "1790000000";
        using var page = new MiniHttpServer(path => path switch
        {
            var p when p.StartsWith("/releases/latest") => (302, "/releases/tag/v9.9.9", string.Empty),
            var p when p.Contains("/expanded_assets/") => (200, null, pageHtml),
            _ => (404, null, string.Empty),
        });

        var endpoints = new UpdateEndpoints(
            api.BaseUrl + "/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

        var outcome = await UpdateChecker.CheckWithReasonAsync("1.0.0", NoProxy, endpoints);

        Assert.True(outcome.Ok);
        Assert.Equal("v9.9.9", outcome.Info!.Latest);
        Assert.Equal("MIDITap-v9.9.9-win-x64.zip", outcome.Info.Asset!.Name);
        Assert.Contains("/releases/latest", page.Requests);
    }

    [Fact]
    public async Task Falls_back_when_the_api_returns_a_server_error()
    {
        // 放宽后的回退条件：API 的 **任何** 失败都触发回退，不只 403
        // api.github.com 与 github.com 是不同主机，5xx 可能只影响其中一个
        //
        // The relaxed fallback condition: **any** API failure triggers the fallback, not just 403
        // api.github.com and github.com are different hosts, so a 5xx may affect only one of them
        const string pageHtml = """
            <a href="/username/repository/releases/download/v3.0.0/MIDITap-v3.0.0-win-x64.zip">dl</a>
            """;

        using var api = new MiniHttpServer(_ => (500, null, "server error"));
        using var page = new MiniHttpServer(path => path switch
        {
            var p when p.StartsWith("/releases/latest") => (302, "/releases/tag/v3.0.0", string.Empty),
            var p when p.Contains("/expanded_assets/") => (200, null, pageHtml),
            _ => (404, null, string.Empty),
        });

        var endpoints = new UpdateEndpoints(
            api.BaseUrl + "/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

        var outcome = await UpdateChecker.CheckWithReasonAsync("1.0.0", NoProxy, endpoints);

        Assert.True(outcome.Ok);
        Assert.Equal("v3.0.0", outcome.Info!.Latest);
    }

    [Fact]
    public async Task Falls_back_when_the_api_is_unreachable()
    {
        // 网络/代理层失败（异常路径）同样要回退：某些代理会拦截 API 域名而放行网页
        //
        // A network/proxy-layer failure (the exception path) must fall back too
        // Some proxies block the API domain while letting the web page through
        const string pageHtml = """
            <a href="/username/repository/releases/download/v4.2.0/MIDITap-v4.2.0-win-x64.zip">dl</a>
            """;

        using var page = new MiniHttpServer(path => path switch
        {
            var p when p.StartsWith("/releases/latest") => (302, "/releases/tag/v4.2.0", string.Empty),
            var p when p.Contains("/expanded_assets/") => (200, null, pageHtml),
            _ => (404, null, string.Empty),
        });

        // 端口 9 通常无人监听 -> 连接失败 / Port 9 usually has nobody listening -> the connection fails
        var endpoints = new UpdateEndpoints(
            "http://127.0.0.1:9/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

        var outcome = await UpdateChecker.CheckWithReasonAsync("1.0.0", NoProxy, endpoints);

        Assert.True(outcome.Ok);
        Assert.Equal("v4.2.0", outcome.Info!.Latest);
    }

    [Fact]
    public async Task Reports_the_api_failure_when_both_paths_fail()
    {
        // 两条路都失败时，上报 API 的原因（带 HTTP 状态码，比网页路径的异常更有说明性）
        // 而不是换成一条更含糊的错误
        //
        // When both paths fail, report the API reason rather than swapping in a vaguer error
        // The API reason carries the HTTP status code and is more informative than the web path's exception
        using var api = new MiniHttpServer(_ => (500, null, "boom"));
        using var page = new MiniHttpServer(_ => (500, null, "also boom"));

        var endpoints = new UpdateEndpoints(
            api.BaseUrl + "/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

        var outcome = await UpdateChecker.CheckWithReasonAsync("1.0.0", NoProxy, endpoints);

        Assert.False(outcome.Ok);
        Assert.Equal(UpdateFailure.Http, outcome.Failure);
        Assert.Equal(500, outcome.HttpStatus);
    }

    [Fact]
    public async Task Does_not_fall_back_when_the_api_says_up_to_date()
    {
        // "已是最新"是**成功**结果，不能触发回退 —— 否则每次启动都会多打一次网页请求
        //
        // "Already up to date" is a **success** result and must not trigger the fallback
        // Otherwise every launch would make one extra web request
        const string apiJson = """{"tag_name":"v1.0.0","assets":[]}""";

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var endpoints = new UpdateEndpoints(
            api.BaseUrl + "/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

        var outcome = await UpdateChecker.CheckWithReasonAsync("1.0.0", NoProxy, endpoints);

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Info);   // 无更新 / No update
        Assert.Empty(page.Requests);
    }

    [Fact]
    public async Task Web_path_that_finds_an_older_tag_reports_no_update()
    {
        // 回退路径也必须遵守版本比较：网页说最新是旧版本时，不能报告"有更新"
        //
        // The fallback path must obey version comparison too
        // When the page says the newest release is older, "an update is available" must not be reported
        using var api = new MiniHttpServer(_ => (403, null, "rate limited"));
        using var page = new MiniHttpServer(path => path.StartsWith("/releases/latest")
            ? (302, "/releases/tag/v1.0.0", string.Empty)
            : (404, null, string.Empty));

        var endpoints = new UpdateEndpoints(
            api.BaseUrl + "/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

        var outcome = await UpdateChecker.CheckWithReasonAsync("2.0.0", NoProxy, endpoints);

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Info);
    }

    [Fact]
    public async Task Web_path_skips_the_asset_request_when_already_up_to_date()
    {
        // 已是最新时不必再取资源列表（省一次往返）。用请求数验证
        //
        // When already up to date the asset list need not be fetched again (saving one round trip)
        // Verified through the request count
        using var api = new MiniHttpServer(_ => (403, null, "rate limited"));
        using var page = new MiniHttpServer(path => path.StartsWith("/releases/latest")
            ? (302, "/releases/tag/v1.0.0", string.Empty)
            : (200, null, "<a href=\"/username/repository/releases/download/v1.0.0/x-win-x64.zip\">d</a>"));

        var endpoints = new UpdateEndpoints(
            api.BaseUrl + "/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

        await UpdateChecker.CheckWithReasonAsync("2.0.0", NoProxy, endpoints);

        Assert.Single(page.Requests);
        Assert.StartsWith("/releases/latest", page.Requests[0]);
    }
}
