// InstallerIdentityTests.cs — 安装包身份校验
//
// 为什么值得测：这一步通过之后应用会**执行**那个文件
// 判宽了等于把任意下载到的 exe 当安装包跑起来，判严了安装器路线永远走不通
// 因此这里把四条依据逐一钉住，并确保每一条单独不满足都会被拒
//
// 真实的安装包不能作为测试数据（它来自公网，且体积几十 MB）
// 因此文件层用合成的 PE 头测试，字段层用纯函数测试
//
// Why these matter: once this passes, the app EXECUTES the file
// Too loose means running whatever exe was downloaded, too strict means the installer route never works
// Each of the four checks is therefore pinned here, and each is shown to reject on its own
//
// A real installer cannot serve as test data: it comes from the public internet and is tens of MB
// The file layer is therefore tested with a synthetic PE header, and the field layer with the pure function
//
// InstallerIdentityTests.cs — confirming a downloaded exe is this app's installer

using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class InstallerIdentityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "miditap-installer-identity-" + Guid.NewGuid().ToString("N"));

    public InstallerIdentityTests() => Directory.CreateDirectory(_dir);

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

    // ---------------- 字段层 / The field layer ----------------

    [Fact]
    public void The_measured_release_values_pass()
    {
        // 这三个值是实测自一份真实发布包的版本资源
        // 改动 .iss 的 AppName 或 AppPublisher 会让它们变，CI 那一步会先发现
        //
        // These three values were measured from a real release package's version resource
        // Changing the .iss AppName or AppPublisher would alter them, and the CI step notices first
        var result = InstallerIdentity.Evaluate("MIDITap", "MarchSnow", "This installation was built with Inno Setup.");

        Assert.True(result.Ok);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void A_wrong_product_name_is_rejected()
    {
        var result = InstallerIdentity.Evaluate("OtherApp", "MarchSnow", "This installation was built with Inno Setup.");

        Assert.False(result.Ok);
        Assert.Contains("ProductName", result.Reason);
    }

    [Fact]
    public void A_wrong_company_name_is_rejected()
    {
        var result = InstallerIdentity.Evaluate("MIDITap", "SomeoneElse", "This installation was built with Inno Setup.");

        Assert.False(result.Ok);
        Assert.Contains("CompanyName", result.Reason);
    }

    [Fact]
    public void A_missing_inno_comment_is_rejected()
    {
        // Comments 是"它确实是 Inno 装出来的包"这条依据
        // 少了它，一个碰巧带上产品名与公司名的普通 exe 就能通过
        //
        // Comments is the evidence that Inno built this package
        // Without it an ordinary exe that happens to carry the product and company names would pass
        var result = InstallerIdentity.Evaluate("MIDITap", "MarchSnow", null);

        Assert.False(result.Ok);
        Assert.Contains("Comments", result.Reason);
    }

    [Fact]
    public void Missing_fields_are_all_rejected()
    {
        Assert.False(InstallerIdentity.Evaluate(null, null, null).Ok);
        Assert.False(InstallerIdentity.Evaluate("", "MarchSnow", "Inno Setup").Ok);
        Assert.False(InstallerIdentity.Evaluate("MIDITap", "", "Inno Setup").Ok);
        Assert.False(InstallerIdentity.Evaluate("MIDITap", "MarchSnow", "").Ok);
    }

    [Fact]
    public void Matching_ignores_case_and_surrounding_whitespace()
    {
        // 版本资源里的值实测带大量尾随空格，因此必须先去空白
        //
        // The measured values carry a lot of trailing whitespace, so trimming is required
        var result = InstallerIdentity.Evaluate("  miditap  ", " MARCHSNOW ", "this installation was built with inno setup.");

        Assert.True(result.Ok);
    }

    // ---------------- 文件层 / The file layer ----------------

    [Fact]
    public void A_missing_file_is_rejected()
    {
        var result = InstallerIdentity.Verify(Path.Combine(_dir, "absent.exe"));

        Assert.False(result.Ok);
        Assert.Contains("missing", result.Reason);
    }

    [Fact]
    public void A_file_without_the_mz_signature_is_rejected()
    {
        var path = Write(Path.Combine(_dir, "not-pe.exe"), new byte[512]);

        var result = InstallerIdentity.Verify(path);

        Assert.False(result.Ok);
        Assert.Contains("MZ", result.Reason);
    }

    [Fact]
    public void A_file_without_the_pe_signature_is_rejected()
    {
        // 有 MZ 但 e_lfanew 处不是 PE 签名：一个 DOS 可执行文件、或被人改坏的文件
        //
        // MZ present but no PE signature at e_lfanew: a DOS executable, or a file someone damaged
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);

        var result = InstallerIdentity.Verify(Write(Path.Combine(_dir, "dos.exe"), bytes));

        Assert.False(result.Ok);
        Assert.Contains("PE signature", result.Reason);
    }

    [Fact]
    public void An_offset_pointing_outside_the_file_is_rejected()
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, 0x3C);

        var result = InstallerIdentity.Verify(Write(Path.Combine(_dir, "beyond.exe"), bytes));

        Assert.False(result.Ok);
        Assert.Contains("outside", result.Reason);
    }

    [Fact]
    public void A_valid_pe_without_our_version_resource_is_rejected()
    {
        // 这条把文件层与字段层串起来：PE 头合法，但没有版本资源
        // 真实场景就是"下载到一个别的程序的 exe"，它必须是拒绝
        //
        // This ties the two layers together: a valid PE header with no version resource
        // The real-world case is "downloaded some other program's exe", and it must be rejected
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        bytes[0x82] = 0;
        bytes[0x83] = 0;

        var result = InstallerIdentity.Verify(Write(Path.Combine(_dir, "valid-pe.exe"), bytes));

        Assert.False(result.Ok);
        Assert.Contains("ProductName", result.Reason);
    }

    [Fact]
    public void A_file_too_short_to_hold_a_dos_header_is_rejected()
    {
        var result = InstallerIdentity.Verify(Write(Path.Combine(_dir, "tiny.exe"), new byte[16]));

        Assert.False(result.Ok);
        Assert.Contains("too short", result.Reason);
    }

    private string Write(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
