// ConfigEditor.cs — 映射配置文件的增删改，以及新建/重命名文件
// 增改与删除走"保留注释"式修改，新建与重命名是文件级操作
//
// 字节级扫描器保住注释与格式
// 只动目标的那一个顶层字符串属性
//
// Edits, creation and renaming of mapping config files
// Adding and deleting a mapping are comment-preserving edits, while creating and renaming are file-level operations
// The byte-level scanners keep comments and formatting intact
// Only the targeted top-level string property is touched

using MIDITap.Core.Settings;

namespace MIDITap.Core.Config;

public static class ConfigEditor
{
    /// <summary>
    /// 重命名配置文件：**改的是磁盘上的文件名**，不动文件内容
    /// 显示名取自文件名，改名因此必须落到文件上，否则界面上的名字与 config/ 里的文件仍然对不上
    /// 名字非法或已有同名文件时不改动任何东西
    /// 返回新路径；名字未变时返回原路径；失败返回 null
    ///
    /// Renames a config file: it changes the FILE NAME on disk and leaves the contents alone
    /// The display name comes from the file name, so a rename has to reach the disk
    /// Otherwise the name in the UI still would not match any file under config/
    /// A rejected name or an existing target leaves everything untouched
    /// Returns the new path, the original path when the name did not change, or null on failure
    /// </summary>
    public static string? RenameConfigFile(string baseDir, string filename, string typedName)
    {
        var configPath = ConfigLocator.ResolveConfigPath(baseDir, filename);
        if (configPath is null)
        {
            return null;
        }

        // 排除自身：把文件改成自己原来的名字不算冲突
        //
        // The file itself is excluded: renaming it to its own name is not a clash
        if (ConfigLocator.ValidateConfigName(baseDir, typedName, filename) != ConfigNameError.None)
        {
            return null;
        }

        var targetName = ConfigLocator.BuildConfigFileName(typedName);
        if (string.Equals(targetName, filename, StringComparison.Ordinal))
        {
            return configPath;
        }

        var targetPath = Path.Combine(AppPaths.ConfigDir(baseDir), targetName);
        try
        {
            // Move 而不是 Copy+Delete：前者在同一个卷上是原子改名，中途失败不会留下半个文件
            // 目标已存在时 Move 会抛异常，这里此前已由 ValidateConfigName 挡掉
            //
            // Move rather than copy-and-delete: on one volume that is an atomic rename
            // A failure part-way cannot leave half a file behind
            // An existing target makes Move throw, and ValidateConfigName has already ruled that out
            File.Move(configPath, targetPath);
            return targetPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 新建一个配置文件，内容是一段模板注释加空映射
    /// 文件名**就是用户输入的名字**（补 .json），不做清洗：用户输入什么，config/ 里就叫什么
    /// 名字非法或已有同名文件时拒绝，返回 null
    /// 返回新文件的完整路径
    ///
    /// Creates a config file whose contents are a template comment plus an empty mapping set
    /// The file name **is the name the user typed** (with .json appended) and is not sanitised
    /// Whatever the user typed is what the file under config/ is called
    /// An invalid name or an existing target is rejected with null
    /// Returns the full path of the new file
    /// </summary>
    public static string? CreateConfigFile(string baseDir, string typedName)
    {
        if (ConfigLocator.ValidateConfigName(baseDir, typedName) != ConfigNameError.None)
        {
            return null;
        }

        var configDir = AppPaths.ConfigDir(baseDir);
        try
        {
            Directory.CreateDirectory(configDir);
            var target = Path.Combine(configDir, ConfigLocator.BuildConfigFileName(typedName));
            File.WriteAllText(target, TemplateContent);
            return target;
        }
        catch
        {
            return null;
        }
    }

    // 新配置文件的内容模板
    // 与仓库里 config/mapping.json 的写法一致，因此两者打开后长得一样
    // 注释是中英两行而不是按界面语言取一条：文件在手写编辑时并不知道界面语言，写死两行才不会因切语言而变
    //
    // The template a new config file starts from
    // It matches how config/mapping.json in the repository is written, so the two look alike when opened
    // The comments carry both languages rather than following the UI locale
    // A hand-edited file has no UI locale, and fixing both lines keeps the file from changing with the language
    private const string TemplateContent =
        "{\n"
        + "  // MIDITap 配置文件\n"
        + "  // MIDITap Config File\n"
        + "\n"
        + "  // Example:\n"
        + "  // \"48\": \"a\",\n"
        + "  // \"50\": \"s\"\n"
        + "}";

    /// <summary>
    /// 把显示名安全化为文件名主干
    /// 去掉路径分隔符、Windows 保留字符与首尾空白/点
    /// 并限制长度，避免超过文件系统上限
    /// 结果为空时回退为 "config"
    ///
    /// Sanitises a display name into a file-name stem
    /// Strips path separators, Windows reserved characters and leading/trailing whitespace or dots
    /// Caps the length so the file-system limit is never exceeded
    /// An empty result falls back to "config"
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var result = builder.ToString().Trim().Trim('.');
        // 仅由保留字符构成的名字会被替换成一串下划线
        // 它仍然可用，无需特别处理
        //
        // A name made only of reserved characters becomes a run of underscores
        // That is still usable, so it needs no special case
        if (result.Length == 0)
        {
            result = "config";
        }
        // 200 是保守上限
        // 多数文件系统限制 255 字节
        // 中文等多字节字符按 UTF-8 会占 3-4 字节
        //
        // 200 is a conservative cap
        // Most file systems limit names to 255 bytes
        // A multi-byte character such as Chinese takes 3-4 bytes in UTF-8
        return result.Length > 200 ? result[..200] : result;
    }

    /// <summary>
    /// 向配置文件中添加或更新一个映射条目（保留原有注释和格式）
    ///
    /// Adds or updates one mapping entry in a config file, preserving existing comments and formatting
    /// </summary>
    public static bool AddMappingToConfig(string baseDir, string configPath, string note, string key)
    {
        var resolvedPath = ConfigLocator.ResolveConfigPath(baseDir, configPath);
        if (resolvedPath is null)
        {
            return false;
        }

        // 与 deleteMappingFromConfig 保持对称：note 必须是 0~127 的整数
        //
        // Kept symmetric with deleteMappingFromConfig: note must be an integer in 0..127
        var noteNum = JsNumber.ToNumber(note);
        if (!JsNumber.IsInteger(noteNum) || noteNum < 0 || noteNum > 127)
        {
            return false;
        }
        try
        {
            var content = File.ReadAllText(resolvedPath);
            var root = Json5TextScanner.FindRootObject(content);
            if (ParseConfigObject(content) is null || root is null)
            {
                return false;
            }
            var noteStr = ((int)noteNum).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var serializedKey = JsonString.Encode(key);
            var valueRange = Json5TextScanner.FindTopLevelStringValue(content, noteStr, root.Value);
            var newContent = valueRange is { } range
                ? content[..range.Start] + serializedKey + content[range.End..]
                : Json5TextScanner.InsertRootProperty(content, root.Value, noteStr, serializedKey);
            return WriteConfigFile(resolvedPath, newContent);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从配置文件中删除一个映射条目（保留原有注释和格式）
    ///
    /// Deletes one mapping entry from a config file, preserving existing comments and formatting
    /// </summary>
    public static bool DeleteMappingFromConfig(string baseDir, string configPath, string note)
    {
        var resolvedPath = ConfigLocator.ResolveConfigPath(baseDir, configPath);
        if (resolvedPath is null)
        {
            return false;
        }
        var noteNum = JsNumber.ToNumber(note);
        if (!JsNumber.IsInteger(noteNum) || noteNum < 0 || noteNum > 127)
        {
            return false;
        }
        try
        {
            var content = File.ReadAllText(resolvedPath);
            var root = Json5TextScanner.FindRootObject(content);
            if (ParseConfigObject(content) is null || root is null)
            {
                return false;
            }
            var noteStr = ((int)noteNum).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var entryRange = Json5TextScanner.FindTopLevelEntryRange(content, noteStr, root.Value);
            if (entryRange is null)
            {
                return true;
            }
            var newContent = content[..entryRange.Value.Start] + content[entryRange.Value.End..];
            return WriteConfigFile(resolvedPath, newContent);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 顶层必须是普通对象
    ///
    /// The top level must be a plain object
    /// </summary>
    private static Json5Value? ParseConfigObject(string content)
    {
        try
        {
            var parsed = Json5Parser.Parse(content);
            return parsed.IsObject ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// v1 迁移：v1 的配置文件把显示名写在 name 字段里，v2 改从文件名取
    /// 因此扫一遍 config/，凡是内部有 name 的就照它把文件改名，并把那个字段删掉
    /// 这样老用户升级后，界面上的名字与磁盘上的文件名立刻对上，配置内容本身一条不动
    /// 撞名时顺延序号（example、example - 1、example - 2…），不覆盖已有文件
    /// 名字本身不可用时（含非法字符、保留设备名）强制改名为 Config，同样顺延序号
    /// 文件损坏时跳过，不影响其它文件
    /// 返回真正改了名的文件数
    ///
    /// v1 migration: a v1 config kept its display name in a name field, while v2 takes it from the file name
    /// So config/ is scanned once, any file holding a name is renamed after it and that field is deleted
    /// An upgraded user therefore sees the UI name and the file name agree at once, with no mapping entry touched
    /// On a clash the number advances (example, example - 1, example - 2…) rather than overwriting
    /// A name unusable in itself (illegal characters, a reserved device name) is force-renamed to Config, advancing the same way
    /// A corrupt file is skipped without affecting the rest
    /// Returns how many files were actually renamed
    /// </summary>
    public static int MigrateV1NameField(string baseDir)
    {
        var configDir = AppPaths.ConfigDir(baseDir);
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(configDir, "*.json");
        }
        catch
        {
            return 0;
        }

        // 先读出记录中的"上次使用的配置"，任一文件改了名就要跟着更新它
        // 否则那份记录会指向一个已经不存在的路径，用户下次启动会掉回别的配置
        //
        // The recorded last-used config is read up front, because any rename has to follow it
        // Otherwise the record would point at a path that no longer exists and the next launch would fall back to another config
        var lastPath = ConfigLocator.GetLastConfigPath(baseDir);
        var lastPathMoved = false;

        var renamedCount = 0;
        foreach (var path in entries)
        {
            try
            {
                var newPath = MigrateOneV1File(baseDir, path, lastPath);
                if (newPath is null)
                {
                    continue;
                }
                renamedCount++;
                if (lastPath is not null && string.Equals(newPath, path, StringComparison.OrdinalIgnoreCase) is false
                    && string.Equals(path, lastPath, StringComparison.OrdinalIgnoreCase))
                {
                    lastPath = newPath;
                    lastPathMoved = true;
                }
            }
            catch
            {
                // 单个文件出问题不能中断整轮迁移：其它文件仍然值得迁移
                //
                // One bad file must not abort the whole pass: the remaining files are still worth migrating
            }
        }

        if (lastPathMoved && lastPath is not null)
        {
            ConfigLocator.SaveLastConfigPath(baseDir, lastPath);
        }
        return renamedCount;
    }

    /// <summary>
    /// 迁移单个文件
    /// 返回**迁移后的新路径**；只有在文件名真的变了时才返回非 null，字段的删除不算一次迁移
    /// 计数因此等于"有多少个文件换了名字"，调用方也据此知道 last_config 要不要跟着改
    /// 无需改名（name 与文件名本来一致）时仍然会把字段删掉，只是返回 null
    /// lastPath 用于判断这个文件是不是记录中的"上次使用的配置"，是的话要触发一次路径更新
    ///
    /// Migrates one file
    /// It returns the NEW PATH, and only when the file name actually changed; stripping the field does not count
    /// The count therefore equals how many files were given a new name, and the caller learns from it whether last_config has to follow
    /// When no rename is needed (the name already matches the file name) the field is still stripped and null is returned
    /// lastPath tells whether this file is the recorded last-used config, in which case a path update is due
    /// </summary>
    private static string? MigrateOneV1File(string baseDir, string path, string? lastPath)
    {
        var content = File.ReadAllText(path);
        var root = Json5TextScanner.FindRootObject(content);
        var parsed = root is null ? null : ParseConfigObject(content);
        if (root is null || parsed is null)
        {
            return null;
        }

        // 顶层没有该键就不迁移：FindTopLevelStringValue 会把注释里的同名键排除掉
        //
        // No such top-level key means no migration
        // FindTopLevelStringValue rules out a same-named key inside a comment
        if (Json5TextScanner.FindTopLevelStringValue(content, "name", root.Value) is null)
        {
            return null;
        }

        var desired = parsed.TryGetString("name")?.Trim();
        if (string.IsNullOrEmpty(desired))
        {
            // 键在但值不是可用的字符串：删掉它，名字取自文件名
            //
            // The key is there but its value is not a usable string: drop it and let the file name speak
            RemoveTopLevelName(path);
            return null;
        }

        var current = Path.GetFileName(path);
        var targetPath = Path.Combine(AppPaths.ConfigDir(baseDir), FreeMigrationName(baseDir, desired, current));
        var willRename = !string.Equals(targetPath, path, StringComparison.OrdinalIgnoreCase);

        // 先改名再删字段：反过来若改名失败，字段已被删掉，那个名字就永久丢了
        //
        // Rename first and strip the field afterwards
        // The other order would lose the name for good if the rename failed
        if (willRename)
        {
            File.Move(path, targetPath);
        }

        // 到这里名字已经保住了：要么文件叫这个名字，要么本来就叫这个名字
        // 因此删字段不再丢信息
        //
        // The name is safe by this point: the file either took it or already had it
        // Stripping the field therefore loses nothing
        RemoveTopLevelName(targetPath);
        return willRename ? targetPath : null;
    }

    // 名字没法当文件名用时的兜底主干
    //
    // The fallback stem used when a name cannot serve as a file name
    private const string FallbackStem = "Config";

    /// <summary>
    /// 为迁移挑一个可用文件名
    /// name 本身可用时就用它，撞名则依次试 "name - 1"、"name - 2"…
    /// name **本身**不可用（含非法字符、是保留设备名等）时改用 Config，同样撞名就顺延序号
    /// 强制改名而不是放弃：名字不可用时搁置不动，界面上的名字与磁盘上的文件名就还是对不上，而这正是本次迁移要解决的问题
    /// currentFilename 是自己，与自己同名不算冲突
    ///
    /// Picks a usable file name for the migration
    /// A usable name is used as-is, and a clash advances the number: "name - 1", "name - 2"…
    /// A name that is unusable IN ITSELF (illegal characters, reserved device name…) falls back to Config, with the same advancing on a clash
    /// It renames rather than giving up: leaving an unusable name in place would keep the UI name and the file name apart, which is the very thing this migration is here to fix
    /// currentFilename is the file itself, and matching it is not a clash
    /// </summary>
    private static string FreeMigrationName(string baseDir, string desired, string currentFilename)
    {
        // 先单独问一次"这个名字本身能不能用"
        // 排除名用一个不可能存在的值，这样 AlreadyExists 只反映磁盘上的真实占用
        // 于是"撞名"与"非法"被分开：前者顺延序号，后者换成兜底主干
        //
        // First ask whether the name is usable AT ALL, on its own
        // The exclusion is a value that cannot exist, so AlreadyExists reflects only real occupancy on disk
        // That separates a clash from an illegal name: the first advances the number, the second switches to the fallback stem
        var ownValidity = ConfigLocator.ValidateConfigName(baseDir, desired, "\u0000never-a-real-file");
        var stem = ownValidity switch
        {
            ConfigNameError.None => desired,
            // 合法但被占：保留这个名字，下面顺延序号
            //
            // Legal yet taken: keep the name and advance the number below
            ConfigNameError.AlreadyExists => desired,
            _ => FallbackStem,
        };

        // 上限防止极端情况下把整轮迁移拖成死循环
        //
        // The cap keeps an extreme case from turning the pass into an endless loop
        const int maxAttempts = 100;
        for (var i = 0; i < maxAttempts; i++)
        {
            var candidate = i == 0 ? stem : stem + " - " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ConfigLocator.ValidateConfigName(baseDir, candidate, currentFilename) == ConfigNameError.None)
            {
                return ConfigLocator.BuildConfigFileName(candidate);
            }
        }
        return currentFilename;
    }

    /// <summary>
    /// 删掉顶层 name 字段，保留其余内容与注释
    /// 没有该字段时原样返回
    ///
    /// Removes the top-level name field, keeping the rest of the contents and the comments
    /// A file without that field is returned unchanged
    /// </summary>
    private static bool RemoveTopLevelName(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            var root = Json5TextScanner.FindRootObject(content);
            if (root is null || Json5TextScanner.FindTopLevelStringValue(content, "name", root.Value) is not { } range)
            {
                return false;
            }

            // 连同该行一起删掉，否则会留下一行空白
            // 行的范围从本行开头算到下一行开头，因此换行符一并带走
            //
            // The whole line goes, otherwise a blank line is left behind
            // The line range runs from this line's start to the next line's start, taking the newline with it
            var lineStart = content.LastIndexOf('\n', Math.Max(0, range.Start - 1)) + 1;
            var lineEnd = content.IndexOf('\n', range.End);
            var newContent = lineEnd < 0
                ? content[..lineStart].TrimEnd() + "\n}"
                : content[..lineStart] + content[(lineEnd + 1)..];
            return WriteConfigFile(path, newContent);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 原子写入：临时文件 + 重命名
    ///
    /// Atomic write: temporary file plus rename
    /// </summary>
    private static bool WriteConfigFile(string filePath, string content)
    {
        var tempPath = $"{filePath}.{Environment.ProcessId}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.tmp";
        try
        {
            File.WriteAllText(tempPath, content);
            // Node 的 fs.renameSync 在 Windows 上直接替换目标文件
            //
            // Node's fs.renameSync replaces the destination file directly on Windows
            File.Move(tempPath, filePath, overwrite: true);
            return true;
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 忽略 / ignore
            }
            return false;
        }
    }
}
