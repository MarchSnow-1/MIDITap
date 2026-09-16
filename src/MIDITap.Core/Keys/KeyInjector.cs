// KeyInjector.cs — 通过 Win32 SendInput 模拟键盘输入（Windows x64）
//
// SendInput 是同步调用，因此一份实现可以服务两处调用点
// INPUT.time 用 GetTickCount 填充
//
// Simulates keyboard input via Win32 SendInput (Windows x64)
// SendInput is a synchronous call, so one implementation serves both call sites
// GetTickCount fills INPUT.time

using System.Runtime.InteropServices;

namespace MIDITap.Core.Keys;

public static class KeyInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    // INPUT 联合体必须和最大的成员（MOUSEINPUT）一样大
    // 否则 cbSize 对不上 SendInput 期望的 40 字节 x64 布局
    //
    // The INPUT union must be as large as the largest member (MOUSEINPUT)
    // Otherwise cbSize will not match the 40-byte x64 layout SendInput expects
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    /// <summary>发送一个按键按下（up = false）或抬起（up = true）事件
    /// Send one key-down/up event</summary>
    public static void SendKey(ushort vkCode, bool up)
    {
        var input = new INPUT
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vkCode,
                    wScan = 0,
                    dwFlags = up ? KeyeventfKeyup : 0,
                    time = GetTickCount(),
                    dwExtraInfo = 0,
                },
            },
        };

        var inputs = new[] { input };
        var sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            Console.Error.WriteLine("[miditap.keyboard] SendInput returned 0");
        }
    }
}
