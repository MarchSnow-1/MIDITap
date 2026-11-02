// BridgeLayout.cs — .bridge/ 目录内部的布局：约定名之外如何回退找那个 exe
//
// 回退要有条件而不是硬猜：上游 CI 把可执行文件命名为
//   MIDIBridge-Client-<tag>-windows-amd64.exe
// 安装流程把它统一改名为 AppPaths.BridgeExecutableFileName，并把这个名字记进 .storage
// 但用户也可能手动解压那个 zip 放进目录，此时文件带着版本号
// 只认约定名的话，这种安装会被判为"未安装"，界面连入口都不给
//
// 查找顺序因此是：记录名、约定名、目录内唯一 *.exe、按序第一个
// 记录名排在第一位：安装时落地的名字是确定的事实，运行时就该照它启动，目录里被另外放进 exe 也不会改变启动目标
// 最后一条兜底到"按序第一个"而不是报歧义：目录里多一个 exe 不该让整个功能不可用，
// 而且第一次定位成功后这个名字会被记下来，此后走的都是第一条
//
// 目录名与目录路径来自 Settings.AppPaths，本类只负责目录内部；依赖方向是 Bridge -> Settings
//
// Why the fallback is conditional rather than a guess: the upstream CI names the executable
//   MIDIBridge-Client-<tag>-windows-amd64.exe
// Installation renames it to AppPaths.BridgeExecutableFileName and records that name in .storage
// A user may also unpack that zip into the directory by hand, leaving the version number in the name
// Matching only the conventional name would report that install as not installed and hide the entry point
//
// The lookup order is therefore: the recorded name, the conventional name, the single *.exe in the directory, then the
// first in ordinal order
// The recorded name comes first: the name landed at install time is a known fact, so starting uses it, and another
// exe dropped into the directory does not change the start target
// The last step falls back to "the first in ordinal order" rather than reporting ambiguity: one extra exe should not
// disable the whole feature, and a successful lookup records the name, so later lookups take the first step
//
// The directory name and path come from Settings.AppPaths; this class owns only what is inside it, and the dependency
// direction is Bridge -> Settings

using MIDITap.Core.Settings;

namespace MIDITap.Core.Bridge;

/// <summary>
/// 在 .bridge/ 里定位可执行文件的结果属于哪一类
/// 只分两类：找到了，和目录里没有任何 *.exe（或目录不存在）
///
/// Which of the two outcomes locating the executable produced
/// Only two: a usable path, or no *.exe at all (including a missing directory)
/// </summary>
public enum BridgeExeStatus
{
    /// <summary>拿到了可用路径 / A usable path was found</summary>
    Found,

    /// <summary>目录不存在，或里面没有任何 *.exe / The directory is absent, or holds no *.exe at all</summary>
    Missing,
}

/// <summary>
/// 一次定位的结果：Path 只在 Found 时非空
///
/// One lookup result: Path is non-null only for Found
/// </summary>
/// <param name="Status">结果类别 / Which outcome class this is</param>
public sealed record BridgeExeLookup(
    BridgeExeStatus Status,
    string? Path = null);

public static class BridgeLayout
{
    /// <summary>
    /// 上游程序自建的配置目录名（在它自己的目录里）
    /// 上游 main.go 把配置锚定到可执行文件所在目录下的 data/config.json
    /// 用户需要 TLS 或改重连参数时编辑的是那个文件，MIDITap 不写它
    ///
    /// The directory the upstream program creates for its own config, inside its own directory
    /// Upstream main.go anchors its config to data/config.json next to the executable
    /// The user edits that file for TLS or reconnect settings, and MIDITap does not write it
    /// </summary>
    public const string UpstreamConfigDirectoryName = "data";

    public const string UpstreamConfigFileName = "config.json";

    /// <summary>
    /// 上游自建配置文件的完整路径，供界面把位置指给用户
    /// 目录里可能还没有这个文件：它由上游程序首次运行时生成
    ///
    /// Full path of the upstream program's own config file, so the UI can point the user at it
    /// The file may not exist yet: the upstream program writes it on its first run
    /// </summary>
    public static string ConfigPath(string baseDir)
        => Path.Combine(AppPaths.BridgeDir(baseDir), UpstreamConfigDirectoryName, UpstreamConfigFileName);

    /// <summary>
    /// 定位 .bridge/ 里要执行的那个 exe
    /// 顺序固定：记录名、约定名、目录内唯一 *.exe、按序第一个
    /// 读目录失败（权限、路径非法）时返回 Missing：界面据此不显示远程入口，属于更安全的一侧
    ///
    /// Locates the executable to run inside .bridge/
    /// Fixed order: the recorded name, the conventional name, the single *.exe, then the first in ordinal order
    /// A failed directory read (permissions, malformed path) returns Missing, so the UI hides the remote entry
    /// That is the safer side
    /// </summary>
    /// <param name="recordedName">安装时记录的文件名，可为空 / The file name recorded at install time, may be empty</param>
    public static BridgeExeLookup ExePath(string baseDir, string? recordedName = null)
    {
        var directory = AppPaths.BridgeDir(baseDir);

        try
        {
            if (!Directory.Exists(directory))
            {
                return new BridgeExeLookup(BridgeExeStatus.Missing);
            }

            // 记录名优先：安装流程记下的就是它自己落地的那个文件，
            // 目录里后来被放进别的 exe 也不改变启动目标
            //
            // The recorded name wins: installation records the file it landed itself, so another exe dropped into the
            // directory later does not change the start target
            if (!string.IsNullOrWhiteSpace(recordedName))
            {
                var recorded = Path.Combine(directory, Path.GetFileName(recordedName));
                if (File.Exists(recorded))
                {
                    return new BridgeExeLookup(BridgeExeStatus.Found, recorded);
                }
            }

            // 约定名其次：没有记录时（手动解压、旧版本安装）按安装流程的命名约定找
            //
            // The conventional name comes second: with no record (a hand-unpacked or older install) the naming
            // convention of the install flow is tried
            var conventional = Path.Combine(directory, AppPaths.BridgeExecutableFileName);
            if (File.Exists(conventional))
            {
                return new BridgeExeLookup(BridgeExeStatus.Found, conventional);
            }

            // 不用 *.exe 搜索模式，而是列出全部文件再按扩展名筛
            // Windows 的搜索模式走 8.3 短名匹配，"*.exe" 可能命中扩展名以 exe 开头的其它文件
            // 这里要的是"以 .exe 结尾"这个确定语义，因此自己比较
            //
            // The *.exe search pattern is not used; every file is listed and filtered by extension instead
            // Windows matches that pattern against 8.3 short names, so *.exe can hit files whose extension merely starts with "exe"
            // The intent here is "ends with .exe", which the comparison states exactly
            var candidates = Directory.EnumerateFiles(directory)
                .Where(file => string.Equals(Path.GetExtension(file), ".exe", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 多个候选时取按序第一个：枚举顺序本身不稳定，排序让每次启动挑到同一个文件
            // 调用方在定位成功后把这个名字记下来，此后不再走到这一步
            //
            // With several candidates the first in ordinal order is taken: enumeration order is not stable, and
            // sorting makes every start pick the same file
            // The caller records the name once the lookup succeeds, so this step is not reached again
            return candidates.Count == 0
                ? new BridgeExeLookup(BridgeExeStatus.Missing)
                : new BridgeExeLookup(BridgeExeStatus.Found, candidates[0]);
        }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // ArgumentException 覆盖路径含非法字符的情况：那来自 baseDir 本身异常，判为未安装即可
            //
            // ArgumentException covers a malformed path, which comes from baseDir being unusable
            // Reporting "not installed" is the useful outcome there
            Console.Error.WriteLine("[miditap.bridge]: Bridge layout lookup failed: " + err.Message);
            return new BridgeExeLookup(BridgeExeStatus.Missing);
        }
    }

    /// <summary>
    /// 是否已安装（可以启动上游程序）
    ///
    /// Whether the upstream program is installed and can be started
    /// </summary>
    public static bool IsInstalled(string baseDir)
        => ExePath(baseDir).Status == BridgeExeStatus.Found;
}
