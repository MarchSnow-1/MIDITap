// BridgeServersTests.cs — 服务器列表的规则与持久化用例：命名、增删、旧配置迁移
//
// 这些结论全都直接出现在界面上：
//   命名规则错了，用户会看到 "MIDIBridge " 后面跟着空串，或删掉一台后序号不连续
//   迁移错了，升级后的用户会看到空配置，而密码就在旁边那个文件里
//   两者都不抛异常，只能在界面上被看见，因此要在这里钉住
//
// 断言的是"输入到结论"，用例只调用 Core 的公开 API
//
// BridgeServersTests.cs — cases for the server list rules and persistence: naming, add/remove, migration
//
// Every conclusion here shows up directly in the UI:
//   With the naming rule wrong, the user sees "MIDIBridge " followed by nothing, or the index skips after a removal
//   With the migration wrong, the upgraded user sees an empty configuration while the password sits in the file
//   right next to it
//   Neither slip throws; both are only visible on screen, so they are pinned down here
//
// What is asserted is "input to outcome"; the cases call Core's public API

using MIDITap.Core.Bridge;
using MIDITap.Core.Settings;
using Xunit;

namespace MIDITap.Core.Tests;

public sealed class BridgeServersTests : IDisposable
{
    private readonly string _dir;

    public BridgeServersTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miditap-bridge-servers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(AppPaths.StorageDir(_dir));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响用例结论 / A temp directory that will not delete does not change any verdict
        }
    }

    // ---------------------------------------------------------------- naming

    [Fact]
    public void Unnamed_server_shows_a_hash_index()
    {
        // 未命名按序号，序号从 1 开始：列表里第 3 台显示 "#3"
        //
        // An unnamed server shows its index, 1-based: the third entry reads "#3"
        Assert.Equal("#3", BridgeServers.ShortLabel(string.Empty, 2, 5));
    }

    [Fact]
    public void Named_server_shows_its_own_name()
    {
        // 用户命名过就只显示名字：不再缀序号，否则"家里服务器 #2"会让人以为还有一台同名的
        //
        // A named server shows its own name alone: no index is appended, or "home server #2" would suggest a second
        // entry of the same name
        Assert.Equal("home server", BridgeServers.ShortLabel("home server", 1, 3));
    }

    [Fact]
    public void No_server_shows_an_empty_label()
    {
        // 一台服务器都没有时返回空串：调用方据此拼出"不带编号"的内置名称，而不是"内置 #1"
        //
        // With no servers the label is empty, so the caller renders the built-in name without an index rather than
        // "built-in #1"
        Assert.Equal(string.Empty, BridgeServers.ShortLabel(string.Empty, 0, 0));
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public void Empty_storage_has_no_servers()
    {
        // 新装用户：没有列表文件时读到空列表，状态是 Missing 而不是 Corrupt
        //
        // A fresh install: with no list file the read yields an empty list and reports Missing rather than Corrupt
        var result = BridgeServers.Read(_dir);
        Assert.Equal(BridgeServerListStatus.Missing, result.Status);
        Assert.Empty(result.Servers);
        Assert.True(result.Ok);
    }

    [Fact]
    public void Servers_survive_a_write_and_read()
    {
        var servers = new[]
        {
            new BridgeServer("a", "home", "192.0.2.10", 9003),
            new BridgeServer("b", string.Empty, "bridge.example.com", 9001),
        };
        Assert.True(BridgeServers.Write(_dir, servers));

        var result = BridgeServers.Read(_dir);
        Assert.Equal(BridgeServerListStatus.Ok, result.Status);
        Assert.Equal(2, result.Servers.Count);
        Assert.Equal("home", result.Servers[0].Name);
        Assert.Equal("bridge.example.com", result.Servers[1].Host);
        Assert.Equal(9001, result.Servers[1].Port);
    }

    [Fact]
    public void Corrupt_file_is_reported_and_left_alone()
    {
        // 损坏的列表文件报 Corrupt，但不覆盖它：用户手改坏了还能自己修回来
        //
        // A corrupt list file reports Corrupt and is left alone, so a hand-broken file can still be repaired by hand
        File.WriteAllText(BridgeServers.ListPath(_dir), "{ not json");
        var result = BridgeServers.Read(_dir);
        Assert.Equal(BridgeServerListStatus.Corrupt, result.Status);
        Assert.False(result.Ok);
        Assert.Equal("{ not json", File.ReadAllText(BridgeServers.ListPath(_dir)));
    }

    [Fact]
    public void Entries_without_id_or_host_are_skipped()
    {
        // 缺 id 或 host 的条目无法使用：跳过它，而不是整份报废
        //
        // An entry without an id or host is unusable: it is skipped rather than voiding the whole file
        File.WriteAllText(BridgeServers.ListPath(_dir), """
            {
              "servers": [
                { "id": "", "name": "x", "host": "192.0.2.1", "port": 9003 },
                { "id": "ok", "name": "", "host": "192.0.2.2", "port": 9003 },
                { "id": "badport", "name": "", "host": "192.0.2.3", "port": 0 }
              ]
            }
            """);
        var result = BridgeServers.Read(_dir);
        Assert.Equal(BridgeServerListStatus.Ok, result.Status);
        Assert.Single(result.Servers);
        Assert.Equal("ok", result.Servers[0].Id);
    }

    // ---------------------------------------------------------------- add / remove / select

    [Fact]
    public void Added_server_becomes_the_first_when_the_list_was_empty()
    {
        // 空列表加一台：它成为唯一一台，也是当前选中的那一台
        //
        // Adding to an empty list: it becomes the only entry and the selected one
        Assert.True(BridgeServerStore.Add(_dir, new BridgeServer("s1", string.Empty, "192.0.2.10", 9003)));
        var selection = BridgeServerStore.ResolveCurrent(_dir);
        Assert.NotNull(selection.Server);
        Assert.Equal("s1", selection.Server!.Id);
        Assert.Equal(0, selection.Index);
    }

    [Fact]
    public void Removing_the_selected_server_falls_back_to_the_first()
    {
        // 删掉正在用的那台：选中键被清空，解析回落到第一台，而不是报错
        //
        // Removing the entry in use clears the selection key, and resolving falls back to the first entry rather
        // than failing
        BridgeServerStore.Add(_dir, new BridgeServer("s1", "one", "192.0.2.10", 9003));
        BridgeServerStore.Add(_dir, new BridgeServer("s2", "two", "192.0.2.11", 9003));
        BridgeServerStore.Select(_dir, "s1");
        Assert.Equal("s1", BridgeServerStore.ResolveCurrent(_dir).Server!.Id);

        Assert.True(BridgeServerStore.Remove(_dir, "s1"));
        var selection = BridgeServerStore.ResolveCurrent(_dir);
        Assert.Equal("s2", selection.Server!.Id);
        Assert.Equal(0, selection.Index);
    }

    [Fact]
    public void Stale_selection_falls_back_instead_of_failing()
    {
        // 选中键指向一台已经不存在的服务器（手改过列表、或跨版本残留的键）：回落到第一台
        //
        // The selection key points at an entry that no longer exists (a hand-edited list, or a key left by an
        // earlier build): resolving falls back to the first entry
        BridgeServerStore.Add(_dir, new BridgeServer("s1", string.Empty, "192.0.2.10", 9003));
        AppStorage.SaveBridgeSelectedServer(_dir, "gone");
        var selection = BridgeServerStore.ResolveCurrent(_dir);
        Assert.Equal("s1", selection.Server!.Id);
    }

    // ---------------------------------------------------------------- migration

    [Fact]
    public void Legacy_single_server_migrates_into_the_first_entry()
    {
        // 升级路径：旧的 host/port/password 三个键搬进第一台服务器，随后旧键被删掉
        // 密码必须跟着走：否则用户升级后要重新去服务端取一次密码
        //
        // The upgrade path: the old host/port/password keys move into the first entry, and the old keys are removed
        // afterwards
        // The password has to come along; otherwise the user would have to fetch it from the server again
        AppStorage.SaveBridgeHost(_dir, "192.0.2.10");
        AppStorage.SaveBridgePort(_dir, 9003);
        BridgeSecret.Save(_dir, "secret-123");

        var selection = BridgeServerStore.ResolveCurrent(_dir);
        Assert.NotNull(selection.Server);
        Assert.Equal("192.0.2.10", selection.Server!.Host);
        Assert.Equal(9003, selection.Server.Port);
        Assert.Equal("secret-123", BridgeServerStore.ReadPassword(_dir, selection));

        // 旧键已清理：再次解析不会冒出第二台
        //
        // The old keys are gone: resolving again cannot produce a second entry
        Assert.Empty(AppStorage.GetBridgeHost(_dir));
        Assert.Null(AppStorage.GetBridgePort(_dir));
        Assert.Single(BridgeServerStore.Load(_dir));
    }

    [Fact]
    public void Migration_runs_once()
    {
        // 迁移只跑一次：第二次解析仍是一台服务器
        //
        // The migration runs once: a second resolve still sees a single entry
        AppStorage.SaveBridgeHost(_dir, "192.0.2.10");
        AppStorage.SaveBridgePort(_dir, 9003);
        BridgeServerStore.ResolveCurrent(_dir);
        BridgeServerStore.ResolveCurrent(_dir);
        Assert.Single(BridgeServerStore.Load(_dir));
    }

    [Fact]
    public void No_legacy_keys_means_no_migration()
    {
        // 没有旧键就没有可迁移的东西：列表保持为空
        //
        // Without the old keys there is nothing to migrate, and the list stays empty
        var selection = BridgeServerStore.ResolveCurrent(_dir);
        Assert.False(selection.Configured);
        Assert.Empty(selection.Servers);
    }
}
