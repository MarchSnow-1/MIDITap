// ReleasePageTests.cs — 网页路径（绕过 GitHub API 限流）的解析逻辑
//
// 为什么值得测试：这条路径是为了"API 用尽时仍能工作"而存在的
// 而它一旦解析错，用户会得到错误的版本号或选错下载文件 —— 在联网环境里极难复现和排查
//
// Parsing logic for the web path that bypasses the GitHub API rate limit
//
// These tests matter because the path exists so the app still works once the API quota is gone
// A parsing mistake there hands the user a wrong version or the wrong download
// Such a mistake is painful to reproduce against a live service

using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class ReleasePageParserTagTests
{
    [Theory]
    [InlineData("https://github.com/username/repository/releases/tag/v1.7.1", "v1.7.1")]
    [InlineData("https://github.com/username/repository/releases/tag/v2.0.0", "v2.0.0")]
    // 查询串与片段必须被剔除，否则版本号里会混进 "?expanded=true"
    //
    // The query string and fragment must be stripped
    // Otherwise the version number would end up with "?expanded=true" mixed into it
    [InlineData("https://github.com/username/repository/releases/tag/v1.2.3?expanded=true", "v1.2.3")]
    [InlineData("https://github.com/username/repository/releases/tag/v1.2.3#notes", "v1.2.3")]
    [InlineData("https://github.com/username/repository/releases/tag/v1.2.3/", "v1.2.3")]
    // 大小写不敏感：GitHub 路径大小写不影响语义
    //
    // Case-insensitive: path case does not change GitHub's semantics
    [InlineData("https://github.com/username/repository/Releases/Tag/v9.9.9", "v9.9.9")]
    public void Parses_tag_from_redirect_url(string url, string expected)
    {
        Assert.Equal(expected, ReleasePageParser.ParseTagFromUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // 仓库没有任何发布时，/releases/latest 会重定向到 /releases 本身
    // 此时必须返回 null，而不是把 "latest" 之类当成版本号
    //
    // When the repository has no releases, /releases/latest redirects to /releases itself
    // This must return null rather than treating something like "latest" as a version number
    [InlineData("https://github.com/username/repository/releases")]
    [InlineData("https://github.com/username/repository/releases/latest")]
    [InlineData("https://github.com/username/repository/releases/tag/")]
    [InlineData("https://example.com/not/github")]
    public void Returns_null_when_there_is_no_tag(string? url)
    {
        Assert.Null(ReleasePageParser.ParseTagFromUrl(url));
    }

    [Fact]
    public void Decodes_a_url_encoded_tag()
    {
        // 标签里可能有需要编码的字符 / Tags may contain characters that need encoding
        Assert.Equal("v1.0.0 beta", ReleasePageParser.ParseTagFromUrl(
            "https://github.com/username/repository/releases/tag/v1.0.0%20beta"));
    }
}

public sealed class ReleasePageParserAssetTests
{
    [Fact]
    public void Parses_a_relative_download_link_into_an_absolute_url()
    {
        // 实测：GitHub 的 expanded_assets 片段返回的是相对路径，必须以 github.com 补全
        // 否则下载器会拿到一个无法请求的地址
        //
        // Measured: GitHub's expanded_assets fragment returns relative paths
        // They must be completed with github.com, otherwise the downloader gets an address it cannot request
        const string html = """
            <div><a href="/username/repository/releases/download/v1.7.1/MIDITap-v1.7.1-win_x64.zip" rel="nofollow">Download</a></div>
            """;

        var assets = ReleasePageParser.ParseAssets(html);

        Assert.Single(assets);
        Assert.Equal("MIDITap-v1.7.1-win_x64.zip", assets[0].Name);
        Assert.Equal(
            "https://github.com/username/repository/releases/download/v1.7.1/MIDITap-v1.7.1-win_x64.zip",
            assets[0].DownloadUrl);
    }

    [Fact]
    public void Keeps_an_absolute_link_unchanged()
    {
        const string html = """
            <a href="https://github.com/username/repository/releases/download/v1.0.0/x-win-x64.zip">d</a>
            """;

        var assets = ReleasePageParser.ParseAssets(html);

        Assert.Single(assets);
        Assert.Equal("https://github.com/username/repository/releases/download/v1.0.0/x-win-x64.zip", assets[0].DownloadUrl);
    }

    [Fact]
    public void Strips_query_and_fragment_from_the_file_name()
    {
        const string html = """
            <a href="/username/repository/releases/download/v1.0.0/pack-win-x64.zip?download=1#frag">d</a>
            """;

        var assets = ReleasePageParser.ParseAssets(html);

        Assert.Single(assets);
        Assert.Equal("pack-win-x64.zip", assets[0].Name);
    }

    [Fact]
    public void Deduplicates_repeated_links()
    {
        // 片段里同一资产可能出现在多处（图标 + 文本各一个链接）
        //
        // The same asset can appear in several places in the fragment (one link for the icon and one for the text)
        const string html = """
            <a href="/username/repository/releases/download/v1.0.0/a-win-x64.zip">1</a>
            <a href="/username/repository/releases/download/v1.0.0/a-win-x64.zip">2</a>
            """;

        Assert.Single(ReleasePageParser.ParseAssets(html));
    }

    [Fact]
    public void Ignores_links_that_are_not_release_downloads()
    {
        // 只认下载链接：页面里还有大量导航/标签链接，误收会把它们当成可下载资产
        //
        // Only download links are accepted
        // The page also carries plenty of navigation and tag links
        // Taking those would treat them as downloadable assets
        const string html = """
            <a href="/username/repository/releases">Releases</a>
            <a href="/username/repository/tags">Tags</a>
            <a href="/username/repository/releases/download/v1.0.0/real-win-x64.zip">real</a>
            """;

        var assets = ReleasePageParser.ParseAssets(html);

        Assert.Single(assets);
        Assert.Equal("real-win-x64.zip", assets[0].Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body>no assets here</body></html>")]
    public void Returns_empty_for_input_without_assets(string? html)
    {
        Assert.Empty(ReleasePageParser.ParseAssets(html));
    }

    [Fact]
    public void Parsed_assets_are_selectable_by_the_normal_selector()
    {
        // 端到端衔接：网页路径解析出的资源必须能被既有的选择器挑中
        // 否则"绕过限流"拿到的列表仍然用不上
        //
        // End-to-end join: the assets parsed out of the web path must be selectable by the existing selector
        // Otherwise the list obtained by "bypassing the rate limit" would still be unusable
        const string html = """
            <a href="/username/repository/releases/download/v2.0.0/MIDITap-v2.0.0-win-x64.zip">a</a>
            <a href="/username/repository/releases/download/v2.0.0/MIDITap-v2.0.0-win-arm64.zip">b</a>
            """;

        var selected = UpdateAssetSelector.Select(ReleasePageParser.ParseAssets(html));

        Assert.NotNull(selected);
        Assert.Equal("MIDITap-v2.0.0-win-x64.zip", selected!.Name);
    }

    [Fact]
    public void Underscore_asset_name_is_not_selected()
    {
        // 下划线命名（win_x64）**刻意不**作为候选：它不属于本应用产出的命名
        // 认它就等于让"哪些名字算本平台的包"有两个答案，而多一个别名就多一种挑错文件的可能
        // 这条断言把该决定锁住 —— 它与"认不认得出历史资源"无关，只保证我们只挑自己产出的命名
        //
        // The underscore naming (win_x64) is deliberately NOT a candidate
        // It is not a naming this app produces
        // Accepting it would give "which names count as this platform's package" two answers
        // Every alias is one more way to pick the wrong file
        // This assertion locks the decision
        // It says nothing about whether the historical asset is recognised
        // It only says that we select the naming we produce
        var assets = new[]
        {
            new UpdateAsset("MIDITap-v1.7.1-win_x64.zip", "https://example/a", 0),
            new UpdateAsset("MIDITap-v1.7.1-win_arm64.zip", "https://example/b", 0),
        };

        Assert.Null(UpdateAssetSelector.Select(assets));
    }
}
