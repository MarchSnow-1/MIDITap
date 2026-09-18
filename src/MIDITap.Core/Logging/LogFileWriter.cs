// LogFileWriter.cs — 把活动日志按行追加到文件，带单会话体积上限
//
// 1) **写入走后台队列，Append 永不阻塞**
//    日志是在 MIDI 回调路径上产生的（音符按下/抬起都要记一条）
//    在那里同步做磁盘 I/O 会把回调拖慢并反压 MIDI 输入，进而丢音符
//    这与注入队列当初要绕开同步 SendInput 是同一类问题
//    因此 Append 只入队，真正的写盘由独立线程完成
//
// 2) **体积有上限，超过就截断**
//    日志文件若不封顶，长期运行会无限增长（尤其按着延音踏板时）
//    达到上限时写一行截断标记，此后不再追加
//    为什么不是轮转成 .1：每个会话已经是独立文件，再轮转会产生 miditap-2026-09-18-3.log.1
//    那不是本应用的命名规则，清理与归档都认不出它，于是它会一直留着
//    上限按**单次会话**算：128 MB 约等于满速演奏 13 小时
//
// Appends activity-log lines to a file, with a per-session size cap
//
// 1) Writes go through a background queue; Append never blocks
//    Log lines are produced on the MIDI callback path (every note on/off is logged)
//    Doing synchronous disk I/O there would slow the callback, back-pressure MIDI input and drop notes
//    That is the same class of problem the injection queue was created to avoid for SendInput
//    Append therefore only enqueues, and a dedicated thread does the writing
//
// 2) The size is capped, and the file is truncated once it is reached
//    An uncapped log grows without bound over a long run, especially while a sustain pedal is held
//    At the cap one truncation marker is written and appending stops
//    Why not rotate to .1: every session is already its own file, so rotating would produce
//    miditap-2026-09-18-3.log.1, which is not one of this app name shapes
//    Cleanup and archiving cannot recognise it, so it would stay forever
//    The cap applies to ONE session: 128 MB is roughly thirteen hours at full playing speed

using System.Collections.Concurrent;
using System.Text;

namespace MIDITap.Core.Logging;

/// <summary>
/// 按行追加写日志文件，后台线程落盘，超过单会话上限后写一行标记并停止追加
///
/// Appends log lines to a file, written by a background thread, stopping after a marker once the per-session cap is reached
/// </summary>
public sealed class LogFileWriter : IDisposable
{
    /// <summary>
    /// 单次会话的默认上限 128 MB
    /// 按满速演奏每秒 20 次按键、每次两条日志估算，这相当于约 13 小时
    ///
    /// The default cap for one session, 128 MB
    /// At twenty key presses a second with two lines each that is roughly thirteen hours
    /// </summary>
    public const long DefaultMaxBytes = 128L * 1024 * 1024;

    /// <summary>
    /// 达到上限时写入的标记行
    /// 用英文而不是本地化文案：本文件在 Core，取不到界面语言，而结构性行（会话头等）本来就都是英文
    ///
    /// The marker line written when the cap is reached
    /// English rather than localized copy: this file lives in Core and has no access to the UI language,
    /// and structural lines such as the session header are English already
    /// </summary>
    public const string TruncationMarker =
        "===== log truncated: the size cap was reached, later entries are not recorded =====";

    // 不带 BOM：日志文件给人和工具看
    // 开头多个不可见字节只会碍事
    //
    // No BOM: a log file is read by people and tools
    // A leading invisible byte only hurts
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly int NewLineBytes = Utf8NoBom.GetByteCount(Environment.NewLine);

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Task _worker;
    private volatile bool _disposed;

    // 已写入的字节数由本类自己累计，而不是每行去问文件大小
    // 每个会话文件只由这一个写入器写，因此累计值是准的，也省掉一次系统调用
    //
    // The byte count is accumulated here rather than asked of the file on every line
    // One session file has exactly one writer, so the running total is accurate and a system call is saved
    private long _written;

    // 截断标记只写一次
    //
    // The truncation marker is written once
    private bool _truncated;

    public LogFileWriter(string path, long maxBytes = DefaultMaxBytes)
    {
        Path = path;
        MaxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        TryCreateDirectory();
        _worker = Task.Factory.StartNew(DrainQueue, TaskCreationOptions.LongRunning);
    }

    /// <summary>日志文件路径 / Log file path</summary>
    public string Path { get; }

    /// <summary>触发截断的字节上限 / Byte cap that triggers truncation</summary>
    public long MaxBytes { get; }

    /// <summary>是否已经因为达到上限而停止追加 / Whether appending stopped because the cap was reached</summary>
    public bool Truncated => _truncated;

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
            WriteLine(line);
        }
    }

    private void WriteLine(string line)
    {
        if (_truncated)
        {
            return;
        }

        var bytes = Utf8NoBom.GetByteCount(line) + NewLineBytes;

        // 达到上限时写一行截断标记，此后不再追加
        // 这一行允许把文件略微推过上限：它必须写进去，否则日志末尾没有任何说明
        //
        // At the cap one truncation marker is written and appending then stops
        // That line may push the file slightly past the cap, which is acceptable: it has to be there,
        // otherwise the log ends with no explanation at all
        if (_written + bytes > MaxBytes)
        {
            _truncated = true;
            TryAppend(TruncationMarker + Environment.NewLine);
            return;
        }

        if (TryAppend(line + Environment.NewLine))
        {
            _written += bytes;
        }
    }

    /// <summary>追加一次，失败时安静返回 / Appends once, returning quietly on failure</summary>
    /// <remarks>
    /// 磁盘满、权限不足等：记日志失败不应影响应用运行
    ///
    /// Disk full, insufficient permissions and the like: a failed log write must not affect the app
    /// </remarks>
    private bool TryAppend(string text)
    {
        try
        {
            File.AppendAllText(Path, text, Utf8NoBom);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>创建日志目录（构造时一次，而不是每行一次）/ Creates the log directory once at construction rather than per line</summary>
    private void TryCreateDirectory()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch
        {
            // 路径非法时保持安静：随后的追加也会失败，但不会抛到调用方
            //
            // An illegal path stays quiet here: the later appends fail too, but never throw at the caller
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
