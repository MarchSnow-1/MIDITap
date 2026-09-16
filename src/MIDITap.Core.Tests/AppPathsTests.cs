// AppPathsTests.cs — 基准目录解析
//
// Guards the base-directory resolution, whose failure mode is silent

using MIDITap.Core.Settings;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Uses_the_directory_of_the_real_executable()
    {
        // 常规发布：exe 目录就是基准目录 / Normal release: the exe directory is the base directory
        Assert.Equal(
            @"C:\Apps\MIDITap",
            AppPaths.ResolveBaseDir(@"C:\Apps\MIDITap\MIDITap.exe", @"C:\ignored"));
    }

    [Fact]
    public void Prefers_the_process_path_over_the_fallback()
    {
        // **这是本函数存在的理由。** 单文件发布时 AppContext.BaseDirectory 是解压目录
        // 该解压目录位于 %TEMP%\.net\... 之下，必须被真实 exe 目录覆盖，否则用户配置会落进临时目录
        //
        // This is the whole point: under single-file publishing the fallback is the extraction directory under %TEMP%
        // It MUST be overridden, or user configs land in temp
        var extractionDir = @"X:\b\.net\MIDITap\abc123=";
        var realExeDir = @"X:\a\MIDITap";

        var resolved = AppPaths.ResolveBaseDir(
            Path.Combine(realExeDir, "MIDITap.exe"), extractionDir);

        Assert.Equal(realExeDir, resolved);
        // 真正要锁的语义是"回退值没有被返回"，而不是"结果里不含某个词"
        //
        // What this locks down is that the FALLBACK was not returned — not that the result avoids some word
        Assert.NotEqual(extractionDir, resolved);
    }

    [Fact]
    public void Falls_back_when_process_path_is_missing()
    {
        var fallback = @"C:\Fallback";
        Assert.Equal(fallback, AppPaths.ResolveBaseDir(null, fallback));
        Assert.Equal(fallback, AppPaths.ResolveBaseDir("", fallback));
        Assert.Equal(fallback, AppPaths.ResolveBaseDir("   ", fallback));
    }

    [Fact]
    public void Falls_back_when_process_path_has_no_directory_component()
    {
        // 只有一个裸文件名（没有目录）时无法推导，必须回退而不是返回空串
        // 返回空串会让 Path.Combine 产生相对路径
        // 这样可变文件会散落到当前工作目录
        //
        // A bare file name has no directory to derive, so falling back matters
        // Returning an empty string would make Path.Combine produce relative paths
        // That would scatter user files into the current working directory
        var fallback = @"C:\Fallback";
        Assert.Equal(fallback, AppPaths.ResolveBaseDir("MIDITap.exe", fallback));
    }

    [Fact]
    public void Never_returns_an_empty_string()
    {
        // 基准目录为空是危险的静默状态，任何输入组合都不应产生它
        //
        // An empty base directory is a dangerous silent state
        // No combination of inputs should produce it
        foreach (var path in new[] { null, "", "   ", "MIDITap.exe", @"/x", @"C:\" })
        {
            var result = AppPaths.ResolveBaseDir(path, @"C:\Fallback");
            Assert.False(string.IsNullOrWhiteSpace(result), $"输入 '{path}' 产生了空基准目录");
        }
    }

    [Fact]
    public void Handles_paths_with_trailing_separator_in_the_exe()
    {
        // 带尾分隔符的 ProcessPath（异常但可能）：目录解析应给出同一结果
        //
        // A ProcessPath with a trailing separator is unusual but possible
        // Directory resolution should give the same result
        var resolved = AppPaths.ResolveBaseDir(@"C:\Apps\MIDITap\", @"C:\Fallback");
        Assert.Equal(@"C:\Apps\MIDITap", resolved.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Portability_invariant_exe_directory_equals_base_directory()
    {
        // 不变式（便携性的定义）：基准目录 == exe 所在目录
        // 无论哪种发布方式，config/ 与 .storage/ 都必须与 exe 同级
        //
        // The invariant (the definition of portability): the base directory == the directory the exe lives in
        // Under any publishing mode, config/ and .storage/ must sit next to the exe
        foreach (var exe in new[]
                 {
                     @"C:\A\MIDITap.exe",
                     @"D:\Deep\Nested\Path\MIDITap.exe",
                     @"E:\other\MIDITap.exe",
                 })
        {
            var expected = Path.GetDirectoryName(exe);
            Assert.Equal(expected, AppPaths.ResolveBaseDir(exe, @"C:\should-not-be-used"));
        }
    }
}
