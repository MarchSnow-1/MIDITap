// UpdateStaging.cs — 更新包解压时的路径规则
// **最关键的一条：解压不得写入用户的 config/ 与 .storage/（IsExcluded 对这两棵子树返回 true，由 UpdateExtractor 逐步校验）**
// 用户在那两棵子树里的东西必须原样留下：映射配置是他自己写的，应用设置是他自己的选择
// 因此解压到暂存区时就把它们排除，更新只替换程序文件
// 这条规则不依赖包里带不带 config/ —— 发布包已经不带（由应用首次启动生成），
// 但旧版本用户手上的包、以及别人自己打的包都可能带，因此这里依旧必须排除
//
// Path rules for unpacking an update archive
// The critical rule: unpacking must not write into the user's config/ or .storage/
// IsExcluded returns true for those two subtrees, and UpdateExtractor checks each entry against it
// Whatever the user put in those two subtrees has to stay: the mappings are his own work and the settings his own choices
// Both subtrees are therefore excluded while staging, so only program files are replaced
// The rule does not depend on whether a package carries config/ — the release package no longer does (the app creates it on first launch)
// Yet packages already in users' hands, and packages built by others, may carry one, so the exclusion stays

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
