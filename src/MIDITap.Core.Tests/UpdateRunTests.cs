// UpdateRunTests.cs — 取消一轮更新时，必须等它真正结束
//
// 为什么值得测：这条顺序错了会在用户那里表现为两个缺陷
// 取消下载后再打开更新弹窗，显示的是上一次的"更新失败"
// 磁盘上留下一个几百 MB 的半成品包
// 两者都源于"在运行还在写文件的时候就去删那个文件"
// 而这段时序在界面层无法覆盖，App 工程没有测试项目
//
// Why these tests matter: getting the order wrong shows up as two defects for the user
// Reopening the update dialog after a cancelled download shows the previous "update failed"
// A partial package of hundreds of MB is left on disk
// Both come from removing a file while the round is still writing to it
// That timing cannot be covered at the UI layer, where the App project has no test project
//
// UpdateRunTests.cs — cancelling an update round has to wait for it to actually end

using MIDITap.Core.Update;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class UpdateRunTests
{
    [Fact]
    public async Task CancelAndWaitAsync_ReturnsTrue_WhenNothingIsRunning()
    {
        var run = new UpdateRun();

        Assert.True(await run.CancelAndWaitAsync());
    }

    [Fact]
    public async Task CancelAndWaitAsync_WaitsForTheBodyToFinish()
    {
        var run = new UpdateRun();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyFinished = false;

        _ = run.Start(async _ =>
        {
            await release.Task;
            bodyFinished = true;
            return true;
        });

        var cancel = run.CancelAndWaitAsync();

        // 运行体还没结束，取消就不能返回，此刻清理磁盘会与它争抢同一个文件
        //
        // The body has not finished, so the cancel must not return yet
        // Cleaning up at this point would race it for the same file
        Assert.False(cancel.IsCompleted);

        release.SetResult();

        Assert.True(await cancel);
        Assert.True(bodyFinished);
    }

    [Fact]
    public async Task CancelAndWaitAsync_WaitsEvenWhenTheBodyIgnoresCancellation()
    {
        var run = new UpdateRun();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCancellation = false;

        _ = run.Start(async token =>
        {
            await release.Task;
            sawCancellation = token.IsCancellationRequested;
            return false;
        });

        var cancel = run.CancelAndWaitAsync();
        Assert.False(cancel.IsCompleted);

        release.SetResult();

        Assert.True(await cancel);
        // 令牌确实被取消了，而什么时候收尾由运行体自己决定
        //
        // The token really was cancelled, and the body decides when to stop
        Assert.True(sawCancellation);
    }

    [Fact]
    public async Task CancelAndWaitAsync_ReturnsFalse_WhenANewerRoundTookOver()
    {
        var run = new UpdateRun();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = run.Start(async _ => { await first.Task; return true; });
        var cancel = run.CancelAndWaitAsync();

        // 第一轮结束之前，新一轮就接手了
        //
        // A newer round takes over before the first one ends
        _ = run.Start(async _ => { await second.Task; return true; });
        first.SetResult();

        // 调用方据此知道不能清理，因为新一轮正在写自己的文件
        //
        // The caller learns from this that it must not clean up, since the newer round is writing its own files
        Assert.False(await cancel);

        second.SetResult();
    }

    [Fact]
    public async Task CancelAndWaitAsync_ReturnsTrue_EvenWhenTheBodyThrows()
    {
        var run = new UpdateRun();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = run.Start(_ => FailAfterAsync(release.Task));
        var cancel = run.CancelAndWaitAsync();
        release.SetResult();

        Assert.True(await cancel);
    }

    [Fact]
    public async Task Start_ReturnsTheTaskTheRoundRunsOn()
    {
        var run = new UpdateRun();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var returned = run.Start(_ => completion.Task);

        completion.SetResult(true);

        Assert.True(await returned);
    }

    [Fact]
    public async Task Start_ReplacesTheCurrentRound()
    {
        var run = new UpdateRun();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = run.Start(async _ => { await first.Task; return true; });
        var cancelOfFirst = run.CancelAndWaitAsync();

        var secondTask = run.Start(async _ => { await second.Task; return false; });
        first.SetResult();
        second.SetResult();

        Assert.False(await cancelOfFirst);
        Assert.True(await firstTask);
        Assert.False(await secondTask);
    }

    private static async Task<bool> FailAfterAsync(Task gate)
    {
        await gate.ConfigureAwait(false);
        throw new InvalidOperationException("example");
    }
}
