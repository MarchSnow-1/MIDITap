// ConfigLoader.cs — 加载并校验一份映射配置
//
// JSON5 读取
// 按 JS Number 语义解析 note 键
// 严格模式下拒绝非法项
// 说明 port 字段的语义
//
// Reads JSON5
// Parses note keys with JS Number semantics
// Rejects invalid entries in strict mode
// Explains the port field semantics

using MIDITap.Core.Settings;

namespace MIDITap.Core.Config;

public static class ConfigLoader
{
    /// <summary>
    /// 加载并校验映射配置
    /// options.ConfigPath 为 null 时加载 config/mapping.json
    /// 失败返回 null（文件不存在、解析失败、结构非法）
    ///
    /// Loads and validates a mapping config
    /// A null options.ConfigPath means config/mapping.json
    /// Returns null on failure (missing file, parse failure, invalid structure)
    /// </summary>
    public static MappingConfig? LoadConfig(string baseDir, LoadOptions options)
    {
        var configPath = options.ConfigPath is not null
            ? ConfigLocator.ResolveConfigPath(baseDir, options.ConfigPath)
            : ConfigLocator.ResolveConfigPath(baseDir, AppPaths.DefaultConfigPath(baseDir));
        if (configPath is null)
        {
            return null;
        }
        var configFileName = Path.GetFileName(configPath);
        var effectiveVerbose = !options.Silent && options.Verbose;

        Json5Value rawMapping;
        string content;
        try
        {
            content = File.ReadAllText(configPath);
        }
        catch (Exception err)
        {
            if (!options.Silent)
            {
                // 读文件与解析分成两个 try/catch，失败时报同一条错误
                //
                // The file read and the parse sit in separate try/catch blocks and report the same error
                Console.Error.WriteLine($"Failed to Load Config ({configFileName}): {err.Message}");
            }
            return null;
        }
        try
        {
            rawMapping = Json5Parser.Parse(content);
        }
        catch (Exception err)
        {
            if (!options.Silent)
            {
                Console.Error.WriteLine($"Failed to Load Config ({configFileName}): {err.Message}");
            }
            return null;
        }

        // 顶层必须是普通对象，防止数组/null 等非法结构进入后续逻辑
        //
        // The top level must be a plain object, so an array or null never reaches the logic below
        if (!rawMapping.IsObject)
        {
            if (!options.Silent)
            {
                Console.Error.WriteLine("Invalid config format: expected an object.");
            }
            return null;
        }

        var noteMap = new Dictionary<byte, ushort[]>();
        var hasValidationError = false;

        foreach (var (noteStr, keyChar) in rawMapping.ObjectEntries)
        {
            // `port` 是唯一的全局配置项，不是音符映射
            // 显示名不在这里：它取自文件名，因此配置文件内部不再有 name 字段
            //
            // 'port' is the only global setting rather than a note mapping
            // The display name is not here: it comes from the file name, so a config file no longer carries a name field
            if (noteStr is "port")
            {
                continue;
            }

            // MIDI note 必须是 0~127 的整数（按 JS Number 语义解析）
            //
            // A MIDI note must be an integer in 0..127, parsed with JS Number semantics
            var note = JsNumber.ToNumber(noteStr);
            if (!JsNumber.IsInteger(note) || note < 0 || note > 127)
            {
                if (!options.Silent)
                {
                    Console.Error.WriteLine(
                        $"[miditap.config] Invalid MIDI note \"{noteStr}\", expected integer in range 0-127, skipping...");
                }
                hasValidationError = true;
                continue;
            }

            // 键值不是字符串时直接拒绝
            //
            // A non-string binding value is rejected outright
            if (keyChar is not { IsString: true })
            {
                if (!options.Silent)
                {
                    Console.Error.WriteLine(
                        $"[miditap.config] Invalid key name for note \"{noteStr}\", expected non-empty string, skipping...");
                }
                hasValidationError = true;
                continue;
            }

            var binding = BindingParser.ParseBinding(keyChar.StringValue, noteStr, options.Silent);
            if (binding is null)
            {
                hasValidationError = true;
                continue;
            }

            // 记录合法映射（单键和组合键统一为 VK 数组）
            // 若同一 note 重复定义，后定义值会覆盖先定义值
            //
            // Records the valid mapping
            // Single keys and combos both become a VK array
            // A note defined twice keeps the later value
            noteMap[(byte)note] = binding.VkCodes.ToArray();
        }

        var parsedPort = ParsePort(rawMapping.Get("port"), options.Silent);
        if (rawMapping.HasProperty("port") && rawMapping.Get("port") is { IsNull: false } && parsedPort is null)
        {
            hasValidationError = true;
        }

        // 严格模式下，只要存在任意校验错误就判定失败
        //
        // In strict mode any validation error at all makes the whole load fail
        if (options.Strict && hasValidationError)
        {
            return null;
        }

        return new MappingConfig(noteMap, parsedPort, configPath);
    }

    /// <summary>
    /// 解析并校验配置中的 port 字段
    /// number：合法端口
    /// null：未配置或非法，主流程回退到默认端口 0
    ///
    /// Parses and validates the port field
    /// A number is a valid port
    /// null means unset or invalid, in which case the caller falls back to the default port 0
    /// </summary>
    public static uint? ParsePort(Json5Value? portValue, bool silent = false)
    {
        if (portValue is null || portValue.IsNull)
        {
            return null;
        }

        var port = portValue.Kind switch
        {
            Json5Kind.Number => portValue.NumberValue,
            Json5Kind.Bool => portValue.BoolValue ? 1 : 0, // Number(true) === 1
            Json5Kind.String => JsNumber.ToNumber(portValue.StringValue),
            // 不做数组 coercion：[] 与 [5] 都不被当作数字
            // 数组/对象一律判为非法端口
            // JSON5 配置里实际不会这样写
            //
            // No array coercion: [] and [5] are not treated as numbers
            // Arrays and objects are always an invalid port here
            // A JSON5 config never writes them anyway
            _ => double.NaN,
        };

        if (!JsNumber.IsInteger(port) || port < 0)
        {
            if (!silent)
            {
                Console.Error.WriteLine($"[miditap.config] Invalid config port \"{Describe(portValue)}\", fallback to default port 0.");
            }
            return null;
        }
        return (uint)port;
    }

    private static string Describe(Json5Value value) => value.Kind switch
    {
        Json5Kind.String => value.StringValue,
        Json5Kind.Number => value.NumberValue.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture),
        Json5Kind.Bool => value.BoolValue ? "true" : "false",
        _ => "(value)",
    };
}
