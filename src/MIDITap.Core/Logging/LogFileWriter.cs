// LogFileWriter.cs — 把活动日志按行追加到文件，带体积上限轮转
//
// 1) **写入走后台队列，Append 永不阻塞**
//    日志是在 MIDI 回调路径上产生的（音符按下/抬起都要记一条）
//    在那里同步做磁盘 I/O 会把回调拖慢并反压 MIDI 输入，进而丢音符
//    这与注入队列当初要绕开同步 SendInput 是同一类问题
//    因此 Append 只入队，真正的写盘由独立线程完成
//
// 2) **体积有上限**
//    日志文件若不封顶，长期运行会无限增长（尤其按着延音踏板时）
//    超过上限就把当前文件改名为 .1（替换旧 .1）
//    因此磁盘占用上限约为 2 × maxBytes
//
// Appends activity-log lines to a file, rotating at a size cap
//
// 1) Writes go through a background queue; Append never blocks
//    Log lines are produced on the MIDI callback path (every note on/off is logged)
//    Doing synchronous disk I/O there would slow the callback, back-pressure MIDI input and drop notes
//    That is the same class of problem the injection queue was created to avoid for SendInput
//
// 2) The file is size-capped
//    Rotating to ".1" whenever the cap is exceeded keeps total disk usage at roughly 2 x maxBytes
//    The file therefore does not grow without bound

using System.Collections.Concurrent;
using System.Text;

namespace MIDITap.Core.Logging;

/// <summary>
/// 按行追加写日志文件，后台线程落盘，超过上限轮转为 .1
///
/// Appends log lines to a file, written by a background thread and rotated to .1 past the cap
/// </summary>
public sealed class LogFileWriter : IDisposable
{
    /// <summary>
    /// 默认上限 2 MB（连同 .1 备份，磁盘占用上限约 4 MB）
    ///
    /// Default cap of 2 MB; with the .1 backup, disk usage stays under about 4 MB
    /// </summary>
    public const long DefaultMaxBytes = 2 * 1024 * 1024;

    // 不带 BOM：日志文件给人和工具看
    // 开头多个不可见字节只会碍事
    //
    // No BOM: a log file is read by people and tools
    // A leading invisible byte only hurts
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Task _worker;
    private volatile bool _disposed;

    public LogFileWriter(string path, long maxBytes = DefaultMaxBytes)
    {
        Path = path;
        MaxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        _worker = Task.Factory.StartNew(DrainQueue, TaskCreationOptions.LongRunning);
    }

    /// <summary>日志文件路径 / Log file path</summary>
    public string Path { get; }

    /// <summary>触发轮转的字节上限 / Byte cap that triggers rotation</summary>
    public long MaxBytes { get; }

    /// <summary>
    /// 轮转后的备份路径（与主文件同目录）
    ///
    /// Backup path after rotation, in the same directory as the main file
    /// </summary>
    public string BackupPath => Path + ".1";

    /// <summary>
    /// 入队一行
    /// 不阻塞、不抛异常 —— 记日志本身不能成为新的失败点
    ///
    /// Enqueues one line
    /// It never blocks and never throws: logging itself must not become a new failure point
    /// </summary>
    public void Append(string line)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            // 队列无上限：宁可多占一点内存，也不要因为"队列满"而丢掉正在发生的日志
            //
            // The queue is unbounded
            // A little more memory beats dropping log lines of something happening right now
            // Those would otherwise be lost merely because the queue was full
            _queue.TryAdd(line);
        }
        catch (InvalidOperationException)
        {
            // 与 Dispose 竞争时 CompleteAdding 已调用，忽略即可
            //
            // When racing with Dispose, CompleteAdding has already been called, so this can be ignored
        }
    }

    private void DrainQueue()
    {
        // GetConsumingEnumerable 在 CompleteAdding 且队列排空后结束
        // 因此 Dispose 能保证已入队的行全部落盘
        // 退出时不该丢掉最后几条（那往往正是最要紧的几条）
        //
        // GetConsumingEnumerable ends after CompleteAdding once drained
        // Dispose therefore guarantees every queued line is written
        // The last few lines are often the ones that matter
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                WriteLine(line);
            }
            catch
            {
                // 磁盘满、权限不足等：记日志失败不应影响应用运行
                //
                // Disk full, insufficient permissions and the like
                // A failed log write must not affect the app
            }
        }
    }

    private void WriteLine(string line)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        RotateIfNeeded();
        File.AppendAllText(Path, line + Environment.NewLine, Utf8NoBom);
    }

    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            if (File.Exists(BackupPath))
            {
                File.Delete(BackupPath);
            }
            File.Move(Path, BackupPath);
        }
        catch
        {
            // 轮转失败就继续往原文件写
            // 日志连续性比严格遵守上限更重要
            //
            // A failed rotation keeps writing to the original file
            // Log continuity matters more than respecting the cap exactly
        }
    }

    /// <summary>停止并等待已入队的行落盘 / Stops and waits for the queued lines to reach disk</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _queue.CompleteAdding();
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 等待超时或线程异常都不阻塞退出
            //
            // Neither a wait timeout nor a thread exception may block shutdown
        }
        finally
        {
            _queue.Dispose();
        }
    }
}
