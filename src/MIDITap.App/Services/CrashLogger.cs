// CrashLogger.cs — 最后一道崩溃日志
//
// WinUI 3 把 UI 线程上的未处理异常变成 stowed exception
// WER / 事件查看器里只能看到"出错模块 Microsoft.UI.Xaml.dll，异常代码 0xc000027b"
// 没有任何托管堆栈
// 这里把真实异常写到 exe 旁的 .storage/crash.log，让现场崩溃可以离线诊断
//
// CrashLogger.cs — last-resort crash logging
//
// WinUI 3 converts an unhandled UI-thread exception into a stowed exception
// Windows Event Viewer then reports only "faulting module Microsoft.UI.Xaml.dll, code 0xc000027b"
// No managed stack is available in that report
// Writing the real exception to .storage/crash.log next to the exe makes a crash in the field diagnosable offline

using System.Text;

namespace MIDITap.App.Services;

public static class CrashLogger
{
    private static readonly object Gate = new();
    private static string? _logPath;

    /// <summary>日志文件路径（首次访问时解析并缓存）</summary>
    /// <remarks>The log file path (resolved and cached on first access)</remarks>
    public static string LogPath => _logPath ??= ResolvePath();

    private static string ResolvePath()
    {
        // 首选 exe 旁的 .storage/（便携约定，应用已经拥有这个目录）
        // 只读介质等异常情况下回退到 %TEMP%，日志本身不能成为新的失败点
        // 这里必须用 AppPaths 解析出的真实 exe 目录
        // 单文件发布时 AppContext.BaseDirectory 指向解压目录，会让崩溃日志落到临时目录里（详见 AppPaths/AppServices 的说明）
        //
        // Prefer .storage/ next to the exe (the app already owns it)
        // Fall back to %TEMP% on read-only media, so logging does not become a new failure point
        // The base must come from AppPaths
        // Under single-file publishing AppContext.BaseDirectory is the extraction directory
        // That would drop the crash log into a temp folder (see AppPaths/AppServices)
        try
        {
            var dir = Core.Settings.AppPaths.StorageDir(
                Core.Settings.AppPaths.ResolveBaseDir(
                    Environment.ProcessPath, AppContext.BaseDirectory));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "crash.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "miditap-crash.log");
        }
    }

    /// <summary>追加一条崩溃记录；任何写入失败都被吞掉（不得引发二次崩溃）</summary>
    /// <remarks>Appends one crash record; any write failure is swallowed (it must not cause a second crash)</remarks>
    public static void Write(string source, Exception? error)
    {
        try
        {
            var text = new StringBuilder()
                .AppendLine("==========================================================")
                .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}")
                .AppendLine(error?.ToString() ?? "(no exception object)")
                .AppendLine()
                .ToString();
            lock (Gate)
            {
                File.AppendAllText(LogPath, text, Encoding.UTF8);
            }
        }
        catch
        {
            // ignore: 记录失败不能引发二次崩溃
            //
            // A failed write must not cause a second crash
        }
    }
}
