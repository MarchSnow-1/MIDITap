# e2e-ui.ps1 — 端到端：界面上看到的东西与真实事件一致
#
# 与另外两个脚本的分工
#   e2e-midi.ps1  按键注入本身
#   e2e-ui.ps1    本脚本：日志页、主页实时预览、切页后补回
#   e2e-log.ps1   日志落盘与启动整理
#   e2e-all.ps1   依次跑上面三个
#
# 这一组以界面文字（UI Automation）为主，按键状态只在收尾处查一次，确认没有卡键
# 两者刻意分开：界面显示正确与按键真的被注入是两件事，混在一起就分不清是哪一层坏了
#
# e2e-ui.ps1 — end to end: what the UI shows agrees with the real events
#
# How this divides the work with the other two scripts
#   e2e-midi.ps1  injection itself
#   e2e-ui.ps1    THIS SCRIPT: the log page, the home page live preview, and the replay after a page switch
#   e2e-log.ps1   writing the log to disk and the start-up housekeeping
#   e2e-all.ps1   runs all three in order
#
# This group mostly reads the app's own text through UI Automation; key state is read once at teardown, to
# confirm nothing was left held down
# The two are kept apart on purpose: the UI showing the right thing and the key really being injected are
# separate facts, and mixing them makes it impossible to tell which layer broke

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

    Write-Section "1. Log page records the events"
    Send-NoteOn 60 100
    Start-Sleep -Milliseconds 900
    $rows = Read-LogRows
    $noteRow = $rows | Where-Object { $_ -match '\b60\b' -and $_ -match 'f13' } | Select-Object -First 1
    Assert-True ($null -ne $noteRow) "the log page shows a note 60 -> f13 row"
    Send-NoteOff 60
    Start-Sleep -Milliseconds 400

    Write-Section "2. Live preview on the home page"
    # 实时预览在主页内测：用户在主页演奏时实时看到触发了什么
    # 「按住琴键时切走再切回」另有第 3 节专门覆盖，不在这里重复
    #
    # The live preview is exercised from WITHIN the home page: playing there shows what was triggered, live
    # The "leave while keys are held, then come back" case has its own section, 3, and is not repeated here
    Select-NavItem 'NavHome'
    Send-NoteOn 60 100
    Start-Sleep -Milliseconds 900

    # chip 容器是 ItemsControl，它的 AutomationProperties.Name 由代码同步为激活键位汇总
    # 无激活音符时该名称为空、元素不进 UIA 树，因此必须在发送之后查询
    #
    # The chip container is an ItemsControl whose AutomationProperties.Name is synced by code to the active key
    # labels
    # With no active notes the name is empty and the element is absent from the UIA tree, so it has to be
    # queried *after* sending
    $chipHost = Find-ById 'ActiveNotesItems'
    $chipText = if ($chipHost) { $chipHost.Current.Name } else { '' }
    Assert-True ($chipText -match 'f13') "the live preview shows the mapped key f13 (actual: '$chipText')"
    # 顺带验证无障碍：读屏用户拿到的就是这段文本（chip 本身是纯视觉的）
    #
    # This doubles as an accessibility check: this is exactly the text a screen-reader user gets, while the
    # chips themselves are purely visual
    Assert-True ($chipText -match 'C4') "the live preview text carries the note name C4 (actual: '$chipText')"

    Send-NoteOn 61 100
    Start-Sleep -Milliseconds 600
    $multi = (Find-ById 'ActiveNotesItems').Current.Name
    Assert-True ($multi -match 'C4' -and $multi -match 'C#4') "both notes are listed when they fire together (actual: '$multi')"

    Send-NoteOff 60
    Send-NoteOff 61
    Start-Sleep -Milliseconds 700
    $afterRelease = Find-ById 'ActiveNotesItems'
    Assert-True ($null -eq $afterRelease -or $afterRelease.Current.Name -eq '') "the live preview is empty once every key is released"
    # 收尾：这一节按下的键应已抬起
    # 这里只查状态，不带「假通过」防护——防护要求本脚本先观察到该键按下过，而本节只读界面文字
    # 那种防护由 e2e-midi.ps1 与下一节（先断言按下、再断言抬起）负责
    #
    # Teardown: the keys pressed in this section should be up
    # This only reads the state and carries no false-pass guard, which requires this script to have observed
    # the key down first, while this section only reads the UI text
    # That guard belongs to e2e-midi.ps1 and to the next section, which asserts down and then up
    Assert-True (-not [MidiTapE2E.Midi]::IsDown($script:e2eVK['F13'])) "the key is up after the live preview releases"

    Write-Section "3. Notes still held are replayed when the home page returns"
    # 主页按导航重建（NavigationCacheMode=Disabled），站在别的页面时按下的音符它收不到 NoteOn
    # 因此加载时重放一份「仍被按住」的快照
    # 不重放的话这些音符要等到抬起那一刻才第一次出现，而抬起只负责把它们移除，等于从未显示过
    #
    # The home page is rebuilt on navigation (NavigationCacheMode=Disabled), so presses made while another page
    # is shown raise no NoteOn it can hear
    # A snapshot of the notes still held is therefore replayed when it loads
    # Without that replay they first appear at the moment of release, and the release only removes them, so they
    # are never shown
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
    Assert-True ($restoredText -match 'f13') "the home page shows the note still held after switching back (actual: '$restoredText')"
    Assert-Key $true 'F13' "the key is still down when the home page returns"

    Send-NoteOff 60
    Start-Sleep -Milliseconds 700
    $cleared = Find-ById 'ActiveNotesItems'
    Assert-True ($null -eq $cleared -or $cleared.Current.Name -eq '') "the home page is empty after the release"

    Assert-NoCrash
}
finally {
    Stop-E2ETarget
    $exitCode = Show-E2ESummary
}

exit $exitCode
