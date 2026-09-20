// InstallKindProbe.cs — 读注册表判断这份应用是安装器装的还是便携包解压的
//
// 判定规则本身在 Core（InstallKindDetector），这里只负责"把注册表里的值读出来"
// 分工的理由：规则可以脱离 Windows 与注册表测试，而读注册表做不到
//
// 为什么只读 HKCU
//   安装器用 PrivilegesRequired=lowest，因此它写的卸载项在 HKCU 下，不需要提权就能读
//   HKLM 下那份属于"为所有用户安装"，本应用不用那种方式，因此不去看它
//
// 为什么找不到就是便携包
//   便携包没有卸载项，这是正常情况
//   而"本该找到却没找到"（注册表被清理工具删了）按便携包处理也是安全的一侧：
//   退回去用辅助脚本替换文件，最坏结果是注册表版本号不更新，而不是另装一份出来
//
// InstallKindProbe.cs — reads the registry to tell an installer install from a portable unpack
//
// The rule itself lives in Core (InstallKindDetector); this file only reads the registry value
// Why the split: the rule can be tested without Windows or a registry, which the reading cannot
//
// Why HKCU alone
//   The installer uses PrivilegesRequired=lowest, so its uninstall entry lives under HKCU and needs no elevation to read
//   An HKLM entry means "installed for all users", which this app does not do, so it is not consulted
//
// Why "not found" means portable
//   A portable copy has no uninstall entry, which is normal
//   And when an entry should exist but does not (a registry cleaner removed it), treating it as portable is also
//   the cautious side: the app falls back to replacing files with the helper script, and the worst outcome is a
//   stale version in the registry rather than a second copy being installed

using MIDITap.Core.Update;
using Microsoft.Win32;

namespace MIDITap.App.Services;

public static class InstallKindProbe
{
    /// <summary>卸载项的注册表位置（Inno 把它的项写在这里）/ Where the uninstall entries live (Inno writes its entry here)</summary>
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// 探测安装方式：在 HKCU 的卸载项里找 InstallLocation 指向本目录的那一条
    /// 找不到、读不到、或任何一项异常都返回便携包
    ///
    /// Detects the install kind: looks through the HKCU uninstall entries for one whose InstallLocation points at
    /// this directory
    /// Not found, unreadable, or any error at all returns portable
    /// </summary>
    public static InstallKind Detect(string appDir)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            if (root is null)
            {
                return InstallKind.Portable;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(name);
                // InstallLocation 缺失时是 null，交给判定函数按"认不出来"处理
                //
                // A missing InstallLocation yields null, which the rule treats as unrecognised
                var location = entry?.GetValue("InstallLocation") as string;
                if (InstallKindDetector.Decide(appDir, location) == InstallKind.Installed)
                {
                    return InstallKind.Installed;
                }
            }
        }
        catch
        {
            // 注册表读不到（权限、损坏）时按便携包处理：见文件头，那是安全的一侧
            //
            // An unreadable registry (permissions, corruption) counts as portable: see the file header for why
            // that is the cautious side
        }

        return InstallKind.Portable;
    }
}
