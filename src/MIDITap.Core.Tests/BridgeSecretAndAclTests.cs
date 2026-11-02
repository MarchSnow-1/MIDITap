// BridgeSecretAndAclTests.cs — 密码条目的读写与「只有当前用户可访问」这条收紧
//
// 覆盖两件事：
//   1) 保存后当前用户仍能读回原始内容
//   2) 保存后文件的访问控制是"受保护 + 显式 ACE 只剩当前用户那一条"
//      只断言"文件存在"不够：一个继承着 Everyone 的密码文件同样"存在"
//
// 已存在文件的 ACL 不随内容重写自动变化，因此"第二次保存仍收紧"单独一条用例
// 该用例在 Save 退化成普通 File.WriteAllText 时会失败
//
// 介质范围：FAT32/exFAT 的 U 盘与未启用 ACL 的 SMB 共享会走到 NotSupported 分支，本仓库没有该介质的自动化覆盖
//
// BridgeSecretAndAclTests.cs — reading and writing the password entry, and the "current user only" narrowing
//
// Two properties are covered:
//   1) The current user can still read the original content back after saving
//   2) The file's access control is "protected, with the explicit ACE list holding the current user's entry alone"
//      Asserting only that "the file exists" is not enough: a password file inheriting Everyone also exists
//
// Rewriting a file's content does not change the ACL of a file that already exists, so "the second save is narrowed
// too" gets its own test
// That test fails when Save degrades into a plain File.WriteAllText
//
// Medium scope: FAT32 or exFAT thumb drives and SMB shares without ACL support take the NotSupported branch, and this
// repository has no automated coverage on such media

using System.Security.AccessControl;
using System.Security.Principal;
using MIDITap.Core.Bridge;
using MIDITap.Core.Settings;
using Xunit;

namespace MIDITap.Core.Tests;

/// <summary>
/// 需要「能改写文件 DACL」这条能力的用例；做不到时跳过而不是失败
///
/// 与 src/MIDITap.Integration.Tests 的 [MidiFact] 同一套做法
/// 判定方式：在临时目录里建一个文件，试着对它调用 SetAccessControl
/// 探测结果只测一次并缓存，因为它是环境属性而不是被测代码的属性
/// 拒绝改写 DACL 的环境里，连裸的 FileInfo.SetAccessControl 都返回 UnauthorizedAccessException，
/// 而普通写入仍然成功，因此跳过只针对 ACL 这一条能力，不涉及被测代码的其它部分
///
/// 不用硬编码 Skip：那会让这条能力在所有环境下都不再被验证
/// 探测到可用就照常跑，因此断言在支持该能力的环境里生效
///
/// Cases that need the ability to rewrite a file's DACL; SKIPPED rather than failed where that is unavailable
///
/// The same approach as [MidiFact] in src/MIDITap.Integration.Tests. The probe creates a file in a temp directory and
/// tries SetAccessControl on it; the result is tested once and cached, since it is a property of the environment
/// rather than of the code under test
/// Where DACL rewriting is refused even a bare FileInfo.SetAccessControl returns UnauthorizedAccessException while an
/// ordinary write still succeeds, so the skip targets exactly the ACL capability and nothing else
///
/// Why Skip is not hard-coded: hard-coding would leave this capability unverified everywhere
/// With a probe the assertions run wherever the capability exists
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class AclCapableFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Supported = new(Detect);

    /// <summary>
    /// 结论写进日志，避免"静默跳过"被误读成一切正常
    ///
    /// The verdict goes to the log so a skip cannot be misread as everything passing
    /// </summary>
    public AclCapableFactAttribute()
    {
        if (!Supported.Value)
        {
            Skip = "rewriting a file's DACL is refused on this machine; the ACL assertions need that capability";
            Console.Error.WriteLine("[miditap.test]: SKIPPING an ACL case — " + Skip);
        }
    }

    private static bool Detect()
    {
        string? probe = null;
        try
        {
            probe = Path.Combine(Path.GetTempPath(), "miditap-acl-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "probe");
            var info = new FileInfo(probe);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(true, false);
            info.SetAccessControl(security);
            return true;
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.test]: ACL capability probe failed: " + err.GetType().Name);
            return false;
        }
        finally
        {
            try
            {
                if (probe is not null && File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch
            {
                // 清理失败不影响探测结论 / A failed cleanup does not change what the probe found
            }
        }
    }
}

public sealed class BridgeSecretAndAclTests : IDisposable
{
    private readonly string _baseDir =
        Directory.CreateTempSubdirectory("miditap-bridge-secret-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_baseDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论 / A failed cleanup does not change the conclusion
        }
    }

    private string EntryPath => AppPaths.StorageFile(_baseDir, AppStorage.BridgePasswordStorageKey);

    // ================================================================ 往返 / Round trip

    [AclCapableFact]
    public void Saves_and_reads_the_password_back()
    {
        Assert.Null(BridgeSecret.Read(_baseDir));

        var result = BridgeSecret.SaveWithResult(_baseDir, "example-password");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(BridgeSecretVerdict.Saved, result.Verdict);
        Assert.True(result.Changed);
        Assert.Equal("example-password", BridgeSecret.Read(_baseDir));
    }

    [AclCapableFact]
    public void Keeps_surrounding_and_inner_spaces_verbatim()
    {
        // 密码里的空格属于密码本身，读写两侧都不修剪
        // 修剪会得到另一个密码，因此需要一条用例锁住原样往返
        //
        // A space inside a password is part of the password, and neither direction trims
        // Trimming would produce a different password, so a test pins the verbatim round trip
        const string password = " example password ";

        BridgeSecret.Save(_baseDir, password);

        Assert.Equal(password, BridgeSecret.Read(_baseDir));
    }

    [AclCapableFact]
    public void Writes_without_a_byte_order_mark()
    {
        // 带 BOM 的密码文件会让用户在同机其它工具里看到一个不可见的前导字符
        //
        // A password file with a BOM shows the user an invisible leading character in any other tool on the machine
        BridgeSecret.Save(_baseDir, "example-password");

        var bytes = File.ReadAllBytes(EntryPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("example-password", System.Text.Encoding.UTF8.GetString(bytes));
    }

    // ================================================================ 收紧访问范围 / Narrowing the access list

    [AclCapableFact]
    public void The_saved_entry_is_protected_and_granted_to_the_current_user_alone()
    {
        // 只检查"文件存在"无法发现一个 Everyone 都能读的密码文件，这里断言的是显式 ACE 只剩当前用户
        //
        // Checking only that the file exists cannot reveal a password file that Everyone can read, so this asserts the
        // explicit ACE list holds the current user alone
        BridgeSecret.Save(_baseDir, "example-password");

        var security = new FileInfo(EntryPath).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "继承的 ACE 必须已断开");

        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        var owner = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(owner);
        Assert.Single(rules);
        Assert.Equal(owner, rules[0].IdentityReference);
        Assert.Equal(AccessControlType.Allow, rules[0].AccessControlType);
    }

    [AclCapableFact]
    public void Saving_again_narrows_the_acl_even_when_it_had_been_loosened()
    {
        // 已存在文件的 ACL 不随内容重写自动变化，因此每次保存都必须显式重设
        // 用例先把 ACL 放宽再保存，锁住的是"重新收紧"这一行为，Save 退化成普通写入时该用例失败
        //
        // Rewriting a file's content does not change the ACL of an existing file, so every save has to set it again
        // The ACL is loosened first and then saved, pinning the re-narrowing; the test fails should Save degrade to a
        // plain write
        BridgeSecret.Save(_baseDir, "first-password");

        var info = new FileInfo(EntryPath);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        info.SetAccessControl(security);

        // 放宽动作本身先被确认生效，否则本用例会在什么都没改的情况下空转通过
        //
        // The loosening itself is confirmed first, or this test would pass without changing anything
        var loosened = new FileInfo(EntryPath)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        Assert.True(loosened.Count > 1, "前置条件失败：放宽 ACL 没有生效");

        BridgeSecret.Save(_baseDir, "second-password");

        var narrowed = new FileInfo(EntryPath)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        Assert.Single(narrowed);
        Assert.Equal("second-password", BridgeSecret.Read(_baseDir));
    }

    [AclCapableFact]
    public void Check_access_repairs_an_entry_whose_acl_was_loosened_elsewhere()
    {
        // 别处改过的 ACL 会让"保存时收紧过"不再成立
        // CheckAccess 既检查也修复，设置页加载时用它复查
        //
        // An ACL edited elsewhere makes "it was narrowed when saved" no longer true
        // CheckAccess both checks and repairs, and the settings page calls it while loading
        BridgeSecret.Save(_baseDir, "example-password");

        var info = new FileInfo(EntryPath);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        info.SetAccessControl(security);

        var result = BridgeSecret.CheckAccess(_baseDir);

        Assert.True(result.Ok, result.Error);
        var rules = new FileInfo(EntryPath)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        Assert.Single(rules);
    }

    [Fact]
    public void Check_access_on_a_missing_entry_reports_applied()
    {
        // 还没有密码文件时没有需要收紧的东西，返回 Applied 而不是 NotSupported
        // 返回 NotSupported 会让界面在"未配置密码"的状态下挂一条误导性的告警
        //
        // With no password file there is nothing to narrow, so Applied is reported rather than NotSupported
        // Reporting NotSupported would make the UI show a misleading warning in the "not configured" state
        var result = BridgeSecret.CheckAccess(_baseDir);

        Assert.Equal(BridgeAclOutcome.Applied, result.Outcome);
    }

    [AclCapableFact]
    public void The_written_entry_is_readable_by_the_current_user_at_once()
    {
        // 收紧到"只有当前用户"之后，同一次运行里仍然要读得到
        // 写错一条 DACL 会让应用自己都打不开它，这同样是收紧逻辑的缺陷
        //
        // After narrowing to "the current user alone" it must still be readable within the same run
        // A wrong DACL would stop the app itself from opening it, which is equally a defect in the narrowing
        BridgeSecret.Save(_baseDir, "example-password");

        Assert.True(File.Exists(EntryPath));
        Assert.Equal("example-password", File.ReadAllText(EntryPath));
    }

    // ================================================================ 读取的失败分支 / Read failure branches

    [Fact]
    public void A_missing_entry_reads_as_null()
    {
        Assert.Null(BridgeSecret.Read(_baseDir));
    }

    [Fact]
    public void A_zero_byte_entry_reads_as_null()
    {
        // 零字节文件按"未配置"处理：把它当成空密码返回，会让上游把空串当密码去连，症状是"一直认证失败"
        //
        // A zero-byte file counts as "not configured": returning it as an empty password would make the upstream try to
        // authenticate with an empty string, and the symptom would be "authentication keeps failing"
        Directory.CreateDirectory(AppPaths.StorageDir(_baseDir));
        File.WriteAllText(EntryPath, string.Empty);

        Assert.Null(BridgeSecret.Read(_baseDir));
    }

    [Fact]
    public void An_entry_that_is_not_valid_utf8_reads_as_null()
    {
        // 非法 UTF-8 不被当成密码：返回一堆替换字符比返回 null 更糟，那会被拿去当真密码用
        //
        // Invalid UTF-8 is not treated as a password: returning a wall of replacement characters is worse than null,
        // since it would be used as the real password
        Directory.CreateDirectory(AppPaths.StorageDir(_baseDir));
        File.WriteAllBytes(EntryPath, [0xFF, 0xFE, 0xFD]);

        Assert.Null(BridgeSecret.Read(_baseDir));
    }

    // ================================================================ 清除 / Clearing

    [AclCapableFact]
    public void Clearing_removes_the_entry()
    {
        BridgeSecret.Save(_baseDir, "example-password");

        var result = BridgeSecret.SaveWithResult(_baseDir, null);

        Assert.True(result.Ok);
        Assert.Equal(BridgeSecretVerdict.Cleared, result.Verdict);
        Assert.True(result.Changed);
        Assert.False(File.Exists(EntryPath));
        Assert.Null(BridgeSecret.Read(_baseDir));
    }

    [Fact]
    public void Clearing_an_absent_entry_still_succeeds()
    {
        // 幂等：重复清除不该让界面报错
        //
        // Idempotent: a repeated clear must not raise an error in the UI
        var result = BridgeSecret.SaveWithResult(_baseDir, string.Empty);

        Assert.True(result.Ok);
        Assert.Equal(BridgeSecretVerdict.Cleared, result.Verdict);
    }

    [AclCapableFact]
    public void An_empty_string_clears_rather_than_storing_an_empty_password()
    {
        // 空串表示"用户清空了密码框"，语义是清除；存一个空密码会让上游拿空串去认证
        //
        // An empty string means "the user cleared the password box", which clears; storing an empty password would make
        // the upstream authenticate with an empty string
        BridgeSecret.Save(_baseDir, "example-password");

        BridgeSecret.Save(_baseDir, string.Empty);

        Assert.False(File.Exists(EntryPath));
        Assert.Null(BridgeSecret.Read(_baseDir));
    }

    // ================================================================ 临时文件 / The .part file

    [AclCapableFact]
    public void No_part_file_is_left_behind_after_a_successful_save()
    {
        // .part 残留会让下一次保存困惑，也可能被误当成条目本身
        //
        // A leftover .part confuses the next save and may even be mistaken for the entry itself
        BridgeSecret.Save(_baseDir, "example-password");

        Assert.False(File.Exists(EntryPath + BridgeAcl.PartSuffix));
        Assert.False(File.Exists(EntryPath + BridgeAcl.PartSuffix + BridgeAcl.PartSuffix));
    }

    [Fact]
    public void A_failed_write_leaves_no_entry_and_no_part()
    {
        // 可构造的失败路径：目标路径是一个目录，File.Move 失败
        // 用例锁住的是"失败之后不留任何文件"：正式路径上没有半成品，也没有 .part
        // 用默认 ACL 存着明文密码的文件正是这一步要防的
        //
        // A constructible failure path: the target path is a DIRECTORY, so File.Move fails
        // The property pinned here is "nothing is left behind after the failure": neither a half-written file at the
        // real path nor a .part
        // A file holding the plain password under default ACLs is exactly what this guards against
        var targetDirectory = Path.Combine(_baseDir, "occupied-by-a-directory");
        Directory.CreateDirectory(targetDirectory);

        var result = BridgeAcl.WritePrivate("example-password", targetDirectory);

        Assert.False(result.Ok);
        Assert.False(File.Exists(targetDirectory + BridgeAcl.PartSuffix));
    }

    // ================================================================ 介质限制 / The unsupported-medium branch

    [Fact]
    public void An_unsupported_medium_is_reported_as_not_supported_rather_than_assumed_to_be_set()
    {
        // 仓库没有 FAT32/exFAT 介质的自动化覆盖（见文件头），本用例锁住的是枚举取值彼此可区分
        // 也就是"不允许静默降级成已经设置好"这一前提
        //
        // The repository has no automated coverage on FAT32 or exFAT media (see the header); what this case pins is that
        // the enum values stay distinguishable, the premise behind "no silent downgrade to already-set"
        var values = Enum.GetValues<BridgeAclOutcome>();

        Assert.Contains(BridgeAclOutcome.Applied, values);
        Assert.Contains(BridgeAclOutcome.NotSupported, values);
        Assert.Contains(BridgeAclOutcome.Failed, values);
        Assert.NotEqual(BridgeAclOutcome.Applied, BridgeAclOutcome.NotSupported);
        // NotSupported 与 Failed 都不能被当作成功
        Assert.False(new BridgeAclResult(BridgeAclOutcome.NotSupported).Ok);
        Assert.False(new BridgeAclResult(BridgeAclOutcome.Failed).Ok);
        Assert.True(new BridgeAclResult(BridgeAclOutcome.Applied).Ok);
    }

    [Fact]
    public void A_secret_save_maps_a_failed_narrowing_to_a_non_success_verdict()
    {
        // SaveWithResult 在收紧失败时必须返回非成功结论，不能仍报 Saved
        // 用已存在的目录占住条目路径来构造失败
        //
        // SaveWithResult has to return a non-success verdict when narrowing fails rather than still reporting Saved
        // The failure is constructed by an existing DIRECTORY occupying the entry path
        var occupied = Path.Combine(AppPaths.StorageDir(_baseDir), AppStorage.BridgePasswordStorageKey);
        Directory.CreateDirectory(occupied);

        var result = BridgeSecret.SaveWithResult(_baseDir, "example-password");

        Assert.False(result.Ok);
        Assert.True(
            result.Verdict is BridgeSecretVerdict.AccessFailed or BridgeSecretVerdict.WriteFailed
                or BridgeSecretVerdict.AccessNotSupported,
            "收紧未成功时不得报告 Saved，实际为 " + result.Verdict);
        Assert.False(result.Changed);
    }
}
