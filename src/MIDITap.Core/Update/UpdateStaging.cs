// UpdateStaging.cs — 更新包解压时的路径规则
// **最关键的一条：解压不得写入用户的 config/ 与 .storage/（IsExcluded 对这两棵子树返回 true，由 UpdateExtractor 逐步校验）**
// CI 打出的 zip 里带一份默认 config/mapping.json
// 若原样覆盖，用户自己的映射配置会被出厂默认值替换掉
// 那是数据丢失，不是"更新"
// 因此解压到暂存区时就把这两棵子树排除，更新只替换程序文件
//
// Path rules for unpacking an update archive
// The critical rule: unpacking must not write into the user's config/ or .storage/
// IsExcluded returns true for those two subtrees, and UpdateExtractor checks each entry against it
// The CI zip ships a default config/mapping.json
// Copying it over would replace the user's own mappings with factory defaults
// That is data loss, not an update
// Both subtrees are therefore excluded while staging, so only program files are replaced

using MIDITap.Core.Settings;

namespace MIDITap.Core.Update;

public static class UpdateStaging
{
    // 需要保留的顶层目录来自 AppPaths：那份清单只定义一次，这里不再重复目录名
    //
    // The preserved top-level directories come from AppPaths, where the list is defined once
    // The directory names are therefore not repeated here

    /// <summary>
    /// 判断归档内的相对路径是否应被排除
    /// 同时排除本地运行时文件：.pdb（调试符号，用户不需要）与旧名残留
    ///
    /// Decides whether a path inside the archive should be excluded
    /// Local runtime files are excluded too: .pdb debug symbols (useless to users)
    /// </summary>
    public static bool IsExcluded(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        // 统一分隔符后比较：zip 里可能是 / 或 \，而且可能带前导分隔符
        //
        // Compared after normalising separators: a zip may use / or \, and the value may carry a leading separator
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        foreach (var directory in AppPaths.UserDataDirectoryNames)
        {
            if (normalized.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, directory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return normalized.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 去掉归档顶层的公共目录前缀（CI 打包成 stage/MIDITap/…，解压后多一层 MIDITap/）
    /// 找不到公共前缀时返回原样
    ///
    /// Strips the archive's single common top-level folder
    /// CI zips stage/MIDITap/…, which unpacks with an extra MIDITap/ layer
    /// Returns the input unchanged when there is no single common prefix
    /// </summary>
    public static string StripCommonRoot(IReadOnlyList<string> relativePaths)
    {
        string? root = null;
        foreach (var path in relativePaths)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0)
            {
                continue;
            }
            var slash = normalized.IndexOf('/');
            if (slash <= 0)
            {
                // 顶层就有文件：没有公共目录前缀
                //
                // A file at the top level means there is no common directory prefix
                return string.Empty;
            }
            var first = normalized[..slash];
            if (root is null)
            {
                root = first;
            }
            else if (!string.Equals(root, first, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }
        return root ?? string.Empty;
    }
}
