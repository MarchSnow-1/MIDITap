# e2e-common.ps1 — 端到端脚本共用的断言、MIDI 发送与界面读取
#
# 三个库文件各管一件事，谁也不掺和谁
#   e2e-common.ps1  本文件：断言、MIDI 打包与发送、按键状态、界面读取
#   e2e-seed.ps1    现场准备与还原（测试配置、日志种子）
#   e2e-app.ps1     应用生命周期（初始化、启动、就绪、收尾、汇总）
#
# 为什么不写成一个几百行的大文件：改一处要在一大团里找，读的人也看不出哪段属于哪件事
# 为什么做成库而不是复制到每个脚本里：三个用例脚本都要「发 MIDI、读界面、断言」
# 各写一遍的话，改一处就要记得改三处，漏掉的那处会在某个脚本上悄悄失效
#
# 用例脚本只需要点源 e2e-app.ps1，另外两个库由它再点源进来
#
# e2e-common.ps1 — assertions, MIDI sending and UI reading, shared by the end-to-end scripts
#
# The three library files each own one concern, and none of them reaches into another
#   e2e-common.ps1  THIS FILE: assertions, MIDI packing and sending, key state, UI reading
#   e2e-seed.ps1    preparing and restoring the scene (test config, log seed files)
#   e2e-app.ps1     the app lifecycle (initialize, start, readiness, teardown, summary)
#
# Why not one file of several hundred lines: a change means hunting through one big block, and a reader cannot
# tell which part belongs to which concern
# Why a library rather than a copy in each script: all three case scripts send MIDI, read the UI and assert
# Writing that out three times means a change has to be applied in three places, and the one that is missed
# silently breaks that script alone
#
# A case script only dot-sources e2e-app.ps1, which in turn dot-sources the other two

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --------------------------------------------------------------------------- 结果统计 / Result counters

$script:e2ePassed = 0
$script:e2eFailed = 0
$script:e2eFailures = [System.Collections.Generic.List[string]]::new()

function Write-Section([string]$title) {
    Write-Host ""
    Write-Host "=== $title ===" -ForegroundColor Cyan
}

function Assert-True([bool]$condition, [string]$what) {
    if ($condition) {
        $script:e2ePassed++
        Write-Host "  [PASS] $what" -ForegroundColor Green
    } else {
        $script:e2eFailed++
        $script:e2eFailures.Add($what)
        Write-Host "  [FAIL] $what" -ForegroundColor Red
    }
}

# --------------------------------------------------------------------------- Win32 / MIDI P/Invoke 声明 / declarations

function Initialize-E2EMidi {
    if ("MidiTapE2E.Midi" -as [type]) { return }
    Add-Type -Namespace MidiTapE2E -Name Midi -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
public struct IN_CAPS { public ushort wMid; public ushort wPid; public uint vDriverVersion;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szPname; public uint dwSupport; }
// MIDIOUTCAPS 比 MIDIINCAPS 多四个字段：用错结构体会让设备名读成空串
// MIDIOUTCAPS has four more fields than MIDIINCAPS
// Using the wrong struct makes the device name read back as an empty string
[StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
public struct OUT_CAPS { public ushort wMid; public ushort wPid; public uint vDriverVersion;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szPname;
  public ushort wTechnology; public ushort wVoices; public ushort wNotes; public ushort wChannelMask; public uint dwSupport; }

[DllImport("winmm.dll")] public static extern int midiInGetNumDevs();
[DllImport("winmm.dll")] public static extern int midiOutGetNumDevs();
[DllImport("winmm.dll", CharSet=CharSet.Unicode)] public static extern int midiInGetDevCaps(UIntPtr id, ref IN_CAPS caps, int size);
[DllImport("winmm.dll", CharSet=CharSet.Unicode)] public static extern int midiOutGetDevCaps(UIntPtr id, ref OUT_CAPS caps, int size);
[DllImport("winmm.dll")] public static extern uint midiOutOpen(out IntPtr h, uint id, IntPtr cb, IntPtr inst, uint flags);
[DllImport("winmm.dll")] public static extern uint midiOutShortMsg(IntPtr h, uint msg);
[DllImport("winmm.dll")] public static extern uint midiOutClose(IntPtr h);

[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

public static int FindInput(string name) {
  for (int i = 0; i < midiInGetNumDevs(); i++) {
    IN_CAPS c = default(IN_CAPS);
    if (midiInGetDevCaps((UIntPtr)i, ref c, Marshal.SizeOf(typeof(IN_CAPS))) == 0 && c.szPname != null
        && string.Equals(c.szPname.Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
  }
  return -1;
}
public static int FindOutput(string name) {
  for (int i = 0; i < midiOutGetNumDevs(); i++) {
    OUT_CAPS c = default(OUT_CAPS);
    if (midiOutGetDevCaps((UIntPtr)i, ref c, Marshal.SizeOf(typeof(OUT_CAPS))) == 0 && c.szPname != null
        && string.Equals(c.szPname.Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
  }
  return -1;
}
public static bool IsDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
'@
}

# --------------------------------------------------------------------------- 发送 MIDI / sending MIDI

# 测试键位：F13~F17。选它们是因为几乎不被任何程序绑定，注入不会干扰用户窗口
#
# Test keys: F13~F17. They are chosen because almost no program binds them
# So the injected keys cannot disturb the user's window
$script:e2eVK = @{ F13 = 0x7C; F14 = 0x7D; F15 = 0x7E; F16 = 0x7F; F17 = 0x80 }

# 记录本次测试中确实观察到按下过的键位，用途是排除假通过
# 断言「应抬起」时，如果这个键从未按下过，那么「现在没按下」恒为真，测试会通过却什么都没验证
#
# Tracks which keys were actually observed down, which exists to rule out a FALSE PASS
# When asserting "should be up", a key that was never down satisfies it trivially: the test passes while
# verifying nothing
$script:e2eObservedDown = @{}

$script:e2eMidiHandle = [IntPtr]::Zero

function Send-Midi([byte]$status, [byte]$d1, [byte]$d2) {
    # **必须显式转成 int 再做位移。**
    # PowerShell 的 -shl 按左操作数的类型决定结果宽度
    # 参数声明为 [byte] 时，60 -shl 8 会因为超出 byte 范围而截断成 0
    # 这样打包出来的是 0x90 00 00，即音符 0 的抬起，应用收到后不注入任何按键
    # The operands MUST be widened to int before shifting
    # PowerShell's -shl uses the left operand's type for the result width
    # With a [byte] parameter 60 -shl 8 truncates to 0 (out of byte range)
    # That produced 0x90 00 00 — a note-off for note 0 — so the app injected nothing
    $packed = [uint32]([int]$status -bor ([int]$d1 -shl 8) -bor ([int]$d2 -shl 16))
    [void][MidiTapE2E.Midi]::midiOutShortMsg($script:e2eMidiHandle, $packed)
}

# 自检：打包结果必须能被还原回原字节，否则测试刺激本身就是错的
# 在发送任何消息之前先验证一次的代价极低，却能避免「整轮测试都发错消息」这种最坏情况
#
# Self-check: the packed word must decompose back to the original bytes, otherwise the stimulus itself is wrong
# Verifying this once before sending anything is cheap insurance against the worst case: an entire run that sent
# malformed messages
function Assert-Packing([byte]$status, [byte]$d1, [byte]$d2) {
    $packed = [uint32]([int]$status -bor ([int]$d1 -shl 8) -bor ([int]$d2 -shl 16))
    $ok = (($packed -band 0xFF) -eq $status) -and
          ((($packed -shr 8) -band 0xFF) -eq $d1) -and
          ((($packed -shr 16) -band 0xFF) -eq $d2)
    if (-not $ok) { throw "MIDI packing self-check failed: $status $d1 $d2 -> 0x$('{0:X8}' -f $packed)" }
}

function Send-NoteOn([byte]$note, [byte]$velocity = 100, [byte]$channel = 0) { Send-Midi ([byte](0x90 -bor $channel)) $note $velocity }
function Send-NoteOff([byte]$note, [byte]$channel = 0) { Send-Midi ([byte](0x80 -bor $channel)) $note 0 }

function Wait-KeyState([int]$vk, [bool]$down, [int]$timeoutMs = 1500) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([MidiTapE2E.Midi]::IsDown($vk) -eq $down) { return $true }
        Start-Sleep -Milliseconds 25
    }
    return $false
}

function Assert-Key([bool]$down, [string]$name, [string]$what) {
    $actual = Wait-KeyState $script:e2eVK[$name] $down
    if ($actual -and $down) {
        $script:e2eObservedDown[$name] = $true
    }
    if ($actual -and -not $down -and -not $script:e2eObservedDown[$name]) {
        Assert-True $false "$what (false pass: $name was never seen down, so its release is unproven)"
        return
    }
    Assert-True $actual "$what ($name should be $(if ($down) { 'down' } else { 'up' }))"
}

# --------------------------------------------------------------------------- UI Automation 辅助 / UI Automation helpers

function Find-ById([string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($script:e2eAE::AutomationIdProperty, $id)
    return $script:e2eWindow.FindFirst($script:e2eTS::Descendants, $c)
}

# 日志在界面上是**一整块文本**（LogText），不是一个逐行的列表
# 早先这里读的是 LogList —— LogPage.xaml 里并没有这个元素
# 于是本函数永远返回空数组，脚本从写下起就没有真正跑通过（等待就绪必然超时）
#
# The log is ONE block of text on screen (LogText), not a row-by-row list
# This used to read LogList, an element LogPage.xaml does not contain
# The function therefore always returned an empty array, and the script never actually ran: waiting for
# readiness was bound to time out
function Read-LogRows {
    $tb = Find-ById 'LogText'
    if (-not $tb) { return @() }
    $text = $tb.Current.Name
    if (-not $text) { return @() }
    return @($text -split "\r?\n" | Where-Object { $_.Trim() })
}

function Select-NavItem([string]$id) {
    $item = Find-ById $id
    if ($item) {
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 1200
    }
}

<#
  等待应用确实开始监听指定端口
  不能靠固定 sleep 猜时间
  切换端口要先关掉旧句柄再打开新的，耗时不定（实测约 2~7 秒，取决于端口数量）
  若在就绪前发送，消息会被直接丢弃，测试随后以一堆「应按下却失败」的形式报错
  那看起来像应用 bug，其实是测试时序

  判定必须与界面语言无关
  加一种语言就要改测试是错的设计
  因此只匹配「日志里同时出现端口名与端口号」——无论哪种语言，这条日志都必然包含这两个值

  Waits for the app to actually start monitoring the given port
  A fixed sleep cannot be trusted
  Switching ports closes the old handle before opening the new one, and the cost varies with port count
  (measured ~2-7s)
  Sending before it is ready drops messages silently, which surfaces later as a wall of "expected key down"
  failures that look like an app bug rather than a test-sequencing bug

  The check is deliberately LANGUAGE-INDEPENDENT
  Requiring a test edit per language is the wrong design
  It matches only "the port name AND the port number appear in the same log row", which that line always
  contains in every language
#>
function Wait-MonitoringOnPort([string]$name, [int]$portIndex, [int]$timeoutMs = 20000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($row in (Read-LogRows)) {
            if ($row -match [regex]::Escape($name) -and $row -match "\b$portIndex\b") { return $true }
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}
