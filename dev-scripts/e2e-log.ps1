# e2e-log.ps1 — 端到端：日志落盘与启动时的整理
#
# 与另外两个脚本的分工
#   e2e-midi.ps1  按键注入本身
#   e2e-ui.ps1    界面显示
#   e2e-log.ps1   本脚本：开关打开后写文件、跨天的会话并成整天归档、旧格式被清理
#   e2e-all.ps1   依次跑上面三个
#
# 本脚本必须用 -SeedLogs 初始化：整理发生在**启动时**，旧格式与昨天的会话要在启动前就放好
# 落盘开关也会被置为关闭，这样在界面上打开它时必定产生一份新文件，时序才是确定的
#
# e2e-log.ps1 — end to end: writing the log to disk and the start-up housekeeping
#
# How this divides the work with the other two scripts
#   e2e-midi.ps1  injection itself
#   e2e-ui.ps1    what the UI shows
#   e2e-log.ps1   THIS SCRIPT: writing a file once the switch is on, merging a past day into one archive, and
#                 removing the legacy shapes
#   e2e-all.ps1   runs all three in order
#
# This script has to initialize with -SeedLogs: housekeeping runs at START-UP, so the legacy shapes and
# yesterday's sessions have to be in place before the app starts
# The log-to-file switch is forced off as well, which makes turning it on in the UI produce a brand new file

[CmdletBinding()]
param(
    [string]$PortName = "MIDITap-TestConfig",
    [string]$AppDir = "src/MIDITap.App/bin/x64/Debug/net10.0-windows10.0.19041.0",
    [switch]$SkipBuild
)

. "$PSScriptRoot/e2e-app.ps1"

Initialize-E2E -PortName $PortName -AppDir $AppDir -SkipBuild:$SkipBuild -SeedLogs

$exitCode = 0
try {
    Start-E2ETarget

    Write-Section "1. Log to file: a session file appears as soon as the switch is on"
    # 先制造一些只存在于内存里的行
    # 用户是在遇到问题之后才打开开关的，要看的正是打开之前那一段
    # 因此这里刻意先演奏若干次：不制造这些行，「补写」与「只有会话头」就分不开
    #
    # Some lines are produced that exist only in memory first
    # The user turns the switch on after hitting a problem, and what is needed is exactly the stretch before that
    # Playing a few notes first is therefore deliberate: without those lines, "backfilled" and "header only"
    # cannot be told apart
    for ($i = 0; $i -lt 12; $i++) { Send-NoteOn 60 100; Send-NoteOff 60 }
    Start-Sleep -Milliseconds 1500
    $rowsBefore = @(Read-LogRows)
    Assert-True ($rowsBefore.Count -gt 8) "the in-memory log already holds more than 8 rows before the switch is on"

    # 启动前已把落盘开关置为关闭（见 Initialize-E2ELogs），因此在界面上打开它之后必定出现一份新文件
    #
    # The switch was forced off before start-up (see Initialize-E2ELogs), so turning it on in the UI must produce
    # a brand new file
    Select-NavItem 'NavSettings'
    Start-Sleep -Milliseconds 1000
    $logToggle = Find-ById 'LogFileToggle'
    Assert-True ($null -ne $logToggle) "found the log-to-file switch"
    if ($logToggle) {
        $togglePattern = $logToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($togglePattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $togglePattern.Toggle() }
    }

    # 等到一份启动时不存在、属于今天的会话文件
    # 用「新出现的名字」而不是「最新的文件」来判定：后者在开关本来就开着时无法区分是不是本次新建的
    #
    # Waits for a today-session file that did NOT exist at start-up
    # A newly appearing name is used rather than the newest file: the latter cannot tell whether it was just
    # created when the switch was already on
    $sessionFile = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline -and -not $sessionFile) {
        $sessionFile = Get-ChildItem $script:e2eLogDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "^miditap-$script:e2eToday-[0-9]+[.]log$" -and $script:e2eLogFilesBefore -notcontains $_.Name } |
            Sort-Object Name | Select-Object -Last 1
        if (-not $sessionFile) { Start-Sleep -Milliseconds 300 }
    }
    Assert-True ($null -ne $sessionFile) "this session's log file appeared after the switch was turned on"
    if ($sessionFile) {
        # 名字里的编号是「当天第几次启动」，只增不减
        #
        # The number in the name is which launch of the day this was, and it only grows
        Assert-True ($sessionFile.Name -match "^miditap-$script:e2eToday-[0-9]+[.]log$") ("the name is miditap-<date>-<number>.log (actual: " + $sessionFile.Name + ")")
        $sessionText = Get-Content $sessionFile.FullName -Raw
        Assert-True ($sessionText -match "===== MIDITap") "the session header was written to the file"
        Assert-True ($sessionText -match "session [0-9]+") "the session header carries the launch number of the day"
        Assert-True ($sessionText -match "logging enabled") "the enable marker was recorded"

        # 打开开关之前就已经发生的那些行必须被补写进来，而且要排在 logging enabled 之前
        # 不按具体文案断言：文案随界面语言变，而行数与位置与语言无关
        #
        # Lines that already happened before the switch was turned on must be backfilled, and must come BEFORE
        # "logging enabled"
        # No specific wording is asserted: wording follows the UI language, whereas a line count and a position
        # do not
        $sessionLines = @(Get-Content $sessionFile.FullName | Where-Object { $_.Trim() })
        $enabledLineIndex = [array]::IndexOf($sessionLines, "logging enabled")
        Assert-True ($sessionLines.Count -gt 8) ("the file holds more than the session header once the switch is on (actual " + $sessionLines.Count + " lines)")
        Assert-True ($enabledLineIndex -gt 1) ("the backfilled lines precede logging enabled (it sits on line " + ($enabledLineIndex + 1) + ")")

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
        Assert-True ($sessionText -match "60") "the playing event reached the session file"
    }

    Write-Section "2. Housekeeping: drop the legacy shapes and archive each past day"
    # 整理在启动时跑，种子文件在启动前就放好了（见 Initialize-E2ELogs）
    #
    # Housekeeping runs at start-up, and the seed files were placed before the app started (see Initialize-E2ELogs)
    $legacyGone = (-not (Test-Path (Join-Path $script:e2eLogDir "miditap.log"))) -and (-not (Test-Path (Join-Path $script:e2eLogDir "miditap.log.1")))
    Assert-True $legacyGone "the legacy miditap.log and miditap.log.1 are gone"

    $archive = Join-Path $script:e2eLogDir "miditap-$script:e2eYesterday.tar.gz"
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path $archive)) { Start-Sleep -Milliseconds 300 }
    Assert-True (Test-Path $archive) "yesterday's sessions were merged into the day archive miditap-$script:e2eYesterday.tar.gz"

    # 源文件收进归档后就不该再留在目录里
    # 会话级的 .gz 也不再产生：整天归档一步到位，没有中间产物
    #
    # Once a source is inside the archive it should no longer sit in the directory
    # No session-level .gz is produced either: the day archive is made in one step, with no intermediate file
    Assert-True (-not (Test-Path (Join-Path $script:e2eLogDir "miditap-$script:e2eYesterday-1.log"))) "the source session file went into the archive (1)"
    Assert-True (-not (Test-Path (Join-Path $script:e2eLogDir "miditap-$script:e2eYesterday-2.log"))) "the source session file went into the archive (2)"
    Assert-True (-not (Test-Path (Join-Path $script:e2eLogDir "miditap-$script:e2eYesterday-1.log.gz"))) "no session-level .gz was produced (1)"
    Assert-True (-not (Test-Path (Join-Path $script:e2eLogDir "miditap-$script:e2eYesterday-2.log.gz"))) "no session-level .gz was produced (2)"

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

        Assert-True ($archiveEntries.Count -eq 2) ("the archive holds two session entries (actual: " + $archiveEntries.Count + ")")
        # 条目名是解压后可直接辨认的明文名字，内容也必须是解压后的原文
        #
        # The entry names are the identifiable plain-text names, and the content must be the decompressed original
        Assert-True ($archiveEntries -contains "miditap-$script:e2eYesterday-1.log|seeded first") "the entry name matches its content (1)"
        Assert-True ($archiveEntries -contains "miditap-$script:e2eYesterday-2.log|seeded second") "the entry name matches its content (2)"
    }

    # 今天的会话必须各自独立：合并它们会破坏「每次启动一个文件」
    #
    # Today's sessions stay separate: merging them would break the one-file-per-launch rule
    $todaySession = Get-ChildItem $script:e2eLogDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "^miditap-$script:e2eToday-[0-9]+[.]log$" } | Select-Object -First 1
    Assert-True ($null -ne $todaySession) "today's session file was not merged into an archive"

    Assert-NoCrash
}
finally {
    Stop-E2ETarget
    $exitCode = Show-E2ESummary
}

exit $exitCode
