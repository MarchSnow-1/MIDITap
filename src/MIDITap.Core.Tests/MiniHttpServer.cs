// MiniHttpServer.cs — 测试用的极简 HTTP 服务器
//
// 为什么不用 HttpListener：它对非管理员进程需要 URL ACL 预留，CI 上常常起不来
// 而 TcpListener 只需一个空闲端口，行为完全可预期
//
// 用途：**确定性地**验证"绕过 API 限流"的那条路径
// 它覆盖真正的 302 处理与 HTML 解析，而不是只测纯函数
// 外网（github.com）在部分网络下不可达，本地服务器不受此影响
//
// A minimal HTTP server for tests
//
// Why not HttpListener: it needs a URL ACL reservation for non-admin processes
// It often fails to start on CI
// TcpListener needs only a free port and behaves predictably
//
// Purpose: verify the API-rate-limit-bypass path DETERMINISTICALLY
// That means the real 302 handling and HTML parsing, not just the pure functions
// External access to github.com is unreliable on some networks
// A local server does not have that problem

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MIDITap.Core.Tests;

/// <summary>
/// 按路径脚本化响应的最小 HTTP 服务器
///
/// A minimal HTTP server whose responses are scripted by path
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    /// <summary>
    /// 一次响应：(状态码, Location 头, 响应体)
    ///
    /// One response: (status code, Location header, body)
    /// </summary>
    public delegate (int Status, string? Location, string Body) Handler(string path);

    /// <summary>
    /// 一次**二进制**响应：(状态码, Location 头, 字节内容)
    /// 更新包是 zip，必须按字节返回
    /// 用字符串会破坏二进制内容
    /// 被破坏的包恰好是校验步骤要拒绝的东西，测试就失去意义了
    ///
    /// A BINARY response. Update packages are zips and must be served as bytes
    /// Returning them as a string would corrupt the content
    /// A corrupted package is exactly what verification is meant to reject
    /// So the test would prove nothing
    /// </summary>
    public delegate (int Status, string? Location, byte[] Body) BinaryHandler(string path);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public MiniHttpServer(Handler handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{Port}";
        _loop = Task.Run(() => AcceptLoop(
            path =>
            {
                var (status, location, body) = handler(path);
                return (status, location, System.Text.Encoding.UTF8.GetBytes(body ?? string.Empty));
            },
            _cts.Token));
    }

    /// <summary>
    /// 用二进制响应的构造函数（服务 zip 等）
    ///
    /// Constructor taking a binary handler (serves zips etc.)
    /// </summary>
    public MiniHttpServer(BinaryHandler handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{Port}";
        _loop = Task.Run(() => AcceptLoop(path => handler(path), _cts.Token));
    }

    public int Port { get; }

    public string BaseUrl { get; }

    /// <summary>
    /// 收到的请求路径（按顺序），用于断言"确实请求了这一步"
    ///
    /// Request paths received, in order, used to assert that a step really was requested
    /// </summary>
    public List<string> Requests { get; } = [];

    /// <summary>
    /// 附加到每个响应的头
    /// 用于复现真实服务的行为
    /// 例如 GitHub 在 403 时同时返回 x-ratelimit-reset
    /// 本程序据此告诉用户何时可重试
    ///
    /// Headers added to every response
    /// Used to reproduce real-service behaviour
    /// For example GitHub sends x-ratelimit-reset alongside a 403
    /// This app turns that into "retry after HH:mm"
    /// </summary>
    public Dictionary<string, string> ExtraHeaders { get; } = [];

    /// <summary>
    /// 覆盖 Content-Length（默认按实际内容算）
    /// 设为比实际内容大即可模拟**传输截断**
    /// 这是弱网下最常见的失败
    /// "下载成功但内容是残的"正是校验必须拦住的场景
    ///
    /// Overrides Content-Length. Setting it larger than the actual body simulates a TRUNCATED transfer
    /// That is the most common failure on a weak link
    /// "Downloaded fine but content is partial" is exactly what verification must catch
    /// </summary>
    public int? ContentLengthOverride { get; set; }

    private async Task AcceptLoop(
        Func<string, (int Status, string? Location, byte[] Body)> handler, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[8192];
                        var read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            return;
                        }

                        // 只取请求行：形如 "GET /path HTTP/1.1"
                        //
                        // Only the request line is taken: it looks like "GET /path HTTP/1.1"
                        var text = Encoding.ASCII.GetString(buffer, 0, read);
                        var firstLine = text.Split("\r\n")[0];
                        var parts = firstLine.Split(' ');
                        var path = parts.Length >= 2 ? parts[1] : "/";
                        lock (Requests)
                        {
                            Requests.Add(path);
                        }

                        var (status, location, bodyBytes) = handler(path);
                        bodyBytes ??= [];
                        var reason = status switch
                        {
                            200 => "OK",
                            302 => "Found",
                            403 => "Forbidden",
                            404 => "Not Found",
                            _ => "Status",
                        };

                        var head = new StringBuilder();
                        head.Append($"HTTP/1.1 {status} {reason}\r\n");
                        if (location is not null)
                        {
                            head.Append($"Location: {location}\r\n");
                        }
                        foreach (var (key, value) in ExtraHeaders)
                        {
                            head.Append($"{key}: {value}\r\n");
                        }
                        head.Append("Content-Type: text/html; charset=utf-8\r\n");
                        head.Append($"Content-Length: {ContentLengthOverride ?? bodyBytes.Length}\r\n");
                        head.Append("Connection: close\r\n\r\n");

                        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
                        stream.Write(headBytes, 0, headBytes.Length);
                        if (bodyBytes.Length > 0)
                        {
                            stream.Write(bodyBytes, 0, bodyBytes.Length);
                        }
                        stream.Flush();
                    }
                }
                catch
                {
                    // 单个连接出错不影响服务器继续服务
                    //
                    // A single connection failing does not stop the server from serving
                }
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // 已停止 / Already stopped
        }
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 忽略取消引起的异常 / Ignore the exception caused by cancellation
        }
        _cts.Dispose();
    }
}
