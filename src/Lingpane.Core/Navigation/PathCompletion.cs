using System.IO;

using Lingpane.Core.FileSystem;

namespace Lingpane.Core.Navigation;

/// <summary>候选类型。<see cref="PathCandidateKind.File"/> 的候选只在"当前目录已枚举"时才可能出现。</summary>
public enum PathCandidateKind
{
    Directory,
    File,
}

/// <summary>地址栏补全的一条候选。</summary>
public readonly record struct PathCandidate(string FullPath, string Name, PathCandidateKind Kind)
{
    public bool IsDirectory => Kind == PathCandidateKind.Directory;
}

/// <summary>
/// 地址栏自动补全（FR-6.1）。纯字符串逻辑 + 一个可注入的目录探测函数，
/// 不碰 UI、不持有状态，因此可以在单测里把"网络盘超时""无权限"这些分支逐个跑到。
///
/// 设计取舍：
///   · **候选以当前目录已枚举的条目为主**。用户九成是在当前目录里跳，这份缓存零 IO、命中率最高。
///   · **目录枚举只作为补充**，由调用方注入（<c>probe</c>），并且有上限。
///     Core 不直接调 <c>Directory.EnumerateDirectories</c> 是为了让测试能构造
///     "枚举抛异常"与"枚举很慢"两种现实情况——网络盘上这两种是常态，不是异常。
///   · **词干为空时直接返回空**。地址栏刚敲完一个 <c>\</c> 就弹出整个目录的内容，
///     等于把列表重复显示一遍，反而挡住视线。
/// </summary>
public static class PathCompletion
{
    /// <summary>候选条数上限。再多就会盖住列表，且用户也不会在几十条里挑。</summary>
    public const int MaxCandidates = 8;

    /// <summary>一次探测最多取几条。限制的是"枚举成本"，不是"要显示几条"。</summary>
    public const int MaxProbeEntries = 200;

    /// <summary>
    /// 把地址栏文本拆成「要枚举的目录」+「词干」。
    ///
    /// 例（当前目录 <c>D:\work</c>）：
    /// <code>
    /// "D:\work\re"   → ("D:\work", "re")
    /// "D:\work\"     → ("D:\work\", "")
    /// "D:\"          → ("D:\", "")
    /// "D:"           → ("D:\", "")
    /// "\\srv\share"  → ("\\srv\share", "")
    /// "re"           → ("D:\work", "re")      // 裸词干按当前目录算
    /// ""             → ("D:\work", "")
    /// </code>
    /// </summary>
    public static (string Directory, string Stem) Split(string? text, string currentDirectory)
    {
        string current = string.IsNullOrEmpty(currentDirectory) ? string.Empty : currentDirectory;
        string value = (text ?? string.Empty).Trim();

        if (value.Length == 0) return (current, string.Empty);

        // 统一成反斜杠：用户从别处粘来的路径常带正斜杠，混着用会让 LastIndexOf 判断错
        value = value.Replace('/', '\\');

        // 以分隔符结尾 = 用户已经选定了目录，正在开始写下一层；此时没有词干
        if (value.EndsWith('\\')) return (value, string.Empty);

        // 根形态（C: / \\server\share）本身就是"目录"，不能按最后一个 \ 切：
        // 切了会得到 "C:" 与 "share"，前者是"当前目录"的相对写法，语义完全变了
        if (IsRootLike(value)) return (EnsureTrailingSeparator(value), string.Empty);

        int cut = value.LastIndexOf('\\');
        if (cut < 0) return (current, value);           // 裸词干

        string directory = value[..cut];
        string stem = value[(cut + 1)..];

        // "C:\work" 切出 "C:"：单独一个盘符不是合法目录写法，补回 "C:\"
        directory = EnsureTrailingSeparator(directory);

        return (directory.Length == 0 ? "\\" : directory, stem);
    }

    /// <summary>
    /// 计算候选。排序规则：前缀命中优先 → 目录优先 → 自然序。去重按完整路径、大小写不敏感。
    /// </summary>
    /// <param name="text">地址栏当前文本。</param>
    /// <param name="currentDirectory">当前窗格所在目录，裸词干时用作枚举起点。</param>
    /// <param name="cachedEntries">当前目录已枚举的条目（可为 null：目录还没读完时）。</param>
    /// <param name="probe">
    /// 目录探测：<c>(目录, 通配符) → 匹配的目录完整路径</c>。允许抛异常——调用方不负责吞，
    /// 这里吞：网络盘断连时抛异常是正常现象，不该让补全把地址栏卡住或弹错误。
    /// </param>
    /// <param name="max">返回条数上限。</param>
    public static IReadOnlyList<PathCandidate> Complete(
        string? text,
        string currentDirectory,
        IReadOnlyList<FileEntry>? cachedEntries = null,
        Func<string, string, IReadOnlyList<string>>? probe = null,
        int max = MaxCandidates)
    {
        var (directory, stem) = Split(text, currentDirectory);

        // 词干为空：不补全。理由见类注释最后一条
        if (string.IsNullOrEmpty(stem)) return [];

        var found = new List<PathCandidate>(16);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 当前目录的已枚举条目：零 IO，且目录和文件都能给
        bool usesCache = cachedEntries is { Count: > 0 } && IsSameDirectory(directory, currentDirectory);
        if (usesCache)
        {
            foreach (FileEntry entry in cachedEntries!)
            {
                if (!entry.Name.Contains(stem, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(PathRules.TrimSeparators(entry.FullPath))) continue;

                found.Add(new PathCandidate(
                    entry.FullPath,
                    entry.Name,
                    entry.IsDirectory ? PathCandidateKind.Directory : PathCandidateKind.File));
            }
        }

        // 2. 目录探测：补齐"当前目录还没枚举到"和"用户输入了别的目录"两种情况
        if (probe is not null && directory.Length > 0)
        {
            IReadOnlyList<string>? probed = null;
            try
            {
                probed = probe(directory, stem + "*");
            }
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or ArgumentException
                                        or NotSupportedException
                                        or System.Security.SecurityException
                                        or InvalidOperationException)
            {
                // 断网、无权限、非法字符都在这里安静降级：补全失败不该比"没有补全"更吵
            }

            if (probed is not null)
            {
                foreach (string path in probed)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!seen.Add(PathRules.TrimSeparators(path))) continue;

                    string name = Path.GetFileName(PathRules.TrimSeparators(path));
                    if (name.Length == 0) continue;

                    found.Add(new PathCandidate(path, name, PathCandidateKind.Directory));
                }
            }
        }

        if (found.Count == 0) return [];

        return found
            .Select(c => (Candidate: c, Prefix: c.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Prefix)
            .ThenByDescending(x => x.Candidate.IsDirectory)
            .ThenBy(x => x.Candidate.Name, NaturalStringComparer.Instance)
            .Take(max <= 0 ? MaxCandidates : max)
            .Select(x => x.Candidate)
            .ToList();
    }

    /// <summary>
    /// 把候选补全成"填回地址栏的文本"。目录补尾分隔符——用户接着就要写下一层，
    /// 少了这个 <c>\</c>，下一次输入又得自己补，或者补全把 <c>D:\work</c> 当成文件继续匹配。
    /// </summary>
    public static string ToInputText(PathCandidate candidate)
    {
        if (!candidate.IsDirectory) return candidate.FullPath;

        return candidate.FullPath.EndsWith('\\')
            ? candidate.FullPath
            : candidate.FullPath + "\\";
    }

    private static bool IsSameDirectory(string a, string b)
        => string.Equals(PathRules.TrimSeparators(a), PathRules.TrimSeparators(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsRootLike(string value)
    {
        if (value.Length == 2 && value[1] == ':') return true;

        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] parts = value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            // \\server\share 及其以上才算根；再往下的 \\server\share\a 是普通目录
            return parts.Length <= 2;
        }

        return false;
    }

    private static string EnsureTrailingSeparator(string directory)
    {
        if (directory.Length == 0) return directory;

        // "C:" → "C:\"，但 "\\server\share" 保持原样（UNC 根不带尾分隔符是合法写法）
        if (directory.Length == 2 && directory[1] == ':') return directory + "\\";

        return directory;
    }
}
