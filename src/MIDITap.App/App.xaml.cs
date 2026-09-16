// App.xaml.cs — 应用入口
//
// 除了创建主窗口，这里还兜住三类未处理异常并落盘到 .storage/crash.log
// WinUI 会把 UI 线程异常包装成 stowed exception（0xc000027b）
// 事件查看器里看不到托管堆栈，没有这层日志就无法定位线上闪退
//
// App.xaml.cs — application entry point
//
// Beyond creating the main window this hooks three unhandled-exception sources
// It records them to .storage/crash.log
// WinUI wraps UI-thread exceptions in a stowed exception (0xc000027b) that hides the managed stack
// Without this a crash is effectively undebuggable after the fact

using MIDITap.App.Services;
using Microsoft.UI.Xaml;

namespace MIDITap.App;

public partial class App : Application
{
    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();

        // 1) UI 线程（XAML 事件处理器、async void 续体）未处理异常
        //
        // 1) Unhandled exceptions on the UI thread (XAML event handlers, async void continuations)
        UnhandledException += OnUnhandledException;
        // 2) 非 UI 线程的未处理异常（轮询线程、注入线程）
        //
        // 2) Unhandled exceptions outside the UI thread (polling thread, injection thread)
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        // 3) 被丢弃的 Task 异常（未 await 的任务）
        //
        // 3) Dropped Task exceptions (tasks that were never awaited)
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLogger.Write($"UI thread: {e.Message}", e.Exception);
        // 不设置 e.Handled：崩溃信息已经落盘
        // 继续吞掉异常会让应用处于未知状态（XAML 树可能已经损坏）
        // 静默死掉比带着坏状态运行更好
        //
        // Do not set e.Handled: the crash is recorded
        // The XAML tree may already be corrupt
        // Dying is preferable to running on with unknown state
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        => CrashLogger.Write($"AppDomain: terminating={e.IsTerminating}", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        CrashLogger.Write("Unobserved task", e.Exception);
        e.SetObserved();
    }
}
