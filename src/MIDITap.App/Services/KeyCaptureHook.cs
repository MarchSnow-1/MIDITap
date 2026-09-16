// KeyCaptureHook.cs — 供"按键捕获"使用的低级键盘钩子
//
// 为什么不能用 WinUI 的 KeyDown 事件：
//   1) 有些键根本不产生 XAML 按键事件被我们收到的机会
//      Win 键会先触发系统 shell 热键（打开开始菜单）
//      焦点被系统抢走，后续按键全部落到开始菜单的搜索框里
//      e.Handled = true 只影响 XAML 路由，**拦不住系统级热键**
//   2) 按下修饰键常伴随 Alt/Ctrl 的系统行为（Alt 激活菜单栏、Ctrl 组合被其它程序消费）
//
// 用 WH_KEYBOARD_LL 可以：在事件进入任何应用（包括 shell）之前看到它，并且**返回 1 阻止继续传递**，从而避免开始菜单被打开
// 安装钩子期间按键被完全接管，这正是"捕获"应有的语义
//
// Low-level keyboard hook used by the mapping editor's key capture
//
// Why WinUI's KeyDown event is insufficient:
//   1) Some keys never reach us as a XAML key event
//      The Windows key triggers a shell hotkey (opening Start)
//      The system takes focus, and every later keystroke lands in Start's search box
//      Setting e.Handled only affects the XAML route; it cannot stop a system-level hotkey
//   2) Modifier presses drag in system behaviour
//      Alt activates the menu bar, and Ctrl combos get consumed elsewhere
//
// A WH_KEYBOARD_LL hook sees the event before any application (including the shell)
// It can return 1 to swallow it, so the Start menu does not open
// While the hook is installed, keys are fully captured
// That is exactly what "capture" should mean

using System.Runtime.InteropServices;

namespace MIDITap.App.Services;

/// <summary>捕获到的一次按键（虚拟键码 + 是否按下）</summary>
/// <remarks>One captured key press (virtual key code plus whether it went down)</remarks>
public sealed record CapturedKey(ushort VirtualKey, bool IsDown);

public static class KeyCaptureHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    // 静态持有：被 GC 回收后系统回调会跳到已释放地址并崩溃进程
    //
    // Held statically: a collected delegate would make the system callback jump to freed memory and crash the process
    private static HookProc? _proc;
    private static IntPtr _hook = IntPtr.Zero;

    /// <summary>捕获回调。返回 true 表示"已消费"，该按键不会再传给任何应用</summary>
    /// <remarks>
    /// The capture callback
    /// Returning true means the key has been consumed and is not passed on to any application
    /// </remarks>
    private static Func<CapturedKey, bool>? _handler;

    /// <summary>钩子是否已安装</summary>
    /// <remarks>Whether the hook is installed</remarks>
    public static bool IsActive => _hook != IntPtr.Zero;

    /// <summary>
    /// 安装钩子 <paramref name="handler"/> 对每个按键调用一次
    /// 返回 true 时拦截该按键（不会到达系统/其它应用）
    /// 返回 false 时放行
    /// 已安装时先卸载，保证同一时刻只有一个捕获源
    ///
    /// Installs the hook; <paramref name="handler"/> is called once per key press
    /// Returning true intercepts that key: it does not reach the system or other applications
    /// Returning false lets it through
    /// An existing installation is uninstalled first, so that only one capture source exists at a time
    /// </summary>
    public static bool Install(Func<CapturedKey, bool> handler)
    {
        Uninstall();
        _handler = handler;
        _proc = Callback;
        // 低级钩子可传 hMod = GetModuleHandle(null)，无需原生 DLL
        //
        // A low-level hook may pass hMod = GetModuleHandle(null); no native DLL is required
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null!), 0);
        if (_hook == IntPtr.Zero)
        {
            _handler = null;
            _proc = null;
            return false;
        }
        return true;
    }

    /// <summary>卸载钩子；未安装时是空操作</summary>
    /// <remarks>Uninstalls the hook; a no-op when it is not installed</remarks>
    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _handler = null;
        _proc = null;
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 钩子位于系统输入路径上：必须尽快返回，且异常绝不能外泄
        //
        // The hook sits on the system input path: it has to return as quickly as it can, and no exception may escape it
        if (nCode >= 0)
        {
            try
            {
                var message = (int)wParam;
                var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                var isUp = message is WM_KEYUP or WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    var handler = _handler;
                    if (handler is not null && handler(new CapturedKey((ushort)info.vkCode, isDown)))
                    {
                        // 返回非零 = 吞掉该事件
                        // 既让捕获只发生在对话框内部，也避免 Win 键打开开始菜单、Alt 激活菜单栏等系统行为
                        //
                        // A non-zero return swallows the event
                        // Capture then stays inside the dialog
                        // System behaviour such as the Windows key opening the Start menu or Alt activating the menu bar is avoided
                        return 1;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
