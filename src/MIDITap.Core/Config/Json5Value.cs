// Json5Value.cs — 读取路径使用的内存表示
//
// 把映射配置解析成普通 JS 对象：键顺序保留
// 重复键保留首次出现的位置，由后出现的值生效
// ObjectEntries 重现了该行为
//
// In-memory representation used by the read path
//
// Mapping configs are parsed into a plain JS object
// Object key order is preserved
// Duplicate keys keep their first position while the later value wins
// ObjectEntries reproduces that behaviour

namespace MIDITap.Core.Config;

public enum Json5Kind
{
    Null,
    Bool,
    Number,
    String,
    Array,
    Object,
}

public sealed class Json5Value
{
    public static readonly Json5Value Null = new(Json5Kind.Null);

    public Json5Value(Json5Kind kind) => Kind = kind;

    public Json5Kind Kind { get; }
    public bool BoolValue { get; init; }
    public double NumberValue { get; init; }
    public string StringValue { get; init; } = string.Empty;
    public List<Json5Value?> ArrayValue { get; } = new();
    public List<KeyValuePair<string, Json5Value?>> ObjectEntries { get; } = new();

    public bool IsNull => Kind == Json5Kind.Null;
    public bool IsObject => Kind == Json5Kind.Object;
    public bool IsString => Kind == Json5Kind.String;

    /// <summary>
    /// 顶层字符串属性；缺失或类型不符返回 null
    ///
    /// A top-level string property; null when it is missing or of another type
    /// </summary>
    public string? TryGetString(string key)
    {
        foreach (var (k, value) in ObjectEntries)
        {
            if (k == key)
            {
                return value?.IsString == true ? value.StringValue : null;
            }
        }
        return null;
    }

    /// <summary>
    /// 顶层属性是否存在（含值为 null 的情形）
    ///
    /// Whether a top-level property exists, including a null value
    /// </summary>
    public bool HasProperty(string key)
    {
        foreach (var (k, _) in ObjectEntries)
        {
            if (k == key)
            {
                return true;
            }
        }
        return false;
    }

    public Json5Value? Get(string key)
    {
        foreach (var (k, value) in ObjectEntries)
        {
            if (k == key)
            {
                return value;
            }
        }
        return null;
    }
}
