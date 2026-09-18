# e2e-all.ps1 — 依次跑三个端到端脚本
#
# 为什么分成三个而不是一个：单个脚本几百行读起来太吃力，改一处要在一大团里找
# 三个脚本各自独立可跑，也都点源同一个 e2e-common.ps1，共用的东西只有一份
#
# 每个脚本会各自启动一次应用：这样互不干扰，一个脚本崩了不影响另一个
# 代价是启动三次（每次约 6 秒）
#
# e2e-all.ps1 — runs the three end-to-end scripts in order
#
# Why three scripts rather than one: a single script of several hundred lines is hard to read, and a change
# means hunting through one big block
# Each script runs on its own and dot-sources the same e2e-common.ps1, so the shared parts exist once
#
# Every script starts the app itself: that keeps them independent, and one failing does not affect another
# The cost is three launches (about six seconds each)

[CmdletBinding()]
param(
    [string]$PortName = "MIDITap-TestConfig",
    [string]$AppDir = "src/MIDITap.App/bin/x64/Debug/net10.0-windows10.0.19041.0",
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scripts = @("e2e-midi.ps1", "e2e-ui.ps1", "e2e-log.ps1")
$failedScripts = [System.Collections.Generic.List[string]]::new()

foreach ($name in $scripts) {
    Write-Host ""
    Write-Host ("################ " + $name + " ################") -ForegroundColor Magenta
    $path = Join-Path $PSScriptRoot $name
    # 必须开子进程：每个脚本结尾都会 exit，同一会话里直接跑会把本脚本一起结束
    #
    # A child process is required: every script ends with exit, and running one in this session would end this
    # script as well
    & pwsh -NoProfile -File $path -PortName $PortName -AppDir $AppDir -SkipBuild:$SkipBuild
    if ($LASTEXITCODE -ne 0) { $failedScripts.Add($name) }
}

Write-Host ""
Write-Host ("=" * 60) -ForegroundColor Cyan
if ($failedScripts.Count -eq 0) {
    Write-Host "all three scripts passed" -ForegroundColor Green
} else {
    Write-Host ("failed: " + ($failedScripts -join ", ")) -ForegroundColor Red
}

exit $(if ($failedScripts.Count -eq 0) { 0 } else { 1 })
