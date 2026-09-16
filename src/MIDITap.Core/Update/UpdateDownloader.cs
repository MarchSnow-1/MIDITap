// UpdateDownloader.cs — 下载更新包，带进度与取消
// 两点与更新检查不同、必须单独处理
//   1) **超时长得多**。检查是 8 秒的小请求，而更新包有 200 MB 以上
//      用同一个 8 秒超时必然失败。这里给下载单独设一个宽裕的超时，并靠 CancellationToken 让用户能取消
//   2) **要用流式读取**（ResponseHeadersRead）
//      默认的缓冲模式会在开始写盘前把整个响应读进内存，200 MB 的包会直接吃掉几百 MB 内存
//
// Downloads an update archive with progress reporting and cancellation
// Two things differ from the update check and need separate handling
//   1) A much longer timeout. The check is a small 8-second request, but the update archive exceeds 200 MB
//      The same timeout would always fail. Downloads get a generous timeout
//      And rely on a CancellationToken for user cancellation
//   2) Streaming (ResponseHeadersRead)
//      The default buffered mode reads the entire response into memory before writing anything
//      That costs hundreds of MB for a 200 MB archive

using System.Net.Http;

namespace MIDITap.Core.Update;

/// <summary>
/// 下载进度：已接收字节与总字节（总长未知时为 0）
///
/// Download progress: bytes received and total bytes (0 when the total length is unknown)
/// </summary>
public sealed record DownloadProgress(long Received, long Total)
{
    /// <summary>
    /// 完成比例 0..1（总长未知时为 0）
    ///
    /// Completion ratio 0..1 (0 when the total length is unknown)
    /// </summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
}

public static class UpdateDownloader
{
    // 下载超时按"整包"给足：200+ MB 在慢速链路上可能要好几分钟
    // 取消交给 CancellationToken
    //
    // A generous whole-transfer timeout: 200+ MB can take minutes on a slow link
    // Cancellation is the user's job via the token
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 从 <paramref name="url"/> 下载到 <paramref name="destinationPath"/>
    /// 先写 .part 临时文件，成功后再改名
    /// 这样中断或失败不会留下一个"看起来完整"的坏包
    /// 而那正是自更新里最危险的状态（把损坏的文件装上去）
    ///
    /// Downloads to a .part file first and renames on success
    /// An interrupted or failed transfer therefore never leaves a plausible-looking but corrupt archive
    /// That is the most dangerous state for a self-updater, since it would install broken files
    /// </summary>
    public static async Task<bool> DownloadAsync(
        string url,
        string destinationPath,
        UpdateOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        var partPath = destinationPath + ".part";
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            DeleteQuietly(partPath);

            using var handler = new HttpClientHandler();
            var proxy = options.BuildProxy();
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = DownloadTimeout };

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("MIDITap-updater/1.0");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    "[miditap.updater]: Download failed with status " + (int)response.StatusCode);
                return false;
            }

            var total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(partPath))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }

            // 已经有一份旧文件时先删掉：File.Move 默认不覆盖
            //
            // An existing old file is deleted first: File.Move does not overwrite by default
            DeleteQuietly(destinationPath);
            File.Move(partPath, destinationPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 用户取消：静默清理，不当成错误上报
            //
            // User cancellation: cleaned up silently and not reported as an error
            DeleteQuietly(partPath);
            return false;
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.updater]: Download failed: " + err.Message);
            DeleteQuietly(partPath);
            return false;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不改变结果
            //
            // A failed cleanup does not change the result
        }
    }
}
