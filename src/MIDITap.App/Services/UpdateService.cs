// UpdateService.cs — 把"点击更新"串起来：下载 → 校验 → 解压暂存 → 交给辅助脚本 → 退出
//
// 为什么必须退出应用才能更新：
//   运行中的 exe 无法覆盖自身，而自包含 WinUI 应用运行时锁住了数百个 DLL（Microsoft.ui.xaml.dll 等）
//   因此流程是"先全部准备好，再退出"
//   由独立脚本（scripts/apply-update.ps1，不位于应用目录、不受更新影响）在退出后替换文件并重启
//
// UpdateService.cs — wires up the "click update" flow
// Download → verify → extract to staging → hand over to the helper script → exit
//
// Why the app has to exit before it can update:
//   A running exe cannot overwrite itself
//   A self-contained WinUI app also holds hundreds of DLLs open while it runs (Microsoft.ui.xaml.dll among them)
//   The flow is therefore "prepare everything first, then exit"
//   The standalone script is scripts/apply-update.ps1, which lives outside the app directory and is unaffected by the update
//   It replaces the files and restarts the app after we are gone

using MIDITap.Core.Settings;
using MIDITap.Core.Update;

namespace MIDITap.App.Services;

/// <summary>更新流程的阶段，供 UI 显示</summary>
/// <remarks>The stages of the update flow, for display in the UI</remarks>
public enum UpdateStage
{
    Idle,
    Downloading,
    Extracting,
    ReadyToRestart,
    Failed,
}

public static class UpdateService
{
    /// <summary>更新用的工作目录（在 exe 旁的 .update/ 下）</summary>
/// <remarks>The working directory used for updates (under .update/ next to the exe)</remarks>
    private static string UpdateRoot => AppPaths.UpdateDir(AppServices.BaseDir);

    // 解压目录的定义在 Core 的 StagedUpdate 里（恢复更新时也要用同一路径）
    // 同一个路径不在两处各写一遍，改名时就不会漏掉一处
    //
    // The staging directory is defined by StagedUpdate in Core, which needs the same path when restoring
    // One path, one definition, so a rename cannot miss a place
    private static string StagingDir => StagedUpdate.StagingDir(AppServices.BaseDir);
    private static string PackagePath => Path.Combine(UpdateRoot, "package.zip");

    /// <summary>当前阶段（UI 读取）</summary>
/// <remarks>The current stage (read by the UI)</remarks>
    public static UpdateStage Stage { get; private set; } = UpdateStage.Idle;

    /// <summary>阶段变化通知（UI 线程调度由订阅方负责）</summary>
/// <remarks>Raised when the stage changes; dispatching to the UI thread is the subscriber's job</remarks>
    public static event Action? StageChanged;

    /// <summary>下载进度 0..1（未开始时为 0）</summary>
/// <remarks>Download progress from 0 to 1 (0 before the download starts)</remarks>
    public static double Progress { get; private set; }

    /// <summary>
    /// 下载进度变化通知（UI 线程调度由订阅方负责）
    /// 为什么不复用 StageChanged：下载过程中的进度变化不动阶段，只在下载开始与结束时各变一次
    /// 只订阅阶段的话，用户看到的是 0% 停着不动，然后一下子跳到"将重启更新"
    ///
    /// Raised as the download progresses (dispatching to the UI thread is the subscriber's job)
    /// Why not reuse StageChanged: progress changes during a download do not move the stage,
    /// which changes only when the download starts and ends
    /// A stage-only subscription leaves the user staring at a stuck 0%, then jumping straight to "restart to update"
    /// </summary>
    public static event Action? ProgressChanged;

    /// <summary>
    /// 进度通知的最小间隔（毫秒）
    /// 下载器每读到一个数据块就上报一次，实测可达每秒数百次
    /// 每次都投递到 UI 线程会让派发队列堆积，界面反而更卡
    /// 按此间隔合并后进度条照样连续，UI 线程最多每秒被叫醒十次
    ///
    /// The minimum interval between progress notifications, in milliseconds
    /// The downloader reports once per data block, measured in the hundreds per second
    /// Dispatching every one of them piles up on the UI thread and makes the interface laggier
    /// Coalescing at this interval keeps the bar smooth while waking the UI thread at most ten times a second
    /// </summary>
    private const int ProgressThrottleMs = 100;

    // 上一次发出进度通知的时刻（Environment.TickCount64 的毫秒值）
    // 用 Interlocked 读写：没有同步上下文时 Progress<T> 的回调会落在多个线程池线程上
    //
    // When the last progress notification went out, in Environment.TickCount64 milliseconds
    // Read and written with Interlocked: without a synchronization context the Progress<T> callback
    // lands on several thread-pool threads
    private static long _lastProgressNotify;

    /// <summary>最近一次失败原因（成功时为 null）</summary>
/// <remarks>The reason for the most recent failure (null on success)</remarks>
    public static string? LastError { get; private set; }

    private static void SetStage(UpdateStage stage)
    {
        Stage = stage;
        StageChanged?.Invoke();
    }

    /// <summary>
    /// 下载并解压到暂存区，成功后 <see cref="Stage"/> 为 ReadyToRestart但不会自动退出
    /// 何时重启由用户决定，避免打断正在进行的工作
    ///
    /// Downloads and stages
    /// On success Stage becomes ReadyToRestart, but the app does NOT exit on its own
    /// When to restart is the user's call, so an update does not interrupt their work
    /// </summary>
    public static async Task<bool> PrepareAsync(UpdateInfo info, CancellationToken cancellationToken = default)
    {
        // 开发构建没有更新这回事（见 AppServices.UpdatesEnabled）
        // 界面已经不会走到这里，这层是让"更新功能"本身不成立，而不只是按钮点不动
        // 将来某个忘记判定的调用方，也不会把开发构建替换成正式版
        //
        // A development build has no update to speak of (see AppServices.UpdatesEnabled)
        // The UI never reaches this point
        // The guard exists so that the *feature* is unavailable rather than just the button
        // A future caller that forgets to check cannot replace a development build with a release
        if (!AppServices.UpdatesEnabled)
        {
            return false;
        }

        LastError = null;
        if (info.Asset is null)
        {
            LastError = "No downloadable asset for this platform";
            SetStage(UpdateStage.Failed);
            return false;
        }

        var options = AppServices.Backend.UpdateOptions;
        try
        {
            Progress = 0;
            // 归零时刻戳：本轮下载的第一次上报要立刻反映到界面上，不能等满一个间隔
            //
            // The timestamp is reset so this download's first report reaches the UI at once
            // rather than waiting out an interval
            Interlocked.Exchange(ref _lastProgressNotify, 0);
            SetStage(UpdateStage.Downloading);

            // 属性每次都更新，通知按 ProgressThrottleMs 合并
            // 没有通知这一步时，进度条只会在阶段切换时重绘两次（0% 与 100%），下载途中的上报全部看不见
            // 而没有节流时，上报的频次要远高于屏幕能呈现的帧数，多出来的只会堆在派发队列里
            //
            // The property updates every time; the notification is coalesced to ProgressThrottleMs
            // Without a notification at all the bar would repaint only when the stage changes, twice (0% and 100%),
            // and every report in between would never reach the screen
            // Without the throttle the reports arrive far more often than the screen can present frames,
            // and the surplus merely queues up on the dispatcher
            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = p.Fraction;

                // 下载收尾那一次必须放行
                // 它之后紧跟着对整包做 SHA-256 校验（几百兆要花一会儿），此间阶段仍是"下载中"
                // 若这次被节流掉，屏幕上会一直停在一个不足 100% 的数字上，看起来像卡住了
                //
                // The final report must go through
                // It is followed by the SHA-256 check over the whole package (a while, at a few hundred MB),
                // and the stage is still "downloading" during that
                // Throttling this one away would leave a number below 100% on screen, looking stuck
                var finished = p.Fraction >= 1.0;
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastProgressNotify);
                if (!finished && now - last < ProgressThrottleMs)
                {
                    return;
                }
                Interlocked.Exchange(ref _lastProgressNotify, now);
                ProgressChanged?.Invoke();
            });

            // 下载 -> **SHA-256 校验** -> 解压，整条链路由 Core 负责（因此可被完整测试，包括"校验和不匹配必须拒绝安装"这条安全关键分支）
            //
            // Download -> SHA-256 verification -> extract, all inside Core
            // That keeps the whole chain testable
            // The security-critical "a mismatch must refuse to install" branch is included
            var staged = await UpdateStager
                .PrepareAsync(info.Asset, options, StagingDir, PackagePath, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!staged.Ok)
            {
                LastError = staged.Failure switch
                {
                    StageFailure.Download => "Download failed",
                    // 明确区分"包不可信"与普通失败：这是用户该知道的重要区别
                    //
                    // The "package is not trustworthy" case is kept apart from an ordinary failure
                    // That is an important difference for the user to know
                    StageFailure.ChecksumMismatch =>
                        "Checksum mismatch — the downloaded package was discarded",
                    StageFailure.Extract => staged.Detail ?? "Extract failed",
                    // 包内容不是本应用：必须说清楚，否则用户只会看到"更新失败"而不知原因
                    //
                    // The package is not this app: say so
                    // Otherwise the user sees only "update failed" with no idea why
                    StageFailure.MissingRequiredContent =>
                        "This release is not a MIDITap build for Windows (missing: " +
                        (staged.Detail ?? "required files") + ")",
                    StageFailure.Cancelled => null,
                    _ => "Update failed",
                };
                if (staged.Failure == StageFailure.Cancelled)
                {
                    SetStage(UpdateStage.Idle);
                }
                else
                {
                    SetStage(UpdateStage.Failed);
                }
                return false;
            }

            // 校验结论记录下来：未提供校验和时会在日志里留痕，便于事后判断
            //
            // The verification verdict is recorded
            // When no checksum was provided, the log keeps a trace of it so the case can be judged afterwards
            if (staged.Checksum?.Verdict == ChecksumVerdict.NotProvided)
            {
                AppServices.Log.Warn(AppServices.I18n.T("update.checksum.notProvided"));
            }

            Progress = 1;
            StagedUpdate.Mark(AppServices.BaseDir, info.Latest);
            SetStage(UpdateStage.ReadyToRestart);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 用户取消：回到空闲而不是失败，避免留下一条实际上没出问题的错误提示
            //
            // The user cancelled: go back to idle rather than failed
            // That way no error is left behind for something that did not actually go wrong
            LastError = null;
            SetStage(UpdateStage.Idle);
            return false;
        }
        catch (Exception err)
        {
            LastError = err.Message;
            SetStage(UpdateStage.Failed);
            return false;
        }
    }

    /// <summary>
    /// 启动辅助脚本并让主程序退出。调用方应在返回后立即请求关闭窗口
    /// 脚本会等待本进程退出，所以这里**必须**真的退：否则脚本会超时并放弃（退出码 3）
    ///
    /// Launches the helper and expects the caller to shut the window down right after
    /// The helper waits for this process to exit, so exiting is mandatory
    /// Otherwise it times out (code 3)
    /// </summary>
    public static bool LaunchApplyAndExit()
    {
        // 开发构建没有可用的更新流程（见 AppServices.UpdatesEnabled）
        // Development builds have no update flow (see AppServices.UpdatesEnabled)
        if (!AppServices.UpdatesEnabled || Stage != UpdateStage.ReadyToRestart)
        {
            return false;
        }

        var script = Path.Combine(AppServices.BaseDir, UpdateStager.ApplyUpdateScriptRelativePath);
        if (!File.Exists(script))
        {
            // 便携版目录里脚本缺失（例如用户只拷贝了 exe）
            // 这是一种真实存在的用法，必须给出可理解的错误，而不是静默什么都不做
            //
            // The script can genuinely be missing (a user who copied just the exe)
            // That usage exists, so report something understandable instead of silently doing nothing
            LastError = "scripts/apply-update.ps1 not found";
            SetStage(UpdateStage.Failed);
            return false;
        }

        var logPath = Path.Combine(UpdateRoot, "apply.log");
        try
        {
            var arguments =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" " +
                $"-AppDir \"{AppServices.BaseDir}\" " +
                $"-StagingDir \"{StagingDir}\" " +
                $"-AppExe \"{Path.GetFileName(Environment.ProcessPath ?? "MIDITap.exe")}\" " +
                $"-ProcessId {Environment.ProcessId} " +
                $"-LogPath \"{logPath}\" " +
                "-WaitSeconds 60";

            // **必须挑一个确实存在的宿主**：
            //   * pwsh（PowerShell 7）**不是 Windows 自带**，用户机器上很可能没有
            //     写死 pwsh 会让更新在这类机器上直接失败，而且失败得无声无息
            //   * powershell.exe（5.1）自 Windows 7 起随系统提供，因此是可靠的兜底
            // 脚本本身已按 5.1 的语法与编码要求编写（带 BOM 的 UTF-8、不用 #Requires），两个宿主都能跑
            //
            // **The host has to be one that actually exists on the machine**
            // pwsh (PowerShell 7) does NOT ship with Windows and is often absent
            // Hardcoding it would make the update fail on such machines, and fail silently
            // powershell.exe (5.1) ships with Windows, so it is the dependable fallback
            // The script is written to 5.1's syntax and encoding rules (UTF-8 with a BOM, no #Requires),
            // so both hosts can run it
            var host = ResolvePowerShellHost();
            var startInfo = new System.Diagnostics.ProcessStartInfo(host)
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppServices.BaseDir,
            };
            System.Diagnostics.Process.Start(startInfo);
            return true;
        }
        catch (Exception err)
        {
            LastError = err.Message;
            SetStage(UpdateStage.Failed);
            return false;
        }
    }

    /// <summary>
    /// 挑一个可用的 PowerShell 宿主：优先 5.1（系统自带），找不到再试 pwsh
    /// 顺序反过来的话，在没装 PowerShell 7 的机器上会直接失败
    ///
    /// Picks a usable PowerShell host: the bundled 5.1 first, then pwsh
    /// The other order would fail outright on a machine without PowerShell 7
    /// </summary>
    private static string ResolvePowerShellHost()
    {
        // 带完整路径而不是裸名字：PATH 被改过时裸名字会解析失败
        //
        // A full path rather than a bare name: a modified PATH could fail to resolve the bare one
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windowsPowerShell = Path.Combine(
            system32,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (File.Exists(windowsPowerShell))
        {
            return windowsPowerShell;
        }

        // 兜底：Windows 没有 powershell.exe 是极罕见的，但仍要有退路
        //
        // Fallback: a Windows without powershell.exe is very unlikely, yet a way out is still needed
        return "pwsh";
    }

    /// <summary>
    /// 启动时把上次"下载好但没重启"的更新恢复成「重启并更新」状态；没有可恢复的更新时返回 null
    /// 返回的版本号供调用方构造更新信息，其余字段那时都已无意义（包已经落盘了）
    ///
    /// On startup, restores an update downloaded earlier but never restarted to the "restart and update" state
    /// Returns null when there is nothing to restore
    /// The returned version lets the caller build the update information; the other fields no longer matter,
    /// because the package is already on disk
    /// </summary>
    public static string? TryRestoreStaged()
    {
        // 开发构建没有更新流程（见 AppServices.UpdatesEnabled）
        //
        // A development build has no update flow (see AppServices.UpdatesEnabled)
        if (!AppServices.UpdatesEnabled)
        {
            return null;
        }

        // 判断本身在 Core（见 StagedUpdate），那里才测得到 —— 本方法只为它挂上界面层的前置条件
        //
        // The decision itself lives in Core (see StagedUpdate), which is where it can be tested
        // This method only adds the UI-layer precondition around it
        var result = StagedUpdate.Restore(AppServices.BaseDir, AppServices.AppVersion);

        if (result.Verdict == StagedRestoreVerdict.Incomplete)
        {
            // 上次的下载不完整：说清是哪个版本被丢弃了，不然用户只会发现"更新不见了"
            //
            // The last download was incomplete: name the version that was discarded,
            // otherwise the user just finds an update that vanished
            AppServices.Log.Warn(
                AppServices.I18n.T("update.staged.incomplete", ("version", result.Version ?? string.Empty)));
        }

        if (result.Verdict != StagedRestoreVerdict.Ready)
        {
            return null;
        }

        Progress = 1;
        Stage = UpdateStage.ReadyToRestart;
        return result.Version;
    }

    /// <summary>丢弃暂存内容（用户放弃更新、或开始新一轮时调用）</summary>
/// <remarks>Discards the staged content (called when the user gives up on the update or starts a new round)</remarks>
    public static void CleanStaging()
    {
        try
        {
            if (Directory.Exists(StagingDir))
            {
                Directory.Delete(StagingDir, recursive: true);
            }
            if (File.Exists(PackagePath))
            {
                File.Delete(PackagePath);
            }
        }
        catch
        {
            // 清理失败不影响流程：下一轮会重建
            //
            // A failed cleanup does not affect the flow: the next round rebuilds these files
        }
        // 内容已经丢掉了，那条"等待重启"的记录也必须跟着失效
        // 两者不一致时下次启动会显示一个已经不存在了的更新
        //
        // The content is gone, so the "awaiting a restart" record has to go with it
        // Leaving the two out of step would make the next launch offer an update that no longer exists
        StagedUpdate.Clear(AppServices.BaseDir);
        Progress = 0;
    }

    /// <summary>把服务状态重置为空闲（关闭更新提示后调用）</summary>
/// <remarks>Resets the service state to idle (called after the update prompt is closed)</remarks>
    public static void Reset()
    {
        LastError = null;
        Progress = 0;
        SetStage(UpdateStage.Idle);
    }
}
