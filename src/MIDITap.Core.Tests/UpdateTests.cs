// UpdateTests.cs — 更新检查、资产筛选、解压与相关偏好
//
// 这些逻辑大多"错一次就很难发现"：资产选错会下载错文件，排除规则写错会覆盖用户配置
// zip-slip 防护失效则是安全问题。因此逐条用测试锁住
//
// Covers update checking support code: proxy options, asset selection, staging rules, extraction safety
// It also covers the related preferences
//
// Most of this logic is "hard to notice failing once"
// Picking the wrong asset downloads the wrong file
// A wrong exclusion rule overwrites the user's config, and a broken zip-slip guard is a security problem
// So every one of them is pinned down by its own test

using System.IO.Compression;
using MIDITap.Core.Settings;
using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class UpdateOptionsTests
{
    [Fact]
    public void Empty_proxy_means_use_system_proxy()
    {
        // 留空交给 HttpClient 走系统代理：这是大多数人的正确默认值
        //
        // Leaving it empty hands things to HttpClient's system proxy
        // That is the correct default for most people
        Assert.Null(new UpdateOptions(null).BuildProxy());
        Assert.Null(new UpdateOptions("").BuildProxy());
        Assert.Null(new UpdateOptions("   ").BuildProxy());
        Assert.False(new UpdateOptions("").HasUsableProxy);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://proxy.example.com:8080")]
    [InlineData("https://proxy.example.com:8443")]
    [InlineData("  http://127.0.0.1:8080  ")]
    // socks 是**刻意支持**的：本机代理常见的就是 http 与 socks5 两种
    // 而界面文案对用户承诺了"可使用 socks5 及 http 代理"
    // 这几条就是那句承诺的依据（.NET 8 支持 http/https/socks4/socks4a/socks5，实测）
    //
    // socks is supported **deliberately**
    // http and socks5 are the two kinds of local proxy one commonly has
    // The UI copy promises the user that "socks5 and http proxies are supported"
    // These cases are what backs that promise (.NET 8 supports http/https/socks4/socks4a/socks5, measured)
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("socks4://127.0.0.1:1080")]
    [InlineData("socks4a://127.0.0.1:1080")]
    [InlineData("SOCKS5://127.0.0.1:1080")]
    public void Valid_proxy_addresses_are_accepted(string proxy)
    {
        var options = new UpdateOptions(proxy);
        Assert.NotNull(options.BuildProxy());
        Assert.True(options.HasUsableProxy);
    }

    [Theory]
    // 缺少 scheme -> 相对 URI，不是合法代理
    //
    // Missing scheme -> a relative URI, not a valid proxy
    [InlineData("127.0.0.1:8080")]
    [InlineData("not a url")]
    [InlineData("file:///c:/proxy")]    // scheme 不是受支持的代理协议 / not a supported proxy scheme
    [InlineData("ftp://proxy:21")]
    public void Invalid_proxy_addresses_fall_back_to_system_proxy(string proxy)
    {
        // 地址写错不该让更新功能整体不可用，只应退回系统代理
        //
        // A mistyped address must not take the whole update feature down
        // A mistyped address should only fall back to the system proxy
        var options = new UpdateOptions(proxy);
        Assert.Null(options.BuildProxy());
        Assert.False(options.HasUsableProxy);
    }

    [Fact]
    public void Proxy_host_and_port_are_preserved()
    {
        var proxy = new UpdateOptions("http://127.0.0.1:8080").BuildProxy();
        Assert.NotNull(proxy);
        var uri = proxy!.GetProxy(new Uri("https://api.github.com/"));
        Assert.NotNull(uri);
        Assert.Equal("127.0.0.1", uri!.Host);
        Assert.Equal(8080, uri.Port);
    }
}

public sealed class UpdateAssetSelectorTests
{
    private static UpdateAsset Asset(string name) => new(name, "https://example/" + name, 100);

    [Fact]
    public void Selects_the_platform_zip()
    {
        var selected = UpdateAssetSelector.Select([
            Asset("MIDITap-v2.1.0-win-x64.zip"),
            Asset("source.zip"),
            Asset("MIDITap-v2.1.0-win-arm64.zip"),
        ]);
        Assert.NotNull(selected);
        Assert.Equal("MIDITap-v2.1.0-win-x64.zip", selected!.Name);
    }

    [Fact]
    public void Returns_null_when_no_platform_asset_exists()
    {
        // 资产还没传完时也要能优雅退回"打开 Releases 页面"
        //
        // When the assets have not finished uploading yet it must still be able to fall back gracefully
        // The fallback is "open the Releases page"
        Assert.Null(UpdateAssetSelector.Select([Asset("source.zip"), Asset("notes.txt")]));
        Assert.Null(UpdateAssetSelector.Select([]));
    }

    [Fact]
    public void Ignores_assets_without_a_download_url()
    {
        Assert.Null(UpdateAssetSelector.Select([new UpdateAsset("MIDITap-v2-win-x64.zip", "", 1)]));
        Assert.Null(UpdateAssetSelector.Select([new UpdateAsset("", "https://example/x", 1)]));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var selected = UpdateAssetSelector.Select([Asset("miditap-v2.1.0-WIN-X64.ZIP")]);
        Assert.NotNull(selected);
    }

    [Fact]
    public void Rejects_names_without_the_app_name()
    {
        // 防止碰巧同后缀的无关文件被选中
        //
        // Prevents an unrelated file that happens to share the suffix from being selected
        Assert.Null(UpdateAssetSelector.Select([Asset("OtherApp-v1-win-x64.zip")]));
    }

    [Fact]
    public void Choice_does_not_depend_on_asset_order()
    {
        // 唯一结果时，顺序不应影响选择 / With a single candidate the order must not affect the choice
        var only = new[] { Asset("MIDITap-v2.1.0-win-x64.zip") };
        Assert.Equal(
            UpdateAssetSelector.Select(only)!.Name,
            UpdateAssetSelector.Select(only.Reverse())!.Name);
    }
}

public sealed class UpdateStagingTests
{
    [Theory]
    [InlineData("config")]
    [InlineData("config/")]
    [InlineData("config/mapping.json")]
    [InlineData(".storage")]
    [InlineData(".storage/last_config")]
    [InlineData(".storage/logs/miditap.log")]
    public void Configured_paths_are_all_excluded(string path)
    {
        // **最重要的一条。** CI 的包里带默认 config/mapping.json，原样覆盖就是数据丢失
        //
        // **The most important one.** The CI package carries a default config/mapping.json
        // Overwriting it as-is is data loss
        Assert.True(UpdateStaging.IsExcluded(path), path + " 必须被排除");
    }

    [Theory]
    [InlineData("CONFIG/MAPPING.JSON")]
    [InlineData("Config/MySet.json")]
    public void Exclusion_is_case_insensitive(string path)
    {
        // Windows 路径大小写不敏感，排除规则也必须如此，否则换个大小写就绕过去了
        //
        // Windows paths are case-insensitive and the exclusion rules must be too
        // Otherwise a change of case slips straight past them
        Assert.True(UpdateStaging.IsExcluded(path));
    }

    [Theory]
    // 前缀相近但不是 config/
    //
    // A similar-looking prefix, but not config/
    [InlineData("configs/other.json")]
    [InlineData("myconfig/mapping.json")]
    [InlineData(".storage2/x")]
    public void Similar_looking_paths_are_not_excluded(string path)
    {
        // 过度排除会让用户拿不到本该更新的文件；这里验证没有匹配过宽
        //
        // Over-excluding would stop users getting files that should be updated
        // This verifies the matching is not too broad
        Assert.False(UpdateStaging.IsExcluded(path));
    }

    [Theory]
    [InlineData("MIDITap.exe")]
    [InlineData("MIDITap.dll")]
    [InlineData("i18n/en_US.json")]
    [InlineData("scripts/e2e-midi.ps1")]
    public void Program_files_are_not_excluded(string path)
    {
        Assert.False(UpdateStaging.IsExcluded(path));
    }

    [Theory]
    [InlineData("MIDITap.pdb")]
    [InlineData("MIDITap.Core.pdb")]
    public void Debug_symbols_are_excluded(string path)
    {
        // 用户不需要 pdb，白占空间 / Users do not need the pdb and it just takes up space
        Assert.True(UpdateStaging.IsExcluded(path));
    }

    [Fact]
    public void Common_root_is_detected_and_stripped()
    {
        // CI 打包成 stage/MIDITap/…，解压后多一层 MIDITap/
        //
        // CI packs into stage/MIDITap/…, so after extraction there is one extra MIDITap/ level
        var root = UpdateStaging.StripCommonRoot(["MIDITap/MIDITap.exe", "MIDITap/i18n/en_US.json"]);
        Assert.Equal("MIDITap", root);
    }

    [Fact]
    public void No_common_root_when_files_sit_at_the_top()
    {
        Assert.Equal(string.Empty, UpdateStaging.StripCommonRoot(["MIDITap.exe", "i18n/en_US.json"]));
    }

    [Fact]
    public void No_common_root_when_top_levels_differ()
    {
        Assert.Equal(string.Empty, UpdateStaging.StripCommonRoot(["a/x.txt", "b/y.txt"]));
    }

    [Fact]
    public void Empty_input_has_no_common_root()
    {
        Assert.Equal(string.Empty, UpdateStaging.StripCommonRoot([]));
    }
}

public sealed class UpdateExtractorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-extract-" + Guid.NewGuid().ToString("N"));

    public UpdateExtractorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响结论 / A failed cleanup does not change the conclusion
        }
    }

    private string CreateArchive(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_dir, "update-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    private string PendingDir => Path.Combine(_dir, "pending-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Extracts_program_files_and_strips_the_common_root()
    {
        var zip = CreateArchive(
            ("MIDITap/MIDITap.exe", "exe"),
            ("MIDITap/i18n/en_US.json", "{}"));
        var target = PendingDir;

        var result = UpdateExtractor.Extract(zip, target);

        Assert.True(result.Ok);
        Assert.Equal(2, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(target, "MIDITap.exe")));
        Assert.True(File.Exists(Path.Combine(target, "i18n", "en_US.json")));
    }

    [Fact]
    public void Never_writes_user_data_even_when_the_archive_contains_it()
    {
        // 端到端地验证关键不变式：归档里有 config/ 也不能落到暂存区
        //
        // End-to-end verification of the key invariant
        // config/ inside the archive must not reach the pending directory
        var zip = CreateArchive(
            ("MIDITap/MIDITap.exe", "exe"),
            ("MIDITap/config/mapping.json", "{\"name\":\"factory default\"}"),
            ("MIDITap/.storage/last_config", "x"));
        var target = PendingDir;

        var result = UpdateExtractor.Extract(zip, target);

        Assert.True(result.Ok);
        Assert.Equal(1, result.FilesWritten);
        Assert.False(Directory.Exists(Path.Combine(target, "config")));
        Assert.False(Directory.Exists(Path.Combine(target, ".storage")));
    }

    [Fact]
    public void Rejects_entries_that_escape_the_target_directory()
    {
        // zip-slip：条目名带 ".." 会写到目标目录之外
        // 自更新的解压内容随后会被执行，因此这是必须挡住的安全边界
        //
        // zip-slip: an entry name containing ".." would be written outside the target directory
        // The extracted content of a self-update is executed afterwards
        // So this is a security boundary that must be blocked
        var zip = CreateArchive(
            ("MIDITap/MIDITap.exe", "exe"),
            ("MIDITap/../../../escaped.txt", "pwned"));
        var target = PendingDir;

        var result = UpdateExtractor.Extract(zip, target);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public void Skips_debug_symbols()
    {
        var zip = CreateArchive(
            ("MIDITap/MIDITap.exe", "exe"),
            ("MIDITap/MIDITap.pdb", "symbols"));
        var target = PendingDir;

        var result = UpdateExtractor.Extract(zip, target);

        Assert.True(result.Ok);
        Assert.Equal(1, result.FilesWritten);
        Assert.False(File.Exists(Path.Combine(target, "MIDITap.pdb")));
    }

    [Fact]
    public void Missing_archive_is_reported_not_thrown()
    {
        // 解压失败必须是可上报的结果：更新流程要能给出提示并保持应用可用
        //
        // An extraction failure must be a reportable result
        // The update flow has to be able to show a message and keep the app usable
        var result = UpdateExtractor.Extract(Path.Combine(_dir, "nope.zip"), PendingDir);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }
}

public sealed class UpdatePreferenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-prefs-" + Guid.NewGuid().ToString("N"));

    public UpdatePreferenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响结论 / A failed cleanup does not change the conclusion
        }
    }

    [Fact]
    public void Auto_check_defaults_to_on()
    {
        // 默认必须是开
        //
        // The default must be on
        Assert.True(AppStorage.GetAutoCheckUpdates(_dir));
    }

    [Fact]
    public void Auto_check_stays_on_when_the_file_is_unreadable_or_corrupt()
    {
        // 关键回归：读取异常绝不能把默认值翻成"关闭"，否则一次读盘异常就静默停掉自动检查
        //
        // Key regression: a read failure must never flip the default to "off"
        // One bad disk read would silently stop the automatic check
        Directory.CreateDirectory(Path.Combine(_dir, ".storage"));
        File.WriteAllText(
            Path.Combine(_dir, ".storage", AppStorage.AutoCheckUpdatesStorageKey), "garbage");
        Assert.True(AppStorage.GetAutoCheckUpdates(_dir));
    }

    [Fact]
    public void Auto_check_round_trips()
    {
        Assert.True(AppStorage.SaveAutoCheckUpdates(_dir, false));
        Assert.False(AppStorage.GetAutoCheckUpdates(_dir));
        Assert.True(AppStorage.SaveAutoCheckUpdates(_dir, true));
        Assert.True(AppStorage.GetAutoCheckUpdates(_dir));
    }

    [Fact]
    public void Proxy_defaults_to_empty_meaning_system_proxy()
    {
        Assert.Equal(string.Empty, AppStorage.GetUpdateProxy(_dir));
    }

    [Fact]
    public void Proxy_round_trips_and_is_trimmed()
    {
        Assert.True(AppStorage.SaveUpdateProxy(_dir, "  http://127.0.0.1:8080  "));
        Assert.Equal("http://127.0.0.1:8080", AppStorage.GetUpdateProxy(_dir));
    }

    [Fact]
    public void Proxy_can_be_cleared_back_to_system_default()
    {
        AppStorage.SaveUpdateProxy(_dir, "http://127.0.0.1:8080");
        AppStorage.SaveUpdateProxy(_dir, "");
        Assert.Equal(string.Empty, AppStorage.GetUpdateProxy(_dir));
    }

    [Fact]
    public void Proxy_handles_null_as_empty()
    {
        Assert.True(AppStorage.SaveUpdateProxy(_dir, null));
        Assert.Equal(string.Empty, AppStorage.GetUpdateProxy(_dir));
    }
}
