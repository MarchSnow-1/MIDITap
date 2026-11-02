// BridgeSecret.cs — MIDIBridge 密码条目的读写与格式
// 位置在 .storage 下，与其余设置值同一目录，路径由 AppPaths.StorageFile 派生（本文件不自己拼）
//
// 为什么这一条要走单独一份文件而不是直接 File.WriteAllText：
//   .storage 里的其它值是明文且继承目录 ACL，同机任何用户进程都读得到
//   密码不能那样存，所以写入交给 BridgeAcl：内容先落 .part、DACL 收紧到当前用户 SID、再改名到正式路径
//
// 关于"降级"的边界（这条是安全要求，不是风格选择）：
//   读取失败、文件不存在、内容为零字节 —— 三种情况一律返回 null，由调用方当作"未配置"处理
//   存在文件但内容含非法 UTF-8 字节时同样返回 null：按字节读入后用 throwOnInvalidBytes 的解码器解码，
//     解不出来就落到 catch 并返回 null
//     注意 File.ReadAllText **不会**因非法字节抛出：它用的是宽容解码器，坏字节被换成 U+FFFD 替换字符照常返回
//     所以这里不用它，见 Read 里的注释
//   因此不会出现"读出一堆乱码然后当真密码用"的结果
//   ACL 收紧未成功时**不返回成功**：WritePrivate 会删掉 .part，正式路径保持上一次的内容不变
//   调用方拿到 AccessNotSupported / AccessFailed 之后要不要继续用，是界面层的决定，本文件不替它默认
//
// 修剪的边界：读写两侧都不做 Trim
//   密码里的空格属于密码本身，修剪会把「password 」这样的值改写成另一个密码，而用户看不出发生了什么
//   只有"文件内容为零字节"被解释为未配置（见上）
//
// BridgeSecret.cs — reading and writing the MIDIBridge password entry
// It sits under .storage like the other setting values, and the path comes from AppPaths.StorageFile
// (this file does not assemble it)
//
// Why this entry needs its own file instead of a plain File.WriteAllText:
//   The other values in .storage are plain text under the directory's inherited ACL, so any local process owned by
//   another user can read them
//   A password must not be stored that way, so writing goes through BridgeAcl: content into a .part, the DACL
//   narrowed to the current user's SID, then renamed onto the real path
//
// The boundary around "falling back" (a security requirement, not a style choice):
//   A read failure, a missing file, and a zero-byte file all return null, which the caller treats as "not configured"
//   A file whose bytes are not valid UTF-8 also returns null: the bytes are read first and decoded with a strict
//   decoder, so a decode failure lands in the catch and a wall of garbage is not handed back as the password
//   (File.ReadAllText would not help here — see the comment inside Read)
//   When narrowing the ACL does not succeed the result is not reported as success: WritePrivate deletes the .part and
//   the real path keeps its previous content untouched
//   Whether to carry on after AccessNotSupported / AccessFailed is the UI layer's decision, and this file does not
//   make it on the caller's behalf
//
// Trimming: neither direction trims
//   A space inside a password is part of the password, so trimming would silently rewrite «password » into a
//   different password, with nothing visible to the user
//   Only a zero-length file is interpreted as "not configured" (see above)

using MIDITap.Core.Settings;

namespace MIDITap.Core.Bridge;

/// <summary>一次写入密码的结论 / The verdict of one password write</summary>
public enum BridgeSecretVerdict
{
    /// <summary>
    /// 已保存且访问范围已确认收紧到当前用户
    ///
    /// Saved, and the access list was confirmed narrowed to the current user
    /// </summary>
    Saved,

    /// <summary>
    /// 已清除（传入 null 或空串，条目文件已删除）
    ///
    /// Cleared: a null or empty value was passed and the entry file was deleted
    /// </summary>
    Cleared,

    /// <summary>
    /// 介质存不下收紧后的 DACL（FAT32/exFAT U 盘、未启用 ACL 的共享）
    /// 密码**未落盘**，界面应提示用户换位置或自行知悉风险
    ///
    /// The medium cannot hold the narrowed DACL (FAT32 or exFAT thumb drives, shares without ACL support)
    /// The password was **not written to disk**; the UI should tell the user to move the app or accept the risk
    /// </summary>
    AccessNotSupported,

    /// <summary>
    /// 收紧访问范围失败（权限、文件被占用）；密码同样**未落盘**
    ///
    /// Narrowing failed (permissions, the file is held by another process); the password was **not written**
    /// </summary>
    AccessFailed,

    /// <summary>
    /// 其它写入失败（磁盘满、目录不可写）
    ///
    /// Any other write failure (the disk is full, the directory is not writable)
    /// </summary>
    WriteFailed,
}

/// <summary>
/// 一次写入的结果
/// <see cref="Error"/> 供日志排查；界面按 <see cref="Verdict"/> 取本地化文案
///
/// The result of one write. <see cref="Error"/> is for the log; the UI localises from <see cref="Verdict"/>
/// </summary>
public sealed record BridgeSecretResult(bool Ok, BridgeSecretVerdict Verdict, string? Error = null)
{
    /// <summary>这次调用有没有改动磁盘上的条目 / Whether this call changed the entry on disk</summary>
    public bool Changed => Ok && Verdict is BridgeSecretVerdict.Saved or BridgeSecretVerdict.Cleared;
}

public static class BridgeSecret
{
    /// <summary>
    /// 条目文件名，取自 AppStorage 的存储键名，因此"叫什么"只有一处定义
    /// 方向是 Bridge → Settings：本文件引用设置层的常量，设置层不引用这里的文件名
    ///
    /// The entry file name, taken from AppStorage's key name so the spelling exists in one place
    /// The direction is Bridge → Settings: this file refers to the settings-layer constant, not the reverse
    /// </summary>
    public static string FileName => AppStorage.BridgePasswordStorageKey;

    /// <summary>条目文件的完整路径 / Full path of the entry file</summary>
    public static string EntryPath(string baseDir) => AppPaths.StorageFile(baseDir, FileName);

    /// <summary>
    /// 某个服务器自己的密码文件名
    /// 列表化之后每个服务器一份，文件名带上服务器 id；id 为空时退回单一文件名
    /// 只允许 [0-9a-z] 之外的字符被替换掉，避免 id 里的异常字符把路径带出 .storage
    ///
    /// The password file name for one server
    /// Each server keeps its own, with the server id in the name; an empty id falls back to the single file name
    /// Characters outside [0-9a-z] are replaced, so an odd id cannot walk the path out of .storage
    /// </summary>
    public static string FileNameFor(string? serverId)
    {
        var id = (serverId ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            return FileName;
        }

        var safe = new System.Text.StringBuilder(id.Length);
        foreach (var ch in id)
        {
            safe.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : '_');
        }
        return FileName + "_" + safe;
    }

    /// <summary>某个服务器密码条目的完整路径 / Full path of one server's password entry</summary>
    public static string EntryPathFor(string baseDir, string? serverId)
        => AppPaths.StorageFile(baseDir, FileNameFor(serverId));

    /// <summary>
    /// 读取密码；文件不存在、读不出、内容不是合法 UTF-8、内容长度为零，四种情况都返回 null
    /// 返回值原样取自文件内容，不做修剪（理由见文件头）
    ///
    /// Reads the password; null when the file is missing, unreadable, not valid UTF-8, or zero bytes long
    /// The value comes back exactly as stored, with no trimming (see the header for why)
    /// </summary>
    public static string? Read(string baseDir)
    {
        return ReadFor(baseDir, null);
    }

    /// <summary>
    /// 读取某个服务器的密码；边界与 <see cref="Read"/> 完全一致，只是路径换成该服务器自己的文件
    ///
    /// Reads one server's password; the boundaries match <see cref="Read"/> exactly, only the path differs
    /// </summary>
    public static string? ReadFor(string baseDir, string? serverId)
    {
        try
        {
            var path = EntryPathFor(baseDir, serverId);
            if (!File.Exists(path))
            {
                return null;
            }

            // 按字节读再用「遇到非法字节就抛」的 UTF-8 解码
            // File.ReadAllText 走的是解码器的宽容模式：FF FE 这类非法序列会变成 U+FFFD 替换字符返回，
            // 既不抛异常也不等于 null，于是"返回一堆乱码当真密码用"恰恰会发生
            // 这里要求的是严格解码：解不出来就按未配置处理（见文件头关于降级的边界）
            //
            // Read as bytes, then decode as UTF-8 with the decoder set to throw on invalid sequences
            // File.ReadAllText uses the decoder's lenient mode: an illegal run such as FF FE comes back as U+FFFD
            // replacement characters instead of either throwing or being null, so "a wall of garbage used as the real
            // password" is exactly what would happen
            // Strict decoding is what is wanted: undecodable bytes count as not configured (see the header on falling back)
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return null;
            }
            return StrictUtf8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    // throwOnInvalidBytes: true —— 解码遇到非法字节序列时抛 ArgumentException/DecoderFallbackException
    // 默认那份 Encoding.UTF8 用的是 ReplacementFallback，会把坏字节换成了 U+FFFD 而不报告
    //
    // With throwOnInvalidBytes the decoder raises an exception on an illegal byte sequence
    // The default Encoding.UTF8 uses ReplacementFallback, silently turning bad bytes into U+FFFD with nothing reported
    private static readonly System.Text.Encoding StrictUtf8 =
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 保存密码，并把访问范围收紧到当前用户
    /// 传入 null 或空串表示清除条目
    /// 返回 bool 是为了与 AppStorage 其它 Save* 的写法一致；需要区分失败原因时用 <see cref="SaveWithResult"/>
    ///
    /// Saves the password and narrows its access list to the current user
    /// A null or empty value clears the entry
    /// The bool return matches the other AppStorage Save* methods; use <see cref="SaveWithResult"/> to tell the
    /// failure reasons apart
    /// </summary>
    public static bool Save(string baseDir, string? password) => SaveWithResult(baseDir, password).Ok;

    /// <summary>
    /// 保存并回报具体结论（界面层的常驻提示需要区分 NotSupported 与 Failed）
    /// 顺序：先确认能收紧，再让正式路径上的内容变化 —— 收紧未成功时不写正式路径
    ///
    /// Saves and reports which verdict applies, since a persistent UI banner has to tell NotSupported from Failed
    /// Order: narrowing is confirmed before the real path changes, so a failed narrowing leaves the real path alone
    /// </summary>
    public static BridgeSecretResult SaveWithResult(string baseDir, string? password)
    {
        return SaveWithResultFor(baseDir, null, password);
    }

    /// <summary>
    /// 保存某个服务器的密码并回报具体结论
    /// 顺序与 <see cref="SaveWithResult"/> 相同，只换了路径
    ///
    /// Saves one server's password and reports which verdict applies
    /// Same order as <see cref="SaveWithResult"/>, only the path differs
    /// </summary>
    public static BridgeSecretResult SaveWithResultFor(string baseDir, string? serverId, string? password)
    {
        var path = EntryPathFor(baseDir, serverId);

        if (string.IsNullOrEmpty(password))
        {
            return ClearFor(baseDir, serverId);
        }

        try
        {
            Directory.CreateDirectory(AppPaths.StorageDir(baseDir));
        }
        catch (Exception err)
        {
            return new BridgeSecretResult(false, BridgeSecretVerdict.WriteFailed, err.Message);
        }

        var written = BridgeAcl.WritePrivate(password, path);
        return written.Ok
            ? new BridgeSecretResult(true, BridgeSecretVerdict.Saved)
            : new BridgeSecretResult(
                false,
                written.Outcome == BridgeAclOutcome.NotSupported
                    ? BridgeSecretVerdict.AccessNotSupported
                    : BridgeSecretVerdict.AccessFailed,
                written.Error);
    }

    /// <summary>
    /// 删除条目；文件本来就不存在也算成功（幂等，重复调用不会让界面报错）
    ///
    /// Deletes the entry; a file that does not exist also counts as success, so a repeated call does not raise an
    /// error in the UI
    /// </summary>
    public static BridgeSecretResult Clear(string baseDir)
    {
        return ClearFor(baseDir, null);
    }

    /// <summary>
    /// 删除某个服务器的密码条目；文件本来就不存在也算成功，与 <see cref="Clear"/> 的幂等语义一致
    ///
    /// Deletes one server's password entry; a file that does not exist also counts as success, matching the
    /// idempotent semantics of <see cref="Clear"/>
    /// </summary>
    public static BridgeSecretResult ClearFor(string baseDir, string? serverId)
    {
        try
        {
            var path = EntryPathFor(baseDir, serverId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            DeleteQuietly(path + BridgeAcl.PartSuffix);
            return new BridgeSecretResult(true, BridgeSecretVerdict.Cleared);
        }
        catch (Exception err)
        {
            return new BridgeSecretResult(false, BridgeSecretVerdict.WriteFailed, err.Message);
        }
    }

    /// <summary>
    /// 只检查已存在条目的访问范围，不重写内容
    /// 界面在加载设置页时用它决定要不要挂常驻提示：用户在别处改过 ACL、或应用被从 NTFS 拷到 exFAT U 盘上，
    /// 都会让"上次保存时收紧过"这件事不再成立
    /// 条目不存在时返回 Applied：没有密码文件，也就没有需要收紧的东西（调用方应先判 File.Exists）
    ///
    /// Checks the access list of an existing entry without rewriting its content
    /// The settings page calls it while loading, to decide whether a banner is needed: an ACL edited elsewhere, or
    /// the app copied from NTFS onto an exFAT thumb drive, both make "it was narrowed when it was saved" no longer true
    /// A missing entry returns Applied, since there is nothing to narrow (the caller checks File.Exists first)
    /// </summary>
    public static BridgeAclResult CheckAccess(string baseDir)
    {
        var path = EntryPath(baseDir);
        return File.Exists(path) ? BridgeAcl.ApplyPrivate(path) : new BridgeAclResult(BridgeAclOutcome.Applied);
    }

    /// <summary>
    /// 删除迁移前的密码条目（不带服务器 id 的那一份），在列表文件接管之后调用
    /// 放在本层而不是 Settings 层，是因为密码文件名归本层定义（Settings 不反向引用 Bridge）
    ///
    /// Deletes the pre-migration password entry (the one without a server id), called after the list file takes over
    /// It lives in this layer rather than Settings because this layer owns the password file name
    /// (Settings never refers back to Bridge)
    /// </summary>
    public static void ClearLegacy(string baseDir) => ClearFor(baseDir, null);

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
            // 清理临时文件失败不改变结论 / A failed cleanup of the temporary file does not change the verdict
        }
    }
}
