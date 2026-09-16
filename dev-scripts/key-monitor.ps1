<#
.SYNOPSIS
  低级键盘钩子监视器：验证按键事件是否真的流经系统输入队列

  Low-level keyboard hook monitor: verifies that key events really travel through the system input queue

.DESCRIPTION
  为什么需要它（以及为什么 GetAsyncKeyState 不够）
    GetAsyncKeyState 只返回系统的异步键状态位，能说明"这个键目前被认为是按下状态"
    但**不能证明**有按键事件真的流经输入队列、被目标窗口接收
    本工具用 WH_KEYBOARD_LL 低级钩子拦截流经输入队列的每个按键事件，因此证据更强：
      * 事件确实存在（而不是状态残留）
      * 先后顺序可验证
      * 是否带 LLKHF_INJECTED 标志 —— 用来区分"程序注入的按键"与"人真的按下的按键"
        MIDITap 通过 SendInput 注入，因此它的按键**一定**带 INJECTED 标志
        而你自己敲键盘不带，这是"这个键由 MIDITap 发出"的直接证据

  Why this tool is needed (and why GetAsyncKeyState is not enough)
  GetAsyncKeyState only returns the system's asynchronous key-state bits
  It can say "this key is currently considered down"
  But it CANNOT show that a key event really travelled through the input queue
  It also cannot show that the target window received it
  This tool intercepts every key event passing through the input queue
  It uses a WH_KEYBOARD_LL low-level hook
  So the evidence is stronger
    * the event really exists (rather than being a leftover state bit)
    * the order in time can be verified
    * whether the LLKHF_INJECTED flag is set
      This tells "a key injected by a program" apart from "a key a human really pressed"
      MIDITap injects through SendInput, so its keys ALWAYS carry the INJECTED flag
      Your own typing does not
      That is direct evidence that a given key was emitted by MIDITap

.EXAMPLE
  # 监视 10 秒，只显示注入的按键（屏蔽你自己的敲击）
  # Watch for 10 seconds, showing only injected keys (your own typing is filtered out)
  pwsh dev-scripts/key-monitor.ps1 -Seconds 10 -OnlyInjected

.EXAMPLE
  # 作为断言工具：15 秒内必须观察到 F13 的按下与抬起，否则退出码非 0
  # Use it as an assertion: F13 must be seen pressed and released within 15 seconds
  # Otherwise the exit code is non-zero
  pwsh dev-scripts/key-monitor.ps1 -ExpectDown 0x7C -ExpectUp 0x7C -Seconds 15

.NOTES
  隐私：钩子是全屏的，会看到所有按键。因此默认只打印虚拟键码，不打印任何文本内容、也不落盘
  -OnlyInjected 可进一步只保留注入事件
  钩子回调内不做耗时操作：低级钩子变慢会拖慢整个系统的输入

  Privacy: the hook is system-wide and sees every key press
  So by default only virtual key codes are printed
  No text content is printed and nothing is written to disk
  -OnlyInjected narrows it further to injected events
  The hook callback does no expensive work
  A slow low-level hook slows down input for the whole system
#>
# 为什么放在 dev-scripts/ 而不是 scripts/
#   scripts/ 下的内容会被原样复制进用户发布包（见 .github/workflows/*.yml 的 stage 步骤）
#   而本脚本是排查用的工具，不发也不该发给用户 —— 它只用于验证按键是否真的流经系统输入队列，不是程序运行所需
#   因此按 AGENTS.md §5 的分工放进 dev-scripts/：它是仓库的一部分（开发时可用、可 review）
#   但不进入发布包。打包只复制 scripts/*.ps1，所以这里不需要额外的排除规则
#
# Why dev-scripts/ rather than scripts/
# Everything under scripts/ is copied verbatim into the user package
# See the stage steps in .github/workflows/*.yml
# This script is a diagnostic tool
# It is neither needed by nor meant for users
# It exists only to verify whether key events really travel through the system input queue
# It therefore lives in dev-scripts/ per AGENTS.md §5
# That keeps it part of the repository (usable and reviewable during development)
# It stays outside the package
# Packaging copies scripts/*.ps1 only, so no extra exclusion rule is needed
[CmdletBinding()]
param(
    [int]$Seconds = 10,
    [switch]$OnlyInjected,
    [string]$ExpectDown,
    [string]$ExpectUp,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not ("KeyMonitor.Hook" -as [type])) {
    # -ReferencedAssemblies 一旦给出，就**替换**掉 Add-Type 的默认引用集，而不是追加
    # 因此下面必须把这段 C# 用到的程序集列全
    # 只写 WinForms 时，System.Threading.Thread 就没得引用，编译会报 CS0103「当前上下文中不存在名称 Thread」
    # 不写这个参数，System.Windows.Forms 就解析不到
    #
    # Passing -ReferencedAssemblies REPLACES Add-Type's default reference set rather than adding to it
    # So every assembly this C# needs must be listed
    # Listing only WinForms leaves System.Threading.Thread unbound
    # The compile then fails with CS0103 "the name Thread does not exist in the current context"
    # Omitting the parameter does not work either
    # Then System.Windows.Forms cannot be resolved
    Add-Type -ReferencedAssemblies System.Windows.Forms, System.Runtime.InteropServices, System.Threading.Thread -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyMonitor
{
    public sealed class KeyEvent
    {
        public uint VkCode;
        public uint ScanCode;
        public bool IsDown;
        public bool IsInjected;
        public uint Time;
        public ulong ExtraInfo;
        public string Name;
    }

    public static class Hook
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_LOWER_IL_INJECTED = 0x02;
        private const uint LLKHF_INJECTED = 0x10;
        private const uint PM_REMOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
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

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        // 静态持有委托：若被 GC 回收，系统回调会跳到已释放的地址并崩溃
        // Held statically: a collected delegate would make the OS callback jump to freed memory and crash
        private static HookProc _proc;
        private static IntPtr _hook = IntPtr.Zero;
        private static Action<KeyEvent> _sink;

        public static int LastError { get; private set; }

        public static bool Install(Action<KeyEvent> sink)
        {
            _sink = sink;
            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) { LastError = Marshal.GetLastWin32Error(); return false; }
            return true;
        }

        public static void Uninstall()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            _sink = null;
            _proc = null;
        }

        private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    int msg = (int)wParam;
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                    if (down || up)
                    {
                        var e = new KeyEvent
                        {
                            VkCode = info.vkCode,
                            ScanCode = info.scanCode,
                            IsDown = down,
                            IsInjected = (info.flags & LLKHF_INJECTED) != 0
                                         || (info.flags & LLKHF_LOWER_IL_INJECTED) != 0,
                            Time = info.time,
                            ExtraInfo = (ulong)info.dwExtraInfo.ToInt64(),
                            Name = ((System.Windows.Forms.Keys)info.vkCode).ToString(),
                        };
                        var sink = _sink;
                        if (sink != null) { sink(e); }
                    }
                }
                catch
                {
                    // 回调内异常绝不能外泄：它位于系统输入路径上
                    // An exception must never escape the callback: it sits on the system input path
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 抽水消息直到 deadline；低级钩子回调就在 PeekMessage 期间被调用
        ///
        /// Pump messages until the deadline; the low-level hook callback is invoked during PeekMessage
        /// </summary>
        public static void PumpUntil(DateTime deadline)
        {
            var msg = default(MSG);
            while (DateTime.UtcNow < deadline)
            {
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                Thread.Sleep(2);
            }
        }
    }
}
'@
}

$events = [System.Collections.Generic.List[KeyMonitor.KeyEvent]]::new()

function Format-Event([KeyMonitor.KeyEvent]$e) {
    $vk = "0x{0:X2}" -f $e.VkCode
    $dir = if ($e.IsDown) { "DOWN" } else { "UP  " }
    $inj = if ($e.IsInjected) { "INJECTED" } else { "physical" }
    $msg = "t={0} {1} vk={2} {3,-14} scan=0x{4:X2} {5} extra=0x{6:X}" -f $e.Time, $dir, $vk, $e.Name, $e.ScanCode, $inj, $e.ExtraInfo
    return $msg
}

$sink = {
    param($e)
    $events.Add($e)
    if ($Quiet) { return }
    if ($OnlyInjected -and -not $e.IsInjected) { return }
    Write-Host (Format-Event $e)
}

if (-not [KeyMonitor.Hook]::Install($sink)) {
    Write-Host "SetWindowsHookEx 失败，Win32 错误码 $([KeyMonitor.Hook]::LastError)" -ForegroundColor Red
    exit 2
}

Write-Host "键盘钩子已安装（低级 WH_KEYBOARD_LL）。监视 $Seconds 秒…" -ForegroundColor Cyan
if ($OnlyInjected) { Write-Host "只显示注入事件（你的物理敲击会被忽略）。" -ForegroundColor DarkGray }

$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
try {
    [KeyMonitor.Hook]::PumpUntil($deadline)
}
finally {
    [KeyMonitor.Hook]::Uninstall()
    Write-Host ""
    Write-Host "监视结束，共捕获 $($events.Count) 个按键事件。" -ForegroundColor Cyan
}

# 把命令行上传入的虚拟键码文本转成整数：0x7C 与 124 两种写法都接受，空串返回 -1 表示"未指定"
# 动词用 ConvertFrom —— Get-Verb 认可它
# 自造的 Parse-Vk 会触发 PSScriptAnalyzer 的"uses an unapproved verb"（Get-Verb 的动词表里没有 Parse）
#
# Converts the virtual-key text passed on the command line into an integer
# Both 0x7C and 124 are accepted, and an empty string yields -1, meaning "not specified"
# The verb is ConvertFrom, which Get-Verb approves
# A hand-rolled Parse-Vk trips PSScriptAnalyzer's "uses an unapproved verb"
# There is no Parse in Get-Verb's list
function ConvertFrom-VkText([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return -1 }
    if ($Text -match "^0[xX]([0-9a-fA-F]+)$") { return [Convert]::ToInt32($Matches[1], 16) }
    return [int]$Text
}

$exitCode = 0
foreach ($pair in @(@("ExpectDown", (ConvertFrom-VkText $ExpectDown), $true), @("ExpectUp", (ConvertFrom-VkText $ExpectUp), $false))) {
    $label = $pair[0]; $vk = $pair[1]; $wantDown = $pair[2]
    if ($vk -lt 0) { continue }
    $hit = @($events | Where-Object { $_.VkCode -eq $vk -and $_.IsDown -eq $wantDown })
    $dirText = if ($wantDown) { "按下" } else { "抬起" }
    if ($hit.Count -gt 0) {
        $injected = @($hit | Where-Object { $_.IsInjected }).Count
        Write-Host ("  [PASS] {0}：观察到 vk=0x{1:X2} 的{2}：{3} 次，其中带注入标志 {4} 次" -f $label, $vk, $dirText, $hit.Count, $injected) -ForegroundColor Green
        if ($injected -eq 0) {
            Write-Host "         （注意：未带 INJECTED 标志，说明是物理按键而非程序注入）" -ForegroundColor Yellow
        }
    } else {
        Write-Host ("  [FAIL] {0}：未观察到 vk=0x{1:X2} 的{2}" -f $label, $vk, $dirText) -ForegroundColor Red
        $exitCode = 1
    }
}

exit $exitCode