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
    // 包名的定义在 Core 的 UpdateStager 里：丢弃一次已落地的更新时也要按它删文件
    //
    // The package name is defined by UpdateStager in Core, which also deletes by it when a landed update is discarded
    private static string PackagePath =>
        Path.Combine(UpdateRoot, UpdateStager.PortablePackageFileName);

    /// <summary>当前阶段（UI 读取）</summary>
/// <remarks>The current stage (read by the UI)</remarks>
    public static UpdateStage Stage { get; private set; } = UpdateStage.Idle;

    /// <summary>
    /// 这一轮更新走的是哪条落地路径
    /// 暂存时定下，恢复暂存记录时也从记录里读回来：两条路的落地动作不同，中途必须保持一致
    ///
    /// Which route this round of updating takes
    /// Decided while staging, and read back from the record when a staged update is restored: the two routes
    /// apply differently, so the choice has to stay consistent throughout
    /// </summary>
    public static UpdatePackageKind Route { get; private set; } = UpdatePackageKind.Portable;

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
    /// 当前这一轮更新的独占运行
    /// 它保证取消时必须等本轮真正结束才清理磁盘，理由见 UpdateRun 的文件头
    ///
    /// The exclusive run for the current round of the update flow
    /// It guarantees the disk is only cleaned after the round has really ended, as UpdateRun explains
    /// </summary>
    private static readonly UpdateRun _run = new();

    /// <summary>
    /// 下载并解压到暂存区，成功后 <see cref="Stage"/> 为 ReadyToRestart但不会自动退出
    /// 何时重启由用户决定，避免打断正在进行的工作
    ///
    /// Downloads and stages
    /// On success Stage becomes ReadyToRestart, but the app does NOT exit on its own
    /// When to restart is the user's call, so an update does not interrupt their work
    /// </summary>
    public static Task<bool> PrepareAsync(UpdateInfo info)
        => _run.Start(token => PrepareCoreAsync(info, token));

    private static async Task<bool> PrepareCoreAsync(UpdateInfo info, CancellationToken cancellationToken)
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

            // 这一轮走哪条路：本机是安装器装的，才跑安装包
            // 便携包没有注册表项，只能靠辅助脚本逐文件替换
            // 资产也是按同一个种类挑出来的（见 BackendService 与设置页），因此两者必然一致
            //
            // Which route this round takes: the installer runs only when this machine installed the app that way
            // A portable copy has no registry entry and can only be updated file by file by the helper script
            // The asset was picked for the same kind (see BackendService and the settings page), so the two agree
            Route = InstallKindDetector.PackageKindFor(AppServices.InstallKind);

            // 下载 -> **SHA-256 校验** -> 落地，整条链路由 Core 负责（因此可被完整测试，包括"校验和不匹配必须拒绝安装"这条安全关键分支）
            // 便携路线落地成解压目录，安装器路线落地成那个安装包本身
            //
            // Download -> SHA-256 verification -> stage, all inside Core
            // That keeps the whole chain testable
            // The security-critical "a mismatch must refuse to install" branch is included
            // The portable route stages an extracted directory; the installer route stages the installer itself
            var staged = Route == UpdatePackageKind.Installer
                ? await UpdateStager
                    .PrepareInstallerAsync(
                        info.Asset, options, StagedUpdate.InstallerPath(AppServices.BaseDir),
                        progress, cancellationToken)
                    .ConfigureAwait(false)
                : await UpdateStager
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
                    // 安装器路线独有的失败：下载到的 exe 没通过身份校验，因此没有被运行
                    //
                    // A failure only the installer route reaches: the downloaded exe failed its identity check and
                    // was therefore never run
                    StageFailure.NotOurInstaller =>
                        "The downloaded installer was not confirmed as this app's: " +
                        (staged.Detail ?? "identity check failed"),
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
            // 这里记下的是 info.Latest，也就是**远端标签**（v2.0.1，带 v）
            // 而重启后核对时传给 StagedUpdate.Restore 的是程序集版本（2.0.1，不带 v）
            // 两者形式不同却指同一个版本，那一步因此按 semver 比较而不是比文字（见 StagedUpdate 的文件头）
            //
            // What is recorded here is info.Latest, the REMOTE TAG (v2.0.1, with the v)
            // After a relaunch StagedUpdate.Restore is handed the assembly version (2.0.1, without it)
            // The two forms differ yet name the same version, so that step compares versions as semver rather than
            // as text (see the StagedUpdate file header)
            StagedUpdate.Mark(
                AppServices.BaseDir, info.Latest, InstallKindDetector.PackageKindFor(AppServices.InstallKind));
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
    /// <summary>
    /// 落地这次更新。返回 true 表示调用方**应立即关闭窗口**
    ///   便携路线：启动辅助脚本，它等着本进程退出，因此必须真的关（否则脚本超时放弃）
    ///   安装器路线：启动安装包，随后由 Inno 关闭我们，因此这里返回 false
    ///     不主动关是有意的：安装包启动失败时用户手上还留着应用与错误提示
    ///
    /// Applies this update. A true return means the caller should close the window right away
    ///   The portable route: the helper script waits for this process to exit, so the window must really close,
    ///   or the script gives up after its timeout
    ///   The installer route: the installer is started and Inno then closes us, so this returns false
    ///   Not closing is deliberate: if the installer fails to start, the user still has the app and an error
    /// </summary>
    public static bool Apply()
    {
        // 开发构建没有可用的更新流程（见 AppServices.UpdatesEnabled）
        // Development builds have no update flow (see AppServices.UpdatesEnabled)
        if (!AppServices.UpdatesEnabled || Stage != UpdateStage.ReadyToRestart)
        {
            return false;
        }

        return Route == UpdatePackageKind.Installer ? LaunchInstaller() : LaunchApplyAndExit();
    }

    /// <summary>
    /// 安装器路线：先登记重启，再静默运行安装包
    /// 不返回"该关窗口"：由 Inno 关我们，它装完再按那条登记把我们拉回来
    ///
    /// The installer route: register for restart first, then run the installer silently
    /// It does not ask for the window to close: Inno closes us, and after installing it relaunches us through
    /// that registration
    /// </summary>
    private static bool LaunchInstaller()
    {
        var installer = StagedUpdate.InstallerPath(AppServices.BaseDir);
        if (!File.Exists(installer))
        {
            // 暂存内容被清理过、或用户手工删了：如实说清，而不是静默什么都不做
            //
            // The staged content was cleaned up, or the user deleted it: say so rather than doing nothing
            LastError = "the staged installer is missing";
            SetStage(UpdateStage.Failed);
            return false;
        }

        try
        {
            // **先登记再启动**：Inno 会在安装过程中关掉我们，那条登记必须已经存在
            // 顺序反了的话，装完不会有任何东西把我们拉回来
            //
            // Register BEFORE starting: Inno closes us during the install, so the registration has to exist by then
            // Reversing the order means nothing brings us back after the install
            AppRestartRegistration.Register();

            // /SILENT 保留进度窗，用户看得见在装
            // /SUPPRESSMSGBOXES 不弹任何询问
            // /NORESTART 不因为文件占用而要求重启系统，那是用户没要求的副作用
            //
            // /SILENT keeps the progress window, so the user can see it working
            // /SUPPRESSMSGBOXES suppresses every prompt
            // /NORESTART avoids a system reboot request over a file in use, which is a side effect nobody asked for
            var startInfo = new System.Diagnostics.ProcessStartInfo(installer)
            {
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = false,
                WorkingDirectory = AppServices.BaseDir,
            };
            System.Diagnostics.Process.Start(startInfo);
            AppServices.Log.Info(AppServices.I18n.T("update.apply.installer"));
            return false;
        }
        catch (Exception err)
        {
            LastError = err.Message;
            SetStage(UpdateStage.Failed);
            return false;
        }
    }

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

        // 更新已经装上了，或记录里的版本比在跑的更旧：两种情形下这条记录都没有意义了
        // 它留在磁盘上的那份内容同样没有用途，一并清掉
        // 少了这一步，包会一直占着几十 MB，而且下次启动仍会被当成"可恢复的更新"再提示一遍
        //
        // The update has already been installed, or the record is older than the running version
        // In both cases the record means nothing any more, and so does what it left on disk
        // Both are cleared here
        // Without this step the package keeps tens of MB occupied and the next launch restores it as an update
        // awaiting a restart, offering the same version again
        if (result.Verdict is StagedRestoreVerdict.AlreadyInstalled or StagedRestoreVerdict.Superseded)
        {
            UpdateStager.DiscardStagedContent(AppServices.BaseDir);
            AppServices.Log.Debug(AppServices.I18n.T(
                "log.debug.updateDiscardLanded", ("version", result.Version ?? string.Empty)));
        }

        if (result.Verdict != StagedRestoreVerdict.Ready)
        {
            return null;
        }

        // 路由从记录里读回来：两条路的落地动作不同，恢复时不能猜
        //
        // The route is read back from the record: the two routes apply differently, so a restore must not guess
        Route = result.Kind;
        Progress = 1;
        Stage = UpdateStage.ReadyToRestart;
        return result.Version;
    }

    /// <summary>
    /// 丢弃本轮更新：取消、等这一轮结束、清掉它留下的文件、回到空闲
    /// 关闭更新弹窗时调用
    /// 顺序不能颠倒，必须等运行结束才清理磁盘，理由见 UpdateRun 的文件头
    ///
    /// Discards this round: cancel it, wait for it to end, remove what it left behind, and return to idle
    /// Called when the update dialog is closed
    /// The order may not be reversed: the disk may only be cleaned once the round has ended, as UpdateRun explains
    /// </summary>
    public static async Task DiscardAsync()
    {
        if (!await _run.CancelAndWaitAsync().ConfigureAwait(true))
        {
            // 新一轮已经接手，它正在写自己的文件，这里什么都不要动
            //
            // A newer round has taken over and is writing its own files, so nothing is touched here
            return;
        }

        // 下载已经完成并暂存好了，那是用户想要的结果，不当作取消丢弃
        // 这一条是防御性的：ReadyToRestart 状态下弹窗不提供关闭入口
        //
        // The download finished and is staged, which is the outcome the user wanted, so it is not discarded as a cancel
        // This is defensive: the dialog offers no way to close in the ReadyToRestart state
        if (Stage == UpdateStage.ReadyToRestart)
        {
            return;
        }

        CleanStaging();
        Reset();

        // 丢弃这件事记一条 debug：取消之后状态是否真的回到空闲，事后只有日志能回答
        //
        // The discard is recorded at debug: only the log can answer afterwards whether cancelling really
        // returned the state to idle
        AppServices.Log.Debug(AppServices.I18n.T("log.debug.updateDiscard"));
    }

    /// <summary>丢弃暂存内容（只由 DiscardAsync 调用，见那里的顺序要求）</summary>
/// <remarks>Discards the staged content (called only by DiscardAsync, see the ordering requirement there)</remarks>
    private static void CleanStaging()
    {
        // 删除交给 Core：三条路径各自取自它们的定义处，界面层不再自己拼路径
        // 因此这一处**会连安装包一起删**：取消安装器路线的那一轮更新时，那个几十 MB 的 setup.exe 同样该走
        //
        // The deletion is delegated to Core, where each of the three paths comes from its own definition, so the UI
        // layer no longer builds them
        // That also means the installer is deleted here: cancelling a round on the installer route should take that
        // setup.exe, tens of MB of it, with it
        UpdateStager.DiscardStagedContent(AppServices.BaseDir);
        // 内容已经丢掉了，那条"等待重启"的记录也必须跟着失效
        // 两者不一致时下次启动会显示一个已经不存在了的更新
        //
        // The content is gone, so the "awaiting a restart" record has to go with it
        // Leaving the two out of step would make the next launch offer an update that no longer exists
        StagedUpdate.Clear(AppServices.BaseDir);
        Progress = 0;
    }

    /// <summary>把服务状态重置为空闲（只由 DiscardAsync 调用）</summary>
/// <remarks>Resets the service state to idle (called only by DiscardAsync)</remarks>
    private static void Reset()
    {
        LastError = null;
        Progress = 0;
        SetStage(UpdateStage.Idle);
    }
}
