using System.IO;
using Jianlan.Core.Settings;

namespace Jianlan.Core.Navigation;

/// <summary>
/// 把目录路径映射到列配置档位（FR-6.4 的 P1：按目录类型记忆）。
///
/// 判定依据是系统给的已知文件夹，不做内容探测：靠扩展名分布猜"这是图片目录"
/// 会在目录内容变化时让配置莫名其妙地换一套，而用户只会觉得"我的列宽丢了"。
///
/// 用**最长前缀匹配**：用户把下载目录放进文档目录（或反过来）时，
/// 只有更具体的那条才对。这个场景不常见，但写错一次就要排查很久。
/// </summary>
public static class ColumnProfiles
{
    public static ColumnProfileKind Resolve(string? path, IReadOnlyList<KnownFolder>? known)
    {
        if (known is null || known.Count == 0) return ColumnProfileKind.Default;

        string? normalized = Normalize(path);
        if (normalized is null) return ColumnProfileKind.Default;

        ColumnProfileKind best = ColumnProfileKind.Default;
        int bestLength = -1;

        foreach (KnownFolder folder in known)
        {
            ColumnProfileKind kind = Map(folder.Kind);
            if (kind == ColumnProfileKind.Default) continue;

            string? root = Normalize(folder.Path);
            if (root is null) continue;

            if (!IsWithin(normalized, root)) continue;

            if (root.Length > bestLength)
            {
                best = kind;
                bestLength = root.Length;
            }
        }

        return best;
    }

    /// <summary>已知文件夹枚举 → 档位。用户主目录不是一个档位：它涵盖下面所有目录，
    /// 归到它只会让"在文档里调过的列宽"跑到桌面上。</summary>
    public static ColumnProfileKind Map(KnownFolderKind kind) => kind switch
    {
        KnownFolderKind.Desktop => ColumnProfileKind.Desktop,
        KnownFolderKind.Documents => ColumnProfileKind.Documents,
        KnownFolderKind.Downloads => ColumnProfileKind.Downloads,
        KnownFolderKind.Pictures => ColumnProfileKind.Pictures,
        KnownFolderKind.Music => ColumnProfileKind.Music,
        KnownFolderKind.Videos => ColumnProfileKind.Videos,
        _ => ColumnProfileKind.Default,
    };

    /// <summary>等号之外还要比"前缀 + 分隔符"：D:\Data 不该匹配 D:\Database。</summary>
    private static bool IsWithin(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;

        return path.Length > root.Length
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string trimmed = path.Trim();
        try
        {
            trimmed = Path.TrimEndingDirectorySeparator(trimmed);
        }
        catch (ArgumentException)
        {
            // 非法路径（含控制字符等）当成"没有类型"处理，不在这里抛
            return null;
        }

        // 根目录（C:\）去掉尾分隔符后是 "C:"，'C:' + '\' 仍能正确匹配它的子项
        return trimmed.Length == 0 ? null : trimmed;
    }
}
