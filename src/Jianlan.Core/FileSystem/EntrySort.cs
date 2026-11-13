using System.Globalization;

using Jianlan.Core.Localization;

namespace Jianlan.Core.FileSystem;

/// <summary>
/// 名称比较（FR-5.2.3）。两个开关组合出四种口径，集中在这里而不是散在调用点：
///
/// <list type="bullet">
///   <item>自然 + 不区分大小写：资源管理器口径，默认。</item>
///   <item>自然 + 区分：数字按数值、字母按码位。</item>
///   <item>字典 + 不区分：<c>file10</c> 排在 <c>file2</c> 之前。</item>
///   <item>字典 + 区分：同上，且大小写不同不再相邻。</item>
/// </list>
///
/// 自然排序的原始实现保留为 <see cref="NaturalStringComparer"/> 一个实例：
/// 别处（收藏、历史）按资源管理器口径排就够了，不该被列表这一项的设置牵连。
/// </summary>
public static class NameComparer
{
    public static int Compare(string? x, string? y, bool natural, bool caseSensitive)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        if (string.Equals(x, y, StringComparison.Ordinal)) return 0;

        return natural
            ? CompareNatural(x, y, caseSensitive)
            : ComparePlain(x, y, caseSensitive);
    }

    /// <summary>逐字符比较（不做数字归并）。</summary>
    private static int ComparePlain(string x, string y, bool caseSensitive)
    {
        int cmp = caseSensitive
            ? string.CompareOrdinal(x, y)
            : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);

        // 不区分大小写时 "a.txt" 与 "A.txt" 会判等，再按码位定序保证结果稳定
        return cmp != 0 ? cmp : string.CompareOrdinal(x, y);
    }

    /// <summary>自然比较：连续数字当数值，<c>file2</c> 在 <c>file10</c> 之前。</summary>
    private static int CompareNatural(string x, string y, bool caseSensitive)
    {
        int i = 0, j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                // 跳过前导零后按"有效位数 → 数值 → 位数"比较，避免解析成大数溢出
                int startX = i, startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                ReadOnlySpan<char> numX = x.AsSpan(startX, i - startX).TrimStart('0');
                ReadOnlySpan<char> numY = y.AsSpan(startY, j - startY).TrimStart('0');

                if (numX.Length != numY.Length) return numX.Length - numY.Length;

                int cmp = numX.SequenceCompareTo(numY);
                if (cmp != 0) return cmp;

                continue;
            }

            char cx = caseSensitive ? x[i] : char.ToUpperInvariant(x[i]);
            char cy = caseSensitive ? y[j] : char.ToUpperInvariant(y[j]);
            if (cx != cy) return cx - cy;

            i++;
            j++;
        }

        // 前缀相同时短者在前；完全相等时用序号比较保证顺序稳定
        int remaining = (x.Length - i) - (y.Length - j);
        return remaining != 0 ? remaining : string.CompareOrdinal(x, y);
    }
}

/// <summary>
/// 自然排序比较器：把连续数字当作数值比较，使 file2 排在 file10 之前。
/// 资源管理器默认就是这个行为，用户对"字典序把 10 排在 2 前面"非常敏感。
///
/// 固定为"自然 + 不区分大小写"：列表里的口径由 <see cref="EntrySort"/> 按设置决定，
/// 这里只服务于"不需要跟着设置走"的调用点（收藏、历史等）。
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y) => NameComparer.Compare(x, y, natural: true, caseSensitive: false);
}

/// <summary>
/// 排序规则。与设置项 <c>behavior.sortBy</c> / <c>sortDescending</c> / <c>foldersFirst</c> /
/// <c>naturalSort</c> / <c>caseSensitiveSort</c> 一一对应，
/// 因此直接复用 <see cref="Settings.SortFieldKind"/>，不再定义第二套枚举。
/// </summary>
public readonly record struct EntrySort(
    Settings.SortFieldKind Field,
    bool Descending,
    bool FoldersFirst,
    bool Natural = true,
    bool CaseSensitive = false)
{
    public static EntrySort Default { get; } = new(Settings.SortFieldKind.Name, Descending: false, FoldersFirst: true);

    /// <summary>点击同一列切换升降序，点击其他列改为该列升序（与资源管理器一致）。</summary>
    public EntrySort Toggle(Settings.SortFieldKind field)
        => field == Field
            ? this with { Descending = !Descending }
            : this with { Field = field, Descending = false };

    /// <summary>
    /// 只换名称/类型的比较口径，不动排序列与方向（FR-5.2.3）。
    /// 设置页改完立即作用于所有窗格，而不是等下次启动。
    /// </summary>
    public EntrySort WithNameOptions(bool natural, bool caseSensitive)
        => this with { Natural = natural, CaseSensitive = caseSensitive };

    /// <summary>
    /// 目录优先不受升降序影响：资源管理器在"名称降序"时文件夹依然排在最前，
    /// 把文件夹一起倒序会让用户找不到目录。
    /// </summary>
    public Comparison<FileEntry> ToComparison()
    {
        // record struct 的 lambda 不能捕获 this，先把字段取到局部变量
        var field = Field;
        bool descending = Descending;
        bool foldersFirst = FoldersFirst;
        bool natural = Natural;
        bool caseSensitive = CaseSensitive;

        return (a, b) =>
        {
            if (foldersFirst && a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            int primary = ComparePrimary(a, b, field, natural, caseSensitive);
            if (primary != 0) return descending ? -primary : primary;

            int byName = NameComparer.Compare(a.Name, b.Name, natural, caseSensitive);
            return byName != 0 ? byName : string.CompareOrdinal(a.Name, b.Name);
        };
    }

    private static int ComparePrimary(
        FileEntry a, FileEntry b, Settings.SortFieldKind field, bool natural, bool caseSensitive)
        => field switch
        {
            Settings.SortFieldKind.Date => a.LastWriteTime.CompareTo(b.LastWriteTime),
            Settings.SortFieldKind.Size => a.Size.CompareTo(b.Size),

            // 类型比较的就是扩展名，口径与名称列保持一致——否则"类型相同"的判定
            // 会和"名称相邻"的判定互相矛盾（.TXT 与 .txt 算不算同类）
            Settings.SortFieldKind.Type => string.Compare(
                TypeKey(a), TypeKey(b), caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),
            _ => NameComparer.Compare(a.Name, b.Name, natural, caseSensitive),
        };

    private static string TypeKey(FileEntry e)
    {
        if (e.IsDirectory) return string.Empty;

        int dot = e.Name.LastIndexOf('.');
        return dot > 0 && dot < e.Name.Length - 1
            ? e.Name[dot..]
            : string.Empty;
    }

    public string DisplayName => Field switch
    {
        Settings.SortFieldKind.Date => Strings.Column_DateModified,
        Settings.SortFieldKind.Size => Strings.Column_Size,
        Settings.SortFieldKind.Type => Strings.Column_Type,
        _ => Strings.Column_Name,
    };

    /// <summary>调试输出用。降序/升序也走资源，避免英文界面下日志里混着中文。</summary>
    public override string ToString()
        => $"{DisplayName} {(Descending ? Strings.Sort_Descending : Strings.Sort_Ascending)}";
}
