// BridgeUpdateCheckerTests.cs — 扩展功能更新检查：回退顺序、版本比较、dev 版本
//
// 专门测顺序：写反了（先网页、或根本不回退）在正常网络下看不出来 —— 两条路都会成功，只是慢一点
// 因此必须把"API 失败"与"网页成功"都构造成确定性条件，顺序才锁得住
//
// dev 版本单独一组用例：上游本地构建输出 "midibridge-client dev"
// 那时没有可比较的版本号，检查必须报"不可比较"而不是"已是最新" —— 后者会让用户以为一切正常，
// 而他跑的是自编译版本，拿不到升级提示
//
// BridgeUpdateCheckerTests.cs — the extension's update check: fallback order, version comparison, dev versions
//
// The order gets its own test: getting it wrong (web first, or no fallback at all) is invisible on a healthy network —
// both paths succeed, just slower
// So the API failure and the page success must both be deterministic conditions for the order to be pinned down
//
// The dev version gets its own group: a local upstream build prints "midibridge-client dev"
// There is no comparable version number then, and the check must report NOT COMPARABLE rather than "up to date" — the
// latter would tell the user everything is fine while the copy he runs is self-built and gets no upgrade notice

using MIDITap.Core.Bridge;
using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class BridgeUpdateCheckerTests
{
    private static readonly UpdateOptions NoProxy = new(null);

    private static BridgeUpdateEndpoints EndpointsFor(MiniHttpServer api, MiniHttpServer page)
        => new(api.BaseUrl + "/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

    /// <summary>与上游命名约定一致的资产名 / An asset name matching the upstream convention</summary>
    private const string UpstreamAssetName = "MIDIBridge-Client-v9.9.9-windows-amd64.zip";

    // ================================================================ API 优先 / API first

    [Fact]
    public async Task Uses_the_api_when_it_succeeds_and_never_touches_the_web_page()
    {
        const string apiJson = """
            {"tag_name":"v9.9.9","assets":[{"name":"MIDIBridge-Client-v9.9.9-windows-amd64.zip","browser_download_url":"https://example/dl","size":123}]}
            """;

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var outcome = await BridgeUpdateChecker.CheckAsync(
            "v1.0.0", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.False(outcome.NotComparable);
        Assert.Equal("v9.9.9", outcome.Info!.Latest);
        Assert.Equal(UpstreamAssetName, outcome.Info.Asset!.Name);
        Assert.False(outcome.Info.ViaFallback);

        // 关键断言：API 成功时完全不访问网页路径
        //
        // The key assertion: when the API succeeds the web path is not touched at all
        Assert.Empty(page.Requests);
    }

    [Fact]
    public async Task Reports_no_update_when_the_installed_version_is_current()
    {
        const string apiJson = """{"tag_name":"v1.0.0","assets":[]}""";

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var outcome = await BridgeUpdateChecker.CheckAsync("v1.0.0", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Info);
        Assert.False(outcome.NotComparable);
        Assert.Empty(page.Requests);
    }

    [Fact]
    public async Task Reports_no_update_when_the_release_is_older()
    {
        // 上游回退过版本时不能提示"有更新"
        //
        // When the upstream rolls back, "an update is available" must not be reported
        const string apiJson = """{"tag_name":"v1.0.0","assets":[]}""";

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var outcome = await BridgeUpdateChecker.CheckAsync("v9.9.9", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Info);
    }

    [Fact]
    public async Task Carries_the_release_notes_from_the_api()
    {
        // 发布说明的 markdown 原文只在 API 的 body 字段里，供界面展示"更新内容"
        //
        // The raw release-notes markdown lives in the API's body field alone, and the UI shows "what's new" from it
        const string markdown = "## Faster\n\n- one\n- two\n";
        const string apiJson = """
            {"tag_name":"v9.9.9","body":"## Faster\n\n- one\n- two\n","assets":[]}
            """;

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var outcome = await BridgeUpdateChecker.CheckAsync("v1.0.0", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.Equal(markdown, outcome.Info!.Notes);
    }

    [Fact]
    public async Task Reports_the_digest_of_the_chosen_asset()
    {
        // 摘要由 GitHub 与资产信息一起给出；DigestAvailable 为 false 时 BridgeInstaller 会拒绝安装
        // 界面据此提前说明，而不是让用户点了"下载并安装"才看到失败
        //
        // The digest arrives with the asset information from GitHub; a false DigestAvailable makes BridgeInstaller refuse
        // the install, and the UI says so up front instead of failing after the user clicks install
        const string digest = "448d50fee6c9086f1c300adab30730a5d0822b10eb53737e7a99db0150c86e0a";
        var apiJson = """
            {"tag_name":"v9.9.9","assets":[{"name":"MIDIBridge-Client-v9.9.9-windows-amd64.zip","browser_download_url":"https://example/dl","size":123,"digest":"sha256:DIGEST"}]}
            """.Replace("DIGEST", digest);

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var outcome = await BridgeUpdateChecker.CheckAsync("v1.0.0", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.True(outcome.Info!.DigestAvailable);
        Assert.Equal(digest, outcome.Info.Asset!.Sha256);
    }

    [Fact]
    public async Task Reports_that_no_digest_is_available_when_the_api_omits_it()
    {
        const string apiJson = """
            {"tag_name":"v9.9.9","assets":[{"name":"MIDIBridge-Client-v9.9.9-windows-amd64.zip","browser_download_url":"https://example/dl","size":123}]}
            """;

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var outcome = await BridgeUpdateChecker.CheckAsync("v1.0.0", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.False(outcome.Info!.DigestAvailable);
    }

    // ================================================================ 回退顺序 / The fallback order

    [Fact]
    public async Task Falls_back_to_the_web_page_when_the_api_is_rate_limited()
    {
        const string pageHtml = """
            <a href="/username/repository/releases/download/v9.9.9/MIDIBridge-Client-v9.9.9-windows-amd64.zip">dl</a>
            """;

        using var api = new MiniHttpServer(_ => (403, null, "{\"message\":\"rate limit\"}"));
        api.ExtraHeaders["x-ratelimit-reset"] = "1790000000";
        using var page = new MiniHttpServer(path => path switch
        {
            var p when p.StartsWith("/releases/latest") => (302, "/releases/tag/v9.9.9", string.Empty),
            var p when p.Contains("/expanded_assets/") => (200, null, pageHtml),
            _ => (404, null, string.Empty),
        });

        var outcome = await BridgeUpdateChecker.CheckAsync("v1.0.0", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.Equal("v9.9.9", outcome.Info!.Latest);
        Assert.Equal(UpstreamAssetName, outcome.Info.Asset!.Name);
        Assert.Contains("/releases/latest", page.Requests);
        // 网页路径拿不到 markdown 原文，因此没有发布说明，且必须标出这是回退路径
        //
        // The web path cannot obtain the markdown source, so there are no notes, and the result must be marked as the
        // fallback path
        Assert.Null(outcome.Info.Notes);
        Assert.True(outcome.Info.ViaFallback);
    }

    [Fact]
    public async Task Carries_the_quota_from_a_403_into_the_fallback_result()
    {
        const string pageHtml = """
            <a href="/username/repository/releases/download/v9.9.9/MIDIBridge-Client-v9.9.9-windows-amd64.zip">dl</a>
            """;

        using var api = new MiniHttpServer(_ => (403, null, "{\"message\":\"rate limit\"}"));
        api.ExtraHeaders["x-ratelimit-reset"] = "1790000000";
        api.ExtraHeaders["x-ratelimit-remaining"] = "0";
        api.ExtraHeaders["x-ratelimit-limit"] = "60";
        using var page = new MiniHttpServer(path => path switch
        {
            var p when p.StartsWith("/releases/latest") => (302, "/releases/tag/v9.9.9", string.Empty),
            var p when p.Contains("/expanded_assets/") => (200, null, pageHtml),
            _ => (404, null, string.Empty),
        });

        var outcome = await BridgeUpdateChecker.CheckAsync("v1.0.0", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        var quota = outcome.RateLimit;
        Assert.NotNull(quota);
        Assert.Equal(0, quota.Remaining);
        Assert.Equal(60, quota.Limit);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000).ToLocalTime(), quota.Reset);
    }

    [Fact]
    public async Task Falls_back_when_the_api_returns_a_server_error()
    {
        // 回退条件放宽到"API 的任何失败"：api 与网页是不同主机，5xx 可能只影响其中一个
        //
        // The fallback covers any API failure: the API and the web page are different hosts, so a 5xx may affect only one
        const string pageHtml = """
            <a href="/username/repository/releases/download/v3.0.0/MIDIBridge-Client-v3.0.0-windows-amd64.zip">dl</a>
            """;

        using var api = new MiniHttpServer(_ => (500, null, "server error"));
        using var page = new MiniHttpServer(path => path switch
        {
            var p when p.StartsWith("/releases/latest") => (302, "/releases/tag/v3.0.0", string.Empty),
            var p when p.Contains("/expanded_assets/") => (200, null, pageHtml),
            _ => (404, null, string.Empty),
        });

        var outcome = await BridgeUpdateChecker.CheckAsync("v1.0.0", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.Equal("v3.0.0", outcome.Info!.Latest);
    }

    [Fact]
    public async Task Falls_back_when_the_api_is_unreachable()
    {
        // 网络/代理层失败（异常路径）同样要回退：某些代理会拦截 API 域名而放行网页
        //
        // A network/proxy-layer failure (the exception path) must fall back too: some proxies block the API domain while
        // letting the web page through
        const string pageHtml = """
            <a href="/username/repository/releases/download/v4.2.0/MIDIBridge-Client-v4.2.0-windows-amd64.zip">dl</a>
            """;

        using var page = new MiniHttpServer(path => path switch
        {
            var p when p.StartsWith("/releases/latest") => (302, "/releases/tag/v4.2.0", string.Empty),
            var p when p.Contains("/expanded_assets/") => (200, null, pageHtml),
            _ => (404, null, string.Empty),
        });

        // 端口 9 通常无人监听 -> 连接失败 / Port 9 usually has nobody listening -> the connection fails
        var endpoints = new BridgeUpdateEndpoints(
            "http://127.0.0.1:9/latest", page.BaseUrl + "/releases/latest", page.BaseUrl + "/releases");

        var outcome = await BridgeUpdateChecker.CheckAsync("v1.0.0", NoProxy, endpoints);

        Assert.True(outcome.Ok);
        Assert.Equal("v4.2.0", outcome.Info!.Latest);
    }

    [Fact]
    public async Task Reports_the_api_failure_when_both_paths_fail()
    {
        // 两条路都失败时上报 API 的原因（带 HTTP 状态码，比网页路径的异常更具说明性）
        //
        // When both fail, the API's reason is reported, since it carries the HTTP status and is more informative
        using var api = new MiniHttpServer(_ => (500, null, "boom"));
        using var page = new MiniHttpServer(_ => (500, null, "also boom"));

        var outcome = await BridgeUpdateChecker.CheckAsync("v1.0.0", NoProxy, EndpointsFor(api, page));

        Assert.False(outcome.Ok);
        Assert.Equal(UpdateFailure.Http, outcome.Failure);
        Assert.Equal(500, outcome.HttpStatus);
        Assert.Null(outcome.Info);
    }

    [Fact]
    public async Task The_web_path_reports_no_update_for_an_older_tag()
    {
        // 回退路径也必须遵守版本比较：网页说最新是旧版本时不能报"有更新"
        //
        // The fallback path must obey version comparison too: an older tag on the page must not read as an update
        using var api = new MiniHttpServer(_ => (403, null, "rate limited"));
        using var page = new MiniHttpServer(path => path.StartsWith("/releases/latest")
            ? (302, "/releases/tag/v1.0.0", string.Empty)
            : (404, null, string.Empty));

        var outcome = await BridgeUpdateChecker.CheckAsync("v2.0.0", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Info);
    }

    [Fact]
    public async Task The_web_path_skips_the_asset_request_when_already_up_to_date()
    {
        // 已是最新时不必再取资源列表（省一次往返），用请求数验证
        //
        // When already up to date the asset list need not be fetched (saving one round trip), verified by request count
        using var api = new MiniHttpServer(_ => (403, null, "rate limited"));
        using var page = new MiniHttpServer(path => path.StartsWith("/releases/latest")
            ? (302, "/releases/tag/v1.0.0", string.Empty)
            : (200, null, "<a href=\"/username/repository/releases/download/v1.0.0/x-windows-amd64.zip\">d</a>"));

        await BridgeUpdateChecker.CheckAsync("v2.0.0", NoProxy, EndpointsFor(api, page));

        Assert.Single(page.Requests);
        Assert.StartsWith("/releases/latest", page.Requests[0]);
    }

    // ================================================================ dev 版本 / Dev builds

    [Theory]
    [InlineData("midibridge-client dev")]
    [InlineData("midibridge-client dev\n")]
    [InlineData("midibridge-client DEV")]
    public void A_dev_build_reports_a_version_that_is_not_comparable(string output)
    {
        // dev 构建没有发布版本号：可以展示（用户看得见自己跑的是 dev），但不能参与更新比较
        // 若把它当成"已是最新"，用户就收不到升级提示 —— 而那正是他最需要的
        //
        // A dev build has no release number: it can be displayed (the user sees he runs a dev build) but must not take
        // part in an update comparison
        // Treating it as "up to date" would deny that user an upgrade notice, and he needs one most
        var version = BridgeUpdateChecker.ParseVersionOutput(output);

        Assert.False(version.Parsed);
        Assert.False(version.Comparable);
        // Version 保留输出里的原样 token（dev 或 DEV），大写形态不在这里改写
        //
        // Version keeps the token as printed (dev or DEV); the uppercase form is not rewritten here
        Assert.Equal(output.Trim().Split(' ')[1], version.Version);
        Assert.False(string.IsNullOrWhiteSpace(version.Detail));
    }

    [Fact]
    public async Task A_dev_installed_version_yields_not_comparable_rather_than_up_to_date()
    {
        // 端到端的那一条：API 正常应答，但已装版本是 dev
        // 检查算成功（有应答），结论不可比较，且不回退网页：API 已经给出答案，回退只为"拿不到答案"服务
        //
        // The end-to-end case: the API answers normally while the installed version is dev
        // The check counts as successful (there was an answer), the verdict is not comparable, and the web path is NOT
        // used: the API already answered, and the fallback serves "no answer could be obtained"
        const string apiJson = """
            {"tag_name":"v9.9.9","assets":[{"name":"MIDIBridge-Client-v9.9.9-windows-amd64.zip","browser_download_url":"https://example/dl","size":123}]}
            """;

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var outcome = await BridgeUpdateChecker.CheckAsync("dev", NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.True(outcome.NotComparable);
        Assert.Null(outcome.Info);
        Assert.Empty(page.Requests);
    }

    [Fact]
    public async Task An_unknown_installed_version_is_not_comparable()
    {
        // 探测失败（exe 缺失、启动失败）时已装版本是 null：同样按不可比较处理，而不是当成 v0.0.0 报"有更新"
        // 后者会在用户还没装好上游时就弹一个升级提示
        //
        // When the probe fails (a missing exe, a failed start) the installed version is null: also not comparable, rather
        // than treated as v0.0.0 and reported as an update
        // The latter would raise an upgrade notice before the upstream is even installed
        const string apiJson = """{"tag_name":"v9.9.9","assets":[]}""";

        using var api = new MiniHttpServer(_ => (200, null, apiJson));
        using var page = new MiniHttpServer(_ => (500, null, "should not be reached"));

        var outcome = await BridgeUpdateChecker.CheckAsync(null, NoProxy, EndpointsFor(api, page));

        Assert.True(outcome.Ok);
        Assert.True(outcome.NotComparable);
        Assert.Null(outcome.Info);
    }

    // ================================================================ -v 输出解析 / Parsing the -v output

    [Theory]
    [InlineData("midibridge-client v1.3.0", "v1.3.0")]
    [InlineData("midibridge-client 1.3.0", "1.3.0")]
    [InlineData("  midibridge-client v1.3.0  ", "v1.3.0")]
    [InlineData("midibridge-client v1.3.0\r\n", "v1.3.0")]
    [InlineData("\n\nmidibridge-client v1.3.0\n", "v1.3.0")]
    [InlineData("MIDIBridge-Client v1.3.0", "v1.3.0")]
    public void Parses_a_release_version_out_of_the_probe_output(string output, string expected)
    {
        // 取第一个非空行并容忍首尾空白：上游只打印一行，但换行符与填充不该让解析失败
        //
        // The first non-empty line is taken with surrounding whitespace tolerated: the upstream prints one line, and
        // neither line endings nor padding may break the parse
        var version = BridgeUpdateChecker.ParseVersionOutput(output);

        Assert.True(version.Parsed, version.Detail);
        Assert.True(version.Comparable);
        Assert.Equal(expected, version.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("something else entirely")]
    [InlineData("midibridge-client")]                       // 只有程序名 / The program name alone
    [InlineData("midibridge-client v1.3.0 extra")]          // 多余 token / A surplus token
    [InlineData("other-program v1.3.0")]                    // 程序名不符 / A different program name
    public void Rejects_output_of_another_shape(string? output)
    {
        // 形状不符时返回"解析不出"，而不是把任意 token 当成版本号
        //
        // A mismatched shape yields "cannot parse" rather than treating any token as the version
        var version = BridgeUpdateChecker.ParseVersionOutput(output);

        Assert.False(version.Parsed);
        Assert.Null(version.Version);
        Assert.False(string.IsNullOrWhiteSpace(version.Detail));
    }

    [Fact]
    public async Task Probing_a_missing_executable_reports_an_unknown_version_without_throwing()
    {
        // 探测的失败分支不能抛：设置页加载时会调它，一个缺失的 exe 不该让页面崩掉
        //
        // The probe's failure branch must not throw: the settings page calls it while loading, and a missing exe must not
        // take the page down
        var missing = Path.Combine(Path.GetTempPath(), "miditap-no-such-" + Guid.NewGuid().ToString("N"), "x.exe");

        var version = await BridgeUpdateChecker.InstalledVersionAsync(missing);

        Assert.False(version.Parsed);
        Assert.Null(version.Version);
        Assert.False(string.IsNullOrWhiteSpace(version.Detail));
    }

    // ================================================================ 端点默认值 / The default endpoints

    [Fact]
    public void The_default_endpoints_come_from_the_upstream_repository()
    {
        // 默认端点必须来自 BridgeRepository（地址的唯一来源），否则改仓库时这里会被漏掉
        //
        // The defaults must come from BridgeRepository (the single source of addresses), or a repository change would
        // miss this spot
        var endpoints = BridgeUpdateEndpoints.Default;

        Assert.Equal(BridgeRepository.LatestReleaseApiUrl, endpoints.ApiUrl);
        Assert.Equal(BridgeRepository.LatestReleaseUrl, endpoints.LatestPageUrl);
        Assert.Equal(BridgeRepository.ReleasesBase, endpoints.ReleasesPageBase);
    }
}
