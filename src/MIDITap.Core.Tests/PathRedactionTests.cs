// PathRedactionTests.cs — 导出路径里的用户名脱敏
//
// 为什么值得单测：这个函数是导出包里唯一会修改**用户自己数据**的地方，而且改错了不会报错
// 两种失效都是静默的
//   * 漏替换 —— 用户名照旧发出去，用户以为已经脱敏
//   * 错替换 —— 把路径里碰巧同名的片段也改掉
//     排查时看到的是被改坏的路径，而支持者无从知道原样是什么
// 因此用例集中在"该替换的替换了"与"不该动的没动"两侧
//
// 测试数据一律用 example / EXAMPLE / examples 这类**示例值**，不使用任何真实名称
// 严格遵循 AGENTS.md 中的规则：测试数据不应携带任何不便于理解的自定义信息
//
// PathRedactionTests.cs — user-name redaction for exported paths
//
// Why it deserves its own tests: this is the only place in the export pipeline that MODIFIES the user's own data
// Getting it wrong raises no error
// Both failure modes are silent
// A missed replacement sends the name out while the user believes it was redacted
// An over-eager one rewrites an unrelated same-named fragment
// Triage then sees a path that was never on disk, while the supporter has no way to know
// The cases therefore cover both sides: what must change, and what must not
//
// Every fixture uses example / EXAMPLE / examples style placeholders
// No fixture uses a real user name or anything tied to reality
// Test data must carry nothing traceable to a specific person or machine

using MIDITap.Core.Settings;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class PathRedactionTests
{
    [Fact]
    public void A_user_folder_is_replaced_and_the_rest_of_the_path_survives()
    {
        // 便携用法：程序放在用户目录下的某一层
        // 目录结构必须留着，那正是判断"便携版还是安装版"的依据
        //
        // The portable layout: the app sits at some depth under the user folder
        // The directory structure must survive, since that is exactly what tells the portable build from the installed one
        var result = PathRedaction.Redact("C:\\Users\\example\\a\\MIDITap", "example");

        Assert.Equal("C:\\Users\\<user>\\a\\MIDITap", result);
    }

    [Fact]
    public void Case_differences_between_the_environment_and_the_path_still_match()
    {
        // 环境变量里的用户名大小写，与路径里实际写的大小写可能不同
        // 路径由别的工具拼出来时尤其如此
        // 不区分大小写才不会漏替换
        //
        // The name's case in the environment can differ from its case inside a path
        // That happens especially when another tool built the path
        // Case-insensitive matching is what prevents a miss
        Assert.Equal("C:\\Users\\<user>\\a",
            PathRedaction.Redact("C:\\Users\\EXAMPLE\\a", "example"));
    }

    [Fact]
    public void Forward_slashes_are_redacted_too()
    {
        // 用户从别处复制过来的路径经常是正斜杠形式
        // Paths pasted from elsewhere are frequently in forward-slash form
        Assert.Equal("C:/Users/<user>/a",
            PathRedaction.Redact("C:/Users/example/a", "example"));
    }

    [Fact]
    public void Occurrences_inside_a_longer_segment_are_left_alone()
    {
        // **这一条是防误伤的关键**：用户名是 example 时，不能把路径里 examples 这段中的 example 也换掉
        // 只替换整段，才不会被同名子串带偏
        //
        // The anti-collateral case: with the name example, the example inside examples must not be touched
        // Only whole segments are replaced, so a same-named substring cannot derail it
        var result = PathRedaction.Redact("C:\\a\\examples\\example\\b", "example");

        Assert.Equal("C:\\a\\examples\\<user>\\b", result);
    }

    [Fact]
    public void A_path_without_the_name_is_returned_unchanged()
    {
        // 装在不含用户名的位置（另一块盘、用户目录之外）时，路径原样保留 —— 不做任何模糊替换
        // Installed outside the user folder (another drive, say), where the path contains no name
        // It is returned untouched, with no fuzzy rewriting
        var path = "D:\\a\\MIDITap";

        Assert.Equal(path, PathRedaction.Redact(path, "example"));
    }

    [Fact]
    public void Every_occurrence_is_replaced_not_just_the_first()
    {
        // 用户名可能出现在路径里的多处（例如解压到了自己名下又嵌了一层同名目录）
        // The name can appear more than once, e.g. when an archive nested a same-named folder
        Assert.Equal("C:\\Users\\<user>\\<user>\\a",
            PathRedaction.Redact("C:\\Users\\example\\example\\a", "example"));
    }

    [Fact]
    public void An_empty_or_missing_user_name_changes_nothing()
    {
        // 拿不到用户名时不能把路径改坏，也不能凭空插入占位符
        // With no user name available the path must not be damaged
        // No placeholder may appear from nowhere
        var path = "C:\\Users\\example\\a";

        Assert.Equal(path, PathRedaction.Redact(path, null));
        Assert.Equal(path, PathRedaction.Redact(path, ""));
        Assert.Equal(path, PathRedaction.Redact(path, "   "));
    }

    [Fact]
    public void A_name_at_the_very_end_of_the_path_is_still_replaced()
    {
        // 边界：用户名是路径最后一段（没有后续分隔符）
        // Boundary: the name is the final segment, with no separator after it
        Assert.Equal("C:\\Users\\<user>",
            PathRedaction.Redact("C:\\Users\\example", "example"));
    }
}
