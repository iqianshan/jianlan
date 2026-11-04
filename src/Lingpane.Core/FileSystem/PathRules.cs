using System.IO;

namespace Lingpane.Core.FileSystem;

/// <summary>
/// 路径关系判定。
///
/// 拖放与粘贴前必须先回答"目标是不是源自身或源的子目录"——把目录放进自己内部
/// 会造成无限递归，Windows 外壳在这种操作上会直接报错或把磁盘写满。
/// 这里只做字符串层面的判断，不访问磁盘，可在 UI 线程逐项调用。
/// </summary>
public static class PathRules
{
    /// <summary>
    /// <paramref name="candidate"/> 与 <paramref name="path"/> 相同，或是它的上级目录。
    ///
    /// 例：IsSameOrAncestor(@"C:\a", @"C:\a\b") → true；
    ///     IsSameOrAncestor(@"C:\a", @"C:\ab") → false（不能按前缀误判）。
    /// </summary>
    public static bool IsSameOrAncestor(string candidate, string path)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(path);

        string source = TrimSeparators(candidate);
        string normalized = TrimSeparators(path);

        if (source.Length == 0 || normalized.Length == 0) return false;

        if (string.Equals(source, normalized, StringComparison.OrdinalIgnoreCase)) return true;

        // 必须带上分隔符再比前缀，否则 C:\a 会把 C:\ab 当成子目录
        string prefix = source.EndsWith(Path.DirectorySeparatorChar)
            ? source
            : source + Path.DirectorySeparatorChar;

        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 是否本地盘路径（<c>C:\...</c>）。网络路径（<c>\\server\share</c>）与相对路径返回 false。
    ///
    /// 用于决定"能不能按真实路径去问 Shell 要图标"：网络路径在对方离线时
    /// 一次 <c>SHGetFileInfo</c> 就能阻塞几十秒，这类路径一律不解析。
    /// </summary>
    public static bool IsLocalDrivePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Length < 3 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return false;

        return path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar;
    }

    /// <summary>去掉结尾的分隔符，但保留根路径本身的 <c>C:\</c> 形态。</summary>
    private static string TrimSeparators(string value)    {
        string trimmed = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // "C:\" 去掉分隔符会变成 "C:"，那是"当前目录"的相对写法，必须补回来
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }
}
