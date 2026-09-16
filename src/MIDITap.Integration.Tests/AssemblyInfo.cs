// AssemblyInfo.cs — 集成测试的并行设置
//
// Parallelism settings for the integration tests

using Xunit;

// MIDI 端口是**进程外共享资源**
// 多个测试类并行打开同一个输入端口会互相抢消息
// 因此会造成难以复现的偶发失败
// 串行执行换来的是稳定结果，对集成测试是划算的
//
// MIDI ports are a shared out-of-process resource
// Parallel test classes opening the same input port steal messages from each other
// That produces hard-to-reproduce flakes
// Running serially costs a little time and buys determinism
// That is the right trade for integration tests
[assembly: CollectionBehavior(DisableTestParallelization = true)]
