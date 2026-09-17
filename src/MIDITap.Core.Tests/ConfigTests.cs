// 配置路径解析与目录逃逸防护
// 映射的新增/删除/重命名
// JSON5 注释与转义字符串的保留
// 绑定解析、端口解析与配置列表
//
// Config path resolution and directory-escape protection
// Mapping add/delete/rename
// JSON5 comment and escaped-string preservation
// Binding parsing, port parsing and config listing

using MIDITap.Core.Config;
using MIDITap.Core.Settings;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class ConfigTests : IDisposable
{
    public string BaseDir { get; }
    public string ConfigDir { get; }

    public ConfigTests()
    {
        BaseDir = Directory.CreateTempSubdirectory("miditap-config-").FullName;
        ConfigDir = Path.Combine(BaseDir, "config");
        Directory.CreateDirectory(ConfigDir);
    }

    // ------------------------------------------------------------------ 新建配置文件
    //
    // Creating a config file

    [Fact]
    public void CreateConfigFile_uses_the_typed_name_as_the_file_name()
    {
        var path = ConfigEditor.CreateConfigFile(BaseDir, "My Setup");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        // 文件名就是用户输入的名字：界面显示什么，磁盘上就叫什么
        //
        // The file name IS the name the user typed: what the UI shows is what the disk holds
        Assert.Equal("My Setup.json", Path.GetFileName(path));
        // 配置内部不再有 name 字段，因此新文件是一段模板注释加空映射
        //
        // A config no longer carries a name field, so a new file is a template comment plus an empty mapping set
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("\"name\"", text);
        Assert.Empty(ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: path!))!.NoteMap);
    }

    [Theory]
    // 空格与中文都是合法字符，必须原样保留
    //
    // Spaces and Chinese are both legal characters and have to survive verbatim
    [InlineData("  spaced  ", "spaced")]
    [InlineData("我的配置", "我的配置")]
    [InlineData("练习 用 配置", "练习 用 配置")]
    [InlineData("a.b", "a.b")]
    public void CreateConfigFile_keeps_legal_names_verbatim(string typed, string expectedStem)
    {
        var path = ConfigEditor.CreateConfigFile(BaseDir, typed);

        Assert.NotNull(path);
        // 首尾空白由调用方 Trim 后传入，这里断言的是中间的空格与中文不被改动
        //
        // Leading and trailing whitespace is trimmed by the caller; what is asserted here is that inner spaces and Chinese survive
        Assert.Equal(expectedStem.Trim() + ".json", Path.GetFileName(path));
        Assert.StartsWith(ConfigDir + Path.DirectorySeparatorChar, path!);
    }

    [Theory]
    // 路径分隔符、保留字符、上跳一律**拒绝**而不是清洗
    // 清洗会造出用户没有输入的名字，用户反而更找不到那个文件
    //
    // Path separators, reserved characters and upward traversal are REJECTED rather than sanitised
    // Sanitising invents a name the user never typed, which makes the file harder to find, not easier
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("bad:name")]
    [InlineData("bad*name")]
    [InlineData("bad?name")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    public void CreateConfigFile_rejects_names_Windows_cannot_use(string input)
    {
        Assert.Null(ConfigEditor.CreateConfigFile(BaseDir, input));
        // 拒绝时不能在 config/ 里留下任何东西
        //
        // A rejection must leave nothing behind under config/
        Assert.Empty(Directory.GetFiles(ConfigDir));
    }

    [Fact]
    public void CreateConfigFile_rejects_an_existing_name_instead_of_overwriting()
    {
        var first = ConfigEditor.CreateConfigFile(BaseDir, "Dup");
        var second = ConfigEditor.CreateConfigFile(BaseDir, "Dup");

        Assert.NotNull(first);
        Assert.Equal("Dup.json", Path.GetFileName(first));
        // 撞名时拒绝：用户输入的就是他想找的名字，静默改成 Dup-2 又会对不上
        //
        // A clash is rejected: the user typed the name they are looking for, and silently becoming Dup-2 would not match it
        Assert.Null(second);
        Assert.Single(Directory.GetFiles(ConfigDir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateConfigFile_rejects_blank_name(string input)
    {
        Assert.Null(ConfigEditor.CreateConfigFile(BaseDir, input));
    }

    [Fact]
    public void CreateConfigFile_rejects_a_quote_in_the_name()
    {
        // 引号在 Windows 文件名里本就不合法
        // 从前它是"写进 JSON 时要转义"的问题，现在文件名叫什么就是什么，转义那一环不存在了
        //
        // A quote is not a legal Windows file-name character to begin with
        // It used to be an escaping concern when written into JSON; now the file name is taken as-is and there is no escaping step
        Assert.Null(ConfigEditor.CreateConfigFile(BaseDir, "He said \"hi\""));
    }

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

    private string WriteConfig(string filename, string content)
    {
        var path = Path.Combine(ConfigDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    // --- 路径解析 ---------------------------------------------------------- / Path resolution

    [Fact]
    public void Configuration_paths_stay_inside_the_config_directory()
    {
        var inside = WriteConfig("mapping.json", "{}");
        var outside = Path.Combine(BaseDir, "outside.json");
        File.WriteAllText(outside, "{}");

        Assert.Equal(ConfigLocator.RealPath(inside), ConfigLocator.ResolveConfigPath(BaseDir, inside));
        Assert.Equal(ConfigLocator.RealPath(inside), ConfigLocator.ResolveConfigPath(BaseDir, "mapping.json"));
        Assert.Null(ConfigLocator.ResolveConfigPath(BaseDir, "../outside.json"));
        Assert.Null(ConfigLocator.ResolveConfigPath(BaseDir, outside));
    }

    // --- 注释保留写入 -------------------------------------------------------
    //
    // Comment-preserving writes

    [Fact]
    public void Renaming_changes_the_file_name_and_leaves_the_contents_alone()
    {
        var configPath = WriteConfig(
            "mapping.json",
            "{\n  // keep this comment\n  \"48\": \"a\",\n}\n");
        var before = File.ReadAllText(configPath);

        var renamed = ConfigEditor.RenameConfigFile(BaseDir, "mapping.json", "My Setup");

        Assert.Equal(Path.Combine(ConfigDir, "My Setup.json"), renamed);
        // 旧文件必须消失：这验证的是改名而不是复制
        //
        // The old file has to be gone: that is what tells a rename apart from a copy
        Assert.False(File.Exists(configPath));
        // 内容一个字节都不该动，注释与格式原样保留
        //
        // Not one byte of the contents should change; comments and formatting stay as they were
        Assert.Equal(before, File.ReadAllText(renamed!));
    }

    [Theory]
    // 文件名里的空格与中文必须原样落到磁盘上
    //
    // Spaces and Chinese in the name have to reach the disk verbatim
    [InlineData("我的配置")]
    [InlineData("practice setup")]
    [InlineData("练习 用")]
    public void Renaming_keeps_legal_names_verbatim(string newStem)
    {
        var configPath = WriteConfig("mapping.json", "{}");

        var renamed = ConfigEditor.RenameConfigFile(BaseDir, "mapping.json", newStem);

        Assert.Equal(Path.Combine(ConfigDir, newStem + ".json"), renamed);
        Assert.False(File.Exists(configPath));
    }

    [Fact]
    public void Renaming_rejects_a_name_that_already_exists()
    {
        var configPath = WriteConfig("mapping.json", "{}");
        WriteConfig("other.json", "{}");

        Assert.Null(ConfigEditor.RenameConfigFile(BaseDir, "mapping.json", "other"));
        // 两边都必须还在，且原名文件的内容没有被动过
        //
        // Both files must still be there, and the original contents must be untouched
        Assert.True(File.Exists(configPath));
        Assert.True(File.Exists(Path.Combine(ConfigDir, "other.json")));
    }

    [Fact]
    public void Renaming_a_file_to_its_own_name_is_not_a_clash()
    {
        var configPath = WriteConfig("mapping.json", "{}");

        var renamed = ConfigEditor.RenameConfigFile(BaseDir, "mapping.json", "mapping");

        Assert.Equal(configPath, renamed);
        Assert.True(File.Exists(configPath));
    }

    [Fact]
    public void Renaming_a_malformed_file_still_works()
    {
        // 改名只动文件名、不读内容，因此损坏的文件改名后还是原样躺在那里，用户可以继续修
        //
        // A rename only touches the file name and never reads the contents
        // A corrupt file therefore ends up renamed and otherwise untouched, ready for the user to fix
        const string malformed = "{ broken";
        WriteConfig("mapping.json", malformed);

        var renamed = ConfigEditor.RenameConfigFile(BaseDir, "mapping.json", "changed");

        Assert.Equal(Path.Combine(ConfigDir, "changed.json"), renamed);
        Assert.Equal(malformed, File.ReadAllText(renamed!));

        // 损坏的文件读不出来，但新增映射要写它，因此必须仍然失败
        //
        // A corrupt file cannot be read, yet adding a mapping writes it, so that has to keep failing
        Assert.False(ConfigEditor.AddMappingToConfig(BaseDir, renamed!, "48", "a"));
        Assert.Equal(malformed, File.ReadAllText(renamed!));
    }

    [Fact]
    public void Deleting_a_mapping_removes_the_entry_and_preserves_comments()
    {
        var configPath = WriteConfig(
            "mapping.json",
            "{\n  // keep this comment\n  \"name\": \"test\",\n  \"48\": \"a\",\n  \"50\": \"b\",\n}\n");

        Assert.True(ConfigEditor.DeleteMappingFromConfig(BaseDir, configPath, "48"));

        var content = File.ReadAllText(configPath);
        var parsed = Json5Parser.Parse(content);
        Assert.Null(parsed.TryGetString("48"));
        Assert.Equal("b", parsed.TryGetString("50"));
        Assert.Contains("keep this comment", content);

        var loaded = ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: configPath));
        Assert.NotNull(loaded);
        Assert.Single(loaded!.NoteMap);
        Assert.True(loaded.NoteMap.ContainsKey(50));
    }

    [Fact]
    public void Deleting_a_non_existent_mapping_returns_true_without_changing_the_file()
    {
        const string original = "{\n  \"name\": \"test\",\n  \"48\": \"a\"\n}\n";
        var configPath = WriteConfig("mapping.json", original);

        Assert.True(ConfigEditor.DeleteMappingFromConfig(BaseDir, configPath, "50"));
        Assert.Equal(original, File.ReadAllText(configPath));
    }

    [Fact]
    public void Deleting_an_out_of_range_note_returns_false()
    {
        var configPath = WriteConfig("mapping.json", "{\"48\": \"a\"}");
        Assert.False(ConfigEditor.DeleteMappingFromConfig(BaseDir, configPath, "200"));
        Assert.False(ConfigEditor.DeleteMappingFromConfig(BaseDir, configPath, "name"));
    }

    [Fact]
    public void Deleting_works_when_a_non_string_top_level_value_precedes_it()
    {
        var configPath = WriteConfig(
            "mapping.json",
            "{\n  \"name\": \"test\",\n  \"port\": 0,\n  \"48\": \"a\",\n  \"60\": \"b\",\n}\n");

        Assert.True(ConfigEditor.DeleteMappingFromConfig(BaseDir, configPath, "60"));

        var parsed = Json5Parser.Parse(File.ReadAllText(configPath));
        Assert.Null(parsed.TryGetString("60"));
        Assert.Equal("a", parsed.TryGetString("48"));
        Assert.Equal(0, parsed.Get("port")!.NumberValue);
        Assert.Equal("test", parsed.TryGetString("name"));
    }

    [Fact]
    public void Deleting_works_when_a_nested_object_precedes_it()
    {
        var configPath = WriteConfig(
            "mapping.json",
            "{\n  \"name\": \"test\",\n  \"meta\": { \"note\": \"60\" },\n  \"48\": \"a\",\n}\n");

        Assert.True(ConfigEditor.DeleteMappingFromConfig(BaseDir, configPath, "48"));

        var parsed = Json5Parser.Parse(File.ReadAllText(configPath));
        Assert.Null(parsed.TryGetString("48"));
        // 嵌套的同名键未被触碰 / nested lookalike untouched
        Assert.Equal("60", parsed.Get("meta")!.TryGetString("note"));
    }

    [Fact]
    public void Renaming_does_not_touch_a_name_property_left_in_the_file()
    {
        // 配置内部不再有 name 字段，但用户手写的文件里可能还留着
        // 改名是文件级操作，因此那个键原样不动 —— 改名不该顺手编辑用户的内容
        //
        // A config no longer carries a name field, yet a hand-written file may still hold one
        // Renaming is a file-level operation, so that key stays exactly as it was
        // A rename has no business editing the user's contents
        var configPath = WriteConfig("mapping.json", "{\n  name: \"old\",\n  \"48\": \"a\",\n}\n");

        var renamed = ConfigEditor.RenameConfigFile(BaseDir, "mapping.json", "new");

        Assert.NotNull(renamed);
        var parsed = Json5Parser.Parse(File.ReadAllText(renamed!));
        Assert.Equal("old", parsed.TryGetString("name"));
        Assert.Equal("a", parsed.TryGetString("48"));
    }

    [Fact]
    public void Adding_a_mapping_validates_the_note_range()
    {
        var configPath = WriteConfig("mapping.json", "{\n  \"name\": \"t\",\n  \"port\": 0\n}\n");

        Assert.False(ConfigEditor.AddMappingToConfig(BaseDir, configPath, "200", "a"));
        Assert.False(ConfigEditor.AddMappingToConfig(BaseDir, configPath, "port", "a"));
        Assert.False(ConfigEditor.AddMappingToConfig(BaseDir, configPath, "name", "a"));
        Assert.False(ConfigEditor.AddMappingToConfig(BaseDir, configPath, "-1", "a"));

        var parsed = Json5Parser.Parse(File.ReadAllText(configPath));
        Assert.Equal(0, parsed.Get("port")!.NumberValue);
        Assert.Equal("t", parsed.TryGetString("name"));
        Assert.Equal(2, parsed.ObjectEntries.Count);

        Assert.True(ConfigEditor.AddMappingToConfig(BaseDir, configPath, "60", "ctrl+b"));
        Assert.Equal("ctrl+b", Json5Parser.Parse(File.ReadAllText(configPath)).TryGetString("60"));
    }

    // --- 绑定解析 ----------------------------------------------------------- / Binding parsing

    [Fact]
    public void Parse_binding_accepts_single_keys_and_combos_rejects_unknown_names()
    {
        Assert.Equal(new ushort[] { 0x41 }, BindingParser.ParseBinding("a", "48")!.VkCodes);
        Assert.Equal(new ushort[] { 0x11, 0x42 }, BindingParser.ParseBinding("ctrl+b", "50")!.VkCodes);
        Assert.Null(BindingParser.ParseBinding("ArrowUp", "60")); // 不是 VK 名称 / not a VK name
        Assert.Null(BindingParser.ParseBinding("", "60"));
        Assert.Null(BindingParser.ParseBinding("ctrl++", "60"));
        Assert.Equal(new ushort[] { 0x41 }, BindingParser.ParseBinding("  a  ", "48")!.VkCodes);
        Assert.Equal("a", BindingParser.ParseBinding("a", "48")!.Label);
        Assert.Equal("ctrl+b", BindingParser.ParseBinding("ctrl+b", "50")!.Label);
    }

    // --- 加载 --------------------------------------------------------------- / Loading

    [Fact]
    public void Load_config_returns_none_for_missing_or_malformed_files()
    {
        Assert.Null(ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: Path.Combine(ConfigDir, "nope.json"))));
        WriteConfig("bad.json", "{ broken");
        Assert.Null(ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: Path.Combine(ConfigDir, "bad.json"))));
    }

    [Fact]
    public void Load_config_builds_note_map_keyed_by_integer_notes_and_keeps_name_port()
    {
        var configPath = WriteConfig(
            "mapping.json",
            "{\n  name: \"My Config\",\n  \"port\": 2,\n  \"48\": \"a\",\n  \"50\": \"ctrl+b\",\n  \"200\": \"z\",\n  \"ArrowUp\": \"q\",\n}\n");

        var result = ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: configPath));
        Assert.NotNull(result);
        // 文件里那行 name 不再是全局字段，但也不能被当成音符键："name" 不是 0~127 的整数
        // 它因此被归入非法项并丢弃，而不是让整份配置加载失败
        //
        // The name line in the file is no longer a global field, yet it must not be read as a note key either: "name" is not an integer in 0..127
        // It therefore lands in the invalid bucket and is dropped instead of failing the whole load
        Assert.Equal(2u, result.Port);
        Assert.Equal(2, result.NoteMap.Count);
        Assert.Equal(new ushort[] { 0x41 }, result.NoteMap[48]);
        Assert.Equal(new ushort[] { 0x11, 0x42 }, result.NoteMap[50]);
        Assert.False(result.NoteMap.ContainsKey(200));
    }

    [Fact]
    public void Strict_mode_rejects_any_config_with_an_invalid_entry()
    {
        var strictPath = WriteConfig("strict.json", "{\n  \"name\": \"t\",\n  \"48\": \"notavalidkeyname\"\n}\n");
        Assert.Null(ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, Strict: true, ConfigPath: strictPath)));

        // 非严格模式仍返回有效条目，只丢弃坏的那条
        //
        // Non-strict mode still returns the valid entries, dropping only the bad one
        var loose = ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: strictPath));
        Assert.NotNull(loose);
        Assert.Empty(loose!.NoteMap);
    }

    [Fact]
    public void Load_config_skips_out_of_range_notes_even_in_non_strict_mode()
    {
        var configPath = WriteConfig("mapping.json", "{\n  \"name\": \"t\",\n  \"128\": \"a\",\n  \"127\": \"b\"\n}\n");
        var result = ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: configPath));
        Assert.NotNull(result);
        Assert.False(result!.NoteMap.ContainsKey(128));
        Assert.True(result.NoteMap.ContainsKey(127));
    }

    [Fact]
    public void Port_field_is_parsed_as_a_non_negative_integer()
    {
        var configPath = WriteConfig("mapping.json", "{\n  \"name\": \"t\",\n  \"port\": 3,\n  \"48\": \"a\"\n}\n");
        Assert.Equal(3u, ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: configPath))!.Port);

        WriteConfig("mapping.json", "{\n  \"name\": \"t\",\n  \"port\": \"x\",\n  \"48\": \"a\"\n}\n");
        Assert.Null(ConfigLoader.LoadConfig(BaseDir, new LoadOptions(Silent: true, ConfigPath: configPath))!.Port);
    }

    // --- 列表与默认目录 ------------------------------------------------------
    //
    // Listing and the default directory

    [Fact]
    public void List_config_files_reports_filename_name_and_path()
    {
        WriteConfig("mapping.json", "{\n  \"name\": \"Default Config\"\n}\n");
        WriteConfig("second.json", "{\n  \"name\": \"Second\"\n}\n");
        File.WriteAllText(Path.Combine(ConfigDir, "ignore.txt"), "not json");

        var files = ConfigLocator.ListConfigFiles(BaseDir);
        Assert.Equal(2, files.Count);
        // 显示名取自文件名而不是文件内容：文件里写什么 name 都不影响下拉框
        //
        // The display name comes from the file name rather than the contents
        // Whatever name a file holds, the drop-down does not change
        var names = files.Select(x => x.DisplayName).ToList();
        Assert.Contains("mapping", names);
        Assert.Contains("second", names);
    }

    [Fact]
    public void List_config_files_falls_back_to_filename_when_name_missing_or_unparseable()
    {
        WriteConfig("noname.json", "{}");
        WriteConfig("broken.json", "{ broken");
        var files = ConfigLocator.ListConfigFiles(BaseDir);
        // 损坏的文件照样列出来：显示名不依赖解析，用户因此看得见它并去修
        //
        // A corrupt file is still listed: the display name does not depend on parsing
        // The user can therefore see it and go fix it
        var names = files.Select(x => x.DisplayName).ToList();
        Assert.Contains("noname", names);
        Assert.Contains("broken", names);
    }

    [Fact]
    public void Ensure_config_dir_creates_a_default_config_named_after_the_language()
    {
        using var temp = new TempBaseDir();
        var created = ConfigLocator.EnsureConfigDir(temp.BaseDir);
        Assert.NotNull(created);
        // 没有 i18n/ 时跟随默认语言，因此名字取自 FallbackConfigStem
        //
        // With no i18n/ directory the default language applies, so the name comes from FallbackConfigStem
        Assert.Equal(LanguageCatalog.FallbackConfigStem + ".json", Path.GetFileName(created!));
        // 与手动新增配置共用同一个模板，两者打开后长得一样
        //
        // It shares the template a hand-created config uses, so the two look alike when opened
        Assert.Equal(ConfigEditor.TemplateContent, File.ReadAllText(created!));
        var loaded = ConfigLoader.LoadConfig(temp.BaseDir, new LoadOptions(Silent: true));
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.NoteMap);
    }

    [Fact]
    public void Ensure_config_dir_follows_the_saved_language()
    {
        using var temp = new TempBaseDir();
        // 放一份中文语言包并选中它，默认配置名应当随之变成中文
        //
        // A Chinese pack is dropped in and selected, and the default config name follows it
        var i18nDir = Path.Combine(temp.BaseDir, LanguageCatalog.DirectoryName);
        Directory.CreateDirectory(i18nDir);
        File.WriteAllText(
            Path.Combine(i18nDir, "zh_CN.json"),
            "{\n  \"" + LanguageCatalog.DefaultConfigStemKey + "\": \"默认配置\"\n}\n");
        Assert.True(AppStorage.SaveLocale(temp.BaseDir, "zh_CN"));

        var created = ConfigLocator.EnsureConfigDir(temp.BaseDir);
        Assert.NotNull(created);
        Assert.Equal("默认配置.json", Path.GetFileName(created!));
    }

    [Fact]
    public void Ensure_config_dir_is_noop_when_configs_exist()
    {
        WriteConfig("existing.json", "{}");
        var created = ConfigLocator.EnsureConfigDir(BaseDir);
        Assert.Null(created); // 已经有 .json 配置 / already has a .json config
    }


    // --- v1 name 字段迁移 ----------------------------------------------------
    //
    // v1 name-field migration

    [Fact]
    public void Migration_renames_a_v1_file_after_its_name_field()
    {
        WriteConfig("config.json", "{\n  // 注释要留下\n  \"name\": \"我的配置\",\n  \"48\": \"a\",\n}\n");

        Assert.Equal(1, ConfigEditor.MigrateV1NameField(BaseDir));

        // 文件名换成 name 字段的值，旧文件不在了
        //
        // The file name becomes the value of the name field and the old file is gone
        Assert.False(File.Exists(Path.Combine(ConfigDir, "config.json")));
        var migrated = Path.Combine(ConfigDir, "我的配置.json");
        Assert.True(File.Exists(migrated));

        // 字段被删掉，其余内容与注释原样保留
        //
        // The field is gone while the rest of the contents and the comments stay as they were
        var text = File.ReadAllText(migrated);
        Assert.DoesNotContain("\"name\"", text);
        Assert.Contains("注释要留下", text);
        Assert.Equal("a", Json5Parser.Parse(text).TryGetString("48"));
    }

    [Fact]
    public void Migration_advances_the_number_when_the_v1_name_is_taken()
    {
        WriteConfig("example.json", "{}");
        WriteConfig("legacy.json", "{\n  \"name\": \"example\",\n  \"48\": \"a\",\n}\n");

        Assert.Equal(1, ConfigEditor.MigrateV1NameField(BaseDir));

        // 撞名时顺延序号，已有文件不被覆盖
        //
        // A clash advances the number and the existing file is not overwritten
        Assert.Equal("{}", File.ReadAllText(Path.Combine(ConfigDir, "example.json")));
        var migrated = Path.Combine(ConfigDir, "example - 1.json");
        Assert.True(File.Exists(migrated));
        Assert.Equal("a", Json5Parser.Parse(File.ReadAllText(migrated)).TryGetString("48"));
    }

    [Fact]
    public void Migration_keeps_last_config_pointing_at_the_renamed_file()
    {
        // 回归：迁移重命名文件后若不同步 last_config，那份记录会指向不存在的路径
        // 用户下次启动就会掉回别的配置，或者干脆报"加载失败"
        //
        // Regression: without syncing last_config after a rename, the record points at a path that no longer exists
        // The next launch would fall back to another config, or simply report a load failure
        WriteConfig("legacy.json", "{\n  \"name\": \"Renamed\",\n  \"48\": \"a\",\n}\n");
        var legacyPath = Path.Combine(ConfigDir, "legacy.json");
        Assert.True(ConfigLocator.SaveLastConfigPath(BaseDir, legacyPath));

        Assert.Equal(1, ConfigEditor.MigrateV1NameField(BaseDir));

        var expected = Path.Combine(ConfigDir, "Renamed.json");
        Assert.Equal(expected, ConfigLocator.GetLastConfigPath(BaseDir));
    }

    [Fact]
    public void Migration_leaves_last_config_alone_when_a_different_file_is_renamed()
    {
        WriteConfig("other.json", "{}");
        WriteConfig("legacy.json", "{\n  \"name\": \"Renamed\",\n}\n");
        var otherPath = Path.Combine(ConfigDir, "other.json");
        Assert.True(ConfigLocator.SaveLastConfigPath(BaseDir, otherPath));

        Assert.Equal(1, ConfigEditor.MigrateV1NameField(BaseDir));

        // 被改名的不是当前配置，记录因此不该被动过
        //
        // The renamed file is not the current config, so the record must not have been touched
        Assert.Equal(otherPath, ConfigLocator.GetLastConfigPath(BaseDir));
    }

    [Fact]
    public void Migration_is_idempotent_and_leaves_v2_files_alone()
    {
        WriteConfig("v2.json", "{\n  \"48\": \"a\",\n}\n");
        WriteConfig("legacy.json", "{\n  \"name\": \"Migrated\",\n  \"48\": \"b\",\n}\n");

        Assert.Equal(1, ConfigEditor.MigrateV1NameField(BaseDir));
        // 再跑一遍无事可做：v2 文件没有 name，迁移过的也没有
        //
        // A second pass has nothing to do: a v2 file holds no name, and neither does a migrated one
        Assert.Equal(0, ConfigEditor.MigrateV1NameField(BaseDir));

        Assert.True(File.Exists(Path.Combine(ConfigDir, "v2.json")));
        Assert.True(File.Exists(Path.Combine(ConfigDir, "Migrated.json")));
        Assert.Equal("a", Json5Parser.Parse(File.ReadAllText(Path.Combine(ConfigDir, "v2.json"))).TryGetString("48"));
    }

    [Fact]
    public void Migration_skips_corrupt_files_and_a_name_that_is_already_the_file_name()
    {
        const string broken = "{ broken";
        WriteConfig("broken.json", broken);
        WriteConfig("same.json", "{\n  \"name\": \"same\",\n  \"48\": \"a\",\n}\n");

        Assert.Equal(0, ConfigEditor.MigrateV1NameField(BaseDir));

        // 损坏的文件原样躺着：迁移读不动它，但也不能把它弄丢
        //
        // The corrupt file stays exactly as it was: the migration cannot read it and must not lose it either
        Assert.Equal(broken, File.ReadAllText(Path.Combine(ConfigDir, "broken.json")));
        // name 与文件名本来就一致，只需把字段去掉
        //
        // The name already matches the file name, so only the field has to go
        var text = File.ReadAllText(Path.Combine(ConfigDir, "same.json"));
        Assert.DoesNotContain("\"name\"", text);
        Assert.Equal("a", Json5Parser.Parse(text).TryGetString("48"));
    }

    [Theory]
    // 名字没法当文件名用时强制改名为 Config：搁置不动只会让界面名与文件名继续对不上
    //
    // An unusable name is force-renamed to Config: leaving it alone would keep the UI name and the file name apart
    [InlineData("bad:name")]
    [InlineData("bad/name")]
    [InlineData("CON")]
    [InlineData("trailing.")]
    public void Migration_force_renames_an_unusable_name_to_Config(string unusable)
    {
        WriteConfig("legacy.json", "{\n  \"name\": \"" + unusable + "\",\n  \"48\": \"a\",\n}\n");

        Assert.Equal(1, ConfigEditor.MigrateV1NameField(BaseDir));

        Assert.False(File.Exists(Path.Combine(ConfigDir, "legacy.json")));
        var migrated = Path.Combine(ConfigDir, "Config.json");
        Assert.True(File.Exists(migrated));
        // 映射与注释照旧，只有那个不可用的名字被换掉
        //
        // The mappings and comments stay; only the unusable name is replaced
        Assert.Equal("a", Json5Parser.Parse(File.ReadAllText(migrated)).TryGetString("48"));
    }

    [Fact]
    public void Migration_advances_the_number_when_Config_is_taken()
    {
        WriteConfig("Config.json", "{}");
        WriteConfig("legacy.json", "{\n  \"name\": \"bad:name\",\n  \"48\": \"a\",\n}\n");

        Assert.Equal(1, ConfigEditor.MigrateV1NameField(BaseDir));

        Assert.Equal("{}", File.ReadAllText(Path.Combine(ConfigDir, "Config.json")));
        Assert.True(File.Exists(Path.Combine(ConfigDir, "Config - 1.json")));
    }

    // --- last_config 存储 ----------------------------------------------------
    //
    // last_config storage

    [Fact]
    public void Last_config_path_round_trips_and_rejects_outside_paths()
    {
        WriteConfig("mapping.json", "{}");
        var configPath = Path.Combine(ConfigDir, "mapping.json");

        Assert.True(ConfigLocator.SaveLastConfigPath(BaseDir, configPath));
        Assert.Equal(ConfigLocator.RealPath(configPath), ConfigLocator.GetLastConfigPath(BaseDir));

        Assert.False(ConfigLocator.SaveLastConfigPath(BaseDir, Path.Combine(BaseDir, "nope.json")));
    }

    private sealed class TempBaseDir : IDisposable
    {
        public TempBaseDir()
        {
            BaseDir = Directory.CreateTempSubdirectory("miditap-base-").FullName;
        }

        public string BaseDir { get; }

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
    }
}
