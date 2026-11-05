namespace Lingpane.Core.Navigation;

/// <summary>面包屑的一段。FullPath 是该段对应的可导航路径。</summary>
public readonly record struct BreadcrumbSegment(string Label, string FullPath);

/// <summary>
/// 把路径拆成面包屑（FR-6.1）。资源管理器的面包屑不是简单按 '\' 切分：
/// 盘符段显示为 "C:"，UNC 的 "\\server\share" 必须作为一段整体，否则点第一段会落到无权限的服务器根。
/// </summary>
public static class Breadcrumb
{
    public static IReadOnlyList<BreadcrumbSegment> Build(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];

        // 统一分隔符，避免 "C:/a/b" 与 "C:\a\b" 得到不同的段数
        string normalized = path.Replace('/', '\\');
        string trimmed = normalized.Length > 3
            ? normalized.TrimEnd('\\')
            : normalized;

        var segments = new List<BreadcrumbSegment>(6);

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC：\\server\share 作为第一段
            string[] parts = trimmed[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return [];

            string root = @"\\" + parts[0] + @"\" + parts[1];
            // 显示保留 "\\" 前缀：丢掉之后 "192.0.2.1\share" 与盘符路径长得一样，
            // 用户无从判断这是网络位置（标题栏是带前缀的，两处不一致更显突兀）。
            segments.Add(new BreadcrumbSegment(@"\\" + parts[0] + @"\" + parts[1], root));

            string accumulated = root;
            for (int i = 2; i < parts.Length; i++)
            {
                accumulated += @"\" + parts[i];
                segments.Add(new BreadcrumbSegment(parts[i], accumulated));
            }

            return segments;
        }

        if (trimmed.Length >= 2 && trimmed[1] == ':')
        {
            string root = char.ToUpperInvariant(trimmed[0]) + @":\";
            segments.Add(new BreadcrumbSegment(root[..2], root));

            string[] parts = trimmed[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            string accumulated = root;
            foreach (string part in parts)
            {
                accumulated += part;
                segments.Add(new BreadcrumbSegment(part, accumulated));
                accumulated += @"\";
            }

            if (accumulated.EndsWith('\\') && accumulated.Length > root.Length)
            {
                accumulated = accumulated.TrimEnd('\\');
            }

            return segments;
        }

        // 非盘符路径（极少见，例如相对路径）：原样作为一段，避免给出无法点击的错误层级
        return [new BreadcrumbSegment(trimmed, trimmed)];
    }
}
