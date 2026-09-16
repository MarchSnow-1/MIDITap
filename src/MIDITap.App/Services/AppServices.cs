// AppServices.cs — 极简的组合根
//
// AppServices.cs — a minimal composition root

using System.Reflection;
using MIDITap.Core.Logging;
using MIDITap.Core.Settings;
using Microsoft.UI.Dispatching;

namespace MIDITap.App.Services;

public static class AppServices
{
    /// <summary>便携约定：一切可变文件（config/、.storage/）都放在 exe 旁</summary>
    /// <remarks>Portable convention: every mutable file (config/, .storage/) lives next to the exe</remarks>
    public static string BaseDir { get; } =
        AppPaths.ResolveBaseDir(Environment.ProcessPath, AppContext.BaseDirectory);

    public static I18nService I18n { get; private set; } = null!;
    public static LogService Log { get; } = new();

    /// <summary>
    /// 「显示哪些级别」的共享状态。日志页与主页都读它，因此两处看到的日志始终一致
    /// 在日志页选了"只看警告"，主页也只剩警告（详见 Core 的 LogFilter）
    ///
    /// Shared "which levels are shown" state
    /// Both the log page and Home read it, so the two always agree
    /// Choose "warnings only" on the log page and Home shows warnings only too
    /// See LogFilter in Core for the reasoning
    /// </summary>
    public static LogFilter LogFilter { get; } = new();

    public static BackendService Backend { get; private set; } = null!;

    private static DispatcherQueue? _dispatcher;

    /// <summary>
    /// 主窗口的原生句柄
    /// 文件选择器等 Win32 互操作需要它，而未打包应用里选择器必须绑定到一个窗口才弹得出来
    /// 放在这里而不是让每个调用方自己找窗口：少一处能漏掉的参数
    ///
    /// The main window's native handle
    /// Win32 interop such as the file picker needs it
    /// In an unpackaged app a picker **must** be bound to a window before it can appear
    /// It lives here rather than being looked up by each caller: one less argument that can be forgotten
    /// </summary>
    public static IntPtr MainWindowHandle { get; private set; }

    /// <summary>由 MainWindow 在构造时调用一次</summary>
    /// <remarks>Called once by MainWindow during construction</remarks>
    public static void AttachWindow(IntPtr handle) => MainWindowHandle = handle;

    /// <summary>
    /// 应用版本号
    /// 取自程序集的 InformationalVersion
    /// 构建时由 `-p:Version=` 注入、或由 csproj 的 &lt;Version&gt; 提供的那个值
    ///
    /// 取不到时回退为 "unknown"
    ///
    /// The app version
    /// Taken from the assembly's InformationalVersion
    /// The value injected at build time by `-p:Version=`, or the one supplied by the csproj's &lt;Version&gt;
    /// Falls back to "unknown" when it cannot be read
    /// </summary>
    public static string AppVersion { get; } =
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? Core.Update.UpdateChannel.UnknownVersion;

    /// <summary>
    /// 本构建是否参与更新（检查、下载、安装）
    ///
    /// 开发构建一律不参与，两条来源合起来覆盖了全部非发布构建
    ///   * 本机 Debug 构建
    ///   * CI 的 dev 构建
    /// 再加上版本号取不到时的 unknown，判定集中在 UpdateChannel 一处
    ///
    /// 为什么开发构建不该去检查：它既不是某个已发布版本的产物，也不该被"更新"成正式版
    ///
    /// Whether this build takes part in updating (checking, downloading, installing)
    ///
    /// A development build does not take part
    /// The two sources together cover every non-release build
    ///   * a local Debug build
    ///   * the CI dev build
    /// Add the unknown version used when the version cannot be read
    /// The decision then sits in one place, UpdateChannel
    ///
    /// Why a development build should not check for updates: it is not the output of any released version
    /// It should not be "updated" into a release
    /// </summary>
    public static bool UpdatesEnabled =>
#if DEBUG
        false;
#else
        !Core.Update.UpdateChannel.IsDevelopment(AppVersion);
#endif

    /// <summary>在 MainWindow 构造最早期调用（页面渲染文本依赖 I18n）</summary>
    /// <remarks>Called at the very start of MainWindow's constructor (pages need I18n to render their text)</remarks>
    public static void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        I18n ??= new I18nService(BaseDir);
        Backend ??= new BackendService(BaseDir);
    }

    /// <summary>
    /// 当前界面是否为浅色
    /// 取元素有效主题（ThemeService.EffectiveTheme），也就是框架实际用来解析 {ThemeResource} 的那个值
    /// 对比度模式下额外按窗口背景亮度判断
    /// 因为对比度方案里既有"高对比黑"也有"高对比白"，光看 ElementTheme 不够
    ///
    /// Whether the UI is currently light
    /// Uses the element's effective theme (ThemeService.EffectiveTheme)
    /// That is the very value the framework uses to resolve {ThemeResource}
    /// Under contrast themes the window background luminance decides as well
    /// Contrast schemes exist both as a high-contrast black and as a high-contrast white
    /// So ElementTheme on its own is not enough
    /// </summary>
    public static bool IsLightTheme
    {
        get
        {
            if (ThemeService.IsHighContrast)
            {
                var window = HighContrastDetector.Window;
                var luminance = (0.2126 * window.R + 0.7152 * window.G + 0.0722 * window.B) / 255.0;
                return luminance > 0.5;
            }
            return ThemeService.EffectiveTheme == Microsoft.UI.Xaml.ElementTheme.Light;
        }
    }

    /// <summary>把后台线程的回调投递到 UI 线程；已在 UI 线程时直接执行</summary>
    /// <remarks>
    /// Posts a background thread's callback to the UI thread
    /// When it is already on the UI thread the callback runs directly
    /// </remarks>
    public static void RunOnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }
        _dispatcher.TryEnqueue(() => action());
    }
}