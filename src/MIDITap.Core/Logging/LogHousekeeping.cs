// LogHousekeeping.cs — 启动时整理上一次留下的日志
//
// 每次启动只做一件事：把**早于今天**的每一天合并成一个 .tar.gz（见 ArchiveEarlierDays）
//
// 为什么放在启动时而不是退出时
// 退出要尽量快，而压缩几十上百 MB 会拖住关闭
// 启动时做还有一个好处：崩溃退出没有走到归档那一步，下一次启动会把它补上
//
// 为什么只压整天，不压单个会话
// 会话文件保持明文，解开归档拿到的就是可直接阅读的文本，用户不必再解一层
// 一天一个包，而这一天过去之前一个字节都不动它
//
// LogHousekeeping.cs — tidying up the previous session's logs at startup
//
// Every launch does one thing: merge every day **earlier than today** into one .tar.gz (see ArchiveEarlierDays)
//
// Why at startup rather than at exit
// Exit should be quick, and compressing tens or hundreds of MB would hold up the shutdown
// Starting also means a session that died before being archived is caught by the next launch
//
// Why whole days only, never a single session
// A session file stays plain text, so extracting an archive yields text that can be read directly
// One package per day, and nothing touches a day until it has passed

using System.Formats.Tar;
using System.IO.Compression;

namespace MIDITap.Core.Logging;

/// <summary>启动时的日志整理 / Log housekeeping performed at startup</summary>
public static class LogHousekeeping
{
    // 临时文件的统一后缀
    // 归档先写 .tmp 再改名，因此中途失败时不会留下一个看起来完整的 .tar.gz
    // 上一次留下的 .tmp 在下一轮开始时清掉，否则它们会一直堆着
    //
    // The shared suffix for temporary files
    // An archive writes .tmp first and renames, so a failure midway leaves no .tar.gz that looks complete
    // A .tmp left behind is removed when the next round starts, since otherwise they accumulate
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// 整天归档保留多少天（含今天）
    /// 7 天是定下的值：正常使用一天几十 MB，一周也就几百 MB
    /// 而「把出问题那天的日志发我」这条要求，至少需要一周的历史
    ///
    /// How many days of whole-day archives are kept, today included
    /// Seven was the agreed figure: ordinary use runs to tens of MB a day, so a week is a few hundred MB
    /// The send-me-that-day-log request needs at least a week of history to be answerable
    /// </summary>
    public const int RetentionDays = 7;

    /// <summary>
    /// 删除旧格式的日志文件（miditap.log 与轮转留下的 miditap.log.1），返回删掉的个数
    /// 旧格式只由上一版产生，新命名不会再写出它们，因此这一步实际上只生效一次
    /// 每次启动都跑一遍也无妨：文件不在了就是空转，而万一用户从旧版升级回来，它仍然会被清掉
    ///
    /// Removes the legacy log files (miditap.log and the miditap.log.1 left by rotation), returning how many went
    /// Only the previous version produced those shapes, and the new naming never writes them,
    /// so in practice this takes effect once
    /// Running it every launch costs nothing: with the files gone it is a no-op, and if a user returns from an
    /// older build they are still cleared
    /// </summary>
    public static int RemoveLegacyFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;

        foreach (var path in Directory.GetFiles(directory))
        {
            // 只认这两个确切的名字：判断走 LogFileNames.Parse，命名规则只有那一处
            //
            // Only those two exact names are recognised, and the decision goes through LogFileNames.Parse,
            // so the naming rules live in one place
            if (LogFileNames.Parse(Path.GetFileName(path)).Shape == LogFileShape.Legacy
                && DeleteQuietly(path))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// 把早于 today 的每一天合并成一个整天归档，返回归档的天数
    /// 今天不合并：它的会话必须各自独立，那正是「多次启动不混在一起」这条要求
    /// 已经合并过、且没有残留会话文件的那一天会被跳过，因此这一步是幂等的
    ///
    /// Merges every day earlier than today into one whole-day archive, returning how many days were archived
    /// Today is not merged: its sessions have to stay separate, which is the launches-must-not-be-mixed rule
    /// A day already merged with no leftover session files is skipped, which makes this step idempotent
    /// </summary>
    public static int ArchiveEarlierDays(string directory, DateOnly today)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        // 先按日期把会话文件分组：一天一个归档，而归档要按编号升序写入
        //
        // The session files are grouped by date first: one archive per day, written in ascending session order
        var byDay = new Dictionary<DateOnly, List<(int Session, string Path)>>();

        foreach (var path in Directory.GetFiles(directory))
        {
            var name = Path.GetFileName(path);

            if (name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuietly(path);
                continue;
            }

            var info = LogFileNames.Parse(name);
            if (info.Shape != LogFileShape.Session
                || info.Date is not { } date
                || info.Session is not { } session)
            {
                continue;
            }

            // 今天的不动：它的会话各自独立，要等到跨天之后才归档
            //
            // Today is left alone: its sessions stay separate and are archived only once the day has passed
            if (date >= today)
            {
                continue;
            }

            if (!byDay.TryGetValue(date, out var sessions))
            {
                sessions = [];
                byDay[date] = sessions;
            }
            sessions.Add((session, path));
        }

        var archived = 0;
        foreach (var (date, sessions) in byDay)
        {
            var sources = sessions
                .OrderBy(entry => entry.Session)
                .Select(entry => entry.Path)
                .ToList();

            if (ArchiveDay(directory, date, sources))
            {
                archived++;
            }
        }

        return archived;
    }

    /// <summary>
    /// 把一天的若干会话文件打成一个 .tar.gz
    /// 归档里装的是明文 .log，因此解开就能直接读，不必再解一层
    /// 先写 .tmp 再改名，归档成功之后才删源文件，中途失败不会丢日志
    ///
    /// Packs one day of session files into a single .tar.gz
    /// The archive holds plain .log files, so extracting it gives text that can be read directly
    /// It writes .tmp first and renames, and removes the sources only after the archive succeeded,
    /// so a failure midway loses no log
    /// </summary>
    private static bool ArchiveDay(string directory, DateOnly date, IReadOnlyList<string> sources)
    {
        var target = Path.Combine(directory, LogFileNames.DayArchiveName(date));
        var temp = target + TempSuffix;

        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                foreach (var source in sources)
                {
                    AddSessionEntry(tar, date, source);
                }
            }

            File.Move(temp, target, overwrite: true);

            // 改名成功之后才删源文件：顺序反了的话，中途失败就等于把日志丢了
            //
            // The sources are removed only after the rename succeeded
            // Reversing the order would lose the log if something failed midway
            foreach (var source in sources)
            {
                DeleteQuietly(source);
            }
            return true;
        }
        catch
        {
            DeleteQuietly(temp);
            return false;
        }
    }

    /// <summary>往归档里写一个会话，条目名用明文的名字，解开后可直接辨认是哪一次会话</summary>
    /// <remarks>Writes one session into the archive, named as its plain-text form so an extracted file is identifiable</remarks>
    private static void AddSessionEntry(TarWriter tar, DateOnly date, string source)
    {
        var info = LogFileNames.Parse(Path.GetFileName(source));
        var entryName = info.Session is { } session
            ? LogFileNames.SessionName(date, session)
            : Path.GetFileName(source);

        // 会话文件一律是明文，因此这里拿到的是 FileStream
        // 它可寻道，TarWriter 能直接问到长度，不必回头去改已经写下的条目头
        //
        // A session file is always plain text, so this is a FileStream
        // It is seekable, so TarWriter reads the length up front and never seeks back to patch the entry header
        using var content = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = content,
        });
    }

    /// <summary>
    /// 删除超出保留期的整天归档，返回删掉的天数
    /// 保留期把今天算在内：keepDays 为 7 时，今天与之前 6 天都留下
    /// 只删整天归档：属于今天的会话文件必须留着，更早日期的那些会话已由 ArchiveEarlierDays 收进归档
    ///
    /// Removes the whole-day archives older than the retention window, returning how many were removed
    /// The window includes today: with keepDays of 7, today and the six days before it all stay
    /// Only archives are removed: the session files of today must stay, and the sessions of an earlier day have
    /// already been collected into that day archive by ArchiveEarlierDays
    /// </summary>
    public static int RemoveExpiredArchives(string directory, DateOnly today, int keepDays)
    {
        if (!Directory.Exists(directory) || keepDays < 1)
        {
            return 0;
        }

        var oldest = today.AddDays(-(keepDays - 1));
        var removed = 0;

        foreach (var path in Directory.GetFiles(directory))
        {
            var name = Path.GetFileName(path);

            if (name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuietly(path);
                continue;
            }

            var info = LogFileNames.Parse(name);
            if (info.Shape != LogFileShape.DayArchive || info.Date is not { } date)
            {
                continue;
            }

            if (date < oldest && DeleteQuietly(path))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>删除一个文件，返回是否确实删掉了 / Removes a file and reports whether it really went away</summary>
    /// <remarks>
    /// 返回值用于计数：删不掉的不能算进「已清理几个」
    /// 删不掉的就留着，下一轮再试，不影响应用运行
    ///
    /// The return value exists for counting: a file that could not be removed must not count as cleaned
    /// One that cannot be removed is left for the next round and does not affect the app
    /// </remarks>
    private static bool DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
