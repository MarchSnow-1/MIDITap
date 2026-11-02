// BridgeServerStore.cs — 服务器列表的"当前选中项"与旧配置迁移（本层要看列表、选中键与旧单组键三者）
// 迁移：列表文件不存在但旧的 miditap_bridge_host/_port 有值时，把它们拼成第一台服务器，
// 密码搬到新条目的文件名下，成功后才删旧键
// 密码搬迁失败不清旧键：宁可留着让用户手动再填一次，也不能把密码弄丢
//
// BridgeServerStore.cs — the selected entry of the server list, plus migration from the older layout
// Migration: when the list file is absent but the old miditap_bridge_host/_port hold values, they become the first
// entry; the password moves under the new entry's file name and only then are the old keys removed
// A failed password move keeps the old keys: retyping a password beats losing it

using MIDITap.Core.Settings;

namespace MIDITap.Core.Bridge;

/// <summary>
/// 一次"当前服务器"解析的结果
/// <see cref="Server"/> 是选中条目本身，只要列表非空就不为 null —— 界面要靠它改名、删除、回填地址
/// <see cref="Configured"/> 才表示"这台能拿去连"：地址非空才算
/// 两者分开是因为"刚添加、地址还没填"的条目必须还能被操作，只是不能用来连接
///
/// The outcome of resolving the current server
/// <see cref="Server"/> is the selected entry itself and is non-null whenever the list is not empty, because the UI
/// needs it to rename, remove and refill fields
/// <see cref="Configured"/> is what says "this one can be connected": only a non-empty address counts
/// The two are kept apart because a freshly added entry without an address still has to be operable, just not
/// connectable
/// </summary>
/// <param name="Server">当前条目；列表为空时为 null / The current entry; null when the list is empty</param>
/// <param name="Index">它在列表中的下标（0 起），无条目时为 -1 / Its zero-based index, or -1 when there is none</param>
public sealed record BridgeServerSelection(BridgeServer? Server, int Index, IReadOnlyList<BridgeServer> Servers)
{
    /// <summary>是否有条目可用（列表非空） / Whether there is an entry to work with (the list is not empty)</summary>
    public bool HasEntry => Server is not null;

    /// <summary>是否已填地址，可以拿去连接 / Whether an address is filled in and the entry can be connected</summary>
    public bool Configured => Server is not null && Server.Host.Length > 0;
}

public static class BridgeServerStore
{
    /// <summary>
    /// 读取整份列表，必要时先做一次旧配置迁移
    /// 迁移只在列表文件缺失时发生，因此重复调用不会再生成第二台服务器
    ///
    /// Reads the whole list, migrating the old layout first when needed
    /// The migration only happens while the list file is absent, so a repeated call cannot create a second entry
    /// </summary>
    public static IReadOnlyList<BridgeServer> Load(string baseDir)
    {
        var result = BridgeServers.Read(baseDir);
        if (result.Status != BridgeServerListStatus.Missing)
        {
            return result.Servers;
        }

        return MigrateLegacy(baseDir, result.Servers);
    }

    /// <summary>
    /// 解析"当前该用哪一台"
    /// 顺序：选中的 id 仍在列表里 → 用它；否则用第一台；列表为空则未配置
    /// 选中项失效（被删掉、手改过 id）时回落到第一台而不是报错：用户删掉正在用的服务器后，
    /// 剩下的那台显然是他想继续用的
    ///
    /// Resolves which entry is in use
    /// Order: the selected id while it is still in the list, otherwise the first entry, otherwise unconfigured
    /// A stale selection (the entry was removed, or the id was hand-edited) falls back to the first entry rather than
    /// failing: after deleting the server in use, the remaining one is plainly the one to carry on with
    /// </summary>
    public static BridgeServerSelection ResolveCurrent(string baseDir)
    {
        var servers = Load(baseDir);
        if (servers.Count == 0)
        {
            return new BridgeServerSelection(null, -1, servers);
        }

        // 地址为空不影响这里返回条目本身：界面要靠它改名与删除，能不能连接由 Configured 判断
        //
        // An empty address does not keep the entry from coming back here, because the UI needs it to rename and remove
        // it; whether it can be connected is Configured's answer
        var selectedId = AppStorage.GetBridgeSelectedServer(baseDir);
        var index = 0;
        if (selectedId.Length > 0)
        {
            var found = -1;
            for (var i = 0; i < servers.Count; i++)
            {
                if (string.Equals(servers[i].Id, selectedId, StringComparison.Ordinal))
                {
                    found = i;
                    break;
                }
            }
            if (found >= 0)
            {
                index = found;
            }
        }

        return new BridgeServerSelection(servers[index], index, servers);
    }

    /// <summary>
    /// 当前选中项对应的密码
    /// 未配置服务器时退回迁就旧条目的读法，避免"刚升级、迁移没成功"时连密码都读不到
    ///
    /// The password for the current selection
    /// With no server configured it falls back to the legacy entry, so a failed migration cannot take the password with it
    /// </summary>
    public static string? ReadPassword(string baseDir, BridgeServerSelection selection)
        => selection.Server is null
            ? BridgeSecret.Read(baseDir)
            : BridgeSecret.ReadFor(baseDir, selection.Server.Id);

    /// <summary>
    /// 选中一台服务器并落盘
    /// 只写选中键，不改列表内容
    ///
    /// Selects a server and persists the choice
    /// Only the selection key is written; the list itself is untouched
    /// </summary>
    public static bool Select(string baseDir, string serverId) => AppStorage.SaveBridgeSelectedServer(baseDir, serverId);

    /// <summary>
    /// 追加一台服务器；id 由调用方给出（界面用 <see cref="BridgeServers.NewId"/> 生成）
    /// 写盘成功才返回 true，失败时列表保持不变
    ///
    /// Appends a server; the id comes from the caller (the UI uses <see cref="BridgeServers.NewId"/>)
    /// True only when the write succeeded, so a failure leaves the list unchanged
    /// </summary>
    public static bool Add(string baseDir, BridgeServer server)
    {
        var servers = Load(baseDir);
        var next = new List<BridgeServer>(servers) { server };
        return BridgeServers.Write(baseDir, next);
    }

    /// <summary>
    /// 修改一台服务器的名字/地址/端口（按 id 定位）；找不到时返回 false
    ///
    /// Updates one server's name/address/port by id; false when the id is absent
    /// </summary>
    public static bool Update(string baseDir, string serverId, string name, string host, int port)
    {
        var servers = Load(baseDir);
        var index = -1;
        for (var i = 0; i < servers.Count; i++)
        {
            if (string.Equals(servers[i].Id, serverId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            return false;
        }

        var next = new List<BridgeServer>(servers)
        {
            [index] = servers[index] with { Name = name, Host = host, Port = port },
        };
        return BridgeServers.Write(baseDir, next);
    }

    /// <summary>
    /// 删除一台服务器（按 id），连同它自己的密码文件；找不到时返回 false
    /// 删除后如果删掉的正是选中项，选中键一并清空，下次解析自然回落到第一台
    ///
    /// Removes one server by id together with its own password file; false when the id is absent
    /// When the removed entry was the selected one the selection key is cleared too, so the next resolve falls back
    /// to the first entry
    /// </summary>
    public static bool Remove(string baseDir, string serverId)
    {
        var servers = Load(baseDir);
        var next = servers.Where(s => !string.Equals(s.Id, serverId, StringComparison.Ordinal)).ToList();
        if (next.Count == servers.Count)
        {
            return false;
        }

        if (!BridgeServers.Write(baseDir, next))
        {
            return false;
        }

        BridgeSecret.ClearFor(baseDir, serverId);

        var selected = AppStorage.GetBridgeSelectedServer(baseDir);
        if (string.Equals(selected, serverId, StringComparison.Ordinal))
        {
            AppStorage.SaveBridgeSelectedServer(baseDir, null);
        }
        return true;
    }

    /// <summary>
    /// 旧单组键 → 第一台服务器的迁移
    /// 只在"列表文件缺失且旧地址有值"时生效；密码搬迁失败时保留旧键并返回空列表，
    /// 让用户下次打开设置页仍看到原来那份配置
    ///
    /// Migration from the old single-set keys to the first entry
    /// It only applies while the list file is absent and an old address holds a value; when moving the password fails
    /// the old keys are kept and an empty list is returned, so the user still sees the previous configuration
    /// </summary>
    private static IReadOnlyList<BridgeServer> MigrateLegacy(string baseDir, IReadOnlyList<BridgeServer> current)
    {
        var host = AppStorage.GetBridgeHost(baseDir);
        var port = AppStorage.GetBridgePort(baseDir);
        if (host.Length == 0 || port is null)
        {
            return current;
        }

        var id = BridgeServers.NewId(current);
        var entry = new BridgeServer(id, string.Empty, host, port.Value);
        if (!BridgeServers.Write(baseDir, [entry]))
        {
            return current;
        }

        // 密码搬迁：读旧条目 -> 写到新条目名下 -> 删除旧条目
        // 读不到密码也算迁移成功（用户本来就没填），此时只需要清键
        //
        // Moving the password: read the legacy entry, write it under the new entry, then remove the legacy one
        // An absent password still counts as a successful migration (the user never filled one); only the keys are removed
        var password = BridgeSecret.Read(baseDir);
        if (!string.IsNullOrEmpty(password))
        {
            var moved = BridgeSecret.SaveWithResultFor(baseDir, id, password);
            if (!moved.Ok)
            {
                // 密码没搬过去就保留旧键，避免"列表有了、密码没了"
                //
                // Keep the old keys when the password did not move, so "the list exists but the password is gone"
                // cannot happen
                return current;
            }
        }

        AppStorage.DeleteLegacyBridgeConnectionKeys(baseDir);
        BridgeSecret.ClearLegacy(baseDir);
        AppStorage.SaveBridgeSelectedServer(baseDir, id);
        return [entry];
    }
}
