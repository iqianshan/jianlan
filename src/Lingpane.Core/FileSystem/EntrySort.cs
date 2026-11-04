using System.Globalization;

namespace Lingpane.Core.FileSystem;

/// <summary>
/// 自然排序比较器：把连续数字当作数值比较，使 file2 排在 file10 之前。
/// 资源管理器默认就是这个行为，用户对"字典序把 10 排在 2 前面"非常敏感。
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

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

            char cx = char.ToUpperInvariant(x[i]);
            char cy = char.ToUpperInvariant(y[j]);
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
/// 排序规则。与设置项 <c>behavior.sortBy</c> / <c>sortDescending</c> / <c>foldersFirst</c> 一一对应，
/// 因此直接复用 <see cref="Settings.SortFieldKind"/>，不再定义第二套枚举。
/// </summary>
public readonly record struct EntrySort(Settings.SortFieldKind Field, bool Descending, bool FoldersFirst)
{
    public static EntrySort Default { get; } = new(Settings.SortFieldKind.Name, Descending: false, FoldersFirst: true);

    /// <summary>点击同一列切换升降序，点击其他列改为该列升序（与资源管理器一致）。</summary>
    public EntrySort Toggle(Settings.SortFieldKind field)
        => field == Field
            ? this with { Descending = !Descending }
            : this with { Field = field, Descending = false };

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

        return (a, b) =>
        {
            if (foldersFirst && a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            int primary = ComparePrimary(a, b, field);
            if (primary != 0) return descending ? -primary : primary;

            int byName = NaturalStringComparer.Instance.Compare(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.Name, b.Name);
        };
    }

    private static int ComparePrimary(FileEntry a, FileEntry b, Settings.SortFieldKind field) => field switch
    {
        Settings.SortFieldKind.Date => a.LastWriteTime.CompareTo(b.LastWriteTime),
        Settings.SortFieldKind.Size => a.Size.CompareTo(b.Size),
        Settings.SortFieldKind.Type => string.Compare(
            TypeKey(a), TypeKey(b), StringComparison.OrdinalIgnoreCase),
        _ => NaturalStringComparer.Instance.Compare(a.Name, b.Name),
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
        Settings.SortFieldKind.Date => "修改日期",
        Settings.SortFieldKind.Size => "大小",
        Settings.SortFieldKind.Type => "类型",
        _ => "名称",
    };

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{DisplayName} {(Descending ? "降序" : "升序")}");
}
