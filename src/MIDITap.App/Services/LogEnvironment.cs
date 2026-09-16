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
        => $"v{AppServices.AppVersion} | {RuntimeInformation.OSDescription} | " +
           $"{RuntimeInformation.ProcessArchitecture} | lang={AppServices.I18n.Current} | " +
           $"theme={ThemeService.Mode}/{ThemeService.EffectiveTheme} | " +
           $"highContrast={ThemeService.IsHighContrast} | logToFile={LogPersistence.Enabled}";

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
