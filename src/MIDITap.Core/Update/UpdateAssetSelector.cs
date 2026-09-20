// UpdateAssetSelector.cs — 从 GitHub Release 的资产里挑出本平台该下载的那一个
// 单独成函数是为了可测：资产筛选一旦挑错，后果是"下载了错误的文件"
// 而这在联网环境里很难复现与排查
// 纯粹的字符串匹配逻辑放在这里用测试锁死
//
// 为什么要分两种包：两种安装方式的更新路径不同
//   便携包解压出来，由辅助脚本逐文件替换
//   安装器装上的跑安装包，由 Inno 一并更新文件、注册表与卸载日志
// 因此"该下载哪一个资产"取决于本机是哪一种安装，由调用方决定并传进来
//
// Picks the right release asset for this platform
// Extracted so it can be tested: picking the wrong asset means downloading the wrong file
// That is painful to reproduce and diagnose against a live API
// Pure string matching logic lives here, locked by tests
//
// Why two kinds of package: the two installation routes update differently
//   A portable copy is unpacked and applied file by file by the helper script
//   An installed copy runs the installer, and Inno updates the files, the registry and the uninstall log together
// Which asset to download therefore depends on how this machine installed the app, and the caller passes that in

namespace MIDITap.Core.Update;

/// <summary>
/// 一个发布资产（文件名 + 下载地址 + 大小 + 可选摘要）
/// <paramref name="Sha256"/> 来自 GitHub 自身：API 的 assets[].digest，或网页路径 expanded_assets HTML 里的同一摘要（实测两者一致）
/// **不需要我们另外发布校验和文件**
///
/// A release asset (file name + download URL + size + optional digest)
/// Sha256 comes from GitHub itself: the API's assets[].digest
/// Or the same digest embedded in the expanded_assets HTML on the web path (measured identical)
/// There is no need for a self-published checksum file
/// </summary>
public sealed record UpdateAsset(string Name, string DownloadUrl, long Size, string? Sha256 = null);

/// <summary>要下载哪一种包 / Which kind of package to download</summary>
public enum UpdatePackageKind
{
    /// <summary>便携包：解压后由辅助脚本逐文件替换 / The portable archive, unpacked and applied file by file by the helper script</summary>
    Portable,

    /// <summary>安装包：静默运行，由 Inno 一并完成替换与注册表更新 / The installer, run silently so Inno replaces the files and updates the registry too</summary>
    Installer,
}

public static class UpdateAssetSelector
{
    /// <summary>
    /// 本平台需要的资产后缀。CI 产出的名字是 MIDITap-&lt;tag&gt;-win-x64.zip
    ///
    /// **只接受这一种拼法。** 下划线写法（win_x64）不是候选
    /// 为它留一个别名，等于让"哪些名字算本平台的包"有两个答案
    /// 换来的只是历史那批资源 —— 它们仍然可以在 Releases 页面手动下载
    /// 多一个别名就多一种挑错文件的可能
    ///
    /// The suffix this platform needs; CI produces MIDITap-&lt;tag&gt;-win-x64.zip
    ///
    /// **This one spelling is the only accepted form**
    /// The underscore form (win_x64) is not a candidate
    /// Keeping an alias for it would give "which names count as this platform's package" two answers
    /// That buys only the historical assets, which can still be downloaded from the Releases page
    /// Every alias is one more way to pick the wrong file
    /// </summary>
    public const string PlatformSuffix = "-win-x64.zip";

    /// <summary>
    /// 安装包的后缀。CI 产出的名字是 MIDITap-&lt;tag&gt;-win-x64-setup.exe
    /// 安装器装的用户下载这一种：由 Inno 一并更新文件、注册表与卸载日志
    /// 两种后缀互不包含（zip 不以 -setup.exe 结尾，反之亦然），因此不会互相误配
    ///
    /// The suffix of the installer package; CI produces MIDITap-&lt;tag&gt;-win-x64-setup.exe
    /// An installed copy downloads this one, so Inno updates the files, the registry and the uninstall log together
    /// Neither suffix contains the other (a zip does not end in -setup.exe, and the reverse holds too),
    /// so the two cannot be confused
    /// </summary>
    public const string InstallerSuffix = "-win-x64-setup.exe";

    /// <summary>某种包对应的资产后缀 / The asset suffix for one kind of package</summary>
    public static string SuffixFor(UpdatePackageKind kind)
        => kind == UpdatePackageKind.Installer ? InstallerSuffix : PlatformSuffix;

    /// <summary>
    /// 挑出该种包的资产。找不到返回 null（调用方据此退回"打开 Releases 页面"）
    /// 多个匹配时选名字最长的那个：正式资产名比任何误加的附带文件更具体，且顺序稳定（不依赖 API 返回次序）
    ///
    /// Returns the asset of the given kind, or null when absent
    /// The caller then falls back to opening the Releases page
    /// With multiple matches the longest name wins, since the real asset name is more specific than any stray file
    /// This also keeps the choice order-independent
    /// </summary>
    public static UpdateAsset? Select(
        IEnumerable<UpdateAsset> assets,
        UpdatePackageKind kind = UpdatePackageKind.Portable)
    {
        var suffix = SuffixFor(kind);
        UpdateAsset? best = null;
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name) || string.IsNullOrWhiteSpace(asset.DownloadUrl))
            {
                continue;
            }
            if (!asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // 排掉源码包之类可能碰巧同后缀的项：名字里必须含 "MIDITap"
            //
            // Rules out items that merely happen to share the suffix, such as source archives
            // The name must contain "MIDITap"
            if (!asset.Name.Contains("MIDITap", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (best is null || asset.Name.Length > best.Name.Length)
            {
                best = asset;
            }
        }
        return best;
    }
}
