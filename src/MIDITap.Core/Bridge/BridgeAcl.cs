// BridgeAcl.cs — 在 Windows 上实现「0600 等价」的独占访问控制
//
// 为什么需要这个文件：POSIX 的权限位在 Windows 上不存在
// 上游 client 的 config.go 用 os.WriteFile(path, data, 0600) + os.Chmod 收紧密码文件，那份语义在 Windows 上没有对应物
// Windows 的等价物是**独占 DACL**：去掉一切继承来的 ACE，只留当前用户这条
// MIDITap 是便携应用，.storage/ 就在 exe 旁边，而 .storage 里的其它值是明文且继承目录 ACL
// 密码不能走那条路，所以单独由这里处理
//
// 三步顺序是刻意的，缺一步就会留下可读的密码：
//   1) 内容先写进 .part —— 在 Move 发生之前的任一时刻，正式路径上不会出现这一名字的半成品（写失败时删的是 .part）
//   2) 在 .part 上设 DACL —— 此时正式路径上还没有这个名字的文件可供打开
//   3) File.Move 到正式路径 —— 移动沿用同一份安全描述符
//   若反过来（先落到正式路径再设 ACL），中间那个窗口里同机的其它进程就能读到明文
//
// 「已存在文件的 ACL 不会因重写内容而变化」是这里的另一条前提
// File.WriteAllText 打开已有文件时不改安全描述符，所以每次保存都必须显式重设一遍
// 换句话说：第一次设好不等于之后一直好，用户手工改过、或从别处拷来的同名文件都带着自己的 DACL
//
// 三种结论，不允许静默降级：
//   Applied      DACL 已确认生效（写回后重新读回核对过，见 Verify）
//   NotSupported 这个卷/这个共享存不下安全描述符（FAT32、exFAT U 盘、未启用 ACL 的 SMB 共享）
//                调用方必须把它显示出来，而不是当作"已经设好了"
//   Failed       其他失败，例如无权修改该文件的安全描述符
//   未能归类的异常按 Failed 处理：那是更保守的一侧，界面会照常告警
//
// BridgeAcl.cs — the Windows equivalent of the POSIX 0600 mode
//
// Why this file exists: POSIX permission bits do not exist on Windows
// The upstream config.go tightens its password file with os.WriteFile(path, data, 0600) plus os.Chmod, and Windows
// has nothing that corresponds to those calls
// The Windows equivalent is an exclusive DACL: every inherited ACE is dropped and only the current user's entry is kept
// MIDITap is portable, so .storage/ sits right next to the exe, and the other values in there are plain text under
// the directory's inherited ACL — a password must not go that way, which is why this handling is separate
//
// The three steps below are in this order on purpose; skipping one leaves the password readable:
//   1) The content goes into a .part first, so at any moment before the Move the real path holds no half-written file
//      under that name — a failed write removes the .part instead of landing there
//   2) The DACL is applied to the .part, while the real path still has no file of that name for anyone to open
//   3) File.Move puts it at the real path, carrying the same security descriptor
//   The reverse order (write to the real path, then set the ACL) leaves a window in which another local process can
//   read the plain text
//
// Another premise here: rewriting a file's content does not change the ACL of a file that already exists
// File.WriteAllText leaves the security descriptor alone, so every save has to set it explicitly again
// Setting it once is therefore not the same as it staying set — a hand-edited or copied-in file brings its own DACL
//
// Three outcomes, and none of them may degrade silently:
//   Applied      the DACL was confirmed in place (written, then read back and checked — see Verify)
//   NotSupported this volume or share cannot store a security descriptor (FAT32 or exFAT thumb drives,
//                SMB shares without ACL support) — the caller has to surface it
//                instead of treating it as "already set"
//   Failed       anything else, e.g. no permission to change this file's security descriptor
//   An exception that cannot be classified is reported as Failed, the more conservative side, so the UI still warns

using System.Security.AccessControl;
using System.Security.Principal;

namespace MIDITap.Core.Bridge;

/// <summary>收紧访问范围的结果 / The outcome of narrowing the access list</summary>
public enum BridgeAclOutcome
{
    /// <summary>
    /// 已生效：独占 DACL 写入后重新读回核对过
    ///
    /// In place: the exclusive DACL was written and then read back and checked
    /// </summary>
    Applied,

    /// <summary>
    /// 该介质不支持 ACL：FAT32/exFAT U 盘、未启用 ACL 的 SMB 共享会走到这里
    /// 调用方必须提示用户，不能当作"已设置"
    ///
    /// The medium cannot hold an ACL: FAT32 or exFAT thumb drives and SMB shares without ACL support land here
    /// The caller has to tell the user rather than treating it as "already set"
    /// </summary>
    NotSupported,

    /// <summary>
    /// 失败：无法读取或写入该文件的安全描述符（例如不属于当前用户）
    ///
    /// Failed: the file's security descriptor could not be read or written, for instance because the current user
    /// does not own it
    /// </summary>
    Failed,
}

/// <summary>
/// 一次收紧的结果，<see cref="Error"/> 只供日志，界面按 <see cref="Outcome"/> 取本地化文案
///
/// One narrowing attempt. <see cref="Error"/> is for the log; the UI localises from <see cref="Outcome"/>
/// </summary>
public sealed record BridgeAclResult(BridgeAclOutcome Outcome, string? Error = null)
{
    /// <summary>是否已确认收紧成功 / Whether narrowing was confirmed</summary>
    public bool Ok => Outcome == BridgeAclOutcome.Applied;
}

public static class BridgeAcl
{
    /// <summary>临时文件的后缀，收紧完成后改名 / Suffix of the temporary file, renamed once the ACL is set</summary>
    public const string PartSuffix = ".part";

    // 不带 BOM 的 UTF-8：密码里若混进一个 BOM 字符，用户在同机其它工具中看到的就是一个不可见的前导字符
    // 而 File.WriteAllText 显式传 Encoding.UTF8 时会写出 BOM，所以这里用不写标识符的那份编码
    //
    // UTF-8 without a BOM: a BOM character inside a password is an invisible leading character the user would see
    // in any other tool on the same machine. File.WriteAllText emits one when Encoding.UTF8 is passed explicitly,
    // so this uses the encoding that writes no identifier
    private static readonly System.Text.Encoding Utf8NoBom =
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // Windows 上「ERROR_NOT_SUPPORTED」与「ERROR_INVALID_DATA」这两个原生错误码，是卷无法保存安全描述符时
    // 最常见回来的两个值；IOException 的 HResult 就是它们加 0x80070000 面罩后的结果
    // 其余异常（尤其 UnauthorizedAccessException）不能归到"介质不支持"上：
    // 那多半是真的没权限，报 NotSupported 会把一个应当修的问题说成环境问题
    //
    // On Windows these two native error codes are what a volume that cannot store a security descriptor most often
    // returns; an IOException's HResult is the code behind the 0x80070000 facility mask
    // Other exceptions, UnauthorizedAccessException in particular, are not classified as "medium unsupported":
    // those usually mean a genuine permission problem, and calling it an environment limitation would excuse a
    // condition the user should fix
    private const int ErrorNotSupported = unchecked((int)0x80070032);
    private const int ErrorInvalidData = unchecked((int)0x80070010);

    /// <summary>
    /// 把文本内容写成"只有当前用户可访问"的文件，并原子地落到 <paramref name="targetPath"/>
    /// 三步顺序的理由：正式路径上不能出现"已可打开但未收紧"的窗口
    /// 收紧未成功时删除 .part 并直接返回该结论：此时默认 ACL 下不会留下一个存着明文密码的文件
    ///
    /// Writes the text as a file only the current user can access, then lands it at the target path
    /// The step order keeps a window from appearing where the real path is openable but not yet narrowed
    /// When narrowing does not succeed the .part is deleted and that verdict is returned as-is: no file holding the
    /// plain password under default ACLs is left at either path
    /// </summary>
    public static BridgeAclResult WritePrivate(string content, string targetPath)
    {
        var partPath = targetPath + PartSuffix;
        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            DeleteQuietly(partPath);
            File.WriteAllText(partPath, content, Utf8NoBom);

            var applied = ApplyPrivate(partPath);
            if (!applied.Ok)
            {
                DeleteQuietly(partPath);
                return applied;
            }

            // File.Move 默认不覆盖，所以先删掉旧的正式文件
            // 删除失败（例如被别的进程占用）会走到 catch，此时 .part 也已删掉，正式路径上留下的仍是上一份内容
            //
            // File.Move does not overwrite, so the previous file at the real path is deleted first
            // A failed delete (the file is held by another process, say) is caught below with the .part already gone,
            // leaving the previous content at the real path untouched
            DeleteQuietly(targetPath);
            File.Move(partPath, targetPath);
            return applied;
        }
        catch (Exception err)
        {
            DeleteQuietly(partPath);
            return new BridgeAclResult(BridgeAclOutcome.Failed, err.Message);
        }
    }

    /// <summary>
    /// 对**已存在**的文件设置独占 DACL
    /// 每次保存密码都要调它一次：重写内容不会让旧文件的 ACL 跟着变
    ///
    /// Sets an exclusive DACL on an existing file
    /// Every password save calls it, since rewriting the content leaves the old file's ACL alone
    /// </summary>
    public static BridgeAclResult ApplyPrivate(string path)
    {
        SecurityIdentifier? owner;
        try
        {
            // 只取 SID，不取 "DOMAIN\user" 这类账户名
            // 名称要与安全描述符里的 SID 对上就得再查一次目录服务，且重名或改名时会匹配错
            //
            // The SID is used rather than a "DOMAIN\user" account name
            // Matching a name against the SID in the descriptor needs a directory lookup, and a duplicate or a
            // renamed account would match the wrong principal
            owner = WindowsIdentity.GetCurrent().User;
        }
        catch (Exception err)
        {
            return new BridgeAclResult(BridgeAclOutcome.Failed, err.Message);
        }

        if (owner is null)
        {
            // 拿不到用户 SID（例如以服务身份运行在无交互用户的会话里）时无法构造"只有当前用户"这条 ACE
            // 归类为 NotSupported：这是环境限制，不是这次写入做错了什么
            //
            // Without a user SID — a service running outside an interactive session, for instance — the "current
            // user only" ACE cannot be built. That is an environment limitation rather than a mistake in this write,
            // so it is classified as NotSupported
            return new BridgeAclResult(
                BridgeAclOutcome.NotSupported, "the current process has no user SID to grant access to");
        }

        try
        {
            // 走 FileInfo 的扩展方法，而不是 File 的静态调用
            // File.GetAccessControl 在 .NET 里并不存在（那是 .NET Framework 的静态成员）
            //
            // Called through FileInfo's extension methods rather than as statics on File
            // File.GetAccessControl does not exist in .NET (it was a .NET Framework static)
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // 去掉**别人已有的显式 ACE**，再加当前用户这一条
            // 少了这一步，本方法只做到"断开继承"，而显式条目里原本就有的 Everyone / Users 仍然生效
            // RemoveAccessRuleAll 会移除指名该身份的全部显式 ACE，因此下面的循环能清掉任何一次放宽留下的痕迹，
            // 而不只是那两个内置组
            //
            // The EXPLICIT ACEs already on the file are dropped before the current user's entry is added
            // Without this step the method only detaches inheritance, while an Everyone or Users ACE that was already
            // explicit keeps applying
            // RemoveAccessRuleAll drops every explicit ACE naming that identity, so the loop clears whatever a previous
            // widening left behind rather than only the two built-in groups
            var existingRules = security
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();
            foreach (var existing in existingRules)
            {
                if (existing.IdentityReference is SecurityIdentifier sid && sid == owner)
                {
                    continue;
                }
                security.RemoveAccessRuleAll(existing);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                owner, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception err) when (IsUnsupportedMedium(err))
        {
            return new BridgeAclResult(BridgeAclOutcome.NotSupported, err.Message);
        }
        catch (Exception err)
        {
            return new BridgeAclResult(BridgeAclOutcome.Failed, err.Message);
        }

        return Verify(path, owner);
    }

    /// <summary>
    /// 读回核对：文件确实处于受保护状态，且**显式** ACE 里只有当前用户这一条
    /// 两条合起来才等价于「只有当前用户能访问」：
    ///   AreAccessRulesProtected 为真说明继承来的 ACE 不参与访问判定
    ///   显式条目只剩当前用户这条，按 DACL 的判定规则就没有其他主体能通过访问检查（显式条目里不再有他们的 ACE）
    /// 某些文件系统接受设置调用却不保存它，随后照样能读回一份空的或继承来的 DACL
    /// 那种情况下应当返回 NotSupported，而把结果读回来比对是唯一能发现它的办法
    ///
    /// Reads the result back: the file really is protected, and the EXPLICIT ACE list holds the current user's entry
    /// alone. Both halves are needed for "only the current user has access": AreAccessRulesProtected being true means
    /// inherited ACEs play no part in the access decision, and with the explicit list holding only that one subject the
    /// DACL evaluation grants no other subject a way through (their ACEs are no longer in the explicit list)
    /// Some file systems accept the call and store nothing, after which an empty or inherited DACL still reads back
    /// cleanly. The verdict there should be NotSupported, and reading the result back and comparing is the only way
    /// to see it
    /// </summary>
    private static BridgeAclResult Verify(string path, SecurityIdentifier owner)
    {
        try
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            if (!security.AreAccessRulesProtected)
            {
                return new BridgeAclResult(
                    BridgeAclOutcome.NotSupported, "the protection flag did not persist on this volume");
            }

            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            var foreign = new List<string>();
            var granted = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.IdentityReference is not SecurityIdentifier sid)
                {
                    foreign.Add(rule.IdentityReference?.ToString() ?? "(unknown)");
                    continue;
                }
                if (sid == owner && rule.AccessControlType == AccessControlType.Allow)
                {
                    granted = true;
                    continue;
                }
                foreign.Add(sid.ToString());
            }

            if (!granted)
            {
                return new BridgeAclResult(
                    BridgeAclOutcome.NotSupported, "the current user's ACE did not persist on this volume");
            }
            if (foreign.Count > 0)
            {
                return new BridgeAclResult(
                    BridgeAclOutcome.NotSupported,
                    "other access entries survived on this volume: " + string.Join(", ", foreign));
            }

            return new BridgeAclResult(BridgeAclOutcome.Applied);
        }
        catch (Exception err) when (IsUnsupportedMedium(err))
        {
            return new BridgeAclResult(BridgeAclOutcome.NotSupported, err.Message);
        }
        catch (Exception err)
        {
            return new BridgeAclResult(BridgeAclOutcome.Failed, err.Message);
        }
    }

    /// <summary>
    /// 判断这次异常是不是"介质存不下安全描述符"
    /// 认不准就返回 false，由调用方按 Failed 处理：把权限问题说成环境问题会让用户以为无需处理
    ///
    /// Decides whether this exception means "the medium cannot store a security descriptor"
    /// When unsure it returns false and the caller reports Failed: presenting a permission problem as an environment
    /// limitation would tell the user there is nothing to fix
    /// </summary>
    private static bool IsUnsupportedMedium(Exception err) => err switch
    {
        PlatformNotSupportedException => true,
        IOException io => io.HResult is ErrorNotSupported or ErrorInvalidData,
        _ => false,
    };

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不改变结论 / A failed cleanup does not change the verdict
        }
    }
}
