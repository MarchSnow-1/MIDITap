// UpdateEndpoints.cs — 更新检查涉及的三个端点地址
// 为什么把地址做成参数而不是直接引用常量：本模块的关键行为是"**API 优先，失败回退网页**"
// 而这正是最该被测试锁住的一点
// 地址被写死成常量时，测试就只能真的去访问 GitHub
// 那样既不稳定（网络波动、限流）又慢
// 把它们参数化后，测试可以让"API"指向一个必然失败的本地端点
// 也可以让"网页"指向一个必然成功的本地端点，从而**确定性地**验证回退顺序
//
// The three endpoint addresses involved in an update check
// Why these are parameters rather than constants: the module's key behaviour is the fallback order
// "API first, fall back to the web page" is exactly what deserves to be locked down by tests
// With hard-coded URLs the only way to test it is to really hit GitHub, which is slow and unreliable
// Parameterising them lets a test point "the API" at a local endpoint that must fail
// And "the web page" at one that must succeed, verifying the fallback order deterministically

namespace MIDITap.Core.Update;

/// <summary>
/// 更新检查使用的端点。默认即真实地址
///
/// The endpoints used by the update check; the defaults are the real addresses
/// </summary>
public sealed record UpdateEndpoints(
    string ApiUrl,
    string LatestPageUrl,
    string ReleasesPageBase)
{
    /// <summary>真实端点 / The real endpoints</summary>
    public static readonly UpdateEndpoints Default =
        new(UpdateChecker.GitHubApiUrl, UpdateChecker.ReleasesUrl, UpdateChecker.ReleasesPageBase);
}
