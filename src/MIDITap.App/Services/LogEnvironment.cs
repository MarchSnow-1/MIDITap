// LogEnvironment.cs — 导出日志时附上的环境摘要
//
// 为什么必须有它：日志回答"发生了什么"，而环境回答"在什么条件下发生的"
// 同一份日志在 1.7.1 与 1.8.0、中文与英文、浅色与深色、对比度开与关之下会有完全不同的解释
// 而这些信息在导出的文本里一条都没有
// 要求用户在反馈里手抄，抄漏的往往正是那一项
//
// 刻意**不收集**的内容：用户名、计算机名、设备序列号、配置文件内容
// 导出是用户主动发给他人的文件，因此只放排查必需且不涉及身份的信息
//
// 安装路径（baseDir）属于必要项 —— 它决定 config/ 与 .storage/ 在哪
// 它也是 Portable 与安装版之间最容易混淆的一点 —— 因此保留**结构**
// 但其中的用户名经 PathRedaction 换成 <user>
// Windows 上路径经常就是身份，只声明"它只是路径"并不成立
// 保留的部分（用户目录下的层级结构）才是排查真正要看的
//
// LogEnvironment.cs — the environment summary attached to an exported log
//
// Why it has to exist: the log answers "what happened", and the environment answers "under what conditions it happened"
// The same log reads completely differently on 1.7.1 and 1.8.0, in Chinese and in English
// It differs again in light and dark, and with contrast mode on and off
// None of that appears in the exported text
// Asking users to copy it into a report by hand means the one item that gets left out is often exactly that one
//
// Deliberately **not collected**: user name, computer name, device serial numbers, config file contents
// An export is a file the user sends to somebody else, so it carries only what triage needs and nothing tied to identity
//
// The install path (baseDir) is a required item
// It decides where config/ and .storage/ live
// It is also the easiest thing to confuse between the portable and installed layouts
// So its **structure** is kept, while the user name inside it is replaced by PathRedaction with <user>
// On Windows a path often is the identity, and claiming "it is only a path" does not hold
// What survives (the folder structure under the user directory) is what triage actually needs to see

using System.Runtime.InteropServices;
using System.Text;
using MIDITap.Core.Settings;

namespace MIDITap.App.Services;

public static class LogEnvironment
{
    /// <summary>
    /// 单行版本，用于启动时那条 debug 日志
    /// 日志行按行阅读，多行摘要会把一条日志撑成十几行，反而挤掉其它条目
    /// 需要细看时导出里的 environment.txt 仍是完整的 key=value 形式
    ///
    /// The single-line form, used for the startup debug entry
    /// Logs are read line by line, and a multi-line summary would stretch one entry into a dozen and squeeze out others
    /// The exported environment.txt still carries the full key=value form when detail is needed
    /// </summary>
    public static string Summary()
        => $"Version: v{AppServices.AppVersion} | {RuntimeInformation.OSDescription} | " +
           $"{RuntimeInformation.ProcessArchitecture} | lang={AppServices.I18n.Current} | " +
           $"theme={ThemeService.Mode}/{ThemeService.EffectiveTheme} | " +
           $"highContrast={ThemeService.IsHighContrast} | logToFile={LogPersistence.Enabled} | " +
           $"memAvailable={AvailableMemoryText()} | memLimit={ProcessLimitText()} | " +
           $"diskFree={FreeSpaceText()}";

    /// <summary>可用的物理内存，取不到时返回 unknown
    /// 内存不足是按键注入偶发丢失这类反馈的一个常见解释，它与版本和系统都无关，因此单独占一项
    ///
    /// The physical memory available, or unknown when it cannot be read
    /// Low memory often explains reports of keys occasionally not being injected
    /// It is independent of the version and the OS, so it gets an entry of its own
    /// </summary>
    private static string AvailableMemoryText()
        => NativeMemory.TryAvailableBytes(out var bytes) ? Format(bytes) : Unknown;

    /// <summary>本进程可用的内存上限，取不到时返回 unknown
    /// 与上一项合起来看才完整：可用量说机器还剩多少，上限说这个进程最多能用多少
    /// 两者接近时说明受限的是系统，上限远小于可用量则说明进程被配额卡住
    ///
    /// The memory limit available to this process, or unknown when it cannot be read
    /// It pairs with the entry above: the available figure says what is left on the machine, and the limit says what this process may use at most
    /// When the two are close the machine is the constraint, while a limit far below the available figure means a quota is capping the process
    /// </summary>
    private static string ProcessLimitText()
        => NativeMemory.TryProcessLimitBytes(out var bytes) ? Format(bytes) : Unknown;

    /// <summary>应用所在分区的可用空间，取不到时返回 unknown
    /// 自更新要先下载再解压，两者都要空间，因此分区满了是更新失败的一个直接原因
    /// 只报可用量：盘符会暴露分区布局，总量则是与排查无关的机器信息
    /// 而排查需要的只是"还够不够下载并解压"，一个可用数字就够
    ///
    /// The free space on the volume the app lives on, or unknown when it cannot be read
    /// A self-update downloads and then extracts, both of which need room
    /// A full volume is therefore a direct cause of update failures
    ///
    /// Only the **available amount** is reported: a drive letter exposes the partition layout, and the total size is machine detail triage does not need
    /// What triage needs is whether there is room to download and extract, and one available figure answers that
    /// </summary>
    private static string FreeSpaceText()
    {
        try
        {
            // baseDir 就是应用目录，它所在的驱动器即更新下载与解压的落点
            // 这个位置只用来查询，不写进输出
            //
            // baseDir is the application directory, so its drive is where an update downloads and extracts
            // The location is used for the query only and never written to the output
            var root = Path.GetPathRoot(Path.GetFullPath(AppServices.BaseDir));
            if (string.IsNullOrEmpty(root))
            {
                return Unknown;
            }
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return Unknown;
            }
            return Format(drive.AvailableFreeSpace);
        }
        catch (Exception)
        {
            // 驱动器瞬时不就绪（可移动盘被拔出）不该让整条摘要失败，记 unknown 后继续
            //
            // A transient drive state (a removable disk pulled out) must not fail the whole summary, so unknown is recorded and the rest carries on
            return Unknown;
        }
    }

    private const string Unknown = "unknown";

    private const double GiB = 1024d * 1024d * 1024d;

    private const double MiB = 1024d * 1024d;

    /// <summary>字节数转成带单位的文本，GiB 以上用 GiB，否则用 MiB，保留一位小数</summary>
    /// <remarks>Bytes as text with a unit: GiB above a gibibyte, otherwise MiB, one decimal place</remarks>
    private static string Format(long bytes)
    {
        return bytes >= GiB ? $"{bytes / GiB:0.0}GiB" : $"{bytes / MiB:0.0}MiB";
    }

    /// <summary>
    /// 生成 key=value 形式的多行摘要
    /// 用 key=value 而不是散文：接收方（人）一眼能找到某一项，将来若要被脚本解析也不需要改格式
    ///
    /// Builds a multi-line key=value summary
    /// Key=value rather than prose: a human recipient can find one field at a glance
    /// A future script then needs no format change to parse it
    /// </summary>
    public static string Compose()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"app={AppServices.AppVersion}");
        builder.AppendLine($"exported={DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"os={RuntimeInformation.OSDescription}");
        builder.AppendLine($"runtime={RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"arch={RuntimeInformation.ProcessArchitecture}");
        // baseDir 里的用户名要换掉：便携用法下它几乎总是 C:\\Users\\<用户名>\\...，而导出包是用户发给别人的文件
        // 路径结构保留，排查仍然可用
        //
        // The user name inside baseDir is replaced
        // In the portable layout it is almost always C:\\Users\\<name>\\...
        // An export is a file the user sends to somebody else
        // The path structure survives, so triage still works
        builder.AppendLine($"baseDir={PathRedaction.Redact(AppServices.BaseDir, Environment.UserName)}");
        builder.AppendLine($"lang={AppServices.I18n.Current}");
        builder.AppendLine($"themeMode={ThemeService.Mode}");
        builder.AppendLine($"effectiveTheme={ThemeService.EffectiveTheme}");
        builder.AppendLine($"highContrast={ThemeService.IsHighContrast}");
        builder.AppendLine($"logToFile={LogPersistence.Enabled}");
        builder.AppendLine($"monitoring={AppServices.Backend.IsRunning}");
        builder.AppendLine($"logEntries={AppServices.Log.Entries.Count}");
        return builder.ToString();
    }
}
