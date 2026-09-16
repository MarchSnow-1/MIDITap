// WindowMinSize.cs — 用 Win32 的 WM_GETMINMAXINFO 给窗口设最小尺寸
//
// WindowMinSize.cs — sets the window's minimum size through Win32's WM_GETMINMAXINFO

using System.Runtime.InteropServices;

namespace MIDITap.App.Services;

public static class WindowMinSize
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint SubclassId = 0x4D54; // "MT"

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate IntPtr SubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    // 必须静态持有：委托被回收后系统回调会跳到已释放地址并崩溃
    //
    // Held statically: a collected delegate would make the OS callback jump to freed memory
    private static SubclassProc? _proc;

    private static int _minWindowWidth;
    private static int _minWindowHeight;

    /// <summary>
    /// 给窗口设最小客户区尺寸
    /// 传的是**客户区**尺寸（与调用方的约定一致），内部按实际非客户区大小换算成窗口尺寸
    /// 边框在不同 DPI/主题下并不固定为某个常量
    ///
    /// Sets the minimum CLIENT size (matching the caller's convention)
    /// It converts that to a window size internally
    /// The frame thickness is not a fixed constant across DPI/theme
    /// </summary>
    public static void Apply(IntPtr hWnd, int minClientWidth, int minClientHeight)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        // 非客户区厚度 = 窗口尺寸 - 客户区尺寸
        //
        // Frame thickness = window size - client size
        var frameWidth = 0;
        var frameHeight = 0;
        if (GetWindowRect(hWnd, out var windowRect) && GetClientRect(hWnd, out var clientRect))
        {
            frameWidth = (windowRect.Right - windowRect.Left) - (clientRect.Right - clientRect.Left);
            frameHeight = (windowRect.Bottom - windowRect.Top) - (clientRect.Bottom - clientRect.Top);
        }

        _minWindowWidth = minClientWidth + Math.Max(0, frameWidth);
        _minWindowHeight = minClientHeight + Math.Max(0, frameHeight);

        _proc = OnMessage;
        SetWindowSubclass(hWnd, _proc, (IntPtr)SubclassId, IntPtr.Zero);
    }

    private static IntPtr OnMessage(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MinTrackSize = new Point { X = _minWindowWidth, Y = _minWindowHeight };
            Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
