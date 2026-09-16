// UpdateChecksum.cs — 更新包的 SHA-256 校验
// 摘要来自 GitHub 自身（API 的 assets[].digest，或网页 expanded_assets 里的同一值）
// 因此**不需要我们另发校验和文件**，也不会多一次可能失败的请求
// 定位（诚实说明，不夸大）：校验和与包走**同一条通道**（都从 github.com 下载）
// 因此它**不能**抵御能同时改写两者的中间人 —— 那是 HTTPS 的职责
// 它实际防的是以下几种情况
//   * 传输损坏 / 截断（弱网、连接中断）
//   * CDN 或**企业代理**返回了错误的/被改写的内容（代理改写响应是真实存在的）
//   * 服务端打包或上传过程出错，导致资产内容与预期不符
// 这些都是"把坏文件装上去"的真实来源
// 而自更新一旦装上坏的可执行文件，后果是应用再也起不来
//
// SHA-256 verification of the update package
// The digest comes from GitHub itself (the API's assets[].digest, or the same value in the web expanded_assets)
// There is therefore no self-published checksum file and no extra request that could fail
// Scope (stated honestly, not oversold): the checksum travels the SAME channel as the package
// Both come from github.com, so it cannot defend against an adversary who can rewrite both — that is HTTPS's job
// The following are what it does catch
//   * transport corruption / truncation (weak links, dropped connections)
//   * a CDN or CORPORATE PROXY returning wrong or rewritten content (proxies really do rewrite)
//   * packaging/upload mistakes making the asset differ from what is expected
// Those are the real ways a bad file gets installed
// For a self-updater that means the app never starts again

using System.Security.Cryptography;

namespace MIDITap.Core.Update;

/// <summary>校验结果 / The verification result</summary>
public enum ChecksumVerdict
{
    /// <summary>摘要一致 / The digests match</summary>
    Match,
    /// <summary>
    /// 摘要不一致 —— 包不可信，必须拒绝安装
    ///
    /// The digests differ — the package is untrusted and installation must be refused
    /// </summary>
    Mismatch,
    /// <summary>
    /// 发布方未提供校验和（旧版本，或 CI 尚未生成）
    ///
    /// The publisher provided no checksum (an older release, or CI has not generated one yet)
    /// </summary>
    NotProvided,
    /// <summary>校验和存在但无法解析 / A checksum exists but cannot be parsed</summary>
    Malformed,
}

/// <summary>一次校验的结论 / The conclusion of one verification</summary>
public sealed record ChecksumResult(ChecksumVerdict Verdict, string? Expected = null, string? Actual = null)
{
    /// <summary>
    /// 是否允许继续安装。只有摘要一致、或发布方确实未提供校验和时才允许
    ///
    /// Whether installation may go ahead
    /// Allowed only when the digests match, or when the publisher genuinely provided no checksum
    /// </summary>
    /// <remarks>
    /// NotProvided 也放行是**刻意的取舍**：本功能刚加入时，历史版本都没有校验和文件
    /// 若一律拒绝，那些用户就再也无法通过应用内更新升级到带校验和的版本
    /// 用一个安全改进把人锁死在旧版本上，得不偿失
    /// 摘要**不一致**时 CanInstall 为 false，UpdateStager 据此拒绝安装并删除已下载的包
    ///
    /// Allowing NotProvided is a DELIBERATE trade-off: when this feature lands, historical releases have no checksum
    /// Refusing outright would strand those users, unable to update in-app to a version that has one
    /// A security improvement must not lock people onto an old build
    /// A MISMATCH makes CanInstall false
    /// UpdateStager then refuses the install and deletes the downloaded package
    /// </remarks>
    public bool CanInstall => Verdict is ChecksumVerdict.Match or ChecksumVerdict.NotProvided;
}

public static class UpdateChecksum
{
    /// <summary>
    /// 计算文件的 SHA-256（小写十六进制）
    /// 流式读取：更新包有 200 MB 以上
    /// 整包读进内存是不可接受的
    ///
    /// Computes the file's SHA-256 as lowercase hex
    /// The file is read as a stream
    /// Update packages exceed 200 MB, so reading one into memory is not an option
    /// </summary>
    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 按**裸摘要**（64 位十六进制，来自 GitHub 的 digest 字段或网页 HTML）校验文件
    /// 这是主路径：摘要与资产信息一起来自 GitHub，不需要额外下载校验和文件
    ///
    /// Verifies against a BARE digest as supplied by GitHub itself
    /// This is the main path: the digest arrives together with the asset information
    /// No separate checksum download is needed
    /// </summary>
    public static ChecksumResult VerifyDigest(string filePath, string? expectedDigest)
    {
        var expected = ChecksumText.IsSha256Hex(expectedDigest)
            ? expectedDigest!.ToLowerInvariant()
            : null;
        if (expected is null)
        {
            return new ChecksumResult(
                string.IsNullOrWhiteSpace(expectedDigest)
                    ? ChecksumVerdict.NotProvided
                    : ChecksumVerdict.Malformed,
                Expected: expectedDigest?.Trim());
        }

        string actual;
        try
        {
            actual = ComputeFileHash(filePath);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.updater]: Checksum computation failed: " + err.Message);
            return new ChecksumResult(ChecksumVerdict.Malformed, expected);
        }

        var match = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        return new ChecksumResult(
            match ? ChecksumVerdict.Match : ChecksumVerdict.Mismatch, expected, actual);
    }

}
