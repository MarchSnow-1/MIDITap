// BridgeReleaseInfoTests.cs — 更新日志读取：成功、失败归因、发布页地址来源
//
// 这个类不做版本比较，比较属于 BridgeUpdateChecker 的测试
// 这里锁住的是：日志原文来自 API 的 body 字段；失败时 ReleaseUrl 依然可用（弹窗要靠它送用户去发布页）；
// 地址优先用清单的 releaseUrl，缺失时按 tag 拼接；tag 也缺失时退回 Releases 列表页
//
// BridgeReleaseInfoTests.cs — release notes reads: success, failure attribution, the release page address's source
//
// This class does no version comparison, and comparison belongs to BridgeUpdateChecker's tests
// What gets pinned here: the notes come from the API's body field; on failure the ReleaseUrl still resolves (the
// dialog needs it to send the user to the release page); the address prefers the manifest's releaseUrl, then builds
// one from the tag; with no tag either it falls back to the release list page

using MIDITap.Core.Bridge;
using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class BridgeReleaseInfoTests
{
    private static readonly UpdateOptions NoProxy = new(null);

    private const string Tag = "v9.9.9";

    [Fact]
    public async Task Returns_the_notes_and_the_manifest_release_url_on_success()
    {
        const string apiJson = """
            {"tag_name":"v9.9.9","body":"### Fixed something","assets":[]}
            """;
        using var api = new MiniHttpServer(_ => (200, null, apiJson));

        var read = await BridgeReleaseInfo.FetchAsync(
            Tag, "https://example.invalid/release", NoProxy, api.BaseUrl + "/latest");

        Assert.True(read.Ok);
        Assert.Equal("### Fixed something", read.Notes);
        // 地址来自清单，而非按 tag 拼接
        //
        // The address comes from the manifest, not from the tag
        Assert.Equal("https://example.invalid/release", read.ReleaseUrl);
        Assert.Equal(UpdateFailure.None, read.Failure);
    }

    [Fact]
    public async Task Builds_the_release_page_url_from_the_tag_when_the_manifest_has_none()
    {
        const string apiJson = """
            {"tag_name":"v9.9.9","body":"notes","assets":[]}
            """;
        using var api = new MiniHttpServer(_ => (200, null, apiJson));

        var read = await BridgeReleaseInfo.FetchAsync(Tag, null, NoProxy, api.BaseUrl + "/latest");

        Assert.True(read.Ok);
        Assert.Equal(BridgeRepository.ReleasePageUrl(Tag), read.ReleaseUrl);
    }

    [Fact]
    public async Task Falls_back_to_the_releases_list_when_the_tag_is_unknown()
    {
        const string apiJson = """
            {"tag_name":"v9.9.9","body":"notes","assets":[]}
            """;
        using var api = new MiniHttpServer(_ => (200, null, apiJson));

        var read = await BridgeReleaseInfo.FetchAsync(null, null, NoProxy, api.BaseUrl + "/latest");

        Assert.True(read.Ok);
        Assert.Equal(BridgeRepository.ReleasesBase, read.ReleaseUrl);
    }

    [Fact]
    public async Task Reports_a_403_with_the_quota_and_keeps_the_release_url()
    {
        // GitHub 的 403（匿名配额用尽）同时带这三个头，缺一个界面就少一段可显示的信息
        //
        // GitHub's 403 (anonymous quota spent) carries all three headers; each missing one costs the UI a fact
        using var api = new MiniHttpServer(_ => (403, null, "{\"message\":\"rate limit\"}"));
        api.ExtraHeaders["x-ratelimit-reset"] = "1790000000";
        api.ExtraHeaders["x-ratelimit-remaining"] = "0";
        api.ExtraHeaders["x-ratelimit-limit"] = "60";

        var read = await BridgeReleaseInfo.FetchAsync(Tag, null, NoProxy, api.BaseUrl + "/latest");

        // 失败不是错误：日志取不到时安装仍可进行，弹窗只负责说明原因并保留发布页入口
        //
        // A failure is not an error: the install can go ahead without the notes, and the dialog only explains why
        // while keeping the release page link
        Assert.False(read.Ok);
        Assert.Null(read.Notes);
        Assert.Equal(UpdateFailure.Http, read.Failure);
        Assert.NotNull(read.RateLimit);
        // 三个头各自独立，逐个核对读出值，而不是只核对“非空”
        // Reset 存的是本地时间，比回去要换回 UTC
        //
        // The three headers are independent, so each read value is checked rather than just "not null"
        // Reset is stored in local time, so the comparison converts back to UTC
        Assert.Equal(0, read.RateLimit!.Remaining);
        Assert.Equal(60, read.RateLimit.Limit);
        Assert.Equal(1790000000L, read.RateLimit.Reset!.Value.ToUniversalTime().ToUnixTimeSeconds());
        Assert.Equal(BridgeRepository.ReleasePageUrl(Tag), read.ReleaseUrl);
    }

    [Fact]
    public async Task Reports_a_server_error_without_notes()
    {
        using var api = new MiniHttpServer(_ => (500, null, "boom"));

        var read = await BridgeReleaseInfo.FetchAsync(Tag, null, NoProxy, api.BaseUrl + "/latest");

        Assert.False(read.Ok);
        Assert.Null(read.Notes);
        Assert.Equal(UpdateFailure.Http, read.Failure);
        // 5xx 不带配额头，因此此处为 null，界面据此不显示"何时可重试"
        //
        // A 5xx carries no quota headers, so this is null and the UI shows no "when to retry" line
        Assert.Null(read.RateLimit);
    }
}
