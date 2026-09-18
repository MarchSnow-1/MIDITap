<#
.SYNOPSIS
  MIDITap 端到端测试：向虚拟 MIDI 端口发送真实数据，验证应用确实注入了按键

  MIDITap end-to-end test: send real data to a virtual MIDI port
  Verify that the app really injected the key presses

.DESCRIPTION
  与单元/集成测试的分工
    * Core.Tests        喂构造好的字节，验证状态机逻辑
    * Integration.Tests 经真实驱动往返字节，验证驱动层与状态机一致
    * **本脚本**        驱动**真实应用进程**，验证从 MIDI 到系统按键注入的整条链路

  验证手段刻意选了两条互相独立的证据
    1) GetAsyncKeyState —— 直接问操作系统"这个虚拟键现在是否被按下"
       这是端到端最强证据：它证明按键真的被注入到了系统输入层
       而不是"应用以为它按了"
       测试键位用 F13~F17：几乎不被任何程序绑定
       因此注入不会干扰用户正在使用的窗口
    2) UI Automation —— 读取应用界面文字（日志行与实时 chip）
       证明事件确实反映到了 UI

  测试配置写在临时文件里，结束时**必定还原**（try/finally），不污染用户配置

  How this divides the work with the unit and integration tests
    * Core.Tests        feed constructed bytes and verify the state-machine logic
    * Integration.Tests round-trip bytes through a real driver
                        It verifies that the driver layer agrees with the state machine
    * THIS SCRIPT       drives the REAL APPLICATION PROCESS
                        It verifies the whole chain from MIDI to system key injection

  Two deliberately independent pieces of evidence are used:
    1) GetAsyncKeyState — ask the operating system directly whether the virtual key is currently held down
       This is the strongest end-to-end evidence
       It proves the key really reached the system input layer
       It is not just the app believing it pressed something
       The test keys are F13~F17, which almost nothing binds
       So the injection does not disturb the window the user is using
    2) UI Automation — read the app's own text (log rows and the live chips)
       That proves the events really surfaced in the UI

  The test config is written to a temporary file and ALWAYS restored at the end (try/finally)
  So user config is never polluted

.PARAMETER PortName
  虚拟回环端口名（输入与输出同名）。默认 MIDITap-TestConfig

  Name of the virtual loopback port (the input and output ports share the name)
  Defaults to MIDITap-TestConfig

.EXAMPLE
  pwsh scripts/e2e-midi.ps1
  pwsh scripts/e2e-midi.ps1 -SkipBuild -PortName "loopMIDI Port"
#>
[CmdletBinding()]
param(
    [string]$PortName = "MIDITap-TestConfig",
    [string]$AppDir = "src/MIDITap.App/bin/x64/Debug/net10.0-windows10.0.19041.0",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

# --------------------------------------------------------------------------- 输出与断言 / Output and assertions

$script:passed = 0
$script:failed = 0
$script:failures = [System.Collections.Generic.List[string]]::new()

function Write-Section([string]$title) {
    Write-Host ""
    Write-Host "=== $title ===" -ForegroundColor Cyan
}
function Assert-True([bool]$condition, [string]$what) {
    if ($condition) {
        $script:passed++
        Write-Host "  [PASS] $what" -ForegroundColor Green
    } else {
        $script:failed++
        $script:failures.Add($what)
        Write-Host "  [FAIL] $what" -ForegroundColor Red
    }
}

# --------------------------------------------------------------------------- Win32 / MIDI P/Invoke 声明 / declarations

if (-not ("MidiTapE2E.Midi" -as [type])) {
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

$inputIndex = [MidiTapE2E.Midi]::FindInput($PortName)
$outputIndex = [MidiTapE2E.Midi]::FindOutput($PortName)
if ($inputIndex -lt 0 -or $outputIndex -lt 0) {
    Write-Host "MIDI 回环端口 '$PortName' 不存在（input=$inputIndex, output=$outputIndex）。" -ForegroundColor Yellow
    Write-Host "请用 loopMIDI 创建同名端口，或用 -PortName 指定已有端口。" -ForegroundColor Yellow
    Pop-Location
    exit 2
}
Write-Host "回环端口 '$PortName'：input=$inputIndex output=$outputIndex" -ForegroundColor DarkGray

$midiHandle = [IntPtr]::Zero
if ([MidiTapE2E.Midi]::midiOutOpen([ref]$midiHandle, [uint32]$outputIndex, [IntPtr]::Zero, [IntPtr]::Zero, 0) -ne 0) {
    Write-Host "midiOutOpen 失败。" -ForegroundColor Red
    Pop-Location
    exit 2
}

function Send-Midi([byte]$status, [byte]$d1, [byte]$d2) {
    # **必须显式转成 int 再做位移。**
    # PowerShell 的 -shl 按左操作数的类型决定结果宽度
    # 参数声明为 [byte] 时，60 -shl 8 会因为超出 byte 范围而**截断成 0**
    # 这样打包出来的是 0x90 00 00，即"音符 0 的抬起"，应用收到后不注入任何按键
    # The operands MUST be widened to int before shifting
    # PowerShell's -shl uses the left operand's type for the result width
    # With a [byte] parameter 60 -shl 8 truncates to 0 (out of byte range)
    # That produced 0x90 00 00 — a note-off for note 0 — so the app injected nothing
    $packed = [uint32]([int]$status -bor ([int]$d1 -shl 8) -bor ([int]$d2 -shl 16))
    [void][MidiTapE2E.Midi]::midiOutShortMsg($midiHandle, $packed)
}

# 自检：打包结果必须能被还原回原字节，否则测试刺激本身就是错的
# 在发送任何消息之前先验证一次的代价极低，却能避免"整轮测试都发错消息"这种最坏情况
# Self-check: the packed word must decompose back to the original bytes
# Otherwise the stimulus itself is wrong
# Verifying this once before sending anything is cheap insurance
# It guards against the worst case — an entire run that sent malformed messages
function Assert-Packing([byte]$status, [byte]$d1, [byte]$d2) {
    $packed = [uint32]([int]$status -bor ([int]$d1 -shl 8) -bor ([int]$d2 -shl 16))
    $ok = (($packed -band 0xFF) -eq $status) -and
          ((($packed -shr 8) -band 0xFF) -eq $d1) -and
          ((($packed -shr 16) -band 0xFF) -eq $d2)
    if (-not $ok) { throw "MIDI 打包自检失败：$status $d1 $d2 -> 0x$('{0:X8}' -f $packed)" }
}
Assert-Packing 0x90 60 100
Assert-Packing 0x90 0 100
Assert-Packing 0x90 127 100
Assert-Packing 0x80 60 0
function Send-NoteOn([byte]$note, [byte]$velocity = 100, [byte]$channel = 0) { Send-Midi ([byte](0x90 -bor $channel)) $note $velocity }
function Send-NoteOff([byte]$note, [byte]$channel = 0) { Send-Midi ([byte](0x80 -bor $channel)) $note 0 }

# 测试键位：F13~F17。选它们是因为几乎不被任何程序绑定，注入不会干扰用户窗口
#
# Test keys: F13~F17. They are chosen because almost no program binds them
# So the injected keys cannot disturb the user's window
$VK = @{ F13 = 0x7C; F14 = 0x7D; F15 = 0x7E; F16 = 0x7F; F17 = 0x80 }

function Wait-KeyState([int]$vk, [bool]$down, [int]$timeoutMs = 1500) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([MidiTapE2E.Midi]::IsDown($vk) -eq $down) { return $true }
        Start-Sleep -Milliseconds 25
    }
    return $false
}
# 记录本次测试中"确实观察到按下过"的键位
# 用途是排除**假通过**
# 断言"应抬起"时，如果这个键从未按下过，那么"现在没按下"恒为真
# 测试会通过，但其实什么都没验证
# Tracks which keys were actually observed down
# This exists to rule out a FALSE PASS
# When asserting "should be up", a key that was never down satisfies it trivially
# The test passes while verifying nothing
$script:observedDown = @{}

function Assert-Key([bool]$down, [string]$name, [string]$what) {
    $actual = Wait-KeyState $VK[$name] $down
    if ($actual -and $down) {
        $script:observedDown[$name] = $true
    }
    if ($actual -and -not $down -and -not $script:observedDown[$name]) {
        Assert-True $false "$what（假通过：全程未观察到 $name 按下过，无法证明抬起）"
        return
    }
    Assert-True $actual "$what（$name 应$(if ($down) { '按下' } else { '抬起' })）"
}

# --------------------------------------------------------------------------- 测试配置 / Test config

# 用脚本自己的配置名，而不是默认配置名：后者跟随界面语言，脚本无从预知
#
# A config name of the script's own rather than the default one, which follows the UI language and cannot be predicted here
#
# 取**绝对路径**：last_config 里存相对路径时，应用会把它拼到 config/ 之下
# （ResolveConfigPath 对非绝对路径就是这么做的），于是指向一个不存在的文件
# 后果很隐蔽：last_config 解析成 null，应用退回去加载默认配置（那份是空的），
# 测试随后以一堆"应按下却未绑定"的形式失败 —— 看起来像应用坏了
#
# An ABSOLUTE path is taken: with a relative one in last_config, the app joins it onto config/
# (that is what ResolveConfigPath does with a non-rooted path) and points at a file that is not there
# The failure is subtle: last_config resolves to null, the app falls back to the default config (an empty one),
# and the test then fails as a wall of "expected a mapping but found none" -- which looks like a broken app
$configPath = [System.IO.Path]::GetFullPath((Join-Path $AppDir "config/e2e-config.json"))
$lastConfigPath = Join-Path $AppDir ".storage/last_config"
$backup = $null
if (Test-Path $configPath) { $backup = Get-Content $configPath -Raw }
$lastBackup = if (Test-Path $lastConfigPath) { Get-Content $lastConfigPath -Raw } else { $null }

$testConfig = @'
{
  // E2E 测试配置（脚本自动写入，结束时还原）/ E2E test config: written by the script, restored when it ends
  "60": "f13",          // C4  -> f13
  "0": "f14",           // 下边界音符 / lowest boundary note
  "127": "f15",         // 上边界音符 / highest boundary note
  "61": "f13",          // 与 60 共用同一虚拟键 -> 测引用计数 / shares a virtual key with 60 -> tests reference counting
  "62": "f16+f17"       // 组合键 -> 测按下/抬起顺序 / combo key -> tests press/release order
}
'@
New-Item -ItemType Directory -Force -Path (Split-Path $configPath) | Out-Null
Set-Content -Path $configPath -Value $testConfig -NoNewline
# 把"上次配置"记录指向刚写的那份，启动时必定加载它
# 早先的做法是删掉这条记录再依赖"随便挑一份能读的"，那要求目录里只有这一份配置
# 而应用首次启动会自己生成一份默认配置，因此那条路不再确定
#
# Point the "last config" record at the file just written, so start-up loads it for certain
# The earlier approach deleted the record and relied on picking any readable config,
# which assumed this was the only one in the directory
# The app now creates a default config on first launch, so that route is no longer deterministic
New-Item -ItemType Directory -Force -Path (Split-Path $lastConfigPath) | Out-Null
Set-Content -Path $lastConfigPath -Value $configPath -NoNewline

# --------------------------------------------------------------------------- 日志整理用的种子文件 / Seed files for the housekeeping test

# 这一段必须在应用**启动之前**放好：整理发生在启动时，启动后再放就赶不上这一轮
# 种子分两类：上一版的旧格式（miditap.log 与 .log.1），以及「昨天」的两份明文会话
# 期望结果：旧格式被删，昨天的两份会话直接并成 miditap-<昨天>.tar.gz，中途不产生任何会话级的压缩文件
#
# This has to be in place BEFORE the app starts: housekeeping runs at start-up, so seeding afterwards misses this round
# Two kinds of seed: the previous version's legacy shapes (miditap.log and .log.1),
# and two plain-text sessions dated yesterday
# Expected: the legacy shapes are removed, and yesterday's two sessions are merged straight into
# miditap-<yesterday>.tar.gz with no per-session compression in between

$logDir = Join-Path $AppDir ".storage/logs"
$logSettingPath = Join-Path $AppDir ".storage/miditap_log_to_file"
$logFilesBefore = @()
if (Test-Path $logDir) { $logFilesBefore = @(Get-ChildItem $logDir -File | ForEach-Object { $_.Name }) }

# 落盘开关先置为关闭，这样第 14 节在界面上打开它时必然产生一份新文件，时序才是确定的
# 原值在 finally 里还原
#
# The log-to-file switch is forced off first, which makes section 14 deterministic:
# turning it on in the UI must then create a brand new file
# The original value is restored in the finally block
$logSettingBackup = if (Test-Path $logSettingPath) { Get-Content $logSettingPath -Raw } else { $null }

$yesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
$today = (Get-Date).ToString("yyyy-MM-dd")

# 本脚本会碰到的确切文件名：先整份备份，结束时原样放回
# 用文件复制而不是读成字符串：.gz 与 .tar.gz 是二进制，读成文本会坏掉
#
# The exact names this script touches: each is backed up whole and put back at the end
# A file copy is used rather than reading text, because .gz and .tar.gz are binary and reading them as text corrupts them
$logTouchNames = @(
    "miditap.log",
    "miditap.log.1",
    "miditap-$yesterday-1.log",
    "miditap-$yesterday-2.log",
    "miditap-$yesterday.tar.gz"
)
$logBackupDir = Join-Path $env:TEMP ("miditap-e2e-logbackup-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $logBackupDir | Out-Null
foreach ($name in $logTouchNames) {
    $seedSource = Join-Path $logDir $name
    if (Test-Path $seedSource) { Copy-Item $seedSource (Join-Path $logBackupDir $name) -Force }
}

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Set-Content -Path $logSettingPath -Value "0" -NoNewline
Set-Content -Path (Join-Path $logDir "miditap.log") -Value "legacy line" -NoNewline
Set-Content -Path (Join-Path $logDir "miditap.log.1") -Value "legacy rotated line" -NoNewline
Set-Content -Path (Join-Path $logDir "miditap-$yesterday-1.log") -Value "seeded first" -NoNewline
Set-Content -Path (Join-Path $logDir "miditap-$yesterday-2.log") -Value "seeded second" -NoNewline

# --------------------------------------------------------------------------- 启动应用 / Launch the app

$appExe = Join-Path $AppDir "MIDITap.exe"
$app = $null
try {
    if (-not $SkipBuild) {
        Write-Host "构建应用…" -ForegroundColor DarkGray
        dotnet build src/MIDITap.App/MIDITap.App.csproj -c Debug 2>&1 | Select-Object -Last 1 | Out-Null
    }

    Get-Process MIDITap -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    Write-Host "启动 $appExe" -ForegroundColor DarkGray
    $app = Start-Process -FilePath $appExe -WorkingDirectory (Resolve-Path $AppDir) -PassThru
    Start-Sleep -Seconds 6

    if ($app.HasExited) { throw "应用启动即退出（exit code $($app.ExitCode)）" }

    # ---- UI Automation：选择我们的端口 + 读取界面文字 / select our port and read UI text ----
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $AE = [System.Windows.Automation.AutomationElement]
    $TS = [System.Windows.Automation.TreeScope]
    $win = $AE::RootElement.FindFirst($TS::Children,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)))
    if (-not $win) { throw "找不到应用窗口" }

    function Find-ById([string]$id) {
        $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
        return $win.FindFirst($TS::Descendants, $c)
    }
    # 日志在界面上是**一整块文本**（LogText），不是一个逐行的列表
    # 早先这里读的是 LogList —— LogPage.xaml 里并没有这个元素
    # 于是本函数永远返回空数组，脚本从写下起就没有真正跑通过（等待就绪必然超时）
    #
    # The log is ONE block of text on screen (LogText), not a row-by-row list
    # This used to read LogList, an element LogPage.xaml does not contain
    # The function therefore always returned an empty array, and the script never actually ran
    # Waiting for readiness was bound to time out
    function Read-LogRows {
        $tb = Find-ById 'LogText'
        if (-not $tb) { return @() }
        $text = $tb.Current.Name
        if (-not $text) { return @() }
        return @($text -split "`r?`n" | Where-Object { $_.Trim() })
    }

    function Select-NavItem([string]$id) {
        $item = Find-ById $id
        if ($item) {
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 1200
        }
    }

    <#
      等待应用**确实开始监听**指定端口
      不能靠固定 sleep 猜时间
      切换端口要先关掉旧句柄再打开新的，耗时不定（实测约 2~7 秒，取决于端口数量）
      若在就绪前发送，消息会被直接丢弃
      测试随后以一堆"应按下却失败"的形式报错 —— 看起来像应用 bug，其实是测试时序

      **判定必须与界面语言无关**
      加语言就要改测试是错的设计
      因此改为只匹配"日志里同时出现端口名与端口号" —— 无论哪种语言，这条日志都必然包含这两个值
      Waits for the app to actually start monitoring the given port
      A fixed sleep cannot be trusted
      Switching ports closes the old handle before opening the new one
      The cost varies with port count (measured ~2-7s)
      Sending before it is ready drops messages silently
      That surfaces later as a wall of "expected key down" failures
      Those look like an app bug rather than a test-sequencing bug

      The check is deliberately LANGUAGE-INDEPENDENT
      Requiring a test edit per language is the wrong design
      So it now matches only "the port name AND the port number appear in the same log row"
      That log line always contains both, in every language
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

    # 不能叫 $home：PowerShell 的 $HOME 是只读内置变量，赋值会抛错并静默中断整个脚本
    # Do NOT name this $home: PowerShell's $HOME is a read-only built-in
    # Assigning to it throws, silently aborting the script
    $navHomeItem = Find-ById 'NavHome'
    if ($navHomeItem) { $navHomeItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1 }

    # 先到日志页：切端口的就绪判定依赖日志内容
#
# Go to the log page first: the readiness check for a port switch reads the log content
    Select-NavItem 'NavLog'

    Write-Section "0. 准备：切换到回环端口并等待就绪"
    $deviceBox = Find-ById 'DeviceBox'
    if (-not $deviceBox) { Select-NavItem 'NavHome'; $deviceBox = Find-ById 'DeviceBox' }
    Assert-True ($null -ne $deviceBox) "找到设备下拉框"
    if ($deviceBox) {
        $deviceBox.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 700
        $itemCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $target = $null
        foreach ($it in $deviceBox.FindAll($TS::Descendants, $itemCond)) {
            if ($it.Current.Name -eq $PortName) { $target = $it }
        }
        Assert-True ($null -ne $target) "下拉框里找到 '$PortName'"
        if ($target) {
            $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 500
        }
        # 折叠下拉框，避免它挡住后续 UIA 查询
#
# Collapse the drop-down so it does not block the later UIA queries
        try { $deviceBox.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
    }

    Select-NavItem 'NavLog'
    $ready = Wait-MonitoringOnPort $PortName $inputIndex
    Assert-True $ready "应用已开始监听 '$PortName'（等到确定性信号，而非固定等待）"
    if (-not $ready) { throw "应用未在超时内开始监听 '$PortName'，后续断言无意义" }

    # 清空日志：后续按"新出现的行"断言，避免被启动期的记录干扰
#
# Clear the log: later assertions are about newly appearing rows
# Start-up records cannot interfere with them
    $clearBtn = Find-ById 'ClearBtn'
    if ($clearBtn) { $clearBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 800 }
    # 必须再包一层 @()
    # Read-LogRows 返回数组，但元素为 0/1 个时 PowerShell 会把它解包掉
    # 直接取 .Count 会在 StrictMode 下抛错
    #
    # The extra @() is required: Read-LogRows returns an array
    # PowerShell unwraps it when it holds 0 or 1 element
    # Reading .Count directly then throws under StrictMode
    Assert-True (@(Read-LogRows).Count -eq 0) "日志已清空"

    # ------------------------------------------------------------------ 用例 / Test cases

    Write-Section "1. 基本按下/抬起：MIDI note-on 真的注入了按键"
    Send-NoteOn 60 100
    Assert-Key $true 'F13' "note 60 按下"
    Send-NoteOff 60
    Assert-Key $false 'F13' "note 60 抬起"

    Write-Section "2. 边界音符 0 与 127"
    Send-NoteOn 0 100
    Assert-Key $true 'F14' "note 0（下边界）按下"
    Send-NoteOff 0
    Assert-Key $false 'F14' "note 0 抬起"
    Send-NoteOn 127 100
    Assert-Key $true 'F15' "note 127（上边界）按下"
    Send-NoteOff 127
    Assert-Key $false 'F15' "note 127 抬起"

    Write-Section "3. velocity 0 的 note-on 等价于 note-off"
    Send-NoteOn 60 100
    Assert-Key $true 'F13' "先按下"
    Send-Midi 0x90 60 0
    Assert-Key $false 'F13' "0x90 velocity 0 后松开（不卡键）"

    Write-Section "4. 重复按下：不得重复注入，且一次抬起即释放"
    Send-NoteOn 60 100
    Assert-Key $true 'F13' "按下"
    Send-NoteOn 60 100
    Start-Sleep -Milliseconds 200
    Assert-True ([MidiTapE2E.Midi]::IsDown($VK['F13'])) "重复按下后仍处于按下（未额外注入）"
    Send-NoteOff 60
    Assert-Key $false 'F13' "单次抬起即释放（引用计数未泄漏）"

    Write-Section "5. 两个音符共用同一虚拟键：引用计数"
    Send-NoteOn 60; Send-NoteOn 61
    Start-Sleep -Milliseconds 300
    Assert-True ([MidiTapE2E.Midi]::IsDown($VK['F13'])) "两个音符按下后 F13 按下"
    Send-NoteOff 60
    Start-Sleep -Milliseconds 300
    Assert-True ([MidiTapE2E.Midi]::IsDown($VK['F13'])) "只松开一个音符时 F13 仍按住"
    Send-NoteOff 61
    Assert-Key $false 'F13' "两个都松开后 F13 抬起"

    Write-Section "6. 组合键：同时按下、成对抬起"
    Send-NoteOn 62
    Start-Sleep -Milliseconds 400
    $bothDown = [MidiTapE2E.Midi]::IsDown($VK['F16']) -and [MidiTapE2E.Midi]::IsDown($VK['F17'])
    if ($bothDown) { $script:observedDown['F16'] = $true; $script:observedDown['F17'] = $true }
    Assert-True $bothDown "组合键 F16+F17 均按下"
    Send-NoteOff 62
    Start-Sleep -Milliseconds 400
    $bothUp = (-not [MidiTapE2E.Midi]::IsDown($VK['F16'])) -and (-not [MidiTapE2E.Midi]::IsDown($VK['F17']))
    # 与 Assert-Key 同理：没按下过就说不上"抬起"
    #
    # Same reasoning as Assert-Key: a key that was never down cannot be said to have come up
    Assert-True ($bothUp -and $script:observedDown.ContainsKey('F16') -and $script:observedDown.ContainsKey('F17')) "组合键 F16+F17 均抬起"

    Write-Section "7. 未映射音符：只广播、不注入"
    $before = @($VK.Values | Where-Object { [MidiTapE2E.Midi]::IsDown($_) })
    Send-NoteOn 63 100
    Start-Sleep -Milliseconds 400
    $after = @($VK.Values | Where-Object { [MidiTapE2E.Midi]::IsDown($_) })
    Assert-True ($before.Count -eq 0 -and $after.Count -eq 0) "未映射音符 63 未注入任何测试键位"
    Send-NoteOff 63

    Write-Section "8. 非音符消息：不注入、不崩溃"
    Send-Midi 0xB0 7 127    # CC
    Send-Midi 0xE0 0 64     # Pitch bend
    Send-Midi 0xA0 60 70    # Poly aftertouch
    Send-Midi 0xC0 42 0     # Program change
    Send-Midi 0xD0 100 0    # Channel pressure
    Start-Sleep -Milliseconds 500
    $anyDown = @($VK.Values | Where-Object { [MidiTapE2E.Midi]::IsDown($_) })
    Assert-True ($anyDown.Count -eq 0) "非音符消息未触发任何按键"
    Assert-True (-not $app.HasExited) "处理非音符消息后应用仍存活"

    Write-Section "9. 突发：全部映射音符按下再抬起，不留卡键"
    $mapped = @(0, 60, 61, 62, 127)
    foreach ($n in $mapped) { Send-NoteOn ([byte]$n) }
    Start-Sleep -Milliseconds 600
    foreach ($n in $mapped) { Send-NoteOff ([byte]$n) }
    Start-Sleep -Milliseconds 800
    $stuck = @($VK.Keys | Where-Object { [MidiTapE2E.Midi]::IsDown($VK[$_]) })
    Assert-True ($stuck.Count -eq 0) "突发后无卡键（残留：$(($stuck -join ', '))）"

    Write-Section "10. 日志页记录"
    Send-NoteOn 60 100
    Start-Sleep -Milliseconds 900
    $rows = Read-LogRows
    $noteRow = $rows | Where-Object { $_ -match '\b60\b' -and $_ -match 'f13' } | Select-Object -First 1
    Assert-True ($null -ne $noteRow) "日志页出现 note 60 -> f13 的记录"
    Send-NoteOff 60
    Start-Sleep -Milliseconds 400

    Write-Section "11. 主页实时预览"
    # 实时预览在**主页内**测：用户在主页演奏时实时看到触发了什么
    # "按住琴键时切走再切回"另有第 12 节专门覆盖，不在这里重复
    #
    # The live preview is exercised from WITHIN the home page: playing there shows what was triggered, live
    # The "leave while keys are held, then come back" case has its own section, 12, and is not repeated here
    Select-NavItem 'NavHome'
    Send-NoteOn 60 100
    Start-Sleep -Milliseconds 900

    # chip 容器是 ItemsControl
    # 它有 AutomationProperties.Name（由代码同步为激活键位汇总）
    # 无激活音符时该名称为空、元素不进 UIA 树
    # 因此必须在发送之后查询
    # The chip container is an ItemsControl
    # Its AutomationProperties.Name is synced by code to the active key labels
    # With no active notes the name is empty and the element is absent from the UIA tree
    # So it must be queried *after* sending
    $chipHost = Find-ById 'ActiveNotesItems'
    $chipText = if ($chipHost) { $chipHost.Current.Name } else { '' }
    Assert-True ($chipText -match 'f13') "主页实时预览显示映射键位 f13（实际：'$chipText'）"
    # 顺带验证无障碍：读屏用户拿到的就是这段文本（chip 本身是纯视觉的）
    #
    # This doubles as an accessibility check: this is exactly the text a screen-reader user gets
    # (the chips themselves are purely visual)
    Assert-True ($chipText -match 'C4') "实时预览文本含音名 C4（实际：'$chipText'）"

    Send-NoteOn 61 100
    Start-Sleep -Milliseconds 600
    $multi = (Find-ById 'ActiveNotesItems').Current.Name
    Assert-True ($multi -match 'C4' -and $multi -match 'C#4') "两个音符同时触发时都列出（实际：'$multi'）"

    Send-NoteOff 60
    Send-NoteOff 61
    Start-Sleep -Milliseconds 700
    $after = Find-ById 'ActiveNotesItems'
    Assert-True ($null -eq $after -or $after.Current.Name -eq '') "全部抬起后实时预览清空"
    Assert-Key $false 'F13' "收尾抬起"

    Write-Section "12. 切页后仍被按住的音符会补回主页"
    # 主页按导航重建（NavigationCacheMode=Disabled），站在别的页面时按下的音符它收不到 NoteOn
    # 因此加载时重放一份"仍被按住"的快照
    # 不重放的话这些音符要等到抬起那一刻才第一次出现，而抬起只负责把它们移除，等于从未显示过
    #
    # The home page is rebuilt on navigation (NavigationCacheMode=Disabled)
    # Presses made while another page is shown raise no NoteOn it can hear
    # So a snapshot of the notes still held is replayed when it loads
    # Without that replay they first appear at the moment of release, and the release only removes them, so they are never shown
    Select-NavItem 'NavHome'
    Send-NoteOn 60 100
    Send-NoteOff 60
    Start-Sleep -Milliseconds 400

    # 站到日志页再按下：这一次主页收不到 NoteOn
    #
    # Move to the log page and press there: this time the home page hears no NoteOn
    Select-NavItem 'NavLog'
    Send-NoteOn 60 100
    Start-Sleep -Milliseconds 500
    Select-NavItem 'NavHome'
    Start-Sleep -Milliseconds 1200
    $restored = Find-ById 'ActiveNotesItems'
    $restoredText = if ($restored) { $restored.Current.Name } else { '' }
    Assert-True ($restoredText -match 'f13') "切回主页后显示仍被按住的音符（实际：'$restoredText'）"
    Assert-Key $true 'F13' "切回主页时该键仍处于按下"

    Send-NoteOff 60
    Start-Sleep -Milliseconds 700
    $cleared = Find-ById 'ActiveNotesItems'
    Assert-True ($null -eq $cleared -or $cleared.Current.Name -eq '') "抬起后主页清空"

    Write-Section "14. 日志落盘：开关一打开就出现会话文件"
    # 启动前已把落盘开关置为关闭（见脚本开头的种子部分）
    # 因此在界面上打开它之后，必定出现一份本次会话的新文件
    #
    # The switch was forced off before start-up (see the seeding at the top of the script)
    # Turning it on in the UI must therefore produce a brand new file for this session
    Select-NavItem 'NavSettings'
    Start-Sleep -Milliseconds 1000
    $logToggle = Find-ById 'LogFileToggle'
    Assert-True ($null -ne $logToggle) "找到日志落盘开关"
    if ($logToggle) {
        $togglePattern = $logToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($togglePattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $togglePattern.Toggle() }
    }

    # 等到一份**启动时不存在**的今天的会话文件
    # 用「新出现的名字」而不是「最新的文件」来判定：后者在开关本来就开着时无法区分是不是本次新建的
    #
    # Waits for a today-session file that did NOT exist at start-up
    # A newly appearing name is used rather than the newest file: the latter cannot tell whether it was just created
    # when the switch was already on
    $sessionFile = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline -and -not $sessionFile) {
        $sessionFile = Get-ChildItem $logDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "^miditap-$today-[0-9]+[.]log$" -and $logFilesBefore -notcontains $_.Name } |
            Sort-Object Name | Select-Object -Last 1
        if (-not $sessionFile) { Start-Sleep -Milliseconds 300 }
    }
    Assert-True ($null -ne $sessionFile) "开关打开后出现本次会话的日志文件"
    if ($sessionFile) {
        # 名字里的编号是「当天第几次启动」，只增不减
        #
        # The number in the name is which launch of the day this was, and it only grows
        Assert-True ($sessionFile.Name -match "^miditap-$today-[0-9]+[.]log$") ("文件名为 miditap-<日期>-<编号>.log（实际：" + $sessionFile.Name + "）")
        $sessionText = Get-Content $sessionFile.FullName -Raw
        Assert-True ($sessionText -match "===== MIDITap") "会话头已写入文件"
        Assert-True ($sessionText -match "session [0-9]+") "会话头含当天第几次启动"
        Assert-True ($sessionText -match "logging enabled") "记录了「日志已开启」"

        # 这一条把「MIDI 回调 → 日志服务 → 落盘」整条链串起来
        # 只验文件名与文件头的话，写入器坏了也照样通过
        #
        # This ties the whole chain together: MIDI callback, log service, file
        # Verifying only the name and the header would still pass with a broken writer
        Send-NoteOn 60 100
        Start-Sleep -Milliseconds 1500
        Send-NoteOff 60
        Start-Sleep -Milliseconds 600
        $sessionText = Get-Content $sessionFile.FullName -Raw
        Assert-True ($sessionText -match "60") "演奏事件写进了会话文件"
    }

    Write-Section "15. 日志整理：删旧格式、跨天的并成整天归档"
    # 整理在启动时跑，种子文件在启动前就放好了（见脚本开头）
    #
    # Housekeeping runs at start-up, and the seed files were placed before the app started (see the top of the script)
    $legacyGone = (-not (Test-Path (Join-Path $logDir "miditap.log"))) -and (-not (Test-Path (Join-Path $logDir "miditap.log.1")))
    Assert-True $legacyGone "旧格式 miditap.log 与 miditap.log.1 已被删除"

    $archive = Join-Path $logDir "miditap-$yesterday.tar.gz"
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path $archive)) { Start-Sleep -Milliseconds 300 }
    Assert-True (Test-Path $archive) "昨天的会话已并成整天归档 miditap-$yesterday.tar.gz"

    # 源文件收进归档后就不该再留在目录里
    # 会话级的 .gz 也不再产生：整天归档一步到位，没有中间产物
    #
    # Once a source is inside the archive it should no longer sit in the directory
    # No session-level .gz is produced either: the day archive is made in one step, with no intermediate file
    Assert-True (-not (Test-Path (Join-Path $logDir "miditap-$yesterday-1.log"))) "源会话文件已收进归档（1）"
    Assert-True (-not (Test-Path (Join-Path $logDir "miditap-$yesterday-2.log"))) "源会话文件已收进归档（2）"
    Assert-True (-not (Test-Path (Join-Path $logDir "miditap-$yesterday-1.log.gz"))) "未产生会话级的 .gz（1）"
    Assert-True (-not (Test-Path (Join-Path $logDir "miditap-$yesterday-2.log.gz"))) "未产生会话级的 .gz（2）"

    if (Test-Path $archive) {
        # 解开归档逐条读回：只看文件存在是不够的，条目名与内容都可能写错
        #
        # The archive is opened and read back entry by entry
        # Checking that the file merely exists would miss a wrong entry name or wrong content
        $archiveEntries = [System.Collections.Generic.List[string]]::new()
        $archiveStream = [System.IO.File]::OpenRead($archive)
        try {
            $archiveGzip = [System.IO.Compression.GZipStream]::new($archiveStream, [System.IO.Compression.CompressionMode]::Decompress)
            try {
                $archiveTar = [System.Formats.Tar.TarReader]::new($archiveGzip)
                try {
                    while ($tarEntry = $archiveTar.GetNextEntry()) {
                        $entryReader = [System.IO.StreamReader]::new($tarEntry.DataStream)
                        try { $archiveEntries.Add($tarEntry.Name + "|" + $entryReader.ReadToEnd()) } finally { $entryReader.Dispose() }
                    }
                } finally { $archiveTar.Dispose() }
            } finally { $archiveGzip.Dispose() }
        } finally { $archiveStream.Dispose() }

        Assert-True ($archiveEntries.Count -eq 2) ("归档里有两个会话条目（实际：" + $archiveEntries.Count + "）")
        # 条目名是解压后可直接辨认的明文名字，内容也必须是解压后的原文
        #
        # The entry names are the identifiable plain-text names, and the content must be the decompressed original
        Assert-True ($archiveEntries -contains "miditap-$yesterday-1.log|seeded first") "归档条目名与内容对应（1）"
        Assert-True ($archiveEntries -contains "miditap-$yesterday-2.log|seeded second") "归档条目名与内容对应（2）"
    }

    # 今天的会话必须各自独立：合并它们会破坏「每次启动一个文件」
    #
    # Today's sessions stay separate: merging them would break the one-file-per-launch rule
    $todaySession = Get-ChildItem $logDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "^miditap-$today-[0-9]+[.]log$" } | Select-Object -First 1
    Assert-True ($null -ne $todaySession) "今天的会话文件没有被并入归档"

    Write-Section "13. 全程无崩溃"
    Assert-True (-not $app.HasExited) "应用在整个测试过程中保持存活"
    $crashLog = Join-Path $AppDir ".storage/crash.log"
    Assert-True (-not (Test-Path $crashLog)) "未产生崩溃日志（.storage/crash.log 不存在）"
}
finally {
    # ------------------------------------------------------------------ 还原 / Restore
    if ($app -and -not $app.HasExited) {
        $app | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
    if ($null -ne $backup) { Set-Content -Path $configPath -Value $backup -NoNewline }
    else { Remove-Item $configPath -Force -ErrorAction SilentlyContinue }
    if ($null -ne $lastBackup) { Set-Content -Path $lastConfigPath -Value $lastBackup -NoNewline }
    else { Remove-Item $lastConfigPath -Force -ErrorAction SilentlyContinue }

    # 日志目录还原：先放回备份，再删掉本脚本新增的文件
    # 顺序不能反：放回的那些文件本来就在「启动前已存在」的名单里，反了会把刚还原的删掉
    #
    # The log directory is restored: backups first, then files this script newly created are removed
    # The order matters: a restored backup is already in the at-start-up list, and reversing the order would delete it
    if (Test-Path $logDir) {
        foreach ($name in $logTouchNames) {
            $restoreTarget = Join-Path $logDir $name
            $restoreBackup = Join-Path $logBackupDir $name
            if (Test-Path $restoreBackup) { Copy-Item $restoreBackup $restoreTarget -Force }
            else { Remove-Item $restoreTarget -Force -ErrorAction SilentlyContinue }
        }
        Get-ChildItem $logDir -File -ErrorAction SilentlyContinue |
            Where-Object { $logFilesBefore -notcontains $_.Name } |
            ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item $logBackupDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($null -ne $logSettingBackup) { Set-Content -Path $logSettingPath -Value $logSettingBackup -NoNewline }
    else { Remove-Item $logSettingPath -Force -ErrorAction SilentlyContinue }

    if ($midiHandle -ne [IntPtr]::Zero) { [void][MidiTapE2E.Midi]::midiOutClose($midiHandle) }
    Pop-Location

    Write-Host ""
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host "通过 $script:passed，失败 $script:failed" -ForegroundColor $(if ($script:failed -eq 0) { 'Green' } else { 'Red' })
    if ($script:failed -gt 0) {
        Write-Host "失败项：" -ForegroundColor Red
        $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    }
    Write-Host "配置已还原。" -ForegroundColor DarkGray
}

exit $(if ($script:failed -eq 0) { 0 } else { 1 })
