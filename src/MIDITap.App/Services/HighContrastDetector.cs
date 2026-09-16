// HighContrastDetector.cs — 检测 Windows 对比度模式，并直接读取对比度调色板
//
// 用 SystemParametersInfo(SPI_GETHIGHCONTRAST) 判断，而不是"探测某个标记资源键是否存在"
// 后者依赖"主题字典查找不会回退到 Default"这一未经验证的假设，一旦回退就会静默走错分支
//
// 调色板用 GetSysColor 直接读系统值，而不是解析 App.xaml 里的 SystemColor 画笔
// 系统值是权威来源，不依赖资源字典的加载状态，也让这段逻辑可以脱离 UI 单独推理
//
// Detects the Windows contrast mode and reads the contrast palette directly
//
// Uses SystemParametersInfo(SPI_GETHIGHCONTRAST) rather than probing for a marker resource key
// The latter would rely on the unverified assumption that theme-dictionary lookup never falls back to Default
// If it did, the code would silently take the wrong branch
//
// The palette is read straight from GetSysColor instead of parsing the SystemColor brushes in App.xaml
// The system values are authoritative and do not depend on resource-dictionary load state
// They also let this logic be reasoned about independently of the UI

using System.Runtime.InteropServices;
using Windows.UI;

namespace MIDITap.App.Services;

public static class HighContrastDetector
{
    // GetSysColor 索引（WinUser.h）
    //
    // GetSysColor indices (WinUser.h)
    private const int ColorButtonFace = 15;
    private const int ColorButtonText = 18;
    private const int ColorWindow = 5;
    private const int ColorWindowText = 8;
    private const int ColorHighlight = 13;
    private const int ColorHighlightText = 14;

    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint cbSize;
        public uint dwFlags;
        // 用 IntPtr 而非 string：不需要读回方案名，这样无需字符串封送（Windows 允许该字段为 NULL）
        //
        // IntPtr rather than string: the scheme name does not have to be read back, so no string marshalling is needed
        // Windows permits this field to be NULL
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref HighContrastInfo pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int nIndex);

    /// <summary>当前是否处于对比度模式；任何失败都按"否"处理</summary>
    /// <remarks>Whether a contrast theme is currently active; any failure is treated as "no"</remarks>
    public static bool IsHighContrast()
    {
        try
        {
            var info = new HighContrastInfo
            {
                cbSize = (uint)Marshal.SizeOf<HighContrastInfo>(),
                lpszDefaultScheme = IntPtr.Zero,
            };
            return SystemParametersInfo(SpiGetHighContrast, 0, ref info, 0)
                && (info.dwFlags & HcfHighContrastOn) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按 Win32 颜色索引读系统色；失败时返回给定兜底色</summary>
    /// <remarks>
    /// Reads a system colour by its Win32 colour index
    /// Returns the given fallback colour when the read fails
    /// </remarks>
    private static Color Sys(int index, byte r, byte g, byte b)
    {
        try
        {
            var value = GetSysColor(index); // COLORREF = 0x00BBGGRR
            return Color.FromArgb(0xFF, (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
        }
        catch
        {
            return Color.FromArgb(0xFF, r, g, b);
        }
    }

    /// <summary>可交互表面的背景色（对比度方案里的"按钮面"）</summary>
    /// <remarks>The background colour of interactive surfaces (the "button face" of a contrast scheme)</remarks>
    public static Color ButtonFace => Sys(ColorButtonFace, 0x00, 0x00, 0x00);

    /// <summary>可交互表面的前景色（与 ButtonFace 是官方保证的高对比配对）</summary>
    /// <remarks>
    /// The foreground colour of interactive surfaces
    /// It is a high-contrast pairing with ButtonFace that the platform guarantees
    /// </remarks>
    public static Color ButtonText => Sys(ColorButtonText, 0xFF, 0xFF, 0xFF);

    public static Color Window => Sys(ColorWindow, 0x00, 0x00, 0x00);

    public static Color WindowText => Sys(ColorWindowText, 0xFF, 0xFF, 0xFF);

    public static Color Highlight => Sys(ColorHighlight, 0xD6, 0xB4, 0xFD);

    public static Color HighlightText => Sys(ColorHighlightText, 0x2B, 0x2B, 0x2B);
}
