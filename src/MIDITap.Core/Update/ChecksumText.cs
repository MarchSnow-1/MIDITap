// ChecksumText.cs — 从 GitHub 返回的文本里取出 SHA-256
// GitHub 以两种形式给出同一份摘要，实测两者一致
//   * API:  assets[].digest = "sha256:448d50fe…"（带前缀）
//   * 网页: expanded_assets 的 HTML 内含裸摘要 "448d50fe…"
// 因此把"取摘要"做成一个纯函数，两种输入都能吃下
//
// Extracts a SHA-256 from the text GitHub returns
// GitHub exposes the same digest two ways, and they were verified identical
// The API's assets[].digest carries a "sha256:" prefix
// The expanded_assets HTML contains the bare digest
// One pure function that accepts both keeps the parsing testable

namespace MIDITap.Core.Update;

public static class ChecksumText
{
    /// <summary>SHA-256 摘要的十六进制长度 / Hex length of a SHA-256 digest</summary>
    public const int HexLength = 64;

    /// <summary>API digest 字段的前缀 / Prefix of the API digest field</summary>
    public const string Sha256Prefix = "sha256:";

    /// <summary>
    /// 从 API 的 <c>digest</c> 值中取摘要（形如 "sha256:&lt;64位&gt;"）
    /// 其它算法（如未来的 sha512:）返回 null：**不认识就当作没有**，而不是把不认识的算法当成 sha256 去比较
    /// 那必然误判为不匹配
    ///
    /// Parses the API digest value
    /// Other algorithms (e.g. a future sha512:) return null
    /// An unrecognised algorithm is treated as absent rather than compared as if it were SHA-256
    /// Comparing it as SHA-256 would always report a mismatch
    /// </summary>
    public static string? FromApiDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }
        var text = digest.Trim();
        if (!text.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var hex = text[Sha256Prefix.Length..].Trim();
        return IsSha256Hex(hex) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>
    /// 从网页 HTML 中取摘要。取**第一个** 64 位十六进制串
    /// 实测该片段里只包含本资产的摘要（虽出现多次，但值相同），因此第一个即可
    ///
    /// Extracts the digest from the web HTML, i.e. the first 64-hex sequence
    /// Measured, the fragment contains only this asset's digest (repeated with the same value)
    /// The first one therefore suffices
    /// </summary>
    public static string? FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(
                     html, "\\b[0-9a-fA-F]{" + HexLength + "}\\b"))
        {
            var value = match.Value;
            // 排除明显不是摘要的其它 64 位十六进制串难度很高，但该片段中实测只有摘要
            // 因此这里只做格式确认
            //
            // Ruling out other 64-hex runs that are clearly not the digest is hard
            // In practice this fragment contains only the digest, so only the format is confirmed here
            if (IsSha256Hex(value))
            {
                return value.ToLowerInvariant();
            }
        }
        return null;
    }

    /// <summary>是否为合法的 64 位十六进制摘要 / Whether the value is a valid 64-hex digest</summary>
    public static bool IsSha256Hex(string? text)
    {
        if (text is null || text.Length != HexLength)
        {
            return false;
        }
        foreach (var c in text)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }
}
