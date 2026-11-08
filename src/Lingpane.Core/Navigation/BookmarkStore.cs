using Lingpane.Core.Settings;

namespace Lingpane.Core.Navigation;

/// <summary>
/// 收藏的读写（FR-7）。数据落在 settings.json 的 <c>bookmarks</c> 段，上限
/// <see cref="BookmarkSettings.MaxItems"/> 条。
///
/// 主页的收藏区（FR-1.1.5）与导航栏的收藏区（FR-1.2.5）读的是同一个实例，
/// 所以两处永远一致——不需要任何同步逻辑，也不存在"主页有、导航栏没有"的中间态。
///
/// 本类不判路径是否存在：收藏一个移动硬盘上的目录，拔盘后它仍然该在列表里（灰掉），
/// 而不是被悄悄删掉。存在性检查归展示方（FR-7.7）。
/// </summary>
public sealed class BookmarkStore
{
    private readonly BookmarkSettings _store;

    public BookmarkStore(BookmarkSettings store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _store.Normalize();
    }

    public IReadOnlyList<BookmarkEntry> Items => _store.Items;

    public int Count => _store.Items.Count;

    public bool IsFull => _store.Items.Count >= BookmarkSettings.MaxItems;

    /// <summary>主页收藏区（最多 8 项，超出由界面显示"查看全部"）。</summary>
    public IReadOnlyList<BookmarkEntry> HomePageItems
        => _store.Items.Take(BookmarkSettings.HomePageLimit).ToList();

    public bool Contains(string path)
        => _store.Items.Any(b => Same(b.Path, path));

    /// <summary>
    /// 新增一条收藏。已存在（同路径）时返回 <c>false</c> 且不改动顺序——
    /// 重复收藏同一个目录时用户想要的是"它已经在里面了"，不是"跳到最前"。
    /// </summary>
    public bool Add(string path, string? name = null, string? group = null, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized = path.Trim();
        if (Contains(normalized) || IsFull) return false;

        _store.Items.Add(new BookmarkEntry
        {
            Path = normalized,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Kind = Directory.Exists(normalized) ? BookmarkKind.Folder : BookmarkKind.File,
            Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
            AddedAt = at ?? DateTimeOffset.UtcNow,
        });

        return true;
    }

    public bool Remove(string path)
    {
        int index = IndexOf(path);
        if (index < 0) return false;

        _store.Items.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// 在列表内上移/下移（FR-7.8 排序）。<paramref name="delta"/> 为负是上移。
    /// 越界时保持原样，不夹到两端——夹取会让"到底了还继续按"变成"停在最后一个"，
    /// 看起来像按了没反应，而实际它确实没法再动。
    /// </summary>
    public void Move(string path, int delta)
    {
        int from = IndexOf(path);
        if (from < 0 || delta == 0) return;

        int to = from + delta;
        if (to < 0 || to >= _store.Items.Count) return;

        BookmarkEntry item = _store.Items[from];
        _store.Items.RemoveAt(from);
        _store.Items.Insert(to, item);
    }

    public void Clear() => _store.Items.Clear();

    /// <summary>显示名：用户起的名字优先，否则取路径末段（盘根时取整个路径）。</summary>
    public static string DisplayName(BookmarkEntry item)
    {
        if (!string.IsNullOrWhiteSpace(item.Name)) return item.Name;

        string trimmed = item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? item.Path : name;
    }

    private int IndexOf(string path)
        => string.IsNullOrWhiteSpace(path) ? -1 : _store.Items.FindIndex(b => Same(b.Path, path));

    private static bool Same(string left, string right)
        => string.Equals(left, right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
