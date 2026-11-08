using Lingpane.Core.Settings;

namespace Lingpane.Core.Navigation;

/// <summary>新增收藏的结果。分开返回是为了让界面能说清楚"为什么没加上"（FR-7.1 / FR-7.9）。</summary>
public enum BookmarkAddResult
{
    /// <summary>已加入。</summary>
    Added,

    /// <summary>同路径已存在。界面应提示并把那一行高亮（FR-7.1 验收）。</summary>
    Duplicate,

    /// <summary>已达 <see cref="BookmarkSettings.MaxItems"/>，拒绝新增（FR-7.9）。</summary>
    Full,

    /// <summary>路径为空。</summary>
    Invalid,
}

/// <summary>
/// 收藏的读写（FR-7）。数据落在 settings.json 的 <c>bookmarks</c> 段，上限
/// <see cref="BookmarkSettings.MaxItems"/> 条。
///
/// 主页的收藏区（FR-1.1.5）与导航栏的收藏区（FR-1.2.5）读的是同一个实例，
/// 所以两处永远一致——不需要任何同步逻辑，也不存在"主页有、导航栏没有"的中间态。
///
/// 本类不判路径是否存在：收藏一个移动硬盘上的目录，拔盘后它仍然该在列表里（灰掉），
/// 而不是被悄悄删掉。存在性检查归展示方（FR-7.7），清理靠显式的
/// <see cref="RemoveMany"/>——只删调用方点名的那些，不自己猜。
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

    public IReadOnlyList<BookmarkGroup> Groups => _store.Groups;

    public int Count => _store.Items.Count;

    public bool IsFull => _store.Items.Count >= BookmarkSettings.MaxItems;

    /// <summary>主页收藏区（最多 8 项，超出由界面显示"查看全部"）。</summary>
    public IReadOnlyList<BookmarkEntry> HomePageItems
        => _store.Items.Take(BookmarkSettings.HomePageLimit).ToList();

    public bool Contains(string path)
        => _store.Items.Any(b => Same(b.Path, path));

    /// <summary>
    /// 新增一条收藏（FR-7.1 / FR-7.2）。已存在（同路径）时返回
    /// <see cref="BookmarkAddResult.Duplicate"/> 且不改动顺序——重复收藏同一个目录时
    /// 用户想要的是"它已经在里面了"，不是"跳到最前"。
    /// </summary>
    public BookmarkAddResult TryAdd(string path, string? name = null, string? group = null, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return BookmarkAddResult.Invalid;
        if (Contains(path)) return BookmarkAddResult.Duplicate;
        if (IsFull) return BookmarkAddResult.Full;

        string normalized = path.Trim();
        string? target = NormalizeGroup(group);
        EnsureGroup(target);

        _store.Items.Add(new BookmarkEntry
        {
            Path = normalized,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Kind = Directory.Exists(normalized) ? BookmarkKind.Folder : BookmarkKind.File,
            Group = target,
            AddedAt = at ?? DateTimeOffset.UtcNow,
        });

        return BookmarkAddResult.Added;
    }

    public bool Remove(string path)
    {
        int index = IndexOf(path);
        if (index < 0) return false;

        _store.Items.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// 批量移除（FR-7.7 的"批量清理失效项"）。返回实际删掉的条数。
    ///
    /// 只删点名的那几个：调用方是异步检测出结果之后才点的名，
    /// 这里再自己扫一遍盘等于把"哪一刻的状态算数"这件事交给两次时序。
    /// </summary>
    public int RemoveMany(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var doomed = new HashSet<string>(paths.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        return doomed.Count == 0 ? 0 : _store.Items.RemoveAll(b => doomed.Contains(b.Path));
    }

    /// <summary>
    /// 在同组内上移/下移一位（FR-7.6）。<paramref name="delta"/> 为负是上移。
    ///
    /// 与"同组的邻居"交换位置，而不是 <c>RemoveAt + Insert</c>：多组并存时扁平列表里
    /// 交错着别的组的条目，插入会把跨组的前后关系一并改掉。
    /// 越界（已经是组内第一个/最后一个）时保持原样，不夹到两端——夹取会让
    /// "到底了还继续按"变成"停在最后一个"，看起来像按了没反应。
    /// </summary>
    public void Move(string path, int delta)
    {
        int from = IndexOf(path);
        if (from < 0 || delta == 0) return;

        int step = delta > 0 ? 1 : -1;
        for (int n = 0; n < Math.Abs(delta); n++)
        {
            int target = -1;
            string? group = _store.Items[from].Group;
            for (int i = from + step; i >= 0 && i < _store.Items.Count; i += step)
            {
                if (SameGroup(_store.Items[i].Group, group)) { target = i; break; }
            }

            if (target < 0) return;

            (_store.Items[from], _store.Items[target]) = (_store.Items[target], _store.Items[from]);
            from = target;
        }
    }

    /// <summary>
    /// 拖拽排序（FR-7.6）：把一条收藏放进某个分组，并落在该组内的第
    /// <paramref name="index"/> 个位置（超出范围时落到该组末尾）。
    /// </summary>
    public void MoveTo(string path, string? group, int index)
    {
        int from = IndexOf(path);
        if (from < 0) return;

        BookmarkEntry item = _store.Items[from];
        _store.Items.RemoveAt(from);
        item.Group = NormalizeGroup(group);
        EnsureGroup(item.Group);

        // 目标组内第 index 个兄弟在扁平列表里的位置；没有兄弟就放到列表末尾
        IReadOnlyList<int> siblings = SiblingIndices(item.Group);
        if (index < 0) index = 0;
        if (index >= siblings.Count)
        {
            _store.Items.Add(item);
            return;
        }

        _store.Items.Insert(siblings[index], item);
    }

    /// <summary>重命名收藏项（FR-7.5）。空名字等同于"回到路径末段"。</summary>
    public bool Rename(string path, string? name)
    {
        int index = IndexOf(path);
        if (index < 0) return false;

        _store.Items[index].Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return true;
    }

    /// <summary>设置自定义图标（FR-7.5）：<paramref name="glyph"/> 为空即回到系统图标。</summary>
    public bool SetIcon(string path, string? glyph)
    {
        int index = IndexOf(path);
        if (index < 0) return false;

        _store.Items[index].Icon = string.IsNullOrWhiteSpace(glyph) ? null : glyph;
        return true;
    }

    /// <summary>设置自定义颜色（FR-7.5）：<paramref name="color"/> 形如 <c>#RRGGBB</c>，为空即跟随主题。</summary>
    public bool SetColor(string path, string? color)
    {
        int index = IndexOf(path);
        if (index < 0) return false;

        _store.Items[index].Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        return true;
    }

    // ── 分组（FR-7.3） ──────────────────────────────────────────────────────

    /// <summary>某个分组下的条目（<paramref name="group"/> 为 null 即"未分组"）。</summary>
    public IReadOnlyList<BookmarkEntry> ItemsOf(string? group)
        => _store.Items.Where(b => SameGroup(b.Group, group)).ToList();

    public bool ContainsGroup(string? name)
    {
        string? normalized = NormalizeGroup(name);
        return normalized is not null &&
               _store.Groups.Any(g => string.Equals(g.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>新建分组。重名（不区分大小写）或已达上限时返回 false。</summary>
    public bool AddGroup(string name)
    {
        string? normalized = NormalizeGroup(name);
        if (normalized is null || ContainsGroup(normalized)) return false;
        if (_store.Groups.Count >= BookmarkSettings.MaxGroups) return false;

        _store.Groups.Add(new BookmarkGroup { Name = normalized });
        return true;
    }

    /// <summary>
    /// 重命名分组。分组名即身份，所以条目上的 <c>Group</c> 要一起改——
    /// 否则重命名之后组里的条目全部掉到"未分组"。
    /// </summary>
    public bool RenameGroup(string from, string to)
    {
        string? target = NormalizeGroup(to);
        if (target is null) return false;

        BookmarkGroup? group = _store.Groups
            .FirstOrDefault(g => string.Equals(g.Name, NormalizeGroup(from), StringComparison.OrdinalIgnoreCase));
        if (group is null || SameGroup(from, target)) return false;
        if (ContainsGroup(target)) return false;

        foreach (BookmarkEntry item in _store.Items)
        {
            if (SameGroup(item.Group, group.Name)) item.Group = target;
        }

        group.Name = target;
        return true;
    }

    /// <summary>
    /// 删除分组。**组里的条目不会跟着消失**，而是落到"未分组"——
    /// 用户删的是分类，不是他收藏的那些目录，顺手带走等于数据丢失。
    /// </summary>
    public bool RemoveGroup(string name)
    {
        int index = _store.Groups.FindIndex(g => SameGroup(g.Name, name));
        if (index < 0) return false;

        string removed = _store.Groups[index].Name;
        _store.Groups.RemoveAt(index);

        foreach (BookmarkEntry item in _store.Items)
        {
            if (SameGroup(item.Group, removed)) item.Group = null;
        }

        return true;
    }

    /// <summary>折叠状态（FR-7.3 验收：折叠状态持久化）。</summary>
    public void SetGroupExpanded(string name, bool expanded)
    {
        BookmarkGroup? group = _store.Groups
            .FirstOrDefault(g => SameGroup(g.Name, name));
        if (group is not null) group.IsExpanded = expanded;
    }

    public bool IsGroupExpanded(string name)
        => _store.Groups.FirstOrDefault(g => SameGroup(g.Name, name))?.IsExpanded ?? true;

    /// <summary>调整分组顺序（FR-7.6 拖拽排序）。</summary>
    public void MoveGroup(string name, int delta)
    {
        int from = _store.Groups.FindIndex(g => SameGroup(g.Name, name));
        if (from < 0 || delta == 0) return;

        int to = from + delta;
        if (to < 0 || to >= _store.Groups.Count) return;

        BookmarkGroup group = _store.Groups[from];
        _store.Groups.RemoveAt(from);
        _store.Groups.Insert(to, group);
    }

    public void Clear()
    {
        _store.Items.Clear();
        _store.Groups.Clear();
    }

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

    private IReadOnlyList<int> SiblingIndices(string? group)
    {
        var result = new List<int>();
        for (int i = 0; i < _store.Items.Count; i++)
        {
            if (SameGroup(_store.Items[i].Group, group)) result.Add(i);
        }

        return result;
    }

    private static string? NormalizeGroup(string? group)
        => string.IsNullOrWhiteSpace(group) ? null : group.Trim();

    /// <summary>归组时组名不存在就顺手建一个——否则条目会指向一个看不见的分组。</summary>
    private bool EnsureGroup(string? name)
    {
        if (name is null || ContainsGroup(name)) return false;
        if (_store.Groups.Count >= BookmarkSettings.MaxGroups) return false;

        _store.Groups.Add(new BookmarkGroup { Name = name });
        return true;
    }

    private static bool SameGroup(string? left, string? right)
    {
        string? a = NormalizeGroup(left);
        string? b = NormalizeGroup(right);
        return a is null || b is null
            ? a is null && b is null
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string left, string right)
        => string.Equals(left, right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
