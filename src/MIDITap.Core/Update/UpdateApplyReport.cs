// UpdateApplyReport.cs — apply-update.ps1 与主程序之间"这次更新结果如何"的传递约定
//
// 为什么需要一条约定：辅助脚本是**独立进程**，它在主程序退出之后才动手
// 更新失败时它会负责把应用重新拉起来
// 而那一刻是**唯一**能告诉用户"失败了、已回滚"的机会
// 错过它，用户只会看到应用莫名其妙地重开、版本还没变，甚至不知道刚刚发生过一次更新
//
// 为什么传一个 id（退出码）而不是一句现成的文案：文案要本地化，而脚本是 PowerShell、没有 i18n
// 传 id、由应用查表给话，语言切换与措辞就都留在应用侧
//
// 为什么放在 Core：解析是**纯粹的字符串规则**，而且必须容忍畸形输入
// 一个坏掉的启动参数不该让应用起不来，因此值得脱离界面单独测
//
// UpdateApplyReport.cs — how apply-update.ps1 reports an update's outcome to the main app
//
// Why a convention is needed: the helper is a **separate process** that acts only after the main app has exited
// On failure it relaunches the app
// That moment is the **only** chance to tell the user the update failed and was rolled back
// Miss it and they just see the app reopen with the same version, unaware an update was attempted
//
// Why an id (the exit code) rather than ready-made copy: the message must be localised
// The helper is PowerShell with no i18n, so passing an id keeps the wording on the app side
// Letting the app look the id up also keeps the language switch on the app side
//
// Why Core: parsing is a **pure string rule** that must also tolerate malformed input
// A broken launch argument must not stop the app from starting, so it deserves a test of its own

using System.Globalization;

namespace MIDITap.Core.Update;

public static class UpdateApplyReport
{
    /// <summary>失败重启时传给应用的参数前缀，形如 <c>--update-failed=5</c>
    /// The argument prefix passed on a failure relaunch, e.g. <c>--update-failed=5</c></summary>
    public const string ArgumentPrefix = "--update-failed=";

    // 以下是 apply-update.ps1 的退出码中"值得告诉用户"的几个
    // 脚本是独立进程，无法引用这些常量，因此两边的数字必须人工同步
    // 脚本头部也列了同一张表，改动时请一并更新
    //
    // These are the script exit codes worth telling the user about
    // The script is a separate process and cannot reference these constants
    // The numbers must therefore be kept in step by hand
    // The script header carries the same table

    /// <summary>主程序未按时退出，脚本什么都没做（此时不会重启应用，因此实际不会传到）
    /// The main app did not exit in time and nothing was done (it is not relaunched, so this id is not delivered)</summary>
    public const int NotExited = 3;

    /// <summary>暂存目录缺失，未做任何改动 / The staging directory was missing; nothing was changed</summary>
    public const int StagingMissing = 4;

    /// <summary>覆盖失败，已回滚到更新前的版本 / Overwriting failed and the previous version was restored</summary>
    public const int RolledBack = 5;

    /// <summary>更新过程中出现意外异常，未完成 / An unexpected exception; the update did not finish</summary>
    public const int UnexpectedError = 6;

    /// <summary>
    /// 退出码 -> 提示文案的 i18n 键
    ///
    /// 放在这里而不是界面的 switch 里：退出码是本类定义的一套约定
    /// "哪个码该说哪句话"是同一套约定的另一端
    /// 分开写就会出现两处各自维护的映射，改一处忘一处
    ///
    /// 认不出的码走通用的那条（它带 {code} 占位符），而不是把原始数字当文案
    /// 数字对用户没有意义，但保留在句子里能让维护者对得上脚本日志
    ///
    /// Exit code to the i18n key of the message to show
    ///
    /// It lives here rather than in a switch in the UI: the exit codes are the convention this class defines
    /// "Which code means which sentence" is the other end of that same convention
    /// Split apart, there would be two mappings to keep in step and one of them would eventually be missed
    ///
    /// An unrecognised code uses the generic key (which carries {code}) rather than the raw number
    /// The number means nothing to a user
    /// Keeping it in the sentence lets a maintainer line it up with the script log
    /// </summary>
    public static string MessageKey(int exitCode) => exitCode switch
    {
        StagingMissing => "update.apply.staging",
        RolledBack => "update.apply.rolledBack",
        UnexpectedError => "update.apply.error",
        _ => "update.apply.other",
    };

    /// <summary>把退出码格式化成一个启动参数（脚本侧用；这里提供是为了让格式只有一处定义）
    /// Formats an exit code into the launch argument (the script side uses this format; defined once here)</summary>
    public static string Format(int exitCode)
        => ArgumentPrefix + exitCode.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 从命令行参数里取出更新失败的 id；没有传、或传了畸形值，都返回 null
    ///
    /// 只接受**纯数字**：值由脚本写入，出现符号、空白或小数点都说明它不是一个退出码
    /// 畸形时返回 null（当作没有传）而不是抛异常
    /// 应用要能照常启动，毕竟它刚刚才从一次失败的更新里恢复过来
    ///
    /// Reads the update-failure id from the command line, returning null when it is absent or malformed
    ///
    /// Only plain digits are accepted: the value is written by the script
    /// A sign, whitespace or a decimal point therefore means it is not an exit code
    /// Malformed input yields null rather than throwing, because the app must still start
    /// The app has just recovered from a failed update, so it must be able to start anyway
    /// </summary>
    public static int? Parse(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (!argument.StartsWith(ArgumentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = argument[ArgumentPrefix.Length..];
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var exitCode)
                ? exitCode
                : null;
        }
        return null;
    }
}
