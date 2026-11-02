// BridgeServers.cs — MIDIBridge 上游服务器列表（多台服务器共用一份组件），列表整体存在一个文件里
// 列表文件只含名称与地址：密码按服务器 id 各存一个独立文件，沿用 BridgeSecret 的 ACL 收紧做法
//
// BridgeServers.cs — the MIDIBridge upstream server list, one component shared by several servers and held in one file
// The list file holds names and addresses only: each password lives in its own file named by server id, reusing
// BridgeSecret's ACL narrowing

using System.Text.Json;
using System.Text.Json.Serialization;
using MIDITap.Core.Settings;

namespace MIDITap.Core.Bridge;

/// <summary>
/// 一个上游服务器条目
/// <see cref="Name"/> 为空表示"未命名"，界面按序号显示
/// <see cref="Id"/> 用于给密码文件命名，改名不影响它
///
/// One upstream server entry
/// An empty <see cref="Name"/> means "unnamed" and the UI shows the index
/// <see cref="Id"/> names the password file, so a rename does not move the password
/// </summary>
public sealed record BridgeServer(string Id, string Name, string Host, int Port);

/// <summary>
/// 列表文件的读写结果
/// 读取失败与"没有服务器"是两件事：前者应当提示用户，后者是正常初始状态
///
/// The outcome of reading or writing the list file
/// A failed read and "no servers" are different things: the former deserves a warning, the latter is the normal
/// initial state
/// </summary>
public enum BridgeServerListStatus
{
    Ok,

    /// <summary>文件不存在（等同于空列表） / The file is absent (same as an empty list)</summary>
    Missing,

    /// <summary>文件存在但内容无法解析 / The file exists but cannot be parsed</summary>
    Corrupt,
}

public sealed record BridgeServerListResult(BridgeServerListStatus Status, IReadOnlyList<BridgeServer> Servers)
{
    /// <summary>是否可继续使用（Missing 也按可用处理，只是列表为空） / Whether the result is usable (Missing counts as usable with an empty list)</summary>
    public bool Ok => Status is BridgeServerListStatus.Ok or BridgeServerListStatus.Missing;
}

/// <summary>
/// 服务器列表的纯规则与持久化
/// 文件名取自 AppStorage 的存储键，方向与本层其它文件一致（Bridge → Settings）
///
/// The list's pure rules plus its persistence
/// The file name comes from AppStorage's key, the same direction as the rest of this layer (Bridge → Settings)
/// </summary>
public static class BridgeServers
{
    /// <summary>列表文件里的 JSON 结构（只含可公开的内容） / The JSON shape inside the list file (public content only)</summary>
    private sealed record StoredServer
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("host")]
        public string? Host { get; init; }

        [JsonPropertyName("port")]
        public int Port { get; init; }
    }

    private sealed record StoredList
    {
        [JsonPropertyName("servers")]
        public List<StoredServer>? Servers { get; init; }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public static string ListPath(string baseDir) => AppPaths.StorageFile(baseDir, AppStorage.BridgeServerListStorageKey);

    /// <summary>
    /// 读取列表
    /// 文件缺失按空列表处理；文件损坏返回 Corrupt 且不覆盖它 —— 用户手改坏了还能自己修回来
    ///
    /// Reads the list
    /// A missing file reads as an empty list; a corrupt file reports Corrupt and is left alone, so a hand-broken file
    /// can still be repaired by hand
    /// </summary>
    public static BridgeServerListResult Read(string baseDir)
    {
        var path = ListPath(baseDir);
        string json;
        try
        {
            if (!File.Exists(path))
            {
                return new BridgeServerListResult(BridgeServerListStatus.Missing, []);
            }
            json = File.ReadAllText(path);
        }
        catch
        {
            return new BridgeServerListResult(BridgeServerListStatus.Corrupt, []);
        }

        StoredList? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoredList>(json);
        }
        catch
        {
            return new BridgeServerListResult(BridgeServerListStatus.Corrupt, []);
        }

        if (parsed?.Servers is null)
        {
            return new BridgeServerListResult(BridgeServerListStatus.Corrupt, []);
        }

        // 逐项过滤：缺 id 或端口越界的条目无法使用，跳过而不是整体报废
        // host 为空的条目要保留 —— "添加服务器"产生的就是空地址条目，用户随后再填；
        // 在读取时丢掉它会让添加看起来没生效，改名与删除也找不到目标
        // 空地址的条目在 ResolveCurrent 里判为"未配置"，因此不会带着空值去连接
        //
        // Filter entry by entry: an entry without an id, or with an out-of-range port, is unusable and is skipped
        // rather than voiding the whole file
        // An entry with an empty host is kept, because "add a server" produces exactly that and the user fills the
        // address afterwards; dropping it here would make the add look like a no-op and leave rename and remove
        // without a target
        // An empty-address entry reads as "not configured" in ResolveCurrent, so no connection is attempted with
        // empty values
        var servers = new List<BridgeServer>(parsed.Servers.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in parsed.Servers)
        {
            var id = item.Id?.Trim() ?? string.Empty;
            if (id.Length == 0 || item.Port is < 1 or > 65535)
            {
                continue;
            }
            if (!seenIds.Add(id))
            {
                continue;
            }
            servers.Add(new BridgeServer(id, item.Name?.Trim() ?? string.Empty, item.Host?.Trim() ?? string.Empty, item.Port));
        }

        return new BridgeServerListResult(BridgeServerListStatus.Ok, servers);
    }

    /// <summary>
    /// 写回列表；失败返回 false，调用方据此提示"保存失败"
    /// 先写临时文件再替换，避免写到一半掉电留下半个 JSON
    ///
    /// Writes the list back; false means the caller should report a failure
    /// It writes a temporary file first and then replaces, so losing power mid-write cannot leave half a JSON
    /// </summary>
    public static bool Write(string baseDir, IReadOnlyList<BridgeServer> servers)
    {
        var path = ListPath(baseDir);
        try
        {
            Directory.CreateDirectory(AppPaths.StorageDir(baseDir));
            var payload = new StoredList
            {
                Servers = [.. servers.Select(s => new StoredServer
                {
                    Id = s.Id,
                    Name = s.Name,
                    Host = s.Host,
                    Port = s.Port,
                })],
            };
            var json = JsonSerializer.Serialize(payload, WriteOptions);
            var temp = path + ".part";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 未命名时是「#序号」，命名过就是名字本身：这些句子前面已经写了 MIDIBridge，
    /// 再拼一次基础名会读成 "MIDIBridge [内置] MIDIBridge 1"，重复且难读
    /// 没有服务器时返回空串，调用方据此拼出不留悬空的句子
    ///
    /// Unnamed entries read "#index", a named one reads as its own name: those sentences already start with
    /// MIDIBridge, and appending the base name again would read "MIDIBridge [内置] MIDIBridge 1"
    /// With no server it returns an empty string, so the caller can build a sentence with nothing left dangling
    /// </summary>
    public static string ShortLabel(string name, int index, int serverCount)
    {
        if (serverCount <= 0 || index < 0)
        {
            return string.Empty;
        }
        var trimmed = name.Trim();
        return trimmed.Length > 0 ? trimmed : $"#{index + 1}";
    }

    /// <summary>
    /// 设备下拉框里某一台服务器对应的端口编号
    /// 从 <see cref="MidiSourcePortBase"/> 往前递推：第 0 台最靠前，后面的依次减一
    /// 之所以占一整段负数区间而不是只用一个哨兵值：每台服务器都是下拉框里独立的一项，
    /// 用户从这里直接选"连哪一台"，不必先去设置页切换
    ///
    /// The port number one server occupies in the device drop-down
    /// It counts down from <see cref="MidiSourcePortBase"/>: entry 0 comes first and later entries are one lower
    /// It reserves a range of negative numbers rather than a single sentinel because every server is a separate
    /// drop-down item, so the user picks which server to connect straight from the home page instead of switching in
    /// the settings page first
    /// </summary>
    /// <param name="index">服务器在列表中的下标（0 起） / Zero-based index in the server list</param>
    public static int SourcePortFor(int index) => MidiSourcePortBase - index;

    /// <summary>
    /// 端口编号是不是某台服务器的
    /// 与 <see cref="SourcePortFor"/> 互为反函数，界面据此把选中项翻译回"第几台"
    ///
    /// Whether a port number belongs to a server
    /// The inverse of <see cref="SourcePortFor"/>; the UI uses it to translate a selected item back into an index
    /// </summary>
    public static bool IsServerPort(int port) => port <= MidiSourcePortBase;

    /// <summary>
    /// 服务器端口编号反解成列表下标；不属于服务器时返回 -1
    ///
    /// Turns a server port number back into a list index; -1 when the port is not a server's
    /// </summary>
    public static int IndexForSourcePort(int port) => IsServerPort(port) ? MidiSourcePortBase - port : -1;

    /// <summary>
    /// 与空行（-3）分开，避免两者撞号；这个值同时是"第一台服务器"的编号
    ///
    /// It stays clear of the blank row (-3) so the two cannot collide, and it is also the first server's number
    /// </summary>
    public const int MidiSourcePortBase = -100;

    /// <summary>
    /// 新条目的 id；用时间戳加序号，保证同一毫秒内连续添加也不重
    /// 只由 [0-9a-z] 组成，因为它要当文件名的一部分
    ///
    /// The id for a new entry; a timestamp plus a counter, so two adds within the same millisecond still differ
    /// Only [0-9a-z] is used because it becomes part of a file name
    /// </summary>
    public static string NewId(IReadOnlyList<BridgeServer> existing)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        for (var attempt = 0; ; attempt++)
        {
            var candidate = $"{stamp}{attempt:D2}s";
            if (!existing.Any(s => string.Equals(s.Id, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }
    }
}
