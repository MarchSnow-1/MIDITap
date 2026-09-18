# e2e-app.ps1 — 端到端脚本的入口库：初始化、启动、就绪、收尾、汇总
#
# 三个库文件各管一件事，见 e2e-common.ps1 的文件头
# 本文件负责应用的生命周期，并把另外两个库点源进来，因此用例脚本只需要点源这一个
#
# 用例脚本的固定骨架
#   . "$PSScriptRoot/e2e-app.ps1"
#   Initialize-E2E -PortName $PortName -AppDir $AppDir -SkipBuild:$SkipBuild
#   $exitCode = 0
#   try { Start-E2ETarget; <用例>; Assert-NoCrash }
#   finally { Stop-E2ETarget; $exitCode = Show-E2ESummary }
#   exit $exitCode
#
# e2e-app.ps1 — the entry library for the end-to-end scripts: initialize, start, readiness, teardown, summary
#
# The three library files each own one concern; see the header of e2e-common.ps1
# This file owns the app lifecycle and dot-sources the other two, so a case script only has to dot-source this one
#
# The fixed skeleton of a case script
#   . "$PSScriptRoot/e2e-app.ps1"
#   Initialize-E2E -PortName $PortName -AppDir $AppDir -SkipBuild:$SkipBuild
#   $exitCode = 0
#   try { Start-E2ETarget; <cases>; Assert-NoCrash }
#   finally { Stop-E2ETarget; $exitCode = Show-E2ESummary }
#   exit $exitCode

. "$PSScriptRoot/e2e-common.ps1"
. "$PSScriptRoot/e2e-seed.ps1"

<#
  参数
    PortName   虚拟回环端口名（输入与输出同名）
    AppDir     应用构建输出目录（相对仓库根）
    SkipBuild  跳过 dotnet build，直接用现有产物
    SeedLogs   是否准备「日志整理」用例的现场（旧格式与昨天的会话），并把落盘开关置为关闭
               只有日志脚本需要它；其余脚本不碰用户既有的日志文件

  Parameters
    PortName   Name of the virtual loopback port (input and output share the name)
    AppDir     The app's build output directory, relative to the repository root
    SkipBuild  Skip dotnet build and use the existing output
    SeedLogs   Whether to prepare the scene for the housekeeping case (legacy shapes and yesterday's
               sessions) and force the log-to-file switch off
               Only the log script needs it; the other scripts leave the user's existing log files alone
#>
function Initialize-E2E {
    param(
        [Parameter(Mandatory)][string]$PortName,
        [Parameter(Mandatory)][string]$AppDir,
        [switch]$SkipBuild,
        [switch]$SeedLogs
    )

    $script:e2ePortName = $PortName
    $script:e2eAppDir = $AppDir
    $script:e2eSkipBuild = [bool]$SkipBuild
    $script:e2eSeedLogs = [bool]$SeedLogs
    $script:e2eApp = $null
    $script:e2eWindow = $null

    $script:e2eRepoRoot = Split-Path -Parent $PSScriptRoot
    Push-Location $script:e2eRepoRoot

    Initialize-E2EMidi
    Assert-Packing 0x90 60 100
    Assert-Packing 0x90 0 100
    Assert-Packing 0x90 127 100
    Assert-Packing 0x80 60 0

    $script:e2eInputIndex = [MidiTapE2E.Midi]::FindInput($PortName)
    $outputIndex = [MidiTapE2E.Midi]::FindOutput($PortName)
    if ($script:e2eInputIndex -lt 0 -or $outputIndex -lt 0) {
        Write-Host "The MIDI loopback port '$PortName' does not exist (input=$script:e2eInputIndex, output=$outputIndex)." -ForegroundColor Yellow
        Write-Host "Create a port of that name with loopMIDI, or name an existing one with -PortName." -ForegroundColor Yellow
        Pop-Location
        exit 2
    }
    Write-Host "Loopback port '$PortName': input=$script:e2eInputIndex output=$outputIndex" -ForegroundColor DarkGray

    if ([MidiTapE2E.Midi]::midiOutOpen([ref]$script:e2eMidiHandle, [uint32]$outputIndex, [IntPtr]::Zero, [IntPtr]::Zero, 0) -ne 0) {
        Write-Host "midiOutOpen failed." -ForegroundColor Red
        Pop-Location
        exit 2
    }

    Initialize-E2EConfig
    if ($SeedLogs) { Initialize-E2ELogs }
}

function Start-E2ETarget {
    if (-not $script:e2eSkipBuild) {
        Write-Host "Building the app..." -ForegroundColor DarkGray
        dotnet build src/MIDITap.App/MIDITap.App.csproj -c Debug 2>&1 | Select-Object -Last 1 | Out-Null
    }

    Get-Process MIDITap -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    $appExe = Join-Path $script:e2eAppDir "MIDITap.exe"
    Write-Host "Starting $appExe" -ForegroundColor DarkGray
    $script:e2eApp = Start-Process -FilePath $appExe -WorkingDirectory (Resolve-Path $script:e2eAppDir) -PassThru
    Start-Sleep -Seconds 6

    if ($script:e2eApp.HasExited) { throw "The app exited immediately (exit code $($script:e2eApp.ExitCode))" }

    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $script:e2eAE = [System.Windows.Automation.AutomationElement]
    $script:e2eTS = [System.Windows.Automation.TreeScope]
    $script:e2eWindow = $script:e2eAE::RootElement.FindFirst($script:e2eTS::Children,
        (New-Object System.Windows.Automation.PropertyCondition($script:e2eAE::ProcessIdProperty, $script:e2eApp.Id)))
    if (-not $script:e2eWindow) { throw "The app window was not found" }

    # 不能叫 $home：PowerShell 的 $HOME 是只读内置变量，赋值会抛错并静默中断整个脚本
    # Do NOT name this $home: PowerShell's $HOME is a read-only built-in
    # Assigning to it throws, silently aborting the script
    $navHomeItem = Find-ById 'NavHome'
    if ($navHomeItem) {
        $navHomeItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Seconds 1
    }

    # 先到日志页：切端口的就绪判定依赖日志内容
    #
    # Go to the log page first: the readiness check for a port switch reads the log content
    Select-NavItem 'NavLog'

    Write-Section "0. Setup: switch to the loopback port and wait until it is ready"
    $deviceBox = Find-ById 'DeviceBox'
    if (-not $deviceBox) { Select-NavItem 'NavHome'; $deviceBox = Find-ById 'DeviceBox' }
    Assert-True ($null -ne $deviceBox) "found the device drop-down"
    if ($deviceBox) {
        $deviceBox.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 700
        $itemCond = New-Object System.Windows.Automation.PropertyCondition(
            $script:e2eAE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $target = $null
        foreach ($it in $deviceBox.FindAll($script:e2eTS::Descendants, $itemCond)) {
            if ($it.Current.Name -eq $script:e2ePortName) { $target = $it }
        }
        Assert-True ($null -ne $target) "found '$script:e2ePortName' in the drop-down"
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
    $ready = Wait-MonitoringOnPort $script:e2ePortName $script:e2eInputIndex
    Assert-True $ready "the app started listening on '$script:e2ePortName' (a deterministic signal, not a fixed wait)"
    if (-not $ready) { throw "The app did not start listening on '$script:e2ePortName' in time, so later assertions are meaningless" }

    # 清空日志：后续按「新出现的行」断言，避免被启动期的记录干扰
    #
    # Clear the log: later assertions are about newly appearing rows, so start-up records cannot interfere
    $clearBtn = Find-ById 'ClearBtn'
    if ($clearBtn) {
        $clearBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 800
    }
    # 必须再包一层 @()：Read-LogRows 返回数组，但元素为 0/1 个时 PowerShell 会把它解包掉
    # 直接取 .Count 会在 StrictMode 下抛错
    #
    # The extra @() is required: Read-LogRows returns an array, and PowerShell unwraps it when it holds 0 or 1
    # element, so reading .Count directly would throw under StrictMode
    Assert-True (@(Read-LogRows).Count -eq 0) "the log was cleared"
}

function Assert-NoCrash {
    Write-Section "No crash throughout"
    Assert-True (-not $script:e2eApp.HasExited) "the app stayed alive for the whole run"
    $crashLog = Join-Path $script:e2eAppDir ".storage/crash.log"
    Assert-True (-not (Test-Path $crashLog)) "no crash log was produced (.storage/crash.log is absent)"
}

function Stop-E2ETarget {
    if ($script:e2eApp -and -not $script:e2eApp.HasExited) {
        $script:e2eApp | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    Restore-E2EConfig
    if ($script:e2eSeedLogs) { Restore-E2ELogs }

    if ($script:e2eMidiHandle -ne [IntPtr]::Zero) { [void][MidiTapE2E.Midi]::midiOutClose($script:e2eMidiHandle) }
    Pop-Location
}

function Show-E2ESummary {
    Write-Host ""
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host "passed $script:e2ePassed, failed $script:e2eFailed" -ForegroundColor $(if ($script:e2eFailed -eq 0) { 'Green' } else { 'Red' })
    if ($script:e2eFailed -gt 0) {
        Write-Host "Failures:" -ForegroundColor Red
        $script:e2eFailures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    }
    Write-Host "Config restored." -ForegroundColor DarkGray
    return $(if ($script:e2eFailed -eq 0) { 0 } else { 1 })
}
