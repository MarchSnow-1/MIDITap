// PathRedaction.cs — 把路径里的用户名换掉，用于要发给别人的文件
//
// 为什么必须有：导出的日志包是用户主动发给**别人**的（issue 附件、论坛贴子）
// 里面的 baseDir 是程序所在目录
// 而便携用法下这个路径几乎总是以 C:\\Users\\<用户名>\\ 开头
// 于是每份导出都自带机主的登录名
// 这不是敏感数据（不是密码），但它是身份，而用户并不知道自己发出了它
//
// 做法是**只替换用户名的字面出现**，保留路径其余部分
// 排查时真正有用的信息是"程序放在用户目录下的哪一层"（层级结构本身），不是用户名
// 路径结构保留后，支持者仍能判断便携版与安装版的差别
//
// 刻意**不做**的事：不处理其它位置（系统临时目录、程序安装目录等），不做正则模糊匹配
// 只处理调用方明确交给它的那一个字符串
// 脱敏范围越大，越容易在别处悄悄改坏一个本来就该保留的路径
//
// PathRedaction.cs — replaces the user name inside a path, for files handed to somebody else
//
// Why it must exist: an exported log bundle is a file the user sends to SOMEONE ELSE
// An issue attachment or a forum post is that kind of file
// It contains baseDir, which is the folder holding the app
// In the portable layout that path almost always starts with C:\\Users\\<name>\\
// So every export carries the machine owner's login name
// That is not sensitive data (it is not a password), but it is identity
// The user does not realise they are sending it
//
// The approach is to replace the user name's LITERAL occurrences and keep the rest of the path
// What triage actually needs is how deep it sits under the user folder, not the name
// The structure survives, so a supporter can still tell the portable layout from the installed one
//
// Deliberately NOT done: other locations (the system temp and install directories, ...) are left alone
// There is no fuzzy regex either
// Only the one string the caller hands over is touched
// The wider the redaction, the more likely it silently rewrites a path meant to stay intact

using System.Text;

namespace MIDITap.Core.Settings;

public static class PathRedaction
{
    /// <summary>替换后的占位符，形态上明显不是真实用户名</summary>
    /// <remarks>The replacement token, shaped so it cannot be mistaken for a real user name</remarks>
    public const string Placeholder = "<user>";

    /// <summary>
    /// 把 <paramref name="path"/> 中出现的 <paramref name="userName"/> 换成占位符
    /// 用户名为空、或路径里不含它时，原样返回
    ///
    /// 比较**不区分大小写**：Windows 路径本身不区分大小写
    /// 用户名在环境变量里的写法与它在路径里的实际写法可能不同（尤其是路径由别的工具生成时）
    /// 分隔符 \\ 与 / 都接受：用户复制的路径两种形式都有
    /// 用户名的前后必须分别是分隔符（或字符串边界）—— 否则子串命中会误伤，例如用户名为 "a" 时会把 "C:\\abc\\a" 变成 "C:\\<user>bc\\a"
    ///
    /// Replaces occurrences of <paramref name="userName"/> in <paramref name="path"/> with the placeholder
    /// It returns the input unchanged when the name is empty or absent
    ///
    /// The comparison is CASE-INSENSITIVE, matching Windows paths
    /// The name as it appears in the environment may differ in case from its appearance inside a path
    /// That happens especially for paths built by other tools
    /// Both \\ and / are accepted as separators, since users paste paths in either form
    /// The name must sit between separators (or at a string boundary)
    /// A bare substring match would damage unrelated text
    /// For instance the name "a" would turn "C:\\abc\\a" into "C:\\<user>bc\\a"
    /// </summary>
    public static string Redact(string? path, string? userName)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(userName))
        {
            return path ?? string.Empty;
        }

        var result = new StringBuilder(path.Length);
        var index = 0;
        while (index < path.Length)
        {
            // 只在一个路径段的**起点**尝试匹配：即 index 为 0，或前一个字符是分隔符
            // Only try to match at the START of a path segment: index 0, or right after a separator
            var atSegmentStart = index == 0 || path[index - 1] is '\\' or '/';
            if (atSegmentStart && MatchesAt(path, index, userName))
            {
                // 段的**末尾**也必须落在这里：下一个字符是分隔符或字符串结尾，才算一整个段
                // The segment must also END here: the next character is a separator or the string's end
                var after = index + userName.Length;
                var endsSegment = after >= path.Length || path[after] is '\\' or '/';
                if (endsSegment)
                {
                    result.Append(Placeholder);
                    index = after;
                    continue;
                }
            }

            result.Append(path[index]);
            index++;
        }

        return result.ToString();
    }

    /// <summary>从 <paramref name="start"/> 起是否不区分大小写地等于用户名</summary>
    /// <remarks>
    /// Whether the text from <paramref name="start"/> equals the user name, case-insensitively
    /// </remarks>
    private static bool MatchesAt(string path, int start, string userName)
    {
        if (start + userName.Length > path.Length)
        {
            return false;
        }
        return string.Compare(path, start, userName, 0, userName.Length,
            StringComparison.OrdinalIgnoreCase) == 0;
    }
}
