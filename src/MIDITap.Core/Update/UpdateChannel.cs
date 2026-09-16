// UpdateChannel.cs — 判断一个构建属于哪个发布渠道，据此决定它是否参与更新
//
// 为什么需要这条判断：dev.yml 注入的版本形如 1.7.1-dev.42+g0d1d487
// release.yml 注入的是标签上的纯版本号
// 让开发构建去和 GitHub 上的最新正式版比较，得不到任何有用结论
// 它既不是那个版本的产物，也不该被"更新"成正式版
// 而每一次检查都会消耗匿名 API 配额（每小时 60 次，按 IP 计），把开发者的日志刷成一串 403
// 因此开发构建不检查，也不提供更新
//
// 为什么放在 Core：这是纯粹的字符串规则，不碰网络，因此可以脱离界面直接测
// 与 UpdateAssetSelector 同样的理由
//
// UpdateChannel.cs — tells which release channel a build belongs to, and whether it takes part in updates
//
// Why this judgement is needed: dev.yml injects a version like 1.7.1-dev.42+g0d1d487
// release.yml injects the plain version from the tag
// Comparing a development build against the latest stable release yields nothing useful
// It is not a product of that version and should not be "updated" into one
// Every check also spends the anonymous API quota (60 per hour per IP), filling a developer's log with 403s
// Development builds therefore neither check nor offer updates
//
// Why it lives in Core: it is a pure string rule that never touches the network, so it needs no UI to test
// The same reasoning as UpdateAssetSelector

namespace MIDITap.Core.Update;

public static class UpdateChannel
{
    /// <summary>
    /// 程序集里没有 InformationalVersion 时对外报出的版本号（见 AppServices.AppVersion）
    /// 定义在这里而不是各写一份字面量：报出的值与本判断是同一个约定的两端
    ///
    /// The version reported when the assembly carries no InformationalVersion (see AppServices.AppVersion)
    /// It is defined here rather than spelled out at each site
    /// The reported value and this rule are two ends of the same convention
    /// </summary>
    public const string UnknownVersion = "unknown";

    /// <summary>dev.yml 注入的预发布标记（1.7.1-dev.42+g0d1d487）
    /// The prerelease marker dev.yml injects (1.7.1-dev.42+g0d1d487)</summary>
    public const string DevelopmentMarker = "-dev.";

    /// <summary>
    /// 是否是开发构建。三种情况算开发构建
    ///   * 版本号为空 —— 拿不到自己在哪个版本，无从比较
    ///   * 版本号是 unknown —— 同上（程序集里没有 InformationalVersion）
    ///   * 版本号带 dev.yml 的 `-dev.` 标记
    ///
    /// 其余都算正式渠道，**包括 `2.0.0-beta.1` 这类预发布版**
    /// 它们有确定的版本号，也确实发布在 Releases 上
    /// 应用查 /releases/latest 会忽略预发布版，那是另一回事
    ///
    /// Whether this is a development build
    /// Three cases count as a development build: an empty version (nothing to compare against)
    /// The "unknown" value (no InformationalVersion in the assembly)
    /// And the `-dev.` marker dev.yml injects
    ///
    /// Everything else is a release channel, **including a prerelease such as `2.0.0-beta.1`**
    /// It carries a definite version and really is published on Releases
    /// That the app queries /releases/latest, which ignores prereleases, is a separate matter
    /// </summary>
    public static bool IsDevelopment(string? informationalVersion)
        => string.IsNullOrWhiteSpace(informationalVersion)
           || informationalVersion.Equals(UnknownVersion, StringComparison.OrdinalIgnoreCase)
           || informationalVersion.Contains(DevelopmentMarker, StringComparison.OrdinalIgnoreCase);
}
