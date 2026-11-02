// AppPaths.cs — 解析"exe 所在目录"（便携约定的基准目录），并**拥有便携布局本身**
//
// 为什么基准目录值得单独一个函数并写测试：
//   AppContext.BaseDirectory 在**单文件发布**（PublishSingleFile）时指向解压目录（%TEMP%\.net\<app>\<hash>\），而不是 exe 所在目录
//   若直接拿它当基准，用户的 config/ 与 .storage/ 会被写进临时目录
//   清理工具随时可能删掉 —— 配置"莫名消失"，便携性失效
//   因为该失效是**静默的**（界面一切正常，只是数据在错的地方），这里把逻辑做成纯函数并用测试锁住，而不是散落在各处靠注释提醒
//
// 布局为什么也放在这里：config/ 与 .storage/ 的改名或挪动只要漏掉一处，就会把数据写到别的地方，而且不报错
// 因此名字与由名字派生的路径都收在这里，其余代码只调用，不再自己拼
//
// 注意 scripts/apply-update.ps1 里那份同名排除**是刻意的第二份**
// 辅助脚本是独立进程，不共享任何代码（见 AGENTS.md 3.3）
// 那份不要在上面这条原则下被"顺手合并"
//
// AppPaths.cs — resolves the directory containing the executable (the base of the portable convention)
// It also **owns the portable layout itself**
//
// Why the base directory is a standalone, tested function: under single-file publishing
// AppContext.BaseDirectory points at the EXTRACTION directory, not the exe's own directory
// Using it as the base would put the user's config/ and .storage/ into a temp folder
// Cleanup tools may delete that folder at any time
// Configs would then vanish and portability would be lost
// The failure is SILENT: the UI looks fine, and the data is just in the wrong place
// The logic therefore lives in one pure function locked down by tests
// It is not spread around with a comment asking people to remember
//
// Why the layout lives here too: renaming or moving config/ or .storage/ while missing a single site writes data somewhere else, and nothing is reported
// The names and every path derived from them therefore live here
// The rest of the code calls in rather than building its own
//
// Note the identically named exclusions inside scripts/apply-update.ps1 are a **deliberate second copy**
// The helper is a separate process and shares no code (see AGENTS.md 3.3)
// That one must not be "merged away" under the rule above

namespace MIDITap.Core.Settings;

public static class AppPaths
{
    // ------------------------------------------------------------------ 便携布局 / Portable layout

    /// <summary>映射配置目录名（exe 旁边） / Mapping-config directory name, next to the exe</summary>
    public const string ConfigDirectoryName = "config";

    /// <summary>应用设置目录名（exe 旁边，以点开头表示"非程序文件"）
    /// App-settings directory name next to the exe; the leading dot marks it as not a program file</summary>
    public const string StorageDirectoryName = ".storage";


    /// <summary>.storage 下存放日志文件的子目录名 / Sub-directory of .storage holding log files</summary>
    public const string LogDirectoryName = "logs";


    /// <summary>.storage 下存放导出日志的子目录名 / Sub-directory of .storage holding exported logs</summary>
    public const string ExportsDirectoryName = "exports";

    /// <summary>
    /// 更新流程的工作目录名（exe 旁，放待安装的包与解压出的新文件）
    /// 它不是用户数据，因此不在 UserDataDirectoryNames 里
    /// 打包产出的归档里也不含它
    ///
    /// Directory name of the update work area next to the exe
    /// It holds the downloaded package and the extracted files
    /// It is not user data and so is not in UserDataDirectoryNames
    /// The packaged archive does not contain it either
    /// </summary>
    public const string UpdateDirectoryName = ".update";

    /// <summary>
    /// MIDIBridge 扩展功能的上游程序目录名（exe 旁，放下载来的 midibridge-client.exe 与它自建的 data/）
    /// 它是"程序文件（上游二进制）与上游自建数据"的混合目录，因此两边都不沾：
    /// **不放进 UserDataDirectoryNames** —— 那份清单的含义是「属于用户 authored 的东西」，
    /// 删掉 config/ 与 .storage/ 属于数据丢失，而这个目录不是用户写的，两者不能混为一谈
    /// **也不进安装载荷** —— 所以卸载时按载荷删除的动作不会涉及它；要清掉它得用户手动删目录
    /// 它仍然需要被两条链路跳过，理由各不相同：
    ///   * 更新不得覆盖它：由 UpdateStaging.IsExcluded 单独返回 true（理由见那里）
    ///   * 打包发布必须排除它：随 MIDITap 的包发一个第三方 exe 属于二次分发，而且它由用户按需下载
    /// 密码不在这里，在 .storage 下（见 AppStorage.BridgePasswordStorageKey）
    ///
    /// Directory name of the MIDIBridge extension's upstream program, next to the exe
    /// It holds the downloaded midibridge-client.exe and the data/ folder that program creates itself
    /// It is a MIX of program files (the upstream binary) and the upstream's own data, so it joins neither side:
    /// it is deliberately NOT in UserDataDirectoryNames, because that list means "content the user authored",
    /// deleting config/ or .storage/ would be data loss, while this directory is not user-written, and the two must
    /// not be merged into one list
    /// It is not in the install payload either, so the uninstall actions MIDITap ships do not touch it; removing it
    /// takes a manual delete of the directory
    /// Two separate paths still have to skip it, for different reasons:
    ///   * An update must not overwrite it: UpdateStaging.IsExcluded returns true for it on its own account (see there)
    ///   * Packaging must exclude it: shipping a third-party exe inside MIDITap's package is redistribution, and the
    ///     user downloads it on demand
    /// The password is not here; it sits under .storage (see AppStorage.BridgePasswordStorageKey)
    ///
    /// 目录**内部**的布局（里面那个 exe 叫什么、如何回退找唯一 exe）归 Bridge/BridgeLayout.cs
    /// 方向是 Bridge → Settings：本类只给目录名与目录路径，不引用 Bridge 下的任何东西
    ///
    /// The layout INSIDE the directory (the exe's file name, the fallback to a single *.exe) belongs to
    /// Bridge/BridgeLayout.cs
    /// The direction is Bridge → Settings: this class supplies the directory name and path only, and refers to
    /// nothing under Bridge
    /// </summary>
    public const string BridgeDirectoryName = ".bridge";

    /// <summary>
    /// 上游程序在本目录里约定的可执行文件名
    /// CI 产出的资产里它可能带版本目录前缀，安装时统一改成这个名字（见 BridgeInstaller）
    /// 名字收在这里，是为了让"程序装在哪、叫什么"只有一处定义
    ///
    /// The executable name this directory is expected to hold
    /// The release asset may wrap it in a version folder, and installation renames it to this name (see BridgeInstaller)
    /// The name lives here so "where the program sits and what it is called" is defined in one place
    /// </summary>
    public const string BridgeExecutableFileName = "midibridge-client.exe";

    /// <summary>
    /// 属于**用户**的顶层目录：打包、更新、卸载都必须跳过（AGENTS.md 2.4）
    /// 更新覆盖用户数据是数据丢失，而不是更新
    /// 因此这份清单只有一处，由需要它的地方引用
    ///
    /// Top-level directories owned by the **user**
    /// Packaging, updating and uninstalling must all skip them (AGENTS.md 2.4)
    /// Overwriting user data is data loss rather than an update
    /// So this list exists in exactly one place and whoever needs it refers to it
    /// </summary>
    public static readonly IReadOnlyList<string> UserDataDirectoryNames =
        [ConfigDirectoryName, StorageDirectoryName];

    /// <summary>映射配置目录的完整路径 / Full path of the mapping-config directory</summary>
    public static string ConfigDir(string baseDir) => Path.Combine(baseDir, ConfigDirectoryName);

    /// <summary>应用设置目录的完整路径 / Full path of the app-settings directory</summary>
    public static string StorageDir(string baseDir) => Path.Combine(baseDir, StorageDirectoryName);

    /// <summary>.storage 下某个条目的完整路径（设置值、last_config 等）
    /// Full path of one entry inside .storage (a setting value, last_config, and so on)</summary>
    public static string StorageFile(string baseDir, string fileName)
        => Path.Combine(StorageDir(baseDir), fileName);


    /// <summary>日志目录的完整路径 / Full path of the log directory</summary>
    public static string LogDir(string baseDir) => Path.Combine(StorageDir(baseDir), LogDirectoryName);

    // 日志**文件名**不在这里：它带日期与当天第几次启动的编号，由 LogFileNames 负责
    // 本类只管目录布局，而目录是固定的
    //
    // The log FILE NAME is not here: it carries a date and a per-day launch number, so LogFileNames owns it
    // This class owns the directory layout alone, and the directory is fixed

    /// <summary>导出日志的兜底目录完整路径 / Full path of the fallback export directory</summary>
    public static string ExportsDir(string baseDir) => Path.Combine(StorageDir(baseDir), ExportsDirectoryName);

    /// <summary>更新工作目录的完整路径 / Full path of the update work directory</summary>
    public static string UpdateDir(string baseDir) => Path.Combine(baseDir, UpdateDirectoryName);

    /// <summary>MIDIBridge 扩展程序目录的完整路径 / Full path of the MIDIBridge extension directory</summary>
    public static string BridgeDir(string baseDir) => Path.Combine(baseDir, BridgeDirectoryName);

    // ------------------------------------------------------------------ 基准目录 / Base directory

    /// <summary>
    /// 由真实 exe 路径推导基准目录
    /// <paramref name="processPath"/> 为 null/空（拿不到时）则回退到 <paramref name="fallbackBaseDirectory"/>
    ///
    /// Derives the base directory from the real executable path
    /// It falls back to the supplied directory when the process path is unavailable
    /// </summary>
    public static string ResolveBaseDir(string? processPath, string fallbackBaseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            try
            {
                var directory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }
            catch (ArgumentException)
            {
                // 路径含非法字符：交给回退值，不要因为解析基准目录而让应用起不来
                //
                // Malformed path: fall back rather than failing to start over a base directory
            }
        }
        return fallbackBaseDirectory;
    }
}
