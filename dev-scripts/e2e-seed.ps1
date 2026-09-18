# e2e-seed.ps1 — 端到端测试的现场准备与还原（测试配置、日志种子）
#
# 三个库文件各管一件事，见 e2e-common.ps1 的文件头
# 本文件只管「应用启动之前要把什么放好」以及「结束时怎么放回去」
#
# 这里的东西都有副作用，因此每一处都先备份、结束必定还原
# 用户既有的配置与日志不能因为跑了一次测试就变样
#
# e2e-seed.ps1 — preparing and restoring the scene for the end-to-end tests (test config, log seed files)
#
# The three library files each own one concern; see the header of e2e-common.ps1
# This file only covers what has to be in place before the app starts, and how it is put back at the end
#
# Everything here has side effects, so each part is backed up first and always restored
# A user's existing config and logs must not change just because a test ran

# 用脚本自己的配置名，而不是默认配置名：后者跟随界面语言，脚本无从预知
# 取绝对路径：last_config 里存相对路径时，应用会把它拼到 config/ 之下，于是指向一个不存在的文件
# 后果很隐蔽：last_config 解析成 null，应用退回去加载默认配置（那份是空的），测试随后以一堆
# 「应按下却未绑定」的形式失败，看起来像应用坏了
#
# A config name of the script's own rather than the default one, which follows the UI language and cannot be
# predicted here
# An ABSOLUTE path is taken: with a relative one in last_config the app joins it onto config/ and points at a
# file that is not there
# The failure is subtle: last_config resolves to null, the app falls back to the empty default config, and the
# test then fails as a wall of "expected a mapping but found none" -- which looks like a broken app
function Initialize-E2EConfig {
    $script:e2eConfigPath = [System.IO.Path]::GetFullPath((Join-Path $script:e2eAppDir "config/e2e-config.json"))
    $script:e2eLastConfigPath = Join-Path $script:e2eAppDir ".storage/last_config"
    $script:e2eConfigBackup = if (Test-Path $script:e2eConfigPath) { Get-Content $script:e2eConfigPath -Raw } else { $null }
    $script:e2eLastConfigBackup = if (Test-Path $script:e2eLastConfigPath) { Get-Content $script:e2eLastConfigPath -Raw } else { $null }

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
    New-Item -ItemType Directory -Force -Path (Split-Path $script:e2eConfigPath) | Out-Null
    Set-Content -Path $script:e2eConfigPath -Value $testConfig -NoNewline

    # 把「上次配置」记录指向刚写的那份，启动时必定加载它
    # 早先的做法是删掉这条记录再依赖「随便挑一份能读的」，那要求目录里只有这一份配置
    # 而应用首次启动会自己生成一份默认配置，因此那条路不再确定
    #
    # Point the "last config" record at the file just written, so start-up loads it for certain
    # The earlier approach deleted the record and relied on picking any readable config, which assumed this was
    # the only one in the directory
    # The app now creates a default config on first launch, so that route is no longer deterministic
    New-Item -ItemType Directory -Force -Path (Split-Path $script:e2eLastConfigPath) | Out-Null
    Set-Content -Path $script:e2eLastConfigPath -Value $script:e2eConfigPath -NoNewline
}

<#
  日志整理用的种子文件，必须在应用启动之前放好：整理发生在启动时，启动后再放就赶不上这一轮
  种子分两类：上一版的旧格式（miditap.log 与 .log.1），以及「昨天」的两份明文会话
  期望结果：旧格式被删，昨天的两份会话直接并成 miditap-<昨天>.tar.gz，中途不产生任何会话级压缩文件

  落盘开关同时被置为关闭，这样日志脚本在界面上打开它时必然产生一份新文件，时序才是确定的

  Seed files for the housekeeping case, which have to be in place BEFORE the app starts: housekeeping runs at
  start-up, so seeding afterwards misses this round
  Two kinds of seed: the previous version's legacy shapes (miditap.log and .log.1), and two plain-text sessions
  dated yesterday
  Expected: the legacy shapes are removed, and yesterday's two sessions are merged straight into
  miditap-<yesterday>.tar.gz with no per-session compression in between

  The log-to-file switch is forced off at the same time, which makes the log script deterministic: turning it
  on in the UI must then create a brand new file
#>
function Initialize-E2ELogs {
    $script:e2eLogDir = Join-Path $script:e2eAppDir ".storage/logs"
    $script:e2eLogSettingPath = Join-Path $script:e2eAppDir ".storage/miditap_log_to_file"
    $script:e2eLogFilesBefore = @()
    if (Test-Path $script:e2eLogDir) {
        $script:e2eLogFilesBefore = @(Get-ChildItem $script:e2eLogDir -File | ForEach-Object { $_.Name })
    }
    $script:e2eLogSettingBackup = if (Test-Path $script:e2eLogSettingPath) { Get-Content $script:e2eLogSettingPath -Raw } else { $null }

    $script:e2eYesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
    $script:e2eToday = (Get-Date).ToString("yyyy-MM-dd")

    # 本脚本会碰到的确切文件名：先整份备份，结束时原样放回
    # 用文件复制而不是读成字符串：.tar.gz 是二进制，读成文本会坏掉
    #
    # The exact names this script touches: each is backed up whole and put back at the end
    # A file copy is used rather than reading text, because .tar.gz is binary and reading it as text corrupts it
    $script:e2eLogTouchNames = @(
        "miditap.log",
        "miditap.log.1",
        "miditap-$script:e2eYesterday-1.log",
        "miditap-$script:e2eYesterday-2.log",
        "miditap-$script:e2eYesterday.tar.gz"
    )
    $script:e2eLogBackupDir = Join-Path $env:TEMP ("miditap-e2e-logbackup-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $script:e2eLogBackupDir | Out-Null
    foreach ($name in $script:e2eLogTouchNames) {
        $seedSource = Join-Path $script:e2eLogDir $name
        if (Test-Path $seedSource) { Copy-Item $seedSource (Join-Path $script:e2eLogBackupDir $name) -Force }
    }

    New-Item -ItemType Directory -Force -Path $script:e2eLogDir | Out-Null
    Set-Content -Path $script:e2eLogSettingPath -Value "0" -NoNewline
    Set-Content -Path (Join-Path $script:e2eLogDir "miditap.log") -Value "legacy line" -NoNewline
    Set-Content -Path (Join-Path $script:e2eLogDir "miditap.log.1") -Value "legacy rotated line" -NoNewline
    Set-Content -Path (Join-Path $script:e2eLogDir "miditap-$script:e2eYesterday-1.log") -Value "seeded first" -NoNewline
    Set-Content -Path (Join-Path $script:e2eLogDir "miditap-$script:e2eYesterday-2.log") -Value "seeded second" -NoNewline
}

function Restore-E2EConfig {
    if ($null -ne $script:e2eConfigBackup) { Set-Content -Path $script:e2eConfigPath -Value $script:e2eConfigBackup -NoNewline }
    else { Remove-Item $script:e2eConfigPath -Force -ErrorAction SilentlyContinue }
    if ($null -ne $script:e2eLastConfigBackup) { Set-Content -Path $script:e2eLastConfigPath -Value $script:e2eLastConfigBackup -NoNewline }
    else { Remove-Item $script:e2eLastConfigPath -Force -ErrorAction SilentlyContinue }
}

function Restore-E2ELogs {
    # 日志目录还原：先放回备份，再删掉本次新增的文件
    # 顺序不能反：放回的那些文件本来就在「启动前已存在」的名单里，反了会把刚还原的删掉
    #
    # The log directory is restored: backups first, then files this run newly created are removed
    # The order matters: a restored backup is already in the at-start-up list, and reversing the order would
    # delete it
    if (Test-Path $script:e2eLogDir) {
        foreach ($name in $script:e2eLogTouchNames) {
            $restoreTarget = Join-Path $script:e2eLogDir $name
            $restoreBackup = Join-Path $script:e2eLogBackupDir $name
            if (Test-Path $restoreBackup) { Copy-Item $restoreBackup $restoreTarget -Force }
            else { Remove-Item $restoreTarget -Force -ErrorAction SilentlyContinue }
        }
        Get-ChildItem $script:e2eLogDir -File -ErrorAction SilentlyContinue |
            Where-Object { $script:e2eLogFilesBefore -notcontains $_.Name } |
            ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item $script:e2eLogBackupDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($null -ne $script:e2eLogSettingBackup) { Set-Content -Path $script:e2eLogSettingPath -Value $script:e2eLogSettingBackup -NoNewline }
    else { Remove-Item $script:e2eLogSettingPath -Force -ErrorAction SilentlyContinue }
}
