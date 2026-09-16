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

    private static string StagingDir => Path.Combine(UpdateRoot, "new");
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
            SetStage(UpdateStage.Downloading);
            var progress = new Progress<DownloadProgress>(p => Progress = p.Fraction);

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
