<#
.SYNOPSIS
  键盘输入接收端：一个真实窗口，用来验证 MIDITap 注入的按键能否被普通程序收到

  Keyboard input sink: a real window that verifies keys injected by MIDITap
  Those keys must actually be received by an ordinary application

.DESCRIPTION
  为什么用"独立窗口"而不是钩子/GetAsyncKeyState
    钩子与 GetAsyncKeyState 都工作在系统输入层，它们能证明"有按键事件存在"
    但**不能证明**这个事件能被一个普通应用正常接收并当作输入处理
    例如被焦点窗口按常规键盘消息路径消费、进入文本框
    本程序是一个普通 WinForms 窗口，主体是一个带焦点的多行文本框。MIDITap 触发按键后：
      * 能看到实际输入的字符（字母/数字/符号）；
      * 能看到光标移动（方向键/Home/End）；
      * 状态栏实时显示最近一次按键的键码 —— 因为功能键（F13 等）、修饰键、Windows 键**不会**产生可见字符，必须另有一条通道才能观察
    因此它对两类键都有效：会打字的键看文本框，不会打字的键看状态栏

  Why a standalone window instead of a hook or GetAsyncKeyState
  Hooks and GetAsyncKeyState both work at the system input layer
  They can show that a key event exists
  But they CANNOT show that the event reaches an ordinary application and is handled there as input
  That means the focused window consumes it through the normal keyboard-message path
  It also means the text is typed into a text box
  This program is an ordinary WinForms window whose body is a focused multi-line text box
  Once MIDITap triggers a key
    * the characters actually typed are visible (letters/digits/symbols)
    * caret movement is visible (arrow keys/Home/End)
    * the status bar shows the key code of the latest key press live
      It is needed for keys that produce no visible character
      Those are function keys (F13 and friends), modifiers and the Windows key
      They require a separate channel to be observed at all
  It therefore covers both kinds of key
  Keys that type show up in the text box
  Keys that do not show up in the status bar

.PARAMETER Seconds
  自动运行时长（秒）。到点后自动把结果写到 -ReportPath 并退出，便于脚本化验证
  省略则一直开着，直到你手动关闭

  Automatic run length in seconds
  When it expires, the result is written to -ReportPath and the program exits
  That makes scripted verification easy
  Omit it to stay open until you close the window yourself

.EXAMPLE
  # 手动用：开一个窗口，用 MIDITap 或键盘去按，自己看
  # Manual use: open the window and press keys with MIDITap or your own keyboard, and watch
  pwsh dev-scripts/input-sink.ps1

.EXAMPLE
  # 自动用：跑 15 秒后输出报告并退出（可写进 CI 或别的脚本里）
  # Automated use: run for 15 seconds, emit a report and exit (scriptable, e.g. from CI)
  pwsh dev-scripts/input-sink.ps1 -Seconds 15 -ReportPath out.json

.NOTES
  隐私：本程序只记录**虚拟键码**与键入到文本框的文本（而文本就是你要验证的对象）
  不监听其它程序的输入、不挂钩子、不写剪贴板

  Privacy: this program records only the VIRTUAL KEY CODES and the text typed into the text box
  That text is exactly what you are verifying
  It does not listen to other applications, installs no hook and writes nothing to the clipboard
#>
# 为什么放在 dev-scripts/ 而不是 scripts/
#   scripts/ 下的内容会被原样复制进用户发布包（见 .github/workflows/*.yml 的 stage 步骤）
#   而本脚本是排查用的工具，不发也不该发给用户 —— 它只用于观察实际落到窗口上的键盘输入，不是程序运行所需
#   因此按 AGENTS.md §5 的分工放进 dev-scripts/：它是仓库的一部分（开发时可用、可 review）
#   但不进入发布包。打包只复制 scripts/*.ps1，所以这里不需要额外的排除规则
#
# Why dev-scripts/ rather than scripts/
# Everything under scripts/ is copied verbatim into the user package
# See the stage steps in .github/workflows/*.yml
# This script is a diagnostic tool
# It is neither needed by nor meant for users
# It exists only to observe the keyboard input that actually reaches a window
# It therefore lives in dev-scripts/ per AGENTS.md §5
# That keeps it part of the repository (usable and reviewable during development)
# It stays outside the package
# Packaging copies scripts/*.ps1 only, so no extra exclusion rule is needed
[CmdletBinding()]
param(
    [int]$Seconds = 0,
    [string]$ReportPath = "",
    [switch]$Topmost
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

# --------------------------------------------------------------------- 窗口 / Window

$form = New-Object System.Windows.Forms.Form
$form.Text = "MIDITap 输入接收端 (Input Sink)"
$form.Size = New-Object System.Drawing.Size(560, 420)
$form.StartPosition = "CenterScreen"
$form.Topmost = [bool]$Topmost
$form.KeyPreview = $true   # 让窗口先看到按键，便于记录功能键
                           # Let the window see the keys first so function keys can be recorded

$info = New-Object System.Windows.Forms.Label
$info.Text = "把焦点放在下面的文本框里。MIDITap 触发的按键应当出现在这里。"
$info.Location = New-Object System.Drawing.Point(12, 10)
$info.Size = New-Object System.Drawing.Size(520, 34)
$form.Controls.Add($info)

$status = New-Object System.Windows.Forms.Label
$status.Text = "最近按键：—"
$status.Location = New-Object System.Drawing.Point(12, 46)
$status.Size = New-Object System.Drawing.Size(520, 24)
$status.Font = New-Object System.Drawing.Font("Consolas", 10)
$form.Controls.Add($status)

$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true
$box.ScrollBars = "Vertical"
$box.AcceptsReturn = $true
$box.Location = New-Object System.Drawing.Point(12, 76)
$box.Size = New-Object System.Drawing.Size(520, 290)
$box.Font = New-Object System.Drawing.Font("Consolas", 12)
$form.Controls.Add($box)

# 记录用容器（供自动报告使用）
#
# Container for the recorded events, used by the automatic report
$script:keyLog = [System.Collections.Generic.List[object]]::new()

$record = {
    param($kind, $keyCode, $keyData, $keyValue)
    $name = [System.Windows.Forms.Keys]$keyCode
    # KeyPress 传进来的是**字符**而不是虚拟键码
    # 若一律强转成 Keys 枚举会显示成 NumPad1/NumPad2（因为 'a' 的字符码 0x61 恰好是 NumPad1 的键码），产生误导
    # KeyPress carries a CHARACTER, not a virtual key code
    # Casting it to the Keys enum would display NumPad1/NumPad2
    # That is because 'a' is 0x61, which is NumPad1's key code
    # It would mislead anyone reading the report
    $display = if ($kind -eq "KeyPress") { "'" + $keyValue + "'" } else { $name.ToString() }
    $entry = [ordered]@{
        kind = $kind
        key = $display
        vk = ("0x{0:X2}" -f $keyCode)
        time = [DateTime]::Now.ToString("HH:mm:ss.fff")
    }
    $script:keyLog.Add($entry)
    $status.Text = "最近按键：$($entry.key)  ($($entry.vk))  [$kind]  $($entry.time)"
}

$box.add_KeyDown({
    param($sender, $e)
    & $record "KeyDown" ([int]$e.KeyCode) 0 $e.KeyValue
})
$box.add_KeyUp({
    param($sender, $e)
    & $record "KeyUp" ([int]$e.KeyCode) 0 $e.KeyValue
})
$box.add_KeyPress({
    param($sender, $e)
    & $record "KeyPress" ([int][char]$e.KeyChar) 0 $e.KeyChar
})

# 文本框获得焦点：否则键盘输入会落到别的窗口（这是"输入是否真的到达"的前提）
#
# Focus the text box, otherwise keyboard input lands in some other window
# Focus is the precondition for "did the input really arrive" to mean anything
$form.add_Shown({
    $form.Activate()
    $box.Focus()
})

# --------------------------------------------------------------- 自动模式 / Automatic mode

$timer = $null
if ($Seconds -gt 0) {
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = $Seconds * 1000
    $timer.add_Tick({
        $timer.Stop()
        $form.Close()
    })
    $timer.Start()
}

[void]$form.ShowDialog()

# ------------------------------------------------------------------------- 结果 / Result

$text = $box.Text
Write-Host ""
Write-Host "=== 接收结果 ===" -ForegroundColor Cyan
Write-Host ("文本框内容（{0} 字符）：" -f $text.Length)
if ($text.Length -gt 0) {
    Write-Host ("  " + ($text -replace "`r`n", " / " -replace "`n", " / "))
} else {
    Write-Host "  （空 —— 若本次测试期望出现字符，则说明输入没有到达）" -ForegroundColor Yellow
}
Write-Host ("按键事件数：{0}" -f $script:keyLog.Count)
foreach ($e in ($script:keyLog | Select-Object -First 40)) {
    Write-Host ("  {0}  {1,-8} {2,-22} {3}" -f $e.time, $e.kind, $e.key, $e.vk)
}
if ($script:keyLog.Count -gt 40) { Write-Host ("  … 其余 {0} 条略" -f ($script:keyLog.Count - 40)) }

if ($ReportPath) {
    $report = [ordered]@{
        text = $text
        textLength = $text.Length
        eventCount = $script:keyLog.Count
        events = $script:keyLog
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -Path $ReportPath -Encoding UTF8
    Write-Host "报告已写入：$ReportPath"
}