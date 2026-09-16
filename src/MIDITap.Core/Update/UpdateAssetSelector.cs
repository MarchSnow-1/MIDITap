// UpdateAssetSelector.cs — 从 GitHub Release 的资产里挑出本平台该下载的那一个
// 单独成函数是为了可测：资产筛选一旦挑错，后果是"下载了错误的文件"
// 而这在联网环境里很难复现与排查
// 纯粹的字符串匹配逻辑放在这里用测试锁死
//
// Picks the right release asset for this platform
// Extracted so it can be tested: picking the wrong asset means downloading the wrong file
// That is painful to reproduce and diagnose against a live API
// Pure string matching logic lives here, locked by tests

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
    /// 挑出 win-x64 的 zip。找不到返回 null（调用方据此退回"打开 Releases 页面"）
    /// 多个匹配时选名字最长的那个：正式资产名比任何误加的附带文件更具体，且顺序稳定（不依赖 API 返回次序）
    ///
    /// Returns the win-x64 zip, or null when absent
    /// The caller then falls back to opening the Releases page
    /// With multiple matches the longest name wins, since the real asset name is more specific than any stray file
    /// This also keeps the choice order-independent
    /// </summary>
    public static UpdateAsset? Select(IEnumerable<UpdateAsset> assets)
    {
        UpdateAsset? best = null;
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name) || string.IsNullOrWhiteSpace(asset.DownloadUrl))
            {
                continue;
            }
            if (!asset.Name.EndsWith(PlatformSuffix, StringComparison.OrdinalIgnoreCase))
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
