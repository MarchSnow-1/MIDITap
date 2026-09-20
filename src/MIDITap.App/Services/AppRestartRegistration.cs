// AppRestartRegistration.cs — 让 Windows 在安装完成后能把本应用重新拉起来
//
// 为什么需要它：安装器路线下，是 Inno 用 Restart Manager 关掉我们，再由它决定装完是否重启
// 而 Inno 的 RestartApplications 有一个前提（官方文档原文）
//   the application needs to be using the Windows RegisterApplicationRestart API function
// 也就是说应用必须先向 Windows 登记"如果哪天我被关掉，请这样把我拉起来"
// 没有这一步，安装器关得掉我们，却开不回来，用户会看到"点了更新，应用就没了"
//
// 为什么只在点更新的那一刻注册，而不是启动时就注册
//   RegisterApplicationRestart 还覆盖系统重启后的自动拉起
//   而本应用启动后会恢复上次配置、可能立刻开始监听端口并注入按键
//   启动时就注册等于给**所有**用户加上"重启电脑后应用自己回来并开始工作"这个副作用
//   只在真正要更新时注册，副作用就只落在这一次更新上
//
// 为什么失败不抛异常
//   注册失败只会让安装器装完不自动重启，用户手动打开即可
//   更新流程不该因为这个附加动作失败而中断
//
// AppRestartRegistration.cs — lets Windows bring the app back after the installer finishes
//
// Why it is needed: on the installer route Inno closes us through the Restart Manager, and Inno then decides
// whether to restart after installing
// Its RestartApplications has a precondition, quoted from the official documentation
//   the application needs to be using the Windows RegisterApplicationRestart API function
// The app therefore has to register "if I am ever closed, here is how to bring me back"
// Without that, the installer can close us but cannot reopen us, and the user sees "I clicked update and the
// app simply vanished"
//
// Why it is registered at the moment of updating rather than at start-up
//   RegisterApplicationRestart also covers being relaunched after a system restart
//   This app restores the previous config on launch and may immediately start listening on a port and injecting keys
//   Registering at start-up would therefore give EVERY user the side effect of "the app comes back by itself
//   after a reboot and starts working"
//   Registering only when an update is really about to happen confines that side effect to that one update
//
// Why a failure is not thrown
//   A failed registration only means the installer will not relaunch the app, and the user can open it by hand
//   An additional step like this must not break the update flow

using System.Runtime.InteropServices;

namespace MIDITap.App.Services;

public static class AppRestartRegistration
{
    // 不传命令行参数：重新拉起时按普通启动处理，与用户双击图标一致
    //
    // No command line is passed: a relaunch is an ordinary start, the same as double-clicking the icon
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);

    /// <summary>
    /// 向 Windows 登记本进程，供安装器装完后重新拉起
    /// 成功返回 true；任何失败都返回 false 并记一条 debug，绝不抛异常
    ///
    /// Registers this process with Windows so the installer can relaunch it after installing
    /// Returns true on success; any failure returns false and leaves a debug line, never an exception
    /// </summary>
    public static bool Register()
    {
        try
        {
            // 返回的是 HRESULT，0 即 S_OK
            //
            // The return value is an HRESULT, and 0 is S_OK
            var result = RegisterApplicationRestart(null, 0);
            if (result == 0)
            {
                AppServices.Log.Debug(AppServices.I18n.T("log.debug.restartRegistered"));
                return true;
            }

            AppServices.Log.Debug(
                AppServices.I18n.T("log.debug.restartRegisterFailed", ("code", result.ToString())));
            return false;
        }
        catch (Exception err)
        {
            // 入口点找不到（极罕见）或其它异常：同样只是不自动重启
            //
            // A missing entry point (very unlikely) or any other error: the only consequence is no automatic relaunch
            AppServices.Log.Debug(
                AppServices.I18n.T("log.debug.restartRegisterFailed", ("code", err.Message)));
            return false;
        }
    }
}
