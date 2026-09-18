# e2e-midi.ps1 — 端到端：从真实 MIDI 数据到系统按键注入
#
# 与另外两个脚本的分工
#   e2e-midi.ps1  本脚本：按键注入本身（按下/抬起、引用计数、组合键、卡键）
#   e2e-ui.ps1    界面：日志页、主页实时预览、切页后补回
#   e2e-log.ps1   日志落盘与启动整理
#   e2e-all.ps1   依次跑上面三个
#
# 验证手段：GetAsyncKeyState 直接问操作系统「这个虚拟键现在是否被按下」
# 这是端到端最强证据——它证明按键真的到了系统输入层，而不是「应用以为它按了」
# 测试键位用 F13~F17：几乎不被任何程序绑定，注入不会干扰用户正在使用的窗口
#
# 测试配置写在临时文件里，结束时必定还原（try/finally），不污染用户配置
#
# e2e-midi.ps1 — end to end: from real MIDI data to system key injection
#
# How this divides the work with the other two scripts
#   e2e-midi.ps1  THIS SCRIPT: injection itself (press and release, reference counting, combos, stuck keys)
#   e2e-ui.ps1    the UI: the log page, the home page live preview, and the replay after a page switch
#   e2e-log.ps1   writing the log to disk and the start-up housekeeping
#   e2e-all.ps1   runs all three in order
#
# The evidence: GetAsyncKeyState asks the operating system directly whether the virtual key is held down
# That is the strongest end-to-end evidence: it proves the key reached the system input layer, rather than
# the app believing it pressed something
# The test keys are F13~F17, which almost nothing binds, so the injection cannot disturb the window in use
#
# The test config is written to a temporary file and always restored at the end (try/finally)

[CmdletBinding()]
param(
    [string]$PortName = "MIDITap-TestConfig",
    [string]$AppDir = "src/MIDITap.App/bin/x64/Debug/net10.0-windows10.0.19041.0",
    [switch]$SkipBuild
)

. "$PSScriptRoot/e2e-app.ps1"

Initialize-E2E -PortName $PortName -AppDir $AppDir -SkipBuild:$SkipBuild

$exitCode = 0
try {
    Start-E2ETarget

    Write-Section "1. Basic press and release: a MIDI note-on really injects a key"
    Send-NoteOn 60 100
    Assert-Key $true 'F13' "note 60 down"
    Send-NoteOff 60
    Assert-Key $false 'F13' "note 60 up"

    Write-Section "2. Boundary notes 0 and 127"
    Send-NoteOn 0 100
    Assert-Key $true 'F14' "note 0 (lowest boundary) down"
    Send-NoteOff 0
    Assert-Key $false 'F14' "note 0 up"
    Send-NoteOn 127 100
    Assert-Key $true 'F15' "note 127 (highest boundary) down"
    Send-NoteOff 127
    Assert-Key $false 'F15' "note 127 up"

    Write-Section "3. A note-on with velocity 0 equals a note-off"
    Send-NoteOn 60 100
    Assert-Key $true 'F13' "pressed first"
    Send-Midi 0x90 60 0
    Assert-Key $false 'F13' "released after 0x90 velocity 0 (no stuck key)"

    Write-Section "4. Repeated note-on: no double injection, and one release frees it"
    Send-NoteOn 60 100
    Assert-Key $true 'F13' "pressed"
    Send-NoteOn 60 100
    Start-Sleep -Milliseconds 200
    Assert-True ([MidiTapE2E.Midi]::IsDown($script:e2eVK['F13'])) "still down after a repeated note-on (no extra injection)"
    Send-NoteOff 60
    Assert-Key $false 'F13' "a single release frees it (no reference-count leak)"

    Write-Section "5. Two notes sharing one virtual key: reference counting"
    Send-NoteOn 60; Send-NoteOn 61
    Start-Sleep -Milliseconds 300
    Assert-True ([MidiTapE2E.Midi]::IsDown($script:e2eVK['F13'])) "F13 is down with both notes pressed"
    Send-NoteOff 60
    Start-Sleep -Milliseconds 300
    Assert-True ([MidiTapE2E.Midi]::IsDown($script:e2eVK['F13'])) "F13 stays down while only one note is released"
    Send-NoteOff 61
    Assert-Key $false 'F13' "F13 goes up once both are released"

    Write-Section "6. Combo key: pressed together, released in pairs"
    Send-NoteOn 62
    Start-Sleep -Milliseconds 400
    $bothDown = [MidiTapE2E.Midi]::IsDown($script:e2eVK['F16']) -and [MidiTapE2E.Midi]::IsDown($script:e2eVK['F17'])
    if ($bothDown) { $script:e2eObservedDown['F16'] = $true; $script:e2eObservedDown['F17'] = $true }
    Assert-True $bothDown "both F16 and F17 are down"
    Send-NoteOff 62
    Start-Sleep -Milliseconds 400
    $bothUp = (-not [MidiTapE2E.Midi]::IsDown($script:e2eVK['F16'])) -and (-not [MidiTapE2E.Midi]::IsDown($script:e2eVK['F17']))
    # 与 Assert-Key 同理：没按下过就说不上抬起
    #
    # Same reasoning as Assert-Key: a key that was never down cannot be said to have come up
    Assert-True ($bothUp -and $script:e2eObservedDown.ContainsKey('F16') -and $script:e2eObservedDown.ContainsKey('F17')) "both F16 and F17 are up"

    Write-Section "7. An unmapped note: broadcast only, no injection"
    $before = @($script:e2eVK.Values | Where-Object { [MidiTapE2E.Midi]::IsDown($_) })
    Send-NoteOn 63 100
    Start-Sleep -Milliseconds 400
    $after = @($script:e2eVK.Values | Where-Object { [MidiTapE2E.Midi]::IsDown($_) })
    Assert-True ($before.Count -eq 0 -and $after.Count -eq 0) "unmapped note 63 injected none of the test keys"
    Send-NoteOff 63

    Write-Section "8. Non-note messages: no injection, no crash"
    Send-Midi 0xB0 7 127    # CC
    Send-Midi 0xE0 0 64     # Pitch bend
    Send-Midi 0xA0 60 70    # Poly aftertouch
    Send-Midi 0xC0 42 0     # Program change
    Send-Midi 0xD0 100 0    # Channel pressure
    Start-Sleep -Milliseconds 500
    $anyDown = @($script:e2eVK.Values | Where-Object { [MidiTapE2E.Midi]::IsDown($_) })
    Assert-True ($anyDown.Count -eq 0) "non-note messages triggered no key"
    Assert-True (-not $script:e2eApp.HasExited) "the app is still alive after the non-note messages"

    Write-Section "9. Burst: press and release every mapped note, leaving no stuck key"
    $mapped = @(0, 60, 61, 62, 127)
    foreach ($n in $mapped) { Send-NoteOn ([byte]$n) }
    Start-Sleep -Milliseconds 600
    foreach ($n in $mapped) { Send-NoteOff ([byte]$n) }
    Start-Sleep -Milliseconds 800
    $stuck = @($script:e2eVK.Keys | Where-Object { [MidiTapE2E.Midi]::IsDown($script:e2eVK[$_]) })
    Assert-True ($stuck.Count -eq 0) "no stuck key after the burst (left over: $(($stuck -join ', ')))"

    Assert-NoCrash
}
finally {
    Stop-E2ETarget
    $exitCode = Show-E2ESummary
}

exit $exitCode
