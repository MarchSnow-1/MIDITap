// 应用设置（语言）与 JSON5 解析器单元测试 / App storage (locale) and JSON5 parser unit tests

using MIDITap.Core.Config;
using MIDITap.Core.Notifications;
using MIDITap.Core.Settings;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class AppStorageTests : IDisposable
{
    public string BaseDir { get; } = Directory.CreateTempSubdirectory("miditap-storage-").FullName;

    public AppStorageTests()
    {
        // 语言可用性要同时满足「清单里有」与「文件在」，所以测试目录也要有语言文件
        // 生产环境里 i18n/ 随包发布，始终存在
        //
        // Locale validity needs both a catalogue entry and a file on disk, so the fixture provides them
        // In production the i18n folder ships with the package and is always there
        var i18n = Path.Combine(BaseDir, "i18n");
        Directory.CreateDirectory(i18n);
        File.WriteAllText(Path.Combine(i18n, "en_US.json"), "{ }");
        File.WriteAllText(Path.Combine(i18n, "zh-Hans.json"), "{ }");
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

    [Fact]
    public void Locale_round_trips()
    {
        Assert.Null(AppStorage.GetLocale(BaseDir));
        Assert.True(AppStorage.SaveLocale(BaseDir, "zh-Hans"));
        Assert.Equal("zh-Hans", AppStorage.GetLocale(BaseDir));
    }

    [Fact]
    public void Locale_without_language_file_is_rejected()
    {
        // fr_FR 既不在清单里也没有文件，必须拒绝，避免存下一个无法加载的语言
        //
        // fr_FR has neither a catalogue entry nor a file and must be rejected
        // That way a language that cannot be loaded is never stored
        Assert.False(AppStorage.SaveLocale(BaseDir, "fr_FR"));
        Assert.Null(AppStorage.GetLocale(BaseDir));
    }

    [Fact]
    public void Stored_locale_that_disappeared_falls_back_to_null()
    {
        // 语言文件被删掉后（用户手动清理），存储里的旧值不能再被采信
        //
        // Once the language file is gone (the user cleaned up by hand)
        // The stored old value can no longer be trusted
        Assert.True(AppStorage.SaveLocale(BaseDir, "zh-Hans"));
        File.Delete(Path.Combine(BaseDir, "i18n", "zh-Hans.json"));
        Assert.Null(AppStorage.GetLocale(BaseDir));
    }

    [Fact]
    public void Missing_file_yields_null()
    {
        Assert.Null(AppStorage.GetLocale(BaseDir));
    }

    [Fact]
    public void Theme_mode_defaults_to_null_so_caller_follows_system()
    {
        // 未设置时必须返回 null，让调用方回退到"跟随系统"（而不是写死深色）
        //
        // When unset it must return null
        // The caller then falls back to "follow the system" rather than hard-coding dark
        Assert.Null(AppStorage.GetThemeMode(BaseDir));
    }

    [Fact]
    public void Theme_mode_round_trips_all_supported_values()
    {
        foreach (var mode in new[] { AppStorage.ThemeSystem, AppStorage.ThemeLight, AppStorage.ThemeDark })
        {
            Assert.True(AppStorage.SaveThemeMode(BaseDir, mode));
            Assert.Equal(mode, AppStorage.GetThemeMode(BaseDir));
        }
    }

    [Fact]
    public void Unsupported_theme_mode_is_rejected_and_old_value_kept()
    {
        Assert.True(AppStorage.SaveThemeMode(BaseDir, AppStorage.ThemeDark));
        Assert.False(AppStorage.SaveThemeMode(BaseDir, "darkish"));
        Assert.False(AppStorage.SaveThemeMode(BaseDir, string.Empty));
        // 拒绝后原有值不得被覆盖 / After a rejection the previous value must not be overwritten
        Assert.Equal(AppStorage.ThemeDark, AppStorage.GetThemeMode(BaseDir));
    }

    [Fact]
    public void Corrupt_theme_file_is_treated_as_unset()
    {
        Directory.CreateDirectory(Path.Combine(BaseDir, ".storage"));
        File.WriteAllText(Path.Combine(BaseDir, ".storage", AppStorage.ThemeStorageKey), "  LIGHT  ");
        // 大小写不匹配 => 非受支持值 => null（回退跟随系统）
        //
        // A case mismatch => unsupported value => null (falls back to following the system)
        Assert.Null(AppStorage.GetThemeMode(BaseDir));
    }
}

/// <summary>
/// 语言清单：清单写死在代码里，目录里多出来的文件不算一门语言
/// 升级不删旧文件，所以「多出来的文件」是常态而不是异常
///
/// The language catalogue is hard-coded, so a file left in the directory is not a language
/// An upgrade does not delete files the new package no longer ships, which makes such a leftover the normal case
/// </summary>
public sealed class LanguageCatalogTests : IDisposable
{
    public string BaseDir { get; } = Directory.CreateTempSubdirectory("miditap-i18n-").FullName;

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

    private void Write(string code, string content)
    {
        var dir = Path.Combine(BaseDir, LanguageCatalog.DirectoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, code + ".json"), content);
    }

    [Fact]
    public void Discover_follows_the_catalogue_order_and_puts_default_first()
    {
        Write("zh-Hant", "{ }");
        Write("en_US", "{ }");
        Write("zh-Hans", "{ }");

        var list = LanguageCatalog.Discover(BaseDir);

        // 顺序取自清单而不是文件名排序，界面顺序因此跨机器一致
        //
        // The order comes from the catalogue rather than from sorting file names, so the UI order is identical across machines
        Assert.Equal(["en_US", "zh-Hans", "zh-Hant"], list.Select(l => l.Code));
        Assert.Equal("English", list[0].NativeName);
        Assert.Equal("简体中文", list[1].NativeName);
        Assert.Equal("繁體中文", list[2].NativeName);
    }

    [Fact]
    public void Missing_directory_still_offers_the_default_language()
    {
        // 目录不存在也不能返回空清单：空的"语言"界面看起来就是功能坏了
        //
        // A missing directory must still not yield an empty list
        // An empty "languages" UI looks like a broken feature
        var list = LanguageCatalog.Discover(BaseDir);
        Assert.Single(list);
        Assert.Equal(LanguageCatalog.DefaultCode, list[0].Code);
    }

    [Fact]
    public void File_outside_the_catalogue_is_not_a_language()
    {
        // 升级只覆盖同名文件、不删旧文件，因此上一版的 zh_CN.json 会留在目录里
        // 它若被当成一门语言，下拉里就会出现两个「简体中文」，且选中的那份可能缺任意键
        //
        // An upgrade overwrites same-named files and deletes nothing, so the previous version's zh_CN.json stays behind
        // Were it treated as a language, the picker would list 简体中文 twice and the selected copy could be missing any key
        Write("en_US", "{ }");
        Write("zh-Hans", "{ }");
        Write("zh_CN", "{\"lang.name\":\"简体中文\"}");

        var list = LanguageCatalog.Discover(BaseDir);

        Assert.Equal(["en_US", "zh-Hans"], list.Select(l => l.Code));
        Assert.False(LanguageCatalog.IsSupported(BaseDir, "zh_CN"));
    }

    [Fact]
    public void Listed_language_without_its_file_is_not_offered()
    {
        // 清单里有、文件却没打包进来：不列出它，否则选中后整屏都是键名
        //
        // Listed but not shipped in the package: it is not offered, since selecting it would fill the screen with key names
        Write("en_US", "{ }");

        Assert.Equal(["en_US"], LanguageCatalog.Discover(BaseDir).Select(l => l.Code));
        Assert.False(LanguageCatalog.IsSupported(BaseDir, "zh-Hans"));
        Assert.False(LanguageCatalog.IsSupported(BaseDir, "zh-Hant"));
    }

    [Fact]
    public void IsSupported_rejects_unknown_and_empty_codes()
    {
        Write("en_US", "{ }");
        Assert.False(LanguageCatalog.IsSupported(BaseDir, "fr_FR"));
        Assert.False(LanguageCatalog.IsSupported(BaseDir, string.Empty));
        Assert.False(LanguageCatalog.IsSupported(BaseDir, null));
    }

    [Fact]
    public void Both_chinese_files_are_distinguished_by_their_native_names()
    {
        // 两份中文各自带母语名，调用方据此显示「简体中文」「繁體中文」
        //
        // The two Chinese files carry their own native names, which is what the caller shows as 简体中文 and 繁體中文
        Write("en_US", "{ }");
        Write("zh-Hans", "{ }");
        Write("zh-Hant", "{ }");

        var list = LanguageCatalog.Discover(BaseDir);

        Assert.Equal(["en_US", "zh-Hans", "zh-Hant"], list.Select(l => l.Code));
        Assert.Equal("简体中文", list[1].NativeName);
        Assert.Equal("繁體中文", list[2].NativeName);
    }
}

/// <summary>
/// 浮窗显示时长策略：带操作的必须常驻，消息类要按时收走
///
/// Toast display-duration policy: those with an action must stay put
/// Message-only ones are taken away on time
/// </summary>
public sealed class ToastPolicyTests
{
    [Fact]
    public void Notification_with_action_never_auto_dismisses()
    {
        // "发现新版本，去下载"这类提示不能在用户读完前消失
        //
        // A notice such as "a new version is available, go download it" must not disappear before the user has read it
        foreach (var severity in Enum.GetValues<ToastSeverity>())
        {
            Assert.Null(ToastPolicy.AutoDismissAfter(severity, hasAction: true));
        }
    }

    [Fact]
    public void Plain_notifications_do_auto_dismiss()
    {
        foreach (var severity in Enum.GetValues<ToastSeverity>())
        {
            var delay = ToastPolicy.AutoDismissAfter(severity, hasAction: false);
            Assert.NotNull(delay);
            Assert.True(delay!.Value > TimeSpan.Zero);
        }
    }

    [Fact]
    public void Warnings_and_errors_stay_longer_than_success()
    {
        // 需要用户读完整句才能判断怎么处理，给的时间必须不短于成功提示
        //
        // The user has to read the whole sentence to decide what to do
        // So the time given must be no shorter than for a success notice
        var success = ToastPolicy.AutoDismissAfter(ToastSeverity.Success, false)!.Value;
        var warning = ToastPolicy.AutoDismissAfter(ToastSeverity.Warning, false)!.Value;
        var error = ToastPolicy.AutoDismissAfter(ToastSeverity.Error, false)!.Value;
        Assert.True(warning >= success);
        Assert.True(error >= success);
    }

    [Fact]
    public void Queue_is_bounded()
    {
        // 上限必须存在且为正，否则连续提示会堆满整屏
        //
        // The cap must exist and be positive
        // Otherwise back-to-back notices would fill the whole screen
        Assert.True(ToastPolicy.MaxVisible > 0);
    }
}

public class Json5ParserTests
{
    private static Json5Value Parse(string text) => Json5Parser.Parse(text);

    [Fact]
    public void Parses_basic_object_with_comments_and_trailing_comma()
    {
        var value = Parse("{\n  // comment\n  'a': 1, /* block */ \"b\": 'two',\n}\n");
        Assert.True(value.IsObject);
        Assert.Equal(1, value.Get("a")!.NumberValue);
        Assert.Equal("two", value.TryGetString("b"));
    }

    [Fact]
    public void Parses_hex_octal_binary_and_exponent_numbers()
    {
        Assert.Equal(0x10, Parse("{ a: 0x10 }").Get("a")!.NumberValue);
        // JSON5 只支持 0x；0o/0b 是 JS Number() 的扩展，npm json5 会拒绝
        //
        // JSON5 only supports 0x; 0o/0b are extensions of JS Number()
        // npm json5 rejects them
        Assert.Throws<Json5ParseException>(() => Parse("{ a: 0o17 }"));
        Assert.Throws<Json5ParseException>(() => Parse("{ a: 0b101 }"));
        Assert.Throws<Json5ParseException>(() => Parse("{ a: 048 }"));
        Assert.Equal(1200, Parse("{ a: 1.2e3 }").Get("a")!.NumberValue);
        Assert.Equal(0.5, Parse("{ a: .5 }").Get("a")!.NumberValue);
        Assert.Equal(-3, Parse("{ a: -3 }").Get("a")!.NumberValue);
        Assert.Equal(48, Parse("{ a: 4.8e1 }").Get("a")!.NumberValue);
    }

    [Fact]
    public void Parses_string_escapes()
    {
        Assert.Equal("line\nname $1", Parse("{ a: 'line\\nname $1' }").TryGetString("a"));
        Assert.Equal("quote\"inside", Parse("{ a: \"quote\\\"inside\" }").TryGetString("a"));
        Assert.Equal("A", Parse("{ a: '\\x41' }").TryGetString("a"));
        Assert.Equal("中", Parse("{ a: '\\u4e2d' }").TryGetString("a"));
        // 续行丢弃换行符 / line continuation drops the newline
        Assert.Equal("no-escape-", Parse("{ a: 'no-escape-\\\n' }").TryGetString("a"));
    }

    [Fact]
    public void Parses_literals_and_infinitiy()
    {
        Assert.True(Parse("{ a: true }").Get("a")!.BoolValue);
        Assert.False(Parse("{ a: false }").Get("a")!.BoolValue);
        Assert.True(Parse("{ a: false }").Get("a")!.Kind == Json5Kind.Bool);
        Assert.True(Parse("{ a: null }").Get("a")!.IsNull);
        Assert.True(double.IsPositiveInfinity(Parse("{ a: Infinity }").Get("a")!.NumberValue));
        Assert.True(double.IsNegativeInfinity(Parse("{ a: -Infinity }").Get("a")!.NumberValue));
        Assert.True(double.IsNaN(Parse("{ a: NaN }").Get("a")!.NumberValue));
    }

    [Fact]
    public void Duplicate_keys_keep_first_position_and_later_value_wins()
    {
        var value = Parse("{ a: 1, b: 2, a: 3 }");
        Assert.Equal(3, value.Get("a")!.NumberValue);
        Assert.Equal(2, value.ObjectEntries.Count); // 键：a, b / keys: a, b
        Assert.Equal("a", value.ObjectEntries[0].Key);
        Assert.Equal("b", value.ObjectEntries[1].Key);
    }

    [Fact]
    public void Rejects_broken_content()
    {
        Assert.Throws<Json5ParseException>(() => Parse("{ broken"));
        Assert.Throws<Json5ParseException>(() => Parse("[1, 2"));
        Assert.Throws<Json5ParseException>(() => Parse("{ a: }"));
        Assert.Throws<Json5ParseException>(() => Parse("trueish"));
    }

    [Fact]
    public void Parses_arrays_and_nested_structures()
    {
        var value = Parse("{ list: [1, 'two', { three: 3 },], }");
        var list = value.Get("list")!;
        Assert.Equal(3, list.ArrayValue.Count);
        Assert.Equal(3, list.ArrayValue[2]!.Get("three")!.NumberValue);
    }

    [Fact]
    public void Parse_quoted_string_decodes_keys()
    {
        Assert.Equal("48", Json5Parser.ParseQuotedString("'48'"));
        Assert.Equal("\"q\"", Json5Parser.ParseQuotedString("\"\\\"q\\\"\""));
        Assert.Null(Json5Parser.ParseQuotedString("unquoted"));
        Assert.Null(Json5Parser.ParseQuotedString("'unterminated"));
    }
}
