// BridgeCompatibility.cs — 三方协议兼容判定：本 Tap 支持的、本地已装的、云端 latest 的
//
// 协议主版本有三个输入：本构建支持的常量、本地探测值、云端清单值。每处调用只比较其中两个，
// 就会出现"界面按云端判为不兼容、而进程按本地判为兼容"这类矛盾，因此收进一个纯函数，
// 界面与进程读的是同一个枚举值
//
// 本地优先于云端：本地已装程序的协议主版本是确定的（探测过），云端值可能取不到（网络失败、清单没拿到）；
// 本地已经不兼容时，能不能升级都改变不了"这个已装程序无法对接"，因此先判本地并给出 LocalUnsupported
// 让用户重装，而不是去谈"有没有新版本"
//
// 云端未知（remoteSchema <= 0：清单没取到、主版本解析失败）不是"不支持"：网络失败与上游真的换了主版本
// 是两件事，混为一谈会让界面在断网时谎报"上游不兼容"，因此按 Unknown 处理
//
// BridgeCompatibility.cs — the three-way protocol compatibility verdict: what this Tap supports, what is installed
// locally, and what the latest remote release declares
//
// The protocol major has three inputs: the constant this build supports, the local probe value and the remote
// manifest value. A call site comparing only two of them produces contradictions such as "the UI reads the remote
// side as incompatible while the process reads the local side as compatible", so the three go into one pure
// function and the UI and the process read the same enum value
//
// The local side outranks the remote one: the installed build's protocol major is definite (it was probed) while the
// remote value may be unobtainable (network failure, missing manifest), and once the local side is incompatible no
// upgrade changes the fact that this installed program cannot talk, so the local check runs first and yields
// LocalUnsupported asking for a reinstall rather than turning to "is there a newer version"
//
// A remote side that is unknown (remoteSchema <= 0: no manifest, unparsable major) is not "unsupported": a network
// failure and the upstream genuinely changing its major are two different things, and conflating them would make the
// UI claim "the upstream is incompatible" while simply offline, so it reads as Unknown

namespace MIDITap.Core.Bridge;

/// <summary>
/// 三方协议兼容判定的四种结论
/// 没有第五种"云端未知"的独立成员：云端未知只在与本地未安装同时出现时才是 Unknown，
/// 本地已装且兼容时云端未知仍然是 Compatible，因为"能不能对接"只取决于两个已确定的主版本
///
/// The four outcomes of the three-way protocol verdict
/// There is no fifth "remote unknown" member: a remote unknown yields Unknown only together with an absent local
/// install, while a compatible local install stays Compatible, since "can the two sides talk" depends only on the two
/// definitively known majors
/// </summary>
public enum BridgeCompatibility
{
    /// <summary>探测不到本地安装，且云端协议版本也取不到 / No local install was found and the remote protocol version is unavailable too</summary>
    Unknown = 0,

    /// <summary>本地已装（或未装）与云端三方一致 / The local side, installed or not, and the remote side all agree with what this build supports</summary>
    Compatible,

    /// <summary>云端 latest 的协议本 Tap 不支持：不提示更新，也不该安装该版本 / The latest remote protocol is unsupported by this Tap: no update is offered and that version must not be installed</summary>
    RemoteUnsupported,

    /// <summary>本地已装程序的协议本 Tap 不支持：应禁用 Bridge 功能并提示重新安装 / The installed protocol is unsupported by this Tap: the Bridge feature is disabled and a reinstall is suggested</summary>
    LocalUnsupported,
}

public static class BridgeCompatibilityPolicy
{
    /// <summary>
    /// 某个协议主版本是否为本构建所支持
    /// 判据只有"等于 <see cref="BridgeProtocolContract.ExpectedSchema"/>"：上游文档 §6 承诺同主版本向下兼容，
    /// 因此次版本与补丁号不参与判定
    ///
    /// Whether this build supports a given protocol major
    /// The only test is equality with <see cref="BridgeProtocolContract.ExpectedSchema"/>: §6 of the upstream
    /// documentation promises compatibility within one major, so minor and patch take no part
    /// </summary>
    public static bool IsSupported(int schema) => schema == BridgeProtocolContract.ExpectedSchema;

    /// <summary>
    /// 三方判定的主入口
    /// <paramref name="localInstalled"/> 为 false 时忽略 <paramref name="localSchema"/>，只看云端一侧
    /// <paramref name="remoteSchema"/> 为 0 或负数表示云端未知（清单没取到、主版本解析失败）
    ///
    /// 判定顺序固定为先本地后云端
    ///
    /// The primary three-way entry point
    /// When <paramref name="localInstalled"/> is false, <paramref name="localSchema"/> is ignored and only the remote
    /// side is considered. A <paramref name="remoteSchema"/> of 0 or below means the remote side is unknown (no
    /// manifest was fetched, or its major did not parse)
    ///
    /// The order is fixed at local first and remote second
    /// </summary>
    public static BridgeCompatibility Judge(int localSchema, int remoteSchema, bool localInstalled)
    {
        // 1) 本地已装且协议不受支持：确定的事实，优先于任何云端信息
        //
        // 1) The local install exists and its protocol is unsupported: a definite fact that outranks any remote data
        if (localInstalled && !IsSupported(localSchema))
        {
            return BridgeCompatibility.LocalUnsupported;
        }

        // 2) 云端主版本已知但不受支持：不提示更新，也不安装
        //    云端未知（<= 0）不走这条，见文件头关于"网络失败不等于不兼容"的说明
        //
        // 2) The remote major is known and unsupported: no update is offered and nothing is installed
        //    An unknown remote side (<= 0) does not take this branch; see the file header on a network failure not
        //    being the same as incompatibility
        if (remoteSchema > 0 && !IsSupported(remoteSchema))
        {
            return BridgeCompatibility.RemoteUnsupported;
        }

        // 3) 本地未装且云端未知：两个输入都没有确定值，给不出结论
        //
        // 3) Nothing installed and the remote side unknown: neither input is definite, so no verdict exists
        if (!localInstalled && remoteSchema <= 0)
        {
            return BridgeCompatibility.Unknown;
        }

        // 4) 其余情形：本地已装且兼容（云端未知或兼容），或本地未装而云端兼容
        //    走到这里时，凡是已确定的主版本都已通过 IsSupported，因此结论是兼容
        //
        // 4) Everything else: installed and compatible (the remote side being unknown or compatible), or nothing
        //    installed while the remote side is compatible
        //    Every definite major has passed IsSupported by this point, so the verdict is compatible
        return BridgeCompatibility.Compatible;
    }

    /// <summary>
    /// 便捷重载：直接吃探测结果，未解析成功时按"未安装"处理
    /// 未安装与"装了但探测不出协议"在这里给出同一个结论（Unknown 或只看云端），
    /// 因为两者对"能不能对接"都没有可用的本地事实；需要区分它们的是界面文案，那读
    /// <see cref="BridgeInstalledProtocol.Detail"/>，不是本函数
    ///
    /// A convenience overload taking the probe result directly, treating an unparsed result as "not installed"
    /// "Nothing installed" and "installed but the protocol cannot be probed" reach the same verdict here (Unknown, or
    /// the remote side alone), since neither supplies a usable local fact about "can the two sides talk"
    /// Telling them apart is the UI's job through <see cref="BridgeInstalledProtocol.Detail"/>, not this function's
    /// </summary>
    public static BridgeCompatibility Judge(BridgeInstalledProtocol local, int remoteSchema)
    {
        // 探测到了协议才算"已装"：Parsed 为 false 时 Schema 为 0，与 ExpectedSchema 不等，
        // 若把它当成已装会误判成 LocalUnsupported，而那实际只是"探测不出"
        //
        // Only a probed protocol counts as installed: with Parsed false, Schema is 0 and differs from ExpectedSchema,
        // so counting it as installed would misreport LocalUnsupported when the truth is merely "could not probe"
        var installed = local.Comparable;
        return Judge(local.Schema, remoteSchema, installed);
    }

    /// <summary>
    /// 便捷重载：吃界面缓存的协议字符串（未探测到时为 null 或空）
    /// 与上一条同样的道理：目录里有 exe 不等于读得到协议号
    /// 主版本解析失败时 <see cref="BridgeManifestParser.MajorOf"/> 给 0，0 与 ExpectedSchema 不等，
    /// 把它连同"已装"一起交给 <see cref="Judge(int, int, bool)"/> 会得出 LocalUnsupported，
    /// 那是把一个探测失败说成"协议不对"，用户照着提示重装也解决不了探测失败
    ///
    /// 因此只有解析出正数主版本才算拿到本地事实，其余按"本地无信息"处理
    ///
    /// A convenience overload taking the protocol string the UI caches (null or empty when not probed)
    /// The reasoning matches the overload above: an exe in the directory does not mean a protocol number can be read
    /// An unparsable major yields 0 from <see cref="BridgeManifestParser.MajorOf"/>, which differs from ExpectedSchema,
    /// and handing that to <see cref="Judge(int, int, bool)"/> together with "installed" produces LocalUnsupported
    /// That reports a failed probe as a wrong protocol, and reinstalling as instructed would not fix the probe
    ///
    /// So only a parsed positive major counts as a local fact; anything else enters as "no local information"
    /// </summary>
    public static BridgeCompatibility Judge(string? localProtocol, bool localInstalled, int remoteSchema = 0)
    {
        var localSchema = BridgeManifestParser.MajorOf(localProtocol);
        var probed = localSchema > 0;
        return Judge(localSchema, remoteSchema, localInstalled && probed);
    }
}
