// ThemeService.cs — 深浅色主题的单一入口
//
// 为什么需要它：XAML 里的 {ThemeResource} 由框架按**元素**的有效主题解析，而且主题变化时会自动重新解析
// 但代码绘制的部分（音符盘、标题栏胶囊）拿不到这套机制
// 一旦用户在系统浅色下手动选择深色，XAML 会变深、代码部分仍是浅色，界面就错配了
// 本服务改为按元素的实际主题（ActualTheme）挑选主题字典，保证两条路径取到同一套值
//
// Single entry point for light/dark theming
//
// Why this exists: {ThemeResource} in XAML is resolved by the framework against the ELEMENT's effective theme
// It is also re-resolved automatically on a theme change
// Code-drawn parts get none of that
// If the user forces dark while the system is light, XAML would go dark while the code-drawn parts stayed light
// The UI would then be inconsistent
// This service selects the dictionary from the element's actual theme instead
// That way both paths resolve identically

using MIDITap.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MIDITap.App.Services;

public static class ThemeService
{
    /// <summary>应用主题的根元素（MainWindow 的根 Grid），由 Attach 记录</summary>
    /// <remarks>The root element the app theme is applied to (MainWindow's root Grid), recorded by Attach</remarks>
    private static FrameworkElement? _root;

    /// <summary>主题（含对比度模式）变化后触发，供代码绘制部分刷新</summary>
    /// <remarks>Raised after the theme (contrast mode included) changes, so the code-drawn parts can refresh</remarks>
    public static event Action? Changed;

    /// <summary>当前模式：system / light / dark</summary>
    /// <remarks>The current mode: system / light / dark</remarks>
    public static string Mode { get; private set; } = AppStorage.ThemeSystem;

    /// <summary>从存储载入模式（启动时调用一次），非法值回退为跟随系统</summary>
    /// <remarks>
    /// Loads the mode from storage (called once at startup)
    /// A value that is not supported falls back to following the system
    /// </remarks>
    public static void Load(string baseDir)
        => Mode = AppStorage.GetThemeMode(baseDir) ?? AppStorage.ThemeSystem;

    /// <summary>
    /// 记录应用主题的元素，并立即应用当前模式，之后 <see cref="Apply"/> 会改它的 RequestedTheme
    ///  FrameworkElement.RequestedTheme 允许运行时修改
    /// （Application.RequestedTheme 则会在运行中抛异常，所以不能用它）
    ///
    /// Records the element that carries the app theme and applies the current mode immediately
    /// Later calls to <see cref="Apply"/> change its RequestedTheme
    /// FrameworkElement.RequestedTheme may be changed at runtime
    /// (Application.RequestedTheme throws while the app is running, so it cannot be used here.)
    /// </summary>
    public static void Attach(FrameworkElement root)
    {
        _root = root;
        Apply();
    }

    /// <summary>应用当前模式到根元素，并通知代码绘制部分刷新</summary>
    /// <remarks>Applies the current mode to the root element and notifies the code-drawn parts to refresh</remarks>
    public static void Apply()
    {
        if (_root is not null)
        {
            _root.RequestedTheme = Mode switch
            {
                AppStorage.ThemeLight => ElementTheme.Light,
                AppStorage.ThemeDark => ElementTheme.Dark,
                // Default 表示"跟随系统"，且系统深浅色切换时框架会自动重解析
                //
                // Default means "follow the system"
                // The framework re-resolves automatically when the system switches between light and dark
                _ => ElementTheme.Default,
            };
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 切换模式并持久化，返回是否已应用（false 表示值非法，未做任何改动）
    ///
    /// Switches the mode and persists it, returning whether it was applied
    /// False means the value is not supported and nothing was changed
    /// </summary>
    public static bool SetMode(string baseDir, string mode)
    {
        if (!AppStorage.IsSupportedTheme(mode))
        {
            return false;
        }
        Mode = mode;
        AppStorage.SaveThemeMode(baseDir, mode);
        Apply();
        return true;
    }

    /// <summary>
    /// 元素当前生效的主题（System 模式下即系统主题，手动模式下即所选值）
    /// 这是权威来源：它已经包含了"跟随系统"的解析结果
    ///
    /// The theme currently in effect for the element (the system theme in System mode, the selected value in a manual mode)
    /// This is the authoritative source: it already contains the result of resolving "follow the system"
    /// </summary>
    public static ElementTheme EffectiveTheme => _root?.ActualTheme ?? ElementTheme.Dark;

    /// <summary>当前是否处于 Windows 对比度模式</summary>
    /// <remarks>Whether a Windows contrast theme is currently active</remarks>
    public static bool IsHighContrast => HighContrastDetector.IsHighContrast();

    /// <summary>
    /// 按元素有效主题解析画笔，对比度模式下返回 null，由调用方改用系统色
    /// App.xaml 的 HighContrast 字典里那些 SystemColor 画笔同样可用
    /// 但音符盘需要按实测亮度决定盘面与格子各用哪一个
    /// XAML 表达不了，必须由代码处理
    ///
    /// Resolves a brush against the element's effective theme
    /// Under contrast themes it returns null and the caller falls back to system colours
    /// The SystemColor brushes in App.xaml's HighContrast dictionary are equally usable
    /// But the note board has to decide from measured luminance which of the two is the board fill and which the cell fill
    /// XAML cannot express that, so code has to handle it
    /// </summary>
    public static Brush? Brush(string key)
    {
        if (IsHighContrast)
        {
            return null;
        }
        var dictionaryKey = EffectiveTheme == ElementTheme.Light ? "Light" : "Default";
        if (Application.Current?.Resources?.ThemeDictionaries is not { } dictionaries)
        {
            return null;
        }
        // TryGetValue 而非索引器：键缺失时索引器会抛异常
        //
        // TryGetValue rather than the indexer: a missing key makes the indexer throw
        if (dictionaries.TryGetValue(dictionaryKey, out var raw)
            && raw is ResourceDictionary dictionary
            && dictionary.TryGetValue(key, out var value)
            && value is Brush brush)
        {
            return brush;
        }
        return null;
    }
}
