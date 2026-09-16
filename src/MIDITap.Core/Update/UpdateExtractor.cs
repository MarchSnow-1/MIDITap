// UpdateExtractor.cs — 把更新包解压到暂存目录
// 安全要点：**防 zip-slip**
// 归档条目名可以包含 ".." 或绝对路径，若直接拼接并写入，恶意（或被篡改）的包能写到目标目录之外
// 对自更新而言这是最严重的风险，因为解压出来的内容随后会被执行
// 这里逐条校验解析后的完整路径确实位于目标目录之下
//
// Extracts an update archive into a staging directory
// Security: defend against zip-slip
// Archive entry names can contain ".." or absolute paths
// Writing them naively lets a malicious or tampered archive escape the destination
// That is the worst case for a self-updater, since the extracted content is then executed
// Every entry's resolved full path is verified to stay under the destination

using System.IO.Compression;

namespace MIDITap.Core.Update;

/// <summary>解压结果 / The extraction result</summary>
public sealed record ExtractResult(bool Ok, int FilesWritten, string? Error = null);

public static class UpdateExtractor
{
    /// <summary>
    /// 解压 <paramref name="archivePath"/> 到 <paramref name="destinationDirectory"/>
    /// 去掉归档顶层公共目录；按 <see cref="UpdateStaging.IsExcluded"/> 排除用户数据与调试符号
    ///
    /// Extracts into the destination, stripping the archive's common root folder
    /// User data and debug symbols are skipped per UpdateStaging.IsExcluded
    /// </summary>
    public static ExtractResult Extract(string archivePath, string destinationDirectory)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            // 先算出顶层公共目录再决定每个条目落到哪，因此需要先看完整列表
            //
            // The common top-level folder is worked out before deciding where each entry lands
            // That means the whole list has to be read first
            var names = archive.Entries.Select(e => e.FullName).ToList();
            var root = UpdateStaging.StripCommonRoot(names);

            var written = 0;
            var destinationFull = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destinationFull);

            foreach (var entry in archive.Entries)
            {
                // 目录条目：名字以 / 结尾，只为建目录，跳过即可（写文件时会自动建）
                //
                // Directory entries, whose names end in /, only create directories, so they can be skipped
                // Writing a file creates its directory anyway
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (root.Length > 0
                    && relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative[(root.Length + 1)..];
                }

                if (relative.Length == 0 || UpdateStaging.IsExcluded(relative))
                {
                    continue;
                }

                var targetPath = Path.GetFullPath(Path.Combine(destinationFull, relative));

                // zip-slip 防护：解析后的路径必须仍在目标目录内
                // 比较时补一个分隔符，避免 "C:\\staging2" 被误判为在 "C:\\staging" 之下
                //
                // Zip-slip guard: the resolved path must stay inside the destination
                // A trailing separator is appended so "C:\\staging2" is not treated as living under "C:\\staging"
                var destinationWithSeparator = destinationFull.EndsWith(Path.DirectorySeparatorChar)
                    ? destinationFull
                    : destinationFull + Path.DirectorySeparatorChar;
                if (!targetPath.StartsWith(destinationWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        "[miditap.updater]: Refusing archive entry outside the target: " + entry.FullName);
                    return new ExtractResult(false, written, "Archive entry escapes the target directory");
                }

                var targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }
                entry.ExtractToFile(targetPath, overwrite: true);
                written++;
            }

            return new ExtractResult(true, written);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine("[miditap.updater]: Extract failed: " + err.Message);
            return new ExtractResult(false, 0, err.Message);
        }
    }
}
