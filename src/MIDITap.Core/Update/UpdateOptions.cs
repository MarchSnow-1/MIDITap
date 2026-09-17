// UpdateOptions.cs — 更新检查与下载的网络选项（目前只有代理）
// 为什么要可配置代理：更新检查直连 api.github.com
// 在代理环境或 GitHub 被墙的网络下会静默失败
// 本模块的契约是"失败即无更新"，用户只会看到"已是最新"，看不出真实原因
// 留空表示跟随系统代理 —— 那是大多数人的正确默认值
//
// Network options for update check and download (currently just the proxy)
// Why a configurable proxy: the check hits api.github.com directly
// It fails silently in proxied or GitHub-blocked networks
// This module's contract is "failure means no update", so the user just sees "up to date" and cannot tell why
// Empty means follow the system proxy, which is the right default for most people

using System.Net;

namespace MIDITap.Core.Update;

/// <summary>更新相关的网络设置 / Network settings for updates</summary>
public sealed record UpdateOptions(string? ProxyUrl = null)
{
    /// <summary>
    /// 允许的代理 scheme（见 BuildProxy 的说明）
    /// socks5 是**刻意支持**的：本机代理常见的就 http 与 socks5 两种
    /// 只认前者会让一大类用户填了地址却被判为无效
    ///
    /// The allowed proxy schemes (see BuildProxy)
    /// socks5 is supported **deliberately**: http and socks5 are the two common kinds of local proxy
    /// Accepting only the former would flag a perfectly good address as invalid
    /// </summary>
    private static readonly string[] AllowedSchemes = ["http", "https", "socks4", "socks4a", "socks5"];
    /// <summary>默认设置：跟随系统代理 / The default: follow the system proxy</summary>
    public static readonly UpdateOptions Default = new();

    /// <summary>
    /// 构造用于 HttpClient 的代理
    /// 返回 null 表示"使用系统默认代理"（HttpClient 的默认行为）
    /// 返回非 null 时使用显式代理
    /// 地址非法时同样返回 null（退回系统代理），而不是抛异常
    /// 更新检查失败不该因为一个写错的代理地址让整个功能不可用
    ///
    /// Builds the proxy for HttpClient
    /// Null means "use the system default proxy" (HttpClient's own default); non-null means an explicit proxy
    /// An invalid address also yields null rather than throwing
    /// A typo in a proxy string must not take the whole update path down
    /// </summary>
    public IWebProxy? BuildProxy()
    {
        if (string.IsNullOrWhiteSpace(ProxyUrl))
        {
            return null;
        }
        if (!Uri.TryCreate(ProxyUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }
        // 只放行 HttpClientHandler 真正支持的 scheme
        // 给 WebProxy 设一个 ftp:// 地址时，运行时会直接报
        // "Only the 'http', 'https', 'socks4', 'socks4a' and 'socks5' schemes are allowed for proxies."
        // 其余 scheme（file: 等）不是代理，放行没有意义
        //
        // Only the schemes HttpClientHandler really supports are allowed
        // Setting an ftp:// address on WebProxy makes the runtime report
        // "Only the 'http', 'https', 'socks4', 'socks4a' and 'socks5' schemes are allowed for proxies."
        // Other schemes (file: and so on) are not proxies, so allowing them would be meaningless
        if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }
        return new WebProxy(uri);
    }

    /// <summary>
    /// 代理设置是否可用（留空，或可解析为受支持的代理地址 —— http/https/socks4/socks4a/socks5）
    ///
    /// Whether the proxy setting is usable
    /// It is usable when empty, or when it parses as a supported proxy address (http/https/socks4/socks4a/socks5)
    /// </summary>
    public bool HasUsableProxy => BuildProxy() is not null;
}
