<#
.SYNOPSIS
  应用更新落地的辅助脚本：等待主程序退出，替换文件，必要时回滚，然后重新启动

  Helper script that applies an update: wait for the main app to exit, replace the files
  It rolls back if anything fails, then relaunches

.DESCRIPTION
  为什么需要**独立进程**和**一个窗口期**
    运行中的 exe 无法覆盖自身
    而且自包含 WinUI 应用在运行时锁住了数百个 DLL（Microsoft.ui.xaml.dll 等）
    因此更新只能发生在"主程序完全退出之后"
    本脚本由主程序在用户点击「重启以更新」时以分离进程启动

  为什么用 PowerShell 而不是 .NET 辅助 exe
    辅助程序要替换的是主程序目录里的文件
    如果它自己是那个目录里的一个 exe，就会多出"替换自己"的问题
    而且它也会随更新被替换
    PowerShell 位于系统目录，天然不受影响，也不需要额外分发文件

  安全措施
    * 替换前把将被覆盖的文件备份到 .update/backup/
    * 任一步失败就回滚（把备份拷回），不留下"半更新"的坏状态
    * 绝不触碰 config/ 与 .storage/（用户数据）
    * 全部结束后清理暂存目录

  退出码：0 成功；3 主程序未退出（未做任何改动）；4 暂存目录缺失；5 覆盖失败并已回滚；6 意外异常

  失败时（4/5/6）重启应用会带上 `--update-failed=<退出码>`，由应用按码给出本地化提示
  成功（0）不带参数
  退出码 3 时**不重启** —— 应用还在运行，再启一个会出现两个实例
  参数格式与应用的解析约定见 src/MIDITap.Core/Update/UpdateApplyReport.cs

  Why a SEPARATE PROCESS and a WINDOW OF OPPORTUNITY are needed
  A running exe cannot overwrite itself
  A self-contained WinUI app holds hundreds of DLLs locked while it runs (Microsoft.ui.xaml.dll among them)
  The update can therefore only happen after the main app has fully exited
  The app starts this script as a detached process when the user clicks "restart to update"

  Why PowerShell rather than a .NET helper exe
  The helper has to replace files in the app directory
  If it were itself an exe in that directory it would add a "replace yourself" problem
  It would also be replaced by the very update it applies
  PowerShell lives in the system directory, is unaffected by all this, and needs no extra file to be shipped

  Safety measures
    * back up every file that is about to be overwritten into .update/backup/
    * roll back on any failure (copy the backups back), leaving no half-updated state behind
    * never touch config/ or .storage/ (user data)
    * clean up the staging directory once everything has finished

  Exit codes: 0 success; 3 the main app did not exit (nothing was changed)
  4 staging directory missing; 5 overwrite failed and the backup was rolled back; 6 unexpected exception

  On failure (4/5/6) the relaunched app is given `--update-failed=<exit code>`
  That lets it present a localised message; on success (0) no argument is passed
  Exit code 3 does NOT relaunch — the app is still running
  Starting another copy would leave two instances
  The argument format is the convention parsed in src/MIDITap.Core/Update/UpdateApplyReport.cs

.PARAMETER AppDir
  主程序所在目录

  Directory containing the main application
.PARAMETER StagingDir
  已解压好的新版本文件所在目录（结构与 AppDir 一致）

  Directory holding the extracted new version (same layout as AppDir)
.PARAMETER AppExe
  主程序可执行文件名

  File name of the main application executable
.PARAMETER ProcessId
  需要等待退出的主程序进程 ID

  Process ID of the main application that must exit first
.PARAMETER LogPath
  本脚本的执行日志（便于失败后排查）

  Log of this script run, so a failure can be diagnosed afterwards
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDir,
    [Parameter(Mandatory)][string]$StagingDir,
    [string]$AppExe = "MIDITap.exe",
    [Parameter(Mandatory)][int]$ProcessId,
    [string]$LogPath = "",
    [int]$WaitSeconds = 60
)

$ErrorActionPreference = "Stop"

function Write-Log([string]$message) {
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $message
    if ($LogPath) {
        try { Add-Content -Path $LogPath -Value $line -Encoding UTF8 } catch { }
    }
    Write-Host $line
}

# 用户数据目录：无论发生什么都不能被更新覆盖
#
# User-data directories: whatever happens, an update must never overwrite them
$protected = @("config", ".storage")

$backupDir = Join-Path $AppDir ".update\backup"

Write-Log "更新辅助启动: AppDir=$AppDir ProcessId=$ProcessId"
Write-Log "暂存目录: $StagingDir"

<#
  注意这里**刻意不使用 exit 穿过 try/finally**
  实测 exit 在 finally 块里不会中断脚本，而是被吞掉后继续往下执行
  那会让"主程序仍在运行"的超时分支接着去做覆盖与清理
  等于在程序还开着的时候改文件
  改为返回码由外层决定，流程控制完全显式
  Deliberately avoids exit across try/finally
  In practice exit inside a finally block does NOT terminate the script
  It is swallowed and execution continues
  That made the "app still running" timeout path fall through into overwriting and cleanup
  A return code decided by the caller keeps control flow explicit
#>
$exitCode = 0
$copied = 0
$restored = 0

try {
    # ---------------------------------------------- 1) 等待主程序退出 / Wait for the app to exit
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 300
    }
    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        Write-Log "主程序在 $WaitSeconds 秒内没有退出；不做任何改动（应用仍在运行）"
        $exitCode = 3
    }
    elseif (-not (Test-Path $StagingDir)) {
        Write-Log "暂存目录不存在，放弃更新"
        $exitCode = 4
    }
    else {
        Write-Log "主程序已退出"
        # 再等一会儿让文件句柄彻底释放（WinUI 的 DLL 释放略有延迟）
        #
        # Wait a moment longer so the file handles are fully released
        # WinUI releases its DLLs with a slight delay
        Start-Sleep -Seconds 2

        # ------------------------------------------------- 2) 备份 + 覆盖 / Back up and copy
        Remove-Item $backupDir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

        $backed = 0
        $failed = $null

        foreach ($item in Get-ChildItem $StagingDir -Recurse -File) {
            $relative = $item.FullName.Substring($StagingDir.Length).TrimStart("\", "/")

            # 跳过受保护目录（结构上已被剔除，这里是第二道防线）
            # PowerShell 的 .Split() 需要字符数组；.Split("/", "\") 会把第二参当成 count 而抛错
            #
            # Skip the protected directories: they are already absent from the layout
            # This is a second line of defence
            # PowerShell's .Split() wants a char array
            # .Split("/", "\") takes the second argument as a count and throws
            $top = $relative.Split([char[]]@("/", "\"))[0]
            if ($protected -contains $top) { continue }

            $target = Join-Path $AppDir $relative
            try {
                if (Test-Path $target) {
                    $backupPath = Join-Path $backupDir $relative
                    $backupParent = Split-Path $backupPath -Parent
                    if ($backupParent) { New-Item -ItemType Directory -Force -Path $backupParent | Out-Null }
                    Copy-Item $target $backupPath -Force
                    $backed++
                }
                $parent = Split-Path $target -Parent
                if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
                Copy-Item $item.FullName $target -Force
                $copied++
            }
            catch {
                $failed = "$relative -> $($_.Exception.Message)"
                break
            }
        }

        if ($failed) {
            Write-Log "覆盖失败: $failed"
            Write-Log "开始回滚…"
            foreach ($item in Get-ChildItem $backupDir -Recurse -File) {
                $relative = $item.FullName.Substring($backupDir.Length).TrimStart("\", "/")
                $target = Join-Path $AppDir $relative
                try {
                    $parent = Split-Path $target -Parent
                    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
                    Copy-Item $item.FullName $target -Force
                    $restored++
                } catch { }
            }
            Write-Log "回滚完成，恢复 $restored 个文件；应用保持更新前的版本"
            $exitCode = 5
        }
        else {
            Write-Log "更新完成: 覆盖 $copied 个文件（备份 $backed 个）"
        }
    }
}
catch {
    Write-Log "更新过程异常: $($_.Exception.Message)"
    $exitCode = 6
}

# ---------------------------------------- 3) 收尾（总会执行）/ Teardown (always runs)
# 重启有两条规则
#   * **只在主程序确实已退出时才重启**
#     退出码 3 恰恰表示"它还在运行、脚本什么都没做"（见上面 1) 等待主程序退出）
#     此时再 Start-Process 会开出**第二个实例**
#     两个实例都会去抢 MIDI 端口并各自注入按键
#   * **失败时把退出码作为 id 带回去**（--update-failed=<码>），由应用本地化后提示用户
#     这次重启是唯一能说明"更新失败了、已回滚"的机会
#     错过它，用户只会看到应用莫名其妙地重开、版本还没变
#     成功（0）时不带参数
# 另外这里**不清理暂存目录**，除非更新真的成功了（exitCode 0）
# 失败时保留现场供排查，下次更新会自行覆盖
#
# Two rules for the relaunch
# Only relaunch once the main app has really exited
# Exit code 3 means it has not, and nothing was done
# Starting another copy would leave two instances competing for the MIDI port
# Both would inject keys
# On failure pass the exit code back as an id (--update-failed=<code>)
# The app can then localise the message
# That relaunch is the only chance to say the update failed
# Miss it and the user just sees the app reopen with the same version
# On success (0) no argument is passed
# The staging directory is cleaned up only when the update really succeeded (exitCode 0)
# On failure the evidence is kept for diagnosis
# The next update overwrites it anyway
if ($exitCode -eq 3) {
    Write-Log "主程序仍在运行（退出码 3），不重启，以免出现第二个实例"
} else {
    $exe = Join-Path $AppDir $AppExe
    if (Test-Path $exe) {
        try {
            if ($exitCode -eq 0) {
                Start-Process -FilePath $exe -WorkingDirectory $AppDir
                Write-Log "已重新启动应用"
            } else {
                Start-Process -FilePath $exe -WorkingDirectory $AppDir -ArgumentList "--update-failed=$exitCode"
                Write-Log "已以 --update-failed=$exitCode 重新启动应用（由应用提示用户）"
            }
        } catch {
            Write-Log "重新启动失败: $($_.Exception.Message)（请手动启动 $exe）"
        }
    } else {
        Write-Log "找不到可执行文件: $exe"
    }
}

if ($exitCode -eq 0) {
    Remove-Item $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Log "暂存目录已清理"
}

Write-Log "辅助脚本结束（退出码 $exitCode）"

# 必须用 SetShouldExit 把退出码交给进程
# 脚本末尾的 return 只设置管道输出，**不会**成为进程退出码
# 调用方要据此判断成败，因此这里显式设置
# SetShouldExit is required: a trailing return only produces pipeline output
# It does NOT become the process exit code
# The caller relies on this code
$host.SetShouldExit($exitCode)