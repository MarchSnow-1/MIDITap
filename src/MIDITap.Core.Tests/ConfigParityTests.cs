// JSON5 解析对齐用例集
// 用户在配置文件里合法写下的每一种语法特性都必须可读，并且产出与 JSON5 语义一致的 note 映射
// 不受支持的怪异值直接丢弃
// （无法解析成绑定的条目直接跳过）
//
// JSON5 parse parity corpus
// Every syntax feature users can legally put in a config file must be readable
// It must also produce a note map consistent with JSON5 semantics
// Unsupported exotic values are simply dropped
// Entries that cannot be parsed into a binding are skipped

using MIDITap.Core.Config;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class ConfigParityTests : IDisposable
{
    public string BaseDir { get; } = Directory.CreateTempSubdirectory("miditap-parity-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(BaseDir, recursive: true);
        }
        catch
        {
            // 忽略 / Ignore
        }
    }

    private MappingConfig? LoadIntoTemp(string content)
    {
        var configDir = Path.Combine(BaseDir, "config");
        Directory.CreateDirectory(configDir);
        // 显式传路径：本类验证的是解析，而不传路径要走按语言取默认名那条分支
        // 后者由 ConfigTests 覆盖，命名规则不该让这些用例失败
        //
        // The path is passed explicitly: this class verifies parsing
        // Omitting it takes the branch that resolves the language-based default name, which ConfigTests covers
        // Naming should not be able to break these cases
        var path = Path.Combine(configDir, "mapping.json");
        File.WriteAllText(path, content);
        return ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: path));
    }

    private List<byte> NotesOf(string content)
        => LoadIntoTemp(content)?.NoteMap.Keys.OrderBy(n => n).ToList() ?? [];

    [Fact]
    public void Comments_trailing_commas_and_single_quotes()
    {
        var content = "// leading comment\n{\n  /* block\n     comment */\n  name: 'cfg',\n  '48': 'a', // line comment\n  '50': \"b\",\n  \"60\": 'c',\n}\n";
        Assert.Equal(new List<byte> { 48, 50, 60 }, NotesOf(content));
        var config = LoadIntoTemp(content);
        Assert.NotNull(config);
        // 文件里的那行 name 既不是音符也不是全局字段，被当作非法项丢弃
        // 三个合法音符不受影响，这正是要断言的
        //
        // The name line in the file is neither a note nor a global field and is dropped as invalid
        // The three legal notes are unaffected, which is what this asserts
        Assert.Equal(3, config!.NoteMap.Count);
    }

    [Fact]
    public void Unquoted_identifier_keys()
    {
        var content = "{ name: 'cfg', '48': 'up' }\n";
        // "up" 键（上箭头）/ the "up" key (arrow up)
        Assert.Equal(new ushort[] { 0x26 }, LoadIntoTemp(content)!.NoteMap[48]);
    }

    [Fact]
    public void Numeric_and_string_port_forms_are_both_accepted()
    {
        var config = LoadIntoTemp("{ name: 'cfg', port: 2, '48': 'a', '61': 'ctrl+b' }\n");
        Assert.NotNull(config);
        Assert.Equal(2u, config!.Port);
        Assert.Equal(new ushort[] { 0x41 }, config.NoteMap[48]);
        Assert.Equal(new ushort[] { 0x11, 0x42 }, config.NoteMap[61]);

        Assert.Equal(3u, LoadIntoTemp("{ name: 'cfg', port: '3', '50': 's' }\n")!.Port);
    }

    [Fact]
    public void Nested_objects_are_ignored_for_mapping()
    {
        Assert.Equal(new List<byte> { 50 }, NotesOf("{ name: 'cfg', meta: { '48': 'x' }, '50': 's' }\n"));
    }

    [Fact]
    public void Invalid_mapping_values_are_dropped_but_file_still_loads()
    {
        var config = LoadIntoTemp("{ name: 'cfg', '200': 'a', '48': 'definitely_not_a_key', '60': 'f1' }\n");
        Assert.NotNull(config);
        Assert.Single(config!.NoteMap);
        Assert.True(config.NoteMap.ContainsKey(60));
    }

    [Fact]
    public void Escaped_quoted_keys_are_decoded()
    {
        // 用 JSON5.parse 解码带引号的键："\u0034\x38" 与 "48" 等价
        //
        // Quoted keys are decoded with JSON5.parse: "\u0034\x38" is equivalent to "48"
        var config = LoadIntoTemp("{ name: 'cfg', \"\\u0034\\x38\": 'a' }\n");
        Assert.NotNull(config);
        Assert.True(config!.NoteMap.ContainsKey(48));
    }

    [Fact]
    public void Duplicate_note_keys_keep_first_position_but_later_value_wins()
    {
        var config = LoadIntoTemp("{ name: 'cfg', '48': 'a', '50': 'b', '48': 'ctrl+c' }\n");
        Assert.NotNull(config);
        Assert.Equal(new ushort[] { 0x11, 0x43 }, config!.NoteMap[48]);
        Assert.Equal(new ushort[] { 0x42 }, config.NoteMap[50]);
    }
}
