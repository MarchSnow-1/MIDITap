// InstallerIdentity.cs — 确认下载到的 exe 确实是"本应用的 Inno 安装包"
//
// 为什么需要它：便携路线靠"解压后必须有 MIDITap.exe 与 scripts/apply-update.ps1"来确认包是我们的
// 安装器路线不解压，那条凭据用不上，因此需要一条等价的
// 摘要对只说明"这就是 GitHub 上那个文件"，**不说明**那个文件是我们的安装包
// 而这一步之后要**执行**它，所以判断必须落在文件内容上，不能只看文件名
//
// 判定依据（全部实测自一份真实发布包）
//   * 前两字节是 MZ，且 e_lfanew 处是 PE 签名 —— 它确实是个 Windows 可执行文件
//   * 版本资源 ProductName 是 MIDITap
//   * 版本资源 CompanyName 是 MarchSnow
//   * 版本资源 Comments 含 Inno Setup —— 它确实是 Inno 装出来的安装包
// 后三条由 Inno 从 .iss 的 AppName / AppPublisher 与它自己写入
//
// 为什么读版本资源而不是扫全文找字节串
//   安装包有几十 MB，扫一遍要多读一次整个文件
//   版本资源就在 PE 头里，读一次是常数开销
//   实测 Comments 的值是 "This installation was built with Inno Setup."
//
// InstallerIdentity.cs — confirms the downloaded exe really is this app's Inno installer
//
// Why it is needed: the portable route confirms the package is ours by requiring MIDITap.exe and
// scripts/apply-update.ps1 after extraction
// The installer route does not extract, so that evidence is unavailable and an equivalent is needed
// A matching digest only says "this is the file GitHub has", NOT that the file is our installer
// The step after this one EXECUTES it, so the judgement has to rest on the file's contents rather than its name
//
// What is checked, all measured on a real release package
//   The first two bytes are MZ and the PE signature sits at e_lfanew, so it really is a Windows executable
//   The version resource's ProductName is MIDITap
//   The version resource's CompanyName is MarchSnow
//   The version resource's Comments mentions Inno Setup, so it really is an installer Inno built
// Inno writes the last three from the .iss AppName and AppPublisher, plus a comment of its own
//
// Why the version resource rather than scanning for a byte string
//   The installer is tens of MB, and a scan would read the whole file a second time
//   The version resource sits in the PE header, so reading it costs the same regardless of size
//   The measured value of Comments is "This installation was built with Inno Setup."

using System.Diagnostics;

namespace MIDITap.Core.Update;

/// <summary>身份校验的结论 / The outcome of the identity check</summary>
/// <param name="Ok">是否确认是本应用的安装包 / Whether it is confirmed to be this app's installer</param>
/// <param name="Reason">不通过的原因，通过时为 null（供日志使用，界面另有本地化文案）</param>
public sealed record InstallerIdentityResult(bool Ok, string? Reason = null);

public static class InstallerIdentity
{
    /// <summary>安装包版本资源里必须出现的产品名（由 .iss 的 AppName 写入）/ The product name that must appear, written from the .iss AppName</summary>
    public const string ExpectedProductName = "MIDITap";

    /// <summary>安装包版本资源里必须出现的公司名（由 .iss 的 AppPublisher 写入）/ The company name that must appear, written from the .iss AppPublisher</summary>
    public const string ExpectedCompanyName = "MarchSnow";

    /// <summary>Inno 自己写进 Comments 的标记，用来确认它确实是 Inno 装出来的包 / The marker Inno writes into Comments, confirming it is an Inno-built installer</summary>
    public const string InnoMarker = "Inno Setup";

    /// <summary>
    /// 校验一个文件是不是本应用的安装包
    /// 任何一条不通过都返回失败并带上原因，绝不抛异常：这个判断本身不该成为新的失败点
    ///
    /// Verifies that a file is this app's installer
    /// Any failed check returns a failure with a reason, and nothing is thrown: the judgement itself must not
    /// become a new failure point
    /// </summary>
    public static InstallerIdentityResult Verify(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new InstallerIdentityResult(false, "file is missing");
            }

            var header = VerifyPeHeader(path);
            if (header is not null)
            {
                return new InstallerIdentityResult(false, header);
            }

            // 版本资源读不到时 GetVersionInfo 返回一个字段全空的对象，而不是抛异常
            // 因此下面按"字段内容不对"处理即可
            //
            // When the version resource is unreadable GetVersionInfo returns an object with empty fields rather
            // than throwing, so the empty case is handled by the field rules below
            var info = FileVersionInfo.GetVersionInfo(path);
            return Evaluate(info.ProductName, info.CompanyName, info.Comments);
        }
        catch (Exception err)
        {
            return new InstallerIdentityResult(false, err.Message);
        }
    }

    /// <summary>
    /// 按版本资源的三个字段判定，与文件读取分开，因此可以脱离真实安装包测试
    ///
    /// Decides from the three version-resource fields
    /// Kept apart from the file reading so it can be tested without a real installer
    /// </summary>
    public static InstallerIdentityResult Evaluate(string? productName, string? companyName, string? comments)
    {
        if (!Contains(productName, ExpectedProductName))
        {
            return new InstallerIdentityResult(
                false, "ProductName is not " + ExpectedProductName + ": " + Describe(productName));
        }

        if (!Contains(companyName, ExpectedCompanyName))
        {
            return new InstallerIdentityResult(
                false, "CompanyName is not " + ExpectedCompanyName + ": " + Describe(companyName));
        }

        if (!Contains(comments, InnoMarker))
        {
            return new InstallerIdentityResult(
                false, "Comments does not mention " + InnoMarker + ": " + Describe(comments));
        }

        return new InstallerIdentityResult(true);
    }

    /// <summary>
    /// 校验 PE 头：前两字节是 MZ，且 e_lfanew 处是 PE 签名
    /// 通过返回 null，不通过返回原因
    ///
    /// Checks the PE header: the first two bytes are MZ and the PE signature sits at e_lfanew
    /// Returns null when it passes, or the reason when it does not
    /// </summary>
    private static string? VerifyPeHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // 0x3C 处是 e_lfanew（PE 头相对文件开头的偏移），这是 DOS 头固定下来的布局
        //
        // e_lfanew sits at 0x3C and holds the offset of the PE header, a fixed part of the DOS header layout
        if (stream.Length < 0x40)
        {
            return "file is too short to be an executable";
        }

        var dos = new byte[2];
        if (stream.Read(dos, 0, 2) != 2 || dos[0] != (byte)'M' || dos[1] != (byte)'Z')
        {
            return "missing the MZ signature";
        }

        stream.Position = 0x3C;
        var offsetBytes = new byte[4];
        if (stream.Read(offsetBytes, 0, 4) != 4)
        {
            return "cannot read the PE header offset";
        }

        var peOffset = BitConverter.ToInt32(offsetBytes, 0);
        if (peOffset < 0 || peOffset + 4 > stream.Length)
        {
            return "the PE header offset points outside the file";
        }

        stream.Position = peOffset;
        var signature = new byte[4];
        if (stream.Read(signature, 0, 4) != 4
            || signature[0] != (byte)'P' || signature[1] != (byte)'E'
            || signature[2] != 0 || signature[3] != 0)
        {
            return "missing the PE signature";
        }

        return null;
    }

    /// <summary>包含判断，忽略大小写与首尾空白 / A containment check that ignores case and surrounding whitespace</summary>
    private static bool Contains(string? value, string expected)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>把字段值变成日志里能读的一小段 / Turns a field value into a short readable fragment for the log</summary>
    private static string Describe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Trim();
}
