// RateLimitInfo.cs — GitHub 匿名 API 的配额信息，只在 403 时可得
//
// 为什么单独一个类型：三个值同生共死
// 它们一起从响应头解析出来，也一起送到界面
// 拆成三个参数传递容易漏掉其中一个，而界面文案要么三个齐全，要么退回通用说明
// 三者都可为 null：x-ratelimit-* 只在 403 时出现，网络不通或 5xx 都没有这些头
//
// RateLimitInfo.cs — the anonymous GitHub API quota, available on a 403 alone
//
// Why a type of its own: the three values live and die together
// They are parsed from the response headers together and travel to the UI together
// Passing them as three separate parameters invites dropping one, and the message needs all of them or generic wording
// Any of the three may be null: x-ratelimit-* appears only on a 403, so a dead network or a 5xx carries none of it

namespace MIDITap.Core.Update;

/// <summary>
/// 配额信息：剩余次数、每小时上限、重置时间
/// 界面用前两项显示「0/60」，用重置时间告诉用户什么时候能再试
///
/// The quota: remaining requests, the hourly limit and the reset time
/// The UI shows the first two as "0/60" and uses the reset time to say when to try again
/// </summary>
public sealed record RateLimitInfo(int? Remaining = null, int? Limit = null, DateTimeOffset? Reset = null);
