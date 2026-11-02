// BridgePlatformMatcher.cs — 从清单里挑出当前运行平台该用的那个产物
//
// 匹配以 os + arch 两个字段为准，而不是 platform 字符串
// 上游文档声明 os 与 arch 拼起来才是 platform，且 os 对应 GOOS、arch 对应 GOARCH
// 按两个字段分别比对，就不必依赖中间那个连字符的拼法
//
// armv7 单独处理：上游把 arch 写成 armv7，而 Go 运行时的 GOARCH 在 32 位 ARM 上是 arm，且 Go 没有 GOARM 的运行时值
// 因此这里不为 arm 推断成 armv7，只有调用方明确按 armv7 传入时才匹配
// 推断错的后果是装上一个跑不起来的二进制，比返回"找不到可用产物"更糟
//
// BridgePlatformMatcher.cs — picks the artifact for the platform this build runs on
//
// Matching uses the os and arch fields rather than the platform string
// The upstream documentation states os plus arch forms platform, and that os maps to GOOS while arch maps to GOARCH
// Comparing the two fields directly removes any dependence on how the hyphen is spelled
//
// armv7 is handled explicitly: the upstream writes arch as armv7, while Go reports GOARCH as arm on 32-bit ARM and
// exposes no GOARM at run time
// This matcher therefore does not infer armv7 from arm; it matches armv7 only when the caller passes it
// A wrong inference installs a binary that will not run, which is worse than reporting that no artifact is available

namespace MIDITap.Core.Bridge;

/// <summary>本轮运行的平台：os 与 arch 取自清单的字面值，例如 windows / amd64</summary>
/// <remarks>The platform this run is on: os and arch as the manifest spells them, for instance windows / amd64</remarks>
public sealed record BridgePlatform(string Os, string Arch)
{
    /// <summary>
    /// 本程序当前支持的那一个平台，即清单里的 windows / amd64
    ///
    /// The one platform this program currently supports: windows / amd64 in the manifest
    /// </summary>
    public static readonly BridgePlatform WindowsAmd64 = new("windows", "amd64");

    /// <summary>平台的显示文本，用于日志与提示 / A display form for logs and messages</summary>
    public override string ToString() => Os + "-" + Arch;
}

public static class BridgePlatformMatcher
{
    /// <summary>
    /// 从清单里挑出与 <paramref name="platform"/> 一致的产物
    /// 找不到返回 null，由调用方给出"该发布没有本平台的产物"这类提示
    /// 多个匹配时取文件名最长的一个：正式产物名比误加的附带文件更具体，且不依赖清单次序
    ///
    /// Picks the artifact matching <paramref name="platform"/>, or null when none does
    /// Null lets the caller say "this release has no build for this platform"
    /// With multiple matches the longest file name wins: a real artifact name is more specific than a stray file, and the choice stays order-independent
    /// </summary>
    public static BridgeManifestArtifact? Select(
        IEnumerable<BridgeManifestArtifact> artifacts, BridgePlatform platform)
    {
        BridgeManifestArtifact? best = null;
        foreach (var artifact in artifacts)
        {
            if (!Matches(artifact, platform))
            {
                continue;
            }
            if (best is null || artifact.FileName.Length > best.FileName.Length)
            {
                best = artifact;
            }
        }
        return best;
    }

    /// <summary>
    /// 单个条目是否属于该平台
    /// os 与 arch 都按忽略大小写比较：清单里现在是小写，而比较大小写不该成为选择失败的理由
    /// 字段为空（上游遇到无法识别的产物名会留空）时不匹配任何平台
    ///
    /// Whether one entry belongs to the platform
    /// os and arch compare case-insensitively: the manifest spells them in lower case today, and case should not be the reason a selection fails
    /// An empty field never matches, since the upstream leaves those blank for unrecognised artifact names
    /// </summary>
    public static bool Matches(BridgeManifestArtifact artifact, BridgePlatform platform)
    {
        if (string.IsNullOrWhiteSpace(artifact.Os) || string.IsNullOrWhiteSpace(artifact.Arch))
        {
            return false;
        }
        return string.Equals(artifact.Os, platform.Os, StringComparison.OrdinalIgnoreCase)
            && string.Equals(artifact.Arch, platform.Arch, StringComparison.OrdinalIgnoreCase);
    }
}
