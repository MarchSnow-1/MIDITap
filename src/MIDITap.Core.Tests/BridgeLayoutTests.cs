// BridgeLayoutTests.cs — .bridge/ 布局：记录名、约定名、单 exe 回退与未安装
// 同时覆盖"该下载哪个资产"，因为它问的是同一个问题的上一个阶段
//
// ExePath 的判定直接决定界面上有没有"MIDIBridge"入口，判错的两个方向都有代价：
//   把手动解压的安装判成未安装 —— 入口消失，功能像是坏了
//   把目录里随便一个 exe 当成上游程序 —— 执行一个猜测出来的文件
// 记录名优先于约定名正是为了第二个方向：安装时落地的名字是确定的，启动时按记录名走
// 各用例只在临时目录里构造场景，不碰真实安装目录
//
// BridgeLayoutTests.cs — the .bridge/ layout: the recorded name, the conventional name, the single-exe fallback and absence
// It also covers which asset to download, since that asks the same question one stage earlier
//
// ExePath decides whether the UI shows a MIDIBridge entry, and both wrong directions carry a cost:
//   Calling a hand-unpacked install "not installed" makes the entry vanish as if the feature were broken
//   Treating any exe in the directory as the upstream program executes a guessed file
// The recorded name outranks the conventional one for that second direction: the install-time name is known
// Cases build their scenario in a temp directory only and touch no real installation directory

using MIDITap.Core.Bridge;
using MIDITap.Core.Settings;
using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class BridgeLayoutTests : IDisposable
{
    private readonly string _baseDir =
        Path.Combine(Path.GetTempPath(), "miditap-bridge-layout-" + Guid.NewGuid().ToString("N"));

    public BridgeLayoutTests() => Directory.CreateDirectory(_baseDir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDir))
            {
                Directory.Delete(_baseDir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响结论 / A failed cleanup does not change the conclusion
        }
    }

    /// <summary>上游程序目录 / The upstream program directory</summary>
    private string BridgeDir => AppPaths.BridgeDir(_baseDir);

    [Fact]
    public void Reports_missing_when_the_directory_does_not_exist()
    {
        // 安装从未发生过时的默认状态：目录不在，Missing，入口不显示
        //
        // The state before any install happened: no directory, Missing, no entry
        var lookup = BridgeLayout.ExePath(_baseDir);

        Assert.Equal(BridgeExeStatus.Missing, lookup.Status);
        Assert.Null(lookup.Path);
        Assert.False(BridgeLayout.IsInstalled(_baseDir));
    }

    [Fact]
    public void Reports_missing_when_the_directory_holds_no_exe()
    {
        // 目录存在但为空：安装流程刚建好目录就中断会得到这个状态
        //
        // The directory exists but is empty, the state left behind when installation was interrupted early
        Directory.CreateDirectory(BridgeDir);
        File.WriteAllText(Path.Combine(BridgeDir, "readme.txt"), "example");

        var lookup = BridgeLayout.ExePath(_baseDir);

        Assert.Equal(BridgeExeStatus.Missing, lookup.Status);
        Assert.False(BridgeLayout.IsInstalled(_baseDir));
    }

    [Fact]
    public void Prefers_the_conventional_name_over_any_other_exe()
    {
        // 约定名优先：即使目录里还有别的 exe，也按 AppPaths.BridgeExecutableFileName 找
        //
        // The conventional name wins: other exe files in the directory do not change the answer
        Directory.CreateDirectory(BridgeDir);
        var conventional = Path.Combine(BridgeDir, AppPaths.BridgeExecutableFileName);
        File.WriteAllText(conventional, "example");
        File.WriteAllText(Path.Combine(BridgeDir, "uninstaller.exe"), "example");

        var lookup = BridgeLayout.ExePath(_baseDir);

        Assert.Equal(BridgeExeStatus.Found, lookup.Status);
        Assert.Equal(conventional, lookup.Path);
        Assert.True(BridgeLayout.IsInstalled(_baseDir));
    }

    [Fact]
    public void Falls_back_to_the_single_exe_in_the_directory()
    {
        // 手动解压的安装：目录里唯一一个 exe 带着版本号，仍然算可用
        // 单 exe 回退就是为这类安装留的
        //
        // A hand-unpacked install: the single exe carries the version number and still counts as usable
        // The single-exe fallback exists for exactly such installs
        Directory.CreateDirectory(BridgeDir);
        var single = Path.Combine(BridgeDir, "MIDIBridge-Client-1.3.0-windows-amd64.exe");
        File.WriteAllText(single, "example");

        var lookup = BridgeLayout.ExePath(_baseDir);

        Assert.Equal(BridgeExeStatus.Found, lookup.Status);
        Assert.Equal(single, lookup.Path);
        Assert.True(BridgeLayout.IsInstalled(_baseDir));
    }

    [Fact]
    public void Picks_the_first_exe_when_several_and_none_is_recorded()
    {
        // 多个 exe 且没有记录名：取排序后的第一个，排序保证每次启动挑到同一个文件，不随枚举次序变化
        // 调用方在定位成功后会把这个名字记下来，此后走记录名那条路
        //
        // Several exe files and no recorded name: the first in ordinal order; sorting makes every start pick the same file
        // independent of enumeration order
        // The caller records that name after a successful lookup, and later lookups take the recorded-name path
        Directory.CreateDirectory(BridgeDir);
        var first = Path.Combine(BridgeDir, "MIDIBridge-Client-1.2.0-windows-amd64.exe");
        File.WriteAllText(first, "example");
        File.WriteAllText(Path.Combine(BridgeDir, "MIDIBridge-Client-1.3.0-windows-amd64.exe"), "example");

        var lookup = BridgeLayout.ExePath(_baseDir);

        Assert.Equal(BridgeExeStatus.Found, lookup.Status);
        Assert.Equal(first, lookup.Path);
        Assert.True(BridgeLayout.IsInstalled(_baseDir));
    }

    [Fact]
    public void Prefers_the_recorded_name_over_the_conventional_one()
    {
        // 记录名优先于约定名：目录里同时存在两者时，按安装时记下的那个启动
        // 这条保证"目录里被放进别的 exe"不改变启动目标
        //
        // The recorded name outranks the conventional one: when both exist, the one recorded at install time is started
        // This is what keeps "another exe dropped into the directory" from changing the start target
        Directory.CreateDirectory(BridgeDir);
        File.WriteAllText(Path.Combine(BridgeDir, AppPaths.BridgeExecutableFileName), "example");
        var recorded = Path.Combine(BridgeDir, "MIDIBridge-Client-1.3.0-windows-amd64.exe");
        File.WriteAllText(recorded, "example");

        var lookup = BridgeLayout.ExePath(_baseDir, "MIDIBridge-Client-1.3.0-windows-amd64.exe");

        Assert.Equal(BridgeExeStatus.Found, lookup.Status);
        Assert.Equal(recorded, lookup.Path);
    }

    [Fact]
    public void Falls_back_to_the_conventional_name_when_the_recorded_file_is_gone()
    {
        // 记录了名字但那个文件已被删除：回落到约定名，而不是判成未安装
        // 用户手工替换过 exe 时走的就是这条路
        //
        // The recorded name is set but its file was removed: the conventional name applies rather than "not installed"
        // This is the path taken when the user replaced the exe by hand
        Directory.CreateDirectory(BridgeDir);
        var conventional = Path.Combine(BridgeDir, AppPaths.BridgeExecutableFileName);
        File.WriteAllText(conventional, "example");

        var lookup = BridgeLayout.ExePath(_baseDir, "MIDIBridge-Client-1.1.0-windows-amd64.exe");

        Assert.Equal(BridgeExeStatus.Found, lookup.Status);
        Assert.Equal(conventional, lookup.Path);
    }

    [Fact]
    public void Does_not_count_a_non_exe_file_that_merely_starts_with_exe()
    {
        // "*.exe" 搜索模式走 8.3 短名匹配，可能命中扩展名以 exe 开头的其它文件
        // 实现按"以 .exe 结尾"比较，所以这种文件不进候选
        //
        // The *.exe search pattern matches 8.3 short names and can hit files whose extension merely starts with "exe"
        // The implementation compares "ends with .exe" instead, so such a file stays out
        Directory.CreateDirectory(BridgeDir);
        File.WriteAllText(Path.Combine(BridgeDir, "notes.exeb"), "example");

        var lookup = BridgeLayout.ExePath(_baseDir);

        Assert.Equal(BridgeExeStatus.Missing, lookup.Status);
    }

    [Fact]
    public void Builds_the_upstream_config_path_next_to_the_exe()
    {
        // 上游把配置锚定到它自己目录下的 data/config.json（上游 main.go 的约定）
        // 用户改 TLS 参数编辑的是那个文件，界面把位置指给他
        //
        // The upstream anchors its config to data/config.json inside its own directory (its main.go convention)
        // The user edits that file for TLS, and the UI points him at it
        var path = BridgeLayout.ConfigPath(_baseDir);

        Assert.Equal(Path.Combine(BridgeDir, "data", "config.json"), path);
    }
}

public sealed class BridgeAssetSelectorTests
{
    /// <summary>造一个资产条目，URL 用通用占位域名 / Builds one asset entry with a placeholder download URL</summary>
    private static UpdateAsset Asset(string name)
        => new(name, "https://example.com/username/repository/releases/download/v9.9.9/" + name, 1024);

    /// <summary>造一条清单产物 / Builds one manifest artifact entry</summary>
    private static BridgeManifestArtifact Artifact(string platform, string? sha256 = "abc", string? url = "https://example.com/a")
    {
        var parts = platform.Split('-', 2);
        return new BridgeManifestArtifact(
            platform,
            parts.Length == 2 ? parts[0] : string.Empty,
            parts.Length == 2 ? parts[1] : string.Empty,
            "MIDIBridge-Client-v9.9.9-" + platform + (platform.StartsWith("windows") ? ".exe" : string.Empty),
            1024,
            sha256,
            url);
    }

    /// <summary>造一份正式发布清单 / Builds a release manifest</summary>
    private static BridgeManifest Manifest(params BridgeManifestArtifact[] artifacts)
        => new("midibridge-client", "release", "v9.9.9", "9.9.9", "1.0.0", 1, null, artifacts, []);

    [Fact]
    public void Builds_the_candidate_name_from_the_upstream_convention()
    {
        // v1.3.1 起上游产出裸二进制，Windows 上带 .exe 后缀
        // 这个名字只用于展示与 HEAD 兜底探测，不参与"该装哪个"的判定
        //
        // From v1.3.1 the upstream ships bare binaries, with an .exe suffix on Windows
        // This name serves display and fallback HEAD probing only and takes no part in deciding which build to install
        Assert.Equal(
            "MIDIBridge-Client-v9.9.9-windows-amd64.exe",
            BridgeAssetSelector.CandidateName("v9.9.9", BridgePlatform.WindowsAmd64));
    }

    [Fact]
    public void Picks_the_windows_artifact_from_a_mixed_manifest()
    {
        // 上游一次发布同时产出 linux / darwin / windows 多个平台的产物
        // 选择依据是 os + arch 两个字段，而不是文件名后缀
        //
        // One upstream release carries linux, darwin and windows builds
        // Selection uses the os and arch fields rather than a file-name suffix
        var windows = Artifact("windows-amd64");
        var manifest = Manifest(
            Artifact("linux-amd64"),
            Artifact("darwin-arm64"),
            windows);

        Assert.Same(windows, BridgeAssetSelector.Select(manifest, BridgePlatform.WindowsAmd64));
    }

    [Fact]
    public void Returns_null_when_the_platform_is_absent()
    {
        // 只别的平台的产物时返回 null，由调用方给出"该发布没有本平台构建"
        // 返回 null 而不是猜一个，下载一个跑不起来的二进制比让用户手动下载更糟
        //
        // With only other platforms' builds the result is null, and the caller reports "no build for this platform"
        // Returning null instead of guessing: downloading a binary that cannot run is worse than downloading by hand
        var manifest = Manifest(Artifact("linux-amd64"), Artifact("windows-arm64"));

        Assert.Null(BridgeAssetSelector.Select(manifest, BridgePlatform.WindowsAmd64));
    }

    [Fact]
    public void Returns_null_for_a_dev_build()
    {
        // dev 构建的产物只存在于 Actions run，url 为 null，没有稳定地址可下载
        //
        // A dev build's artifacts exist only as Actions run artifacts with a null url, so there is no stable address
        var manifest = new BridgeManifest(
            "midibridge-client", "dev", "v9.9.9-beta1", "9.9.9-beta1", "1.0.0", 1, null,
            [Artifact("windows-amd64", url: null)], []);

        Assert.Null(BridgeAssetSelector.Select(manifest, BridgePlatform.WindowsAmd64));
    }

    [Fact]
    public void Falls_back_to_a_built_url_when_the_entry_carries_none()
    {
        // url 缺失时按 tag + 文件名兜底：清单里 url 与 filename 属于同一条目，指向同一个资源
        //
        // A missing url falls back to tag plus filename: url and filename belong to the same entry, so they point at
        // the same resource
        var manifest = Manifest(Artifact("windows-amd64", url: null));

        var selected = BridgeAssetSelector.Select(manifest, BridgePlatform.WindowsAmd64);

        Assert.NotNull(selected);
        Assert.Contains("v9.9.9", selected!.Url, StringComparison.Ordinal);
        Assert.Contains("windows-amd64", selected.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignores_entries_whose_os_or_arch_is_blank()
    {
        // 上游遇到无法识别的产物名会留空 os 与 arch，并从 platform 推断；那种条目不该被选中
        //
        // The upstream leaves os and arch blank for unrecognised artifact names and infers platform; such an entry must
        // not be selected
        var blank = new BridgeManifestArtifact(
            "windows-amd64", string.Empty, string.Empty, "MIDIBridge-Client-v9.9.9-windows-amd64.exe", 1024, "abc", "https://example.com/a");

        Assert.Null(BridgeAssetSelector.Select(Manifest(blank), BridgePlatform.WindowsAmd64));
    }

    [Fact]
    public void Matching_ignores_case_in_the_os_and_arch_fields()
    {
        // os 与 arch 的比较忽略大小写：清单里的大小写不是选择失败的合法理由
        //
        // The os and arch comparison ignores case: how the manifest spells them is not a reason for a selection to fail
        var upper = Artifact("windows-amd64") with { Os = "Windows", Arch = "AMD64" };

        Assert.Same(upper, BridgeAssetSelector.Select(Manifest(upper), BridgePlatform.WindowsAmd64));
    }

    [Fact]
    public void Takes_the_longest_file_name_when_several_entries_match()
    {
        // 多个匹配取最长名，与 UpdateAssetSelector.Select 的写法一致，断言不依赖入参顺序
        //
        // With several matches the longest name wins, written the same way as UpdateAssetSelector.Select
        // The assertion does not depend on argument order
        var expected = Artifact("windows-amd64");
        var shortName = expected with { FileName = "a.exe" };

        var first = BridgeAssetSelector.Select(Manifest(shortName, expected), BridgePlatform.WindowsAmd64);
        var second = BridgeAssetSelector.Select(Manifest(expected, shortName), BridgePlatform.WindowsAmd64);

        Assert.Same(expected, first);
        Assert.Same(expected, second);
    }

    [Fact]
    public void Selects_the_windows_build_from_api_assets_for_the_fallback_path()
    {
        // 回退路径（清单不可用时）仍从 API 的资产名里认本平台产物
        // v1.3.1 起是 .exe，v1.3.0 的 .zip 也要认，否则回退在旧发布上失效
        //
        // The fallback path (when the manifest is unavailable) still recognises this platform's asset from the API's
        // asset names. From v1.3.1 that is .exe; the v1.3.0 .zip must also be recognised, or the fallback breaks on
        // older releases
        Assert.NotNull(BridgeAssetSelector.SelectFromApiAssets(
            [Asset("MIDIBridge-Client-v9.9.9-windows-amd64.exe")]));
        Assert.NotNull(BridgeAssetSelector.SelectFromApiAssets(
            [Asset("MIDIBridge-Client-v9.9.9-windows-amd64.zip")]));

        // 别的平台与无关文件都不该被选中 / Neither another platform nor an unrelated file may be selected
        Assert.Null(BridgeAssetSelector.SelectFromApiAssets([
            Asset("MIDIBridge-Client-v9.9.9-linux-amd64"),
            Asset("CHANGELOG.md"),
        ]));
    }
}
