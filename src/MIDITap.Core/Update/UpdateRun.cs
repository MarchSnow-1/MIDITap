// UpdateRun.cs — 更新的"一轮"运行：同一时刻至多一轮，取消时必须等它真正结束
//
// 为什么需要这条规则：取消下载要清掉磁盘上那份半成品，而下载器此刻仍持有那个文件的句柄
// 此时直接去删会同时出两件事
// 删除多半失败（文件被占用），几百 MB 的残包留在磁盘上
// 下载器把"写入失败"当作普通的下载失败回报，于是"取消"被记成一条错误
// 那条错误是在状态已经复位之后才写回的，因此会一直留着，下次打开弹窗就显示"更新失败"
// 因此顺序只能是：取消 -> 等这一轮结束 -> 再清理
//
// 这条规则属于更新流程本身而不是界面，所以放在 Core，并由单元测试锁住
//
// Why the rule is needed: cancelling a download has to remove the half-written package
// The downloader still holds that file's handle, and deleting it at that moment does two things at once
// The delete usually fails (the file is in use), leaving a hundreds-of-MB partial package on disk
// The downloader reports the failed write as an ordinary download failure, so a cancel is recorded as an error
// That error is written back after the state was already reset, so it stays behind
// The next time the dialog opens, it shows "update failed"
// The only workable order is therefore: cancel, wait for the round to end, then clean up
//
// That rule belongs to the update flow rather than to the UI, so it lives in Core and is locked by a unit test

namespace MIDITap.Core.Update;

/// <summary>
/// 更新的独占运行：同一时刻至多一轮，取消时等这一轮真正结束
///
/// One exclusive update round: at most one at a time, and cancelling waits for it to end
/// </summary>
public sealed class UpdateRun
{
    // 只保护这两个字段，运行体本身在锁外执行
    //
    // Only these two fields are guarded; the run body itself executes outside the lock
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task<bool>? _task;

    /// <summary>
    /// 开始新一轮并让它成为当前的一轮
    /// 上一轮的取消源在这里释放，因此同一时刻至多存在一个
    ///
    /// Starts a round and makes it the current one
    /// The previous round's cancellation source is disposed here, so at most one exists at a time
    /// </summary>
    public Task<bool> Start(Func<CancellationToken, Task<bool>> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var cancellation = new CancellationTokenSource();
        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = cancellation;
            try
            {
                _task = body(cancellation.Token);
            }
            catch (Exception err)
            {
                // 运行体同步抛出时也必须交出一个任务，否则取消源已经更新而任务还是上一轮的
                //
                // A body that throws synchronously still has to hand back a task
                // Otherwise the cancellation source is updated while the task is still the previous round's
                _task = Task.FromException<bool>(err);
            }
            return _task;
        }
    }

    /// <summary>
    /// 取消当前一轮并等它结束
    /// 返回 true 表示这一轮已经结束且期间没有新一轮接手，调用方可以安全清理它在磁盘上留下的东西
    /// 返回 false 表示已有新一轮在跑，调用方不得清理，否则会删掉新一轮正在写的文件
    ///
    /// Cancels the current round and waits for it to finish
    /// True means the round ended and no newer round took over, so the caller may safely remove what it left on disk
    /// False means a newer round is already running
    /// The caller must not clean up then, or it would delete files the new round is writing
    /// </summary>
    public async Task<bool> CancelAndWaitAsync()
    {
        Task<bool>? run;
        lock (_gate)
        {
            _cancellation?.Cancel();
            run = _task;
        }

        if (run is null)
        {
            return true;
        }

        try
        {
            await run.ConfigureAwait(false);
        }
        catch
        {
            // 运行体自身的异常不改变"它已经结束"这个结论
            // 调用方要判断的是磁盘上还留着什么，而不是这一轮成功与否
            //
            // An exception from the body does not change the fact that it has ended
            // The caller is deciding what is left on disk, not whether the round succeeded
        }

        lock (_gate)
        {
            return ReferenceEquals(run, _task);
        }
    }
}
