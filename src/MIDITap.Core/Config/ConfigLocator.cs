// ConfigLocator.cs — 配置路径解析（带目录逃逸防护）
// 此外还包含 config 目录枚举、默认配置生成、便携的"上次使用配置"记录
//
// Config path resolution with directory-escape protection
// Config directory enumeration and default-config creation also live here
// So does the portable last-config record

using System.Runtime.InteropServices;
using MIDITap.Core.Settings;

namespace MIDITap.Core.Config;

public static class ConfigLocator
{
    // .storage 下记录「上次使用的配置」的条目名
    // Storage entry holding the last-used config
    private const string LastConfigFileName = "last_config";

    /// <summary>
    /// 解析配置路径：只接受配置目录内的 .json 文件
    /// 判定通过 realpath 归一化与目录逃逸防护
    /// 非法路径返回 null
    ///
    /// Resolves a config path: only a .json file inside the config directory is accepted
    /// The check uses realpath normalisation plus directory-escape protection
    /// An invalid path yields null
    /// </summary>
    public static string? ResolveConfigPath(string baseDir, string configPath)
    {
        var configDir = Path.GetFullPath(AppPaths.ConfigDir(baseDir));
        var candidate = Path.IsPathRooted(configPath)
            ? Path.GetFullPath(configPath)
            : Path.GetFullPath(Path.Combine(configDir, configPath));

        try
        {
            var realConfigDir = RealPath(configDir);
            var realCandidate = RealPath(candidate);
            if (realConfigDir is null || realCandidate is null)
            {
                return null;
            }
            if (!IsPathInside(realConfigDir, realCandidate))
            {
                return null;
            }
            return Path.GetExtension(realCandidate).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? realCandidate
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsPathInside(string parentPath, string targetPath)
    {
        // 目录内判定："" 或既不以 ".." 开头、也不是绝对路径
        //
        // Inside-directory test: "" or a relative path that does not start with ".."
        var relative = Path.GetRelativePath(parentPath, targetPath);
        return relative.Length == 0
            || (relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar)
                && !Path.IsPathRooted(relative));
    }

    /// <summary>
    /// realpathSync 等价物：解析符号链接（含中间目录）并要求目标存在
    /// 走 GetFinalPathNameByHandle，等价于 fs.realpathSync 的完整归一化
    /// 它能识破 config/ 内指向外部的**目录**符号链接
    /// .NET 的 ResolveLinkTarget 只覆盖文件级链接，不足以防目录逃逸，因此不能用它代替
    ///
    /// The realpathSync equivalent: resolves symbolic links (including intermediate directories)
    /// It also requires the target to exist
    /// It uses GetFinalPathNameByHandle, a full normalisation equivalent to fs.realpathSync
    /// That sees through a DIRECTORY symlink inside config/ pointing outside
    /// .NET's ResolveLinkTarget only covers file-level links and cannot prevent a directory escape
    /// So it cannot replace this
    /// </summary>
    public static string? RealPath(string path)
    {
        try
        {
            // FILE_FLAG_BACKUP_SEMANTICS 允许打开目录
            // GENERIC_READ 对目录也可用
            //
            // FILE_FLAG_BACKUP_SEMANTICS allows a directory to be opened
            // GENERIC_READ works on directories too
            using var handle = CreateFile(
                path,
                0x80000000, // GENERIC_READ
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return null;
            }
            var buffer = new char[1024];
            uint length;
            while ((length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0)) > buffer.Length)
            {
                buffer = new char[length];
            }
            if (length == 0)
            {
                return null;
            }
            var finalPath = new string(buffer, 0, (int)length);
            // 返回值形如 "\\?\C:\..."（UNC 为 "\\?\UNC\server\share\..."），剥离前缀
            //
            // The returned value looks like "\\?\C:\..." (UNC: "\\?\UNC\server\share\...")
            // So the prefix is stripped
            if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                finalPath = finalPath.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
                    ? @"\\" + finalPath[8..]
                    : finalPath[4..];
            }
            return Path.GetFullPath(finalPath);
        }
        catch
        {
            // 文件被占用、句柄不可用等瞬时状况：交由调用方按"路径非法"处理
            //
            // Transient conditions such as a file in use or an unusable handle are left to the caller
            // The caller then treats the path as invalid
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    /// <summary>
    /// 扫描 config 目录下所有 .json 文件
    /// 显示名就是**文件名去掉 .json**，因此界面上显示的名字与磁盘上的文件一一对应，用户能直接去 config/ 找到它
    /// 代价是**不读文件内容**：文件损坏或写成乱码也照样列出来，用户能看到它并去修，而不是让它凭空消失
    ///
    /// Scans every .json file in the config directory
    /// The display name IS the file name without .json, so what the UI shows maps one-to-one onto a file on disk
    /// The user can go straight to config/ and find it
    /// The cost is that **the file is not read**: a corrupt or garbled file is still listed
    /// That way the user can see it and fix it instead of it silently vanishing
    /// </summary>
    public static IReadOnlyList<ConfigFileInfo> ListConfigFiles(string baseDir)
    {
        var configDir = AppPaths.ConfigDir(baseDir);
        var results = new List<ConfigFileInfo>();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(configDir);
        }
        catch
        {
            return results;
        }

        foreach (var path in entries)
        {
            var entry = Path.GetFileName(path);
            if (!entry.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }
            results.Add(new ConfigFileInfo(entry, Path.GetFileNameWithoutExtension(entry), path));
        }

        // Windows 的目录枚举本身就是按名称有序的；显式排序保证结果稳定
        //
        // Windows directory enumeration already yields names in order
        // An explicit sort keeps the result stable
        results.Sort((a, b) => string.Compare(a.Filename, b.Filename, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    // Windows 保留设备名：即使带扩展名也无法正常创建
    // 例如 NUL.json 会被当作空设备，读写都落不到文件上
    //
    // Reserved Windows device names: they cannot be created normally even with an extension
    // NUL.json, for instance, resolves to the null device and reads/writes never reach a file
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // 文件系统对单个名字分量的上限是 255 个 UTF-16 码元（不是字节）
    // 中文因此在同一个上限之内，无需按编码折算
    // 减掉 ".json" 的 5 个字符就是主干的上限
    //
    // A file system caps one name component at 255 UTF-16 code units (not bytes)
    // Chinese therefore falls under the same limit and needs no per-encoding conversion
    // Subtracting the five characters of ".json" gives the limit for the stem
    private const int MaxStemLength = 255 - 5;

    /// <summary>
    /// 把用户输入的名字规范成文件名（补 .json）
    /// 用户可能顺手把 .json 也打进去，这里统一去掉再补，避免出现 a.json.json
    /// 与 ValidateConfigName 一样先 Trim，否则校验看到的是 "name" 而生成的是 "  name  .json"，两者对不上
    ///
    /// Turns a user-typed name into a file name (appending .json)
    /// The user may type the .json suffix as well, so it is stripped first
    /// That avoids ending up with a.json.json
    /// It trims just as ValidateConfigName does; otherwise the check would see "name" while the built name is "  name  .json", and the two would not match
    /// </summary>
    public static string BuildConfigFileName(string typedName)
        => StripJsonSuffix((typedName ?? string.Empty).Trim()) + ".json";

    /// <summary>
    /// 校验用户输入的配置文件名
    /// 空格与中文都是合法字符，只有 Windows 明确不允许的才拒绝
    /// 采用**拒绝并说明原因**而不是静默清洗：静默清洗会造出用户没有输入的名字，用户反而更找不到那个文件
    /// excludeFilename 用于重命名自己：与自己同名不算冲突
    ///
    /// Validates a user-typed config file name
    /// Spaces and Chinese are both legal; only what Windows itself forbids is rejected
    /// It **rejects and states the reason** rather than sanitising silently
    /// Silent sanitising produces a name the user never typed, which makes the file harder to find, not easier
    /// excludeFilename is for renaming a file onto its own name: that is not a clash
    /// </summary>
    public static ConfigNameError ValidateConfigName(string baseDir, string typedName, string? excludeFilename = null)
    {
        // 先去掉首尾空白：对话框已经 Trim 过，这里再兜一层，让 Core 单独被调用时也一样
        // 首尾空白因此不会走到"以空格结尾"那条判断上
        //
        // Leading and trailing whitespace is removed first: the dialog already trims, and this repeats it so Core behaves the same when called on its own
        // A trailing space therefore never reaches the ends-with-a-space check
        var stem = StripJsonSuffix((typedName ?? string.Empty).Trim());
        if (stem.Length == 0)
        {
            return ConfigNameError.Empty;
        }

        // 只有点（. / .. / …）的名字等价于目录引用，不是文件名
        //
        // A name of nothing but dots (. / .. / …) is a directory reference, not a file name
        if (stem.Trim('.').Length == 0)
        {
            return ConfigNameError.InvalidCharacters;
        }

        if (stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return ConfigNameError.InvalidCharacters;
        }

        // Windows 会静默去掉结尾的点与空格
        // 放行等于创建了一个与用户输入不符的文件名，因此拒绝
        //
        // Windows silently strips a trailing dot or space
        // Allowing it would create a file name that differs from what the user typed, so it is rejected
        if (stem.EndsWith('.') || stem.EndsWith(' '))
        {
            return ConfigNameError.TrailingDotOrSpace;
        }

        // 保留名判定只看第一个点之前的部分：con.foo.json 同样打不开
        // 加 .json 后缀并不能解除保留
        //
        // The reserved-name test only looks before the first dot: con.foo.json is just as unusable
        // Adding the .json suffix does not lift the reservation
        var devicePart = stem.Split('.')[0];
        if (ReservedDeviceNames.Contains(devicePart))
        {
            return ConfigNameError.ReservedName;
        }

        if (stem.Length > MaxStemLength)
        {
            return ConfigNameError.TooLong;
        }

        var target = Path.Combine(AppPaths.ConfigDir(baseDir), stem + ".json");
        if (File.Exists(target)
            && !string.Equals(stem + ".json", excludeFilename, StringComparison.OrdinalIgnoreCase))
        {
            return ConfigNameError.AlreadyExists;
        }

        return ConfigNameError.None;
    }

    /// <summary>
    /// 去掉结尾的 .json（大小写不敏感）
    /// 只去一次，且只在结尾处 —— 名字中间的 .json 是名字的一部分
    ///
    /// Strips a trailing .json, case-insensitively
    /// Only once and only at the end: a .json in the middle is part of the name
    /// </summary>
    private static string StripJsonSuffix(string name)
        => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;

    /// <summary>
    /// 确保配置目录存在且至少有一个 .json 配置文件
    /// 目录为空时生成默认 mapping.json
    /// 内容是一个空对象，映射由用户自行添加
    /// 目录只读/受限时不应崩溃：失败仅告警并返回 null
    ///
    /// Makes sure the config directory exists and holds at least one .json config
    /// An empty directory gets a default mapping.json
    /// Its contents are an empty object; the user adds the mappings
    /// A read-only or restricted directory must not crash: failure only warns and returns null
    /// </summary>
    public static string? EnsureConfigDir(string baseDir)
    {
        var configDir = AppPaths.ConfigDir(baseDir);
        try
        {
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            var hasJson = Directory
                .EnumerateFileSystemEntries(configDir)
                .Any(entry => Path.GetFileName(entry).EndsWith(".json", StringComparison.Ordinal));
            if (!hasJson)
            {
                const string defaultConfig = "{\n}";
                var defaultPath = Path.Combine(configDir, AppPaths.DefaultConfigFileName);
                File.WriteAllText(defaultPath, defaultConfig);
                return defaultPath;
            }
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.config]: ensureConfigDir failed: " + err.Message);
        }
        return null;
    }

    /// <summary>
    /// 读取上次使用的配置文件路径（.storage/last_config，写入的是绝对路径文本）
    /// 返回 null 表示没有记录或记录的文件已不存在
    ///
    /// Reads the last-used config path (.storage/last_config, written as absolute path text)
    /// Null means there is no record, or the recorded file no longer exists
    /// </summary>
    public static string? GetLastConfigPath(string baseDir)
    {
        var storagePath = AppPaths.StorageFile(baseDir, LastConfigFileName);
        try
        {
            var content = File.ReadAllText(storagePath).Trim();
            if (content.Length == 0)
            {
                return null;
            }
            return ResolveConfigPath(baseDir, content);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 保存最后使用的配置文件路径到 .storage/last_config
    /// 落盘失败只返回 false 并告警
    /// 不向调用链抛异常
    ///
    /// Saves the last-used config path to .storage/last_config
    /// A failed write only returns false and warns
    /// No exception reaches the call chain
    /// </summary>
    public static bool SaveLastConfigPath(string baseDir, string configPath)
    {
        var resolvedPath = ResolveConfigPath(baseDir, configPath);
        if (resolvedPath is null)
        {
            return false;
        }
        try
        {
            var storageDir = AppPaths.StorageDir(baseDir);
            Directory.CreateDirectory(storageDir);
            File.WriteAllText(Path.Combine(storageDir, LastConfigFileName), resolvedPath);
            return true;
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.config]: saveLastConfigPath failed: " + err.Message);
            return false;
        }
    }
}
