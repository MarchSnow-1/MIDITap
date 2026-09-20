// InstallKind.cs — 判断当前这份应用是"便携包解压出来的"还是"安装器装上的"
//
// 为什么需要这条判断：两条安装方式的更新路径不同
//   便携包没有注册表项，只能靠辅助脚本逐文件替换（现在就是这样）
//   安装器装的有注册表项，可以静默跑安装包，由 Inno 一并更新文件、注册表与卸载日志
// 判定错了的后果不对称
//   把安装器装的当便携包：更新后注册表里的版本号不更新，用户看到的就是"更新了但显示还是旧版"
//   把便携包当安装器装的：会另装一份到默认目录，原地那份留在那里，用户得到两个应用
// 因此判定要偏保守：认不出来一律按便携包处理，宁可少做也不多做
//
// 为什么用"InstallLocation 是否等于自身目录"而不是比对 AppId
//   安装器用的 AppId 是 GUID，抄进代码就是第二份安装器知识，改名或换 GUID 时必然漏掉一处
//   而 InstallLocation 指向的就是应用目录，用"它指的是不是我"来判断归属，是自证的
//
// InstallKind.cs — decides whether this copy came from the portable archive or from the installer
//
// Why the distinction is needed: the two installation routes update differently
//   A portable copy has no registry entry, so the helper script replaces files one by one (as it does today)
//   An installed copy has a registry entry, so the installer can run silently and let Inno update the files,
//   the registry and the uninstall log together
// Getting it wrong is not symmetric
//   Treating an installed copy as portable leaves the registry version stale, which the user sees as
//   "it updated but still shows the old version"
//   Treating a portable copy as installed installs a second copy into the default directory and leaves the
//   original in place, giving the user two applications
// The decision therefore errs on the cautious side: anything unrecognised counts as portable
//
// Why "is InstallLocation our own directory" rather than comparing the AppId
//   The installer's AppId is a GUID, and copying it here would be a second piece of installer knowledge that
//   a rename or a new GUID would leave stale
//   InstallLocation points at the application directory, so asking "does it point at me" is self-proving

namespace MIDITap.Core.Update;

/// <summary>这份应用的安装方式 / How this copy of the app was installed</summary>
public enum InstallKind
{
    /// <summary>便携包解压出来的，没有注册表项 / Unpacked from the portable archive, with no registry entry</summary>
    Portable,

    /// <summary>安装器装上的，注册表里有一条指向本目录的卸载项 / Installed, with an uninstall entry pointing at this directory</summary>
    Installed,
}

/// <summary>由注册表里的 InstallLocation 与自身目录判定安装方式 / Decides the install kind from the registry's InstallLocation and our own directory</summary>
public static class InstallKindDetector
{
    /// <summary>
    /// 判定安装方式
    /// <paramref name="uninstallInstallLocation"/> 为 null 或空（没有注册表项、读不到、值缺失）一律按便携包处理
    /// 两侧都先规范化为完整路径再比较，因为注册表里的值带尾部分隔符（实测 ...\\MIDITap\\），而自身目录不带
    ///
    /// Decides the install kind
    /// A null or empty <paramref name="uninstallInstallLocation"/> (no entry, unreadable, or a missing value)
    /// counts as portable
    /// Both sides are normalised to full paths before comparing, because the registry value carries a trailing
    /// separator while the app directory does not
    /// </summary>
    public static InstallKind Decide(string? appDir, string? uninstallInstallLocation)
    {
        var ours = Normalize(appDir);
        var theirs = Normalize(uninstallInstallLocation);

        // 认不出来就是便携包：这是安全的一侧，见文件头
        //
        // Anything unrecognised is portable, which is the cautious side; see the file header
        if (ours is null || theirs is null)
        {
            return InstallKind.Portable;
        }

        // Windows 路径大小写不敏感，因此忽略大小写
        //
        // Windows paths are case-insensitive, so the comparison ignores case
        return string.Equals(ours, theirs, StringComparison.OrdinalIgnoreCase)
            ? InstallKind.Installed
            : InstallKind.Portable;
    }

    /// <summary>
    /// 规范化一个路径：去空白、转成完整路径、裁掉尾部分隔符
    /// 拿不到可用路径时返回 null
    ///
    /// Normalises a path: trims whitespace, makes it a full path, and drops any trailing separator
    /// Returns null when no usable path is available
    /// </summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var text = path.Trim();
        try
        {
            text = Path.GetFullPath(text);
        }
        catch
        {
            // 路径非法（含非法字符、过长等）：用原字符串继续比较
            // 绝不因为一条注册表值就让更新流程抛异常
            //
            // An invalid path (illegal characters, too long, and so on) is compared as it stands
            // A registry value must never make the update flow throw
        }

        // 驱动器根（例如 C:\\ 三个字符）裁掉分隔符会变成 C:，含义不同，因此只在长于它时才裁
        //
        // Trimming a drive root (three characters, such as C:\\ ) would leave C:, which means something else,
        // so the trim only happens when the path is longer than that
        return text.Length > 3
            ? text.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : text;
    }
}
