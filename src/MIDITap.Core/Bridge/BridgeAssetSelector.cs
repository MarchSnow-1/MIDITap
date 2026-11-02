// BridgeAssetSelector.cs — 按清单挑出本平台该装的产物，并给出展示与兜底探测用的资产名
//
// 选择依据是清单的 os + arch 字段（见 BridgePlatformMatcher），资产名不参与判定
// 上游从 v1.3.1 起发裸 .exe，名字里没有可依赖的后缀，按名字推会随上游改名失效
//
// BridgeAssetSelector.cs — picks this platform's installable artifact out of the manifest and builds a display name
//
// Selection reads the manifest's os and arch fields (see BridgePlatformMatcher); asset names take no part in it
// From v1.3.1 the upstream ships a bare .exe, so the name carries no dependable suffix and a name-derived guess
// breaks whenever the upstream renames anything

using MIDITap.Core.Update;

namespace MIDITap.Core.Bridge;

public static class BridgeAssetSelector
{
    /// <summary>
    /// 资产名里必须含的标记，用于识别上游产物并排掉无关文件
    /// 它只用于展示与兜底探测，不再是选择依据
    ///
    /// The marker an upstream artifact name carries, used to recognise it and rule unrelated files out
    /// It serves display and fallback probing only and is no longer the selection criterion
    /// </summary>
    public const string NameMarker = "MIDIBridge-Client";

    /// <summary>
    /// 本平台的产物文件名里应当出现的平台段，用于展示与兜底
    /// 上游的 platform 字段拼法就是 os-arch，因此它等于 BridgePlatform.ToString()
    ///
    /// The platform segment expected in this platform's artifact name, for display and fallback
    /// The upstream spells platform as os-arch, so this equals BridgePlatform.ToString()
    /// </summary>
    public static string PlatformSegment(BridgePlatform platform) => platform.ToString();

    /// <summary>
    /// 按上游命名约定构造资产文件名，仅供展示与 HEAD 兜底探测
    /// tag 带 v 前缀，Windows 产物带 .exe 后缀（v1.3.1 起的形态）
    ///
    /// Builds an asset file name per the upstream convention, for display and fallback HEAD probing only
    /// The tag carries the v prefix and the Windows artifact ends in .exe (the shape from v1.3.1 on)
    /// </summary>
    public static string CandidateName(string tag, BridgePlatform platform)
        => $"{NameMarker}-{tag}-{PlatformSegment(platform)}.exe";

    /// <summary>
    /// 从清单里挑出本平台可安装的产物，找不到返回 null
    /// 判定条件集中在 BridgeInstaller.SelectInstallable：正式发布、平台匹配、url 可用
    /// 这里只做转交，不重复组合条件——两处各写一份是这类 bug 的温床
    ///
    /// Picks the installable artifact for this platform out of a manifest, or returns null
    /// The conditions live in BridgeInstaller.SelectInstallable: a release build, a platform match and a usable url
    /// This method only forwards, so the conditions are not composed twice, which is where such bugs breed
    /// </summary>
    public static BridgeManifestArtifact? Select(BridgeManifest manifest, BridgePlatform platform)
        => BridgeInstaller.SelectInstallable(manifest, platform);

    /// <summary>
    /// 从 Releases API 返回的资产列表里挑本平台的那一个，找不到返回 null
    /// **这条路径只为回退保留**：清单取不到时仍可能从 API 的资产名认出本平台产物
    /// v1.3.1 起资产名是 MIDIBridge-Client-&lt;tag&gt;-windows-amd64.exe，因此按该后缀匹配；
    /// 同时接受旧的 .zip 后缀，使清单不可用而发布仍是 v1.3.0 形态时也能落到回退路径
    ///
    /// Picks this platform's asset out of the asset list the Releases API returns, or null
    /// **This path exists for the fallback only**: when the manifest cannot be fetched, the platform may still be
    /// recognised from the asset names the API lists
    /// From v1.3.1 the name is MIDIBridge-Client-&lt;tag&gt;-windows-amd64.exe, so that suffix is matched;
    /// the older .zip suffix is accepted as well, letting the fallback work when the manifest is unavailable but the
    /// release still has the v1.3.0 shape
    /// </summary>
    public static UpdateAsset? SelectFromApiAssets(IEnumerable<UpdateAsset> assets)
    {
        UpdateAsset? best = null;
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name) || string.IsNullOrWhiteSpace(asset.DownloadUrl))
            {
                continue;
            }
            if (!asset.Name.Contains(NameMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var windowsAmd64 = asset.Name.EndsWith("-windows-amd64.exe", StringComparison.OrdinalIgnoreCase)
                || asset.Name.EndsWith("-windows-amd64.zip", StringComparison.OrdinalIgnoreCase);
            if (!windowsAmd64)
            {
                continue;
            }
            // 多个匹配取名字最长的：正式产物名比任何误加的附带文件更具体，且不依赖 API 返回次序
            //
            // With multiple matches the longest name wins: a real artifact name is more specific than any stray file,
            // and the choice stays independent of the API's return order
            if (best is null || asset.Name.Length > best.Name.Length)
            {
                best = asset;
            }
        }
        return best;
    }
}
