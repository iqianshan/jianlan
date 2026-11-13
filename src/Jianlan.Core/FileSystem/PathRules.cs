using System.IO;

namespace Jianlan.Core.FileSystem;

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

    /// <summary>
    /// 是否网络路径（<c>\\server\share</c>）。只看前缀，不验证可达性——
    /// 验证要发网络请求，那正是这类路径危险的地方。
    ///
    /// 用途是决定"要不要自动预览"（FR-9.2.8）：网络上的一次读盘可能等几秒，
    /// 不能挂在选中动作上。
    /// </summary>
    public static bool IsUncPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2) return false;

        return path[0] == '\\' && path[1] == '\\';
    }

    /// <summary>
    /// 把外部给的路径整理成唯一写法：分隔符统一、去掉重复分隔符与 <c>.</c> / <c>..</c> 段、
    /// 去掉结尾分隔符（盘根与 UNC 根除外），并去掉首尾空白与包裹的引号。
    ///
    /// 为什么必须有这一步：地址栏允许手输，<c>D:/work</c>、<c>D:\\work\\</c>、
    /// <c>D:\work\.\sub</c> 都能打开，但会以原样写进当前路径，于是面包屑、后退栈、
    /// 最近访问、标签、复制出来的路径各留一份写法不同的"同一个目录"。
    /// 路径比较虽然忽略大小写，但不会忽略 <c>//</c> 与结尾斜杠——去重与"是不是同一个目录"
    /// 的判定会在这些写法上出错。
    ///
    /// 只做字符串运算，不访问磁盘：<see cref="Path.GetFullPath(string)"/> 自 .NET Core
    /// 起是纯计算，网络路径也不会因为它而阻塞。非法字符（<c>?</c> <c>|</c> 等）与
    /// 孤立 UNC 前缀（<c>\\server</c>）会抛异常，此时退回"只统一分隔符"的结果——
    /// 让真正打开目录那一步去报错，而不是在这里把用户输入改成别的东西。
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        // 从资源管理器"复制路径"粘过来常带引号，地址栏里出现过就一并去掉
        string value = path.Trim().Trim('"');
        if (value.Length == 0) return string.Empty;

        // 先统一分隔符：GetFullPath 抛异常时回落的结果也要是规范形态
        value = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // 裸盘符 "D:" 是"该盘当前目录"的相对写法，用户想的是盘根
        if (value.Length == 2 && value[1] == ':' && char.IsAsciiLetter(value[0]))
            return value + Path.DirectorySeparatorChar;

        try
        {
            return TrimSeparators(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException
                                    or NotSupportedException
                                    or PathTooLongException
                                    or System.Security.SecurityException)
        {
            return TrimSeparators(value);
        }
    }

    /// <summary>去掉结尾的分隔符，但保留根路径本身的 <c>C:\</c> 形态。</summary>
    public static string TrimSeparators(string value)    {
        string trimmed = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // "C:\" 去掉分隔符会变成 "C:"，那是"当前目录"的相对写法，必须补回来
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }
}
