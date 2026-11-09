using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Lingpane.App.Diagnostics;
using Lingpane.App.Shell;
using Lingpane.Core.Localization;
using Lingpane.Core.Navigation;
using Lingpane.Core.Settings;
using Lingpane.Core.Tagging;
using Lingpane.Core.Workspaces;

namespace Lingpane.App.ViewModels;

/// <summary>
/// 导航栏一行的共同标记。系统节点（<see cref="NavItem"/>）是窗格那边的 record，
/// 不能改成继承本文件的类，所以两者的共同点只能放在接口上。
/// </summary>
public interface INavRow;

/// <summary>收藏区的行（分区标题 / 空态 / 分组 / 收藏项）的共同基类。</summary>
public abstract class NavRow : INavRow;

/// <summary>
/// 分区标题（如"收藏"）。不可选中，点击无动作；右键出该分区的管理菜单。
///
/// 四个分区标题的作用不是装饰，而是"自报身份"：侧栏里同时住着系统位置、
/// 用户钉住的路径、条目上的颜色标记、成组的标签页快照，四者互不相干。
/// 每块顶上一个名字，用户才知道"这一段是什么"，而不是把它们读成一棵树的层级。
/// </summary>
public sealed class NavSectionRow : NavRow
{
    public required string Title { get; init; }

    /// <summary>这份标题属于哪个分区。模板据此决定右键菜单里出哪几项。</summary>
    public NavSectionKind Kind { get; init; } = NavSectionKind.Bookmarks;

    /// <summary>
    /// 是否列表里的第一个分区。第一个标题上方不画分隔线——
    /// 它上面只有主页，再画一条横线会像"这里被切断了"。
    /// </summary>
    public bool IsFirst { get; init; }

    /// <summary>
    /// 标记"这一行是分区标题"，供列表项容器把固定行高放开：
    /// 标题行要装下分隔线与上下留白，28px 的正常行高装不进。
    /// 其它行没有这个属性，绑定失败即不触发，不需要给每个行类型都加一遍。
    /// </summary>
    public bool IsSection => true;

    public bool IsBookmarks => Kind == NavSectionKind.Bookmarks;

    public bool IsWorkspaces => Kind == NavSectionKind.Workspaces;

    /// <summary>位置与标签分区没有管理动作，右键菜单要整份关掉（空菜单弹出一个小空框更糟）。</summary>
    public bool HasMenu => Kind is NavSectionKind.Bookmarks or NavSectionKind.Workspaces;
}

public enum NavSectionKind
{
    /// <summary>系统解析出来的已知文件夹（桌面 / 文档 / 下载 / …）。</summary>
    Locations,

    Bookmarks,

    /// <summary>彩色标签（FR-4.1.5）：按颜色归类查看所有已标记项。</summary>
    Tags,

    Workspaces,
}

/// <summary>分区下方的空态提示，例如"还没有收藏，Ctrl+D 添加当前目录"。</summary>
public sealed class NavEmptyRow : NavRow
{
    public required string Text { get; init; }

    /// <summary>提示行要能折行，行高交给内容（见 Nav.Item 里的触发器）。</summary>
    public bool IsHint => true;
}

/// <summary>
/// 收藏分组标题（FR-7.3）。折叠状态存在设置里，所以这一行是可变的：
/// 点一下只改 <see cref="IsExpanded"/> 与它下面那几行的可见性，不重建整棵树。
/// </summary>
public sealed class NavGroupRow : NavRow, INotifyPropertyChanged
{
    private bool _isExpanded;

    public required string Name { get; init; }

    /// <summary>"未分组"是伪组：它在设置文件里没有对应条目，不能重命名/删除。</summary>
    public bool IsUngrouped { get; init; }

    public required int Count { get; init; }

    public NavGroupRow(bool expanded) => _isExpanded = expanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise();
            Raise(nameof(ChevronGlyph));
        }
    }

    public string ChevronGlyph => _isExpanded ? IconGlyphs.ChevronDown : IconGlyphs.ChevronRight;

    public string Title => $"{Name} ({Count})";

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 一份工作区（FR-8）。行上显示"名字 (标签数)"，提示里给出最后保存时间——
/// 这两项正是 FR-8.3 要求"管理界面可见"的信息；管理动作走右键菜单，不再单开一个窗口。
/// </summary>
public sealed class NavWorkspaceRow : NavRow
{
    public NavWorkspaceRow(Workspace workspace, bool isStartup)
    {
        Workspace = workspace;
        IsStartup = isStartup;
    }

    public Workspace Workspace { get; }

    public string Name => Workspace.Name;

    public int TabCount => Workspace.Tabs.Count;

    /// <summary>起点标记：启动时自动恢复的那一份，行尾有一个小点，避免"改了名不知道哪份在生效"。</summary>
    public bool IsStartup { get; }

    public string Title => $"{Name} ({TabCount})";

    public string Glyph => IconGlyphs.Workspace;

    public string TooltipText
        => $"{Workspace.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n"
         + string.Format(Strings.Nav_Workspace_Tooltip, TabCount);

    /// <summary>右键菜单里那一项的文字：已经是启动项时给"取消"，否则给"设为"。</summary>
    public string StartupMenuText
        => IsStartup ? Strings.Workspace_ClearStartup : Strings.Workspace_SetStartup;
}

/// <summary>
/// 标签分区的一行（FR-4.1.5）：一种颜色 + 该色下的条目数。
///
/// 行尾给的是"这一色有几项"而不是总数：八个颜色节点各自是可点的入口，
/// 数量要能回答"点进去会看到多少东西"。
/// </summary>
public sealed class NavTagRow : NavRow
{
    public NavTagRow(TagColors color, string name, string hex, int count)
    {
        Color = color;
        Name = name;
        Hex = hex;
        Count = count;
    }

    public TagColors Color { get; }

    public string Name { get; }

    /// <summary>圆点颜色。<c>#RRGGBB</c>，与列表行尾那个圆点取自同一张色表。</summary>
    public string Hex { get; }

    public int Count { get; }

    public string Title => $"{Name} ({Count})";
}

/// <summary>一条收藏（FR-7.1 / FR-7.2）。失效项标灰并给出提示（FR-7.7）。</summary>
public sealed class NavBookmarkRow : NavRow, INotifyPropertyChanged
{
    private bool _isMissing;

    public NavBookmarkRow(BookmarkEntry entry)
    {
        Entry = entry;
        Name = BookmarkStore.DisplayName(entry);
        Tooltip = entry.Path;

        // 颜色在设置里是 #RRGGBB 字符串（用户会手改的地方不放 Brush）。
        // 解析失败就当作没设过——一个手抖的色值时，图标该是默认色，而不是让整行崩掉。
        if (!string.IsNullOrWhiteSpace(entry.Color))
        {
            try
            {
                GlyphBrush = (Brush)new BrushConverter().ConvertFromString(entry.Color)!;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                GlyphBrush = null;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BookmarkEntry Entry { get; }

    public string Path => Entry.Path;

    public string Name { get; }

    public string Tooltip { get; }

    /// <summary>
    /// 自定义图标优先（FR-7.5），否则给星标。
    ///
    /// 默认不用文件夹字形：系统位置那七项已经全是文件夹图标，
    /// 收藏再用同一个形状，一行行看下来就分不清"这是位置还是我钉的收藏"。
    /// 星标本身就是"收藏"的通用记号。
    /// </summary>
    public string Glyph => Entry.Icon ?? IconGlyphs.Star;

    /// <summary>自定义颜色（FR-7.5）。null = 跟随主题图标色；解析失败的色值也当没设过。</summary>
    public Brush? GlyphBrush { get; }

    public bool HasColor => GlyphBrush is not null;

    /// <summary>路径当前不存在。异步检测后才置真（FR-7.7），首帧不做同步判断。</summary>
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (_isMissing == value) return;
            _isMissing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMissing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tooltip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TooltipText)));
        }
    }

    /// <summary>失效时把"位置不可用"写进提示，光标悬停就能看出来为什么是灰的。</summary>
    public string TooltipText => _isMissing ? $"{Tooltip}\n{Strings.Nav_Bookmark_Missing}" : Tooltip;
}

/// <summary>
/// 导航栏的完整内容：系统节点（主页 / 桌面 / 文档 / …）在上，收藏区在下（FR-7.3）。
///
/// 为什么拼成**一条扁平列表**而不是"两个列表"或"一棵 TreeView"：
///   · 两个列表 = 两套选中态，点收藏之后快速访问那边还亮着，得手动互斥；
///   · TreeView 要重写分组模板与展开动画，而这里的层级只有两层，
///     折叠只是"少几行"——扁平列表加一次重建就够了（收藏上限 500，重建成本可忽略）。
///
/// 系统节点由 <see cref="PaneViewModel.QuickAccess"/> 提供（它管已知文件夹的解析与刷新），
/// 本类只负责把它们与收藏拼在一起，不重复一套已知文件夹逻辑。
/// </summary>
public sealed class NavPanelViewModel : INotifyPropertyChanged
{
    private readonly BookmarkStore _store;
    private readonly Func<IReadOnlyList<NavItem>> _systemRows;
    private IReadOnlyList<string> _missing = [];

    /// <summary>
    /// 工作区（FR-8）。null 表示"还没加载"——首帧不读 workspaces.json（需求要求它
    /// 不参与冷启动），由窗口在首帧之后调 <see cref="SetWorkspaces"/> 补上。
    /// </summary>
    private WorkspaceStore? _workspaces;

    /// <summary>启动时自动恢复的那份工作区名（FR-8.4），只用来在行尾标个点。</summary>
    private string? _startupWorkspace;

    /// <summary>标签库（FR-4.1.5）。null = 这个宿主不支持标签（单测、将来的无标签形态）。</summary>
    private readonly TagStore? _tags;

    public NavPanelViewModel(BookmarkStore store, Func<IReadOnlyList<NavItem>> systemRows,
        TagStore? tags = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _systemRows = systemRows ?? throw new ArgumentNullException(nameof(systemRows));
        _tags = tags;

        // 这里**不**调 Rebuild：systemRows 要读窗口的活动标签，而本对象在
        // "标签集合刚建出来"之前就被构造了，此时取不到东西（一取就是空引用）。
        // 由窗口在 RestoreTabs 之后显式调一次（MainWindow 的构造里）。
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<INavRow> Rows { get; } = [];

    public bool HasBookmarks => _store.Count > 0;

    /// <summary>当前检测出失效的收藏路径（FR-7.7 的"批量清理"用）。</summary>
    public IReadOnlyList<string> MissingPaths => _missing;

    public bool HasWorkspaces => _workspaces is { Count: > 0 };

    /// <summary>
    /// 接上工作区数据（FR-8.6）。首帧之后再调：workspaces.json 不参与冷启动默认路径，
    /// 而导航栏是在首帧前就建好的，所以只能先出架子、稍后补这一段。
    /// </summary>
    public void SetWorkspaces(WorkspaceStore store, string? startupWorkspace)
    {
        _workspaces = store ?? throw new ArgumentNullException(nameof(store));
        _startupWorkspace = startupWorkspace;
        Rebuild();
    }

    /// <summary>
    /// 重建整条导航列表。系统节点、分区标题、未分组条目、各分组（展开的才带上子项）依次拼起来。
    /// 分组顺序取设置里的顺序（FR-7.6 拖拽排序改的就是这个）。
    /// </summary>
    public void Rebuild()
    {
        Rows.Clear();

        NavItem[] system = [.. _systemRows()];

        // 主页置顶、不归任何分区：它不是一个"位置"，而是应用内的一个视图。
        // 塞进「位置」下面会让那一块的第一项与其余七项不是同一类东西。
        foreach (NavItem item in system)
        {
            if (item.IsHome) Rows.Add(item);
        }

        Rows.Add(new NavSectionRow
        {
            Title = Strings.Nav_Locations,
            Kind = NavSectionKind.Locations,
            IsFirst = true,
        });

        foreach (NavItem item in system)
        {
            if (!item.IsHome) Rows.Add(item);
        }

        Rows.Add(new NavSectionRow { Title = Strings.Nav_Bookmarks, Kind = NavSectionKind.Bookmarks });

        if (_store.Count == 0)
        {
            Rows.Add(new NavEmptyRow { Text = Strings.Nav_Bookmarks_Empty });
        }
        else
        {
            // 未分组的一批先露出来：Ctrl+D 刚加的收藏应该一眼能看见，
            // 而不是先被折进某个分组、用户以为没加上。
            foreach (BookmarkEntry entry in _store.ItemsOf(null)) Rows.Add(Row(entry));

            foreach (BookmarkGroup group in _store.Groups)
            {
                IReadOnlyList<BookmarkEntry> items = _store.ItemsOf(group.Name);
                var header = new NavGroupRow(group.IsExpanded) { Name = group.Name, Count = items.Count };
                Rows.Add(header);

                if (!group.IsExpanded) continue;
                foreach (BookmarkEntry entry in items) Rows.Add(Row(entry));
            }
        }

        Raise(nameof(HasBookmarks));
        AddTagRows();
        AddWorkspaceRows();
        _ = CheckMissingAsync();
    }

    /// <summary>
    /// 标签一段（FR-4.1.5）。只列**用过的**颜色：八个全摆出来更像调色板，
    /// 而这里要回答的是"我标过哪些颜色的东西"。
    ///
    /// 标签库是懒加载的，加载完成前 <see cref="TagStore.Count"/> 为 0，
    /// 于是先出空态、加载完由宿主再重建一次（见 MainWindow.LoadTagsAsync）。
    /// </summary>
    private void AddTagRows()
    {
        Rows.Add(new NavSectionRow { Title = Strings.Nav_Tags, Kind = NavSectionKind.Tags });
        foreach (INavRow row in BuildTagRows()) Rows.Add(row);
    }

    /// <summary>标签分区的内容行（不含分区标题），空库时是一行空态提示。</summary>
    private List<INavRow> BuildTagRows()
    {
        var rows = new List<INavRow>();

        if (_tags is null || _tags.Count == 0)
        {
            rows.Add(new NavEmptyRow { Text = Strings.Nav_Tags_Empty });
            return rows;
        }

        IReadOnlyDictionary<TagColors, int> counts = _tags.CountByColor();

        foreach (TagPresets.ColorPreset preset in TagPresets.Colors)
        {
            int count = counts.TryGetValue(preset.Value, out int n) ? n : 0;
            if (count == 0) continue;

            rows.Add(new NavTagRow(preset.Value, preset.Name, preset.Hex, count));
        }

        return rows;
    }

    /// <summary>
    /// 只重建标签分区，不动别的行。
    ///
    /// 标签库是懒加载的（首帧后才读），读完要把"空态"换成真实颜色；
    /// 用户打一次标也要让数量跟上。这时整条 <see cref="Rebuild"/> 会把当前选中的
    /// 系统节点一起清掉——加个标签，侧栏却闪一下跳回顶部，像出了故障。
    /// </summary>
    public void RefreshTagRows()
    {
        int start = -1;
        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i] is NavSectionRow { Kind: NavSectionKind.Tags }) { start = i; break; }
        }

        if (start < 0) return;

        // 分区到下一个分区标题为止
        int end = start + 1;
        while (end < Rows.Count && Rows[end] is not NavSectionRow) end++;

        for (int i = end - 1; i > start; i--) Rows.RemoveAt(i);

        int insert = start;
        foreach (INavRow row in BuildTagRows()) Rows.Insert(++insert, row);
    }

    /// <summary>工作区一段（FR-8.6）。数据没接上时整段都不出，不留一个空标题。</summary>
    private void AddWorkspaceRows()
    {
        if (_workspaces is not { } store) return;

        Rows.Add(new NavSectionRow { Title = Strings.Nav_Workspaces, Kind = NavSectionKind.Workspaces });

        if (store.Count == 0)
        {
            Rows.Add(new NavEmptyRow { Text = Strings.Nav_Workspaces_Empty });
            return;
        }

        foreach (Workspace ws in store.Items)
        {
            bool isStartup = string.Equals(ws.Name, _startupWorkspace, StringComparison.OrdinalIgnoreCase);
            Rows.Add(new NavWorkspaceRow(ws, isStartup));
        }

        Raise(nameof(HasWorkspaces));
    }

    /// <summary>折叠/展开一个分组（FR-7.3 验收）。状态同时写回设置，下次启动仍是这个样子。</summary>
    public void ToggleGroup(NavGroupRow group)
    {
        group.IsExpanded = !group.IsExpanded;
        _store.SetGroupExpanded(group.Name, group.IsExpanded);
        Rebuild();
    }

    /// <summary>找出某一色的标签节点。标签结果页没有路径，侧栏高亮只能按颜色找。</summary>
    public NavTagRow? FindTagRow(TagColors color)
        => Rows.OfType<NavTagRow>().FirstOrDefault(row => row.Color == color);

    /// <summary>
    /// 找出与某个路径对应的行（系统节点或收藏项）。导航栏高亮用它——
    /// 一个路径在两边都可能有（"下载"既是系统节点、也可能被收藏过），先认系统节点。
    /// </summary>
    public INavRow? FindRow(string path)
    {
        if (string.IsNullOrEmpty(path)) return Rows.OfType<NavItem>().FirstOrDefault(i => i.IsHome);

        return Rows.FirstOrDefault(row => row switch
        {
            NavItem item => !item.IsHome && Same(item.Path, path),
            NavBookmarkRow bookmark => Same(bookmark.Path, path),
            _ => false,
        });

        static bool Same(string left, string right)
            => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 失效检测（FR-7.7）：整批丢到线程池，一次问完再回 UI 线程改标记。
    ///
    /// 逐条 <c>await</c> 会让 500 条收藏产生 500 次线程往返，而且每条都可能在
    /// 断线的网络盘上挂几秒；整批加 <see cref="MissingCheckTimeout"/> 上限，
    /// 超时就当"这一轮没结论"，保持上一次的标记——宁可灰得晚，也不让导航栏卡住。
    /// </summary>
    public async Task CheckMissingAsync()
    {
        NavBookmarkRow[] rows = Rows.OfType<NavBookmarkRow>().ToArray();
        if (rows.Length == 0)
        {
            _missing = [];
            return;
        }

        string[] paths = rows.Select(r => r.Path).ToArray();

        Task<bool[]> probe = Task.Run(() => paths.Select(Exists).ToArray());
        Task finished = await Task.WhenAny(probe, Task.Delay(MissingCheckTimeout)).ConfigureAwait(true);
        if (!ReferenceEquals(finished, probe))
        {
            AppLog.Warn("bookmark.missing", "E7001", $"失效检测超时，{paths.Length} 条按上次结果处理");  // i18n-ignore：只进 app.log
            return;
        }

        bool[] exists = await probe.ConfigureAwait(true);

        var missing = new List<string>();
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i].IsMissing = !exists[i];
            if (!exists[i]) missing.Add(rows[i].Path);
        }

        _missing = missing;
    }

    /// <summary>检查整个收藏列表里哪些路径已经不在了（批量清理前算一次）。</summary>
    public async Task<IReadOnlyList<string>> FindMissingAsync()
    {
        await CheckMissingAsync().ConfigureAwait(true);
        return _missing;
    }

    private static readonly TimeSpan MissingCheckTimeout = TimeSpan.FromSeconds(3);

    private static bool Exists(string path)
    {
        try
        {
            return Directory.Exists(path) || File.Exists(path);
        }
        catch (Exception)
        {
            // 无权限、路径非法（U 盘拔了留下的盘符）都当作"不在"，
            // 但不抛到上层：一次检测失败不该影响整轮结果。
            return false;
        }
    }

    private static NavBookmarkRow Row(BookmarkEntry entry) => new(entry);

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
