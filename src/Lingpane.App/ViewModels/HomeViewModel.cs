using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Lingpane.App.Diagnostics;
using Lingpane.App.Services;
using Lingpane.App.Shell;
using Lingpane.Core.Localization;
using Lingpane.Core.Navigation;
using Lingpane.Core.Settings;

namespace Lingpane.App.ViewModels;

/// <summary>
/// 主页上"点一下就能打开某个位置"的条目的共同形状。
/// 界面只认这一个接口，四种卡片（目录/驱动器/最近/收藏）共用一个点击处理器。
/// </summary>
public interface IHomeEntry
{
    string Path { get; }
}

/// <summary>主页上的常用目录卡片（FR-1.1.1）。</summary>
public sealed class HomeFolderTile : IHomeEntry
{
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required string Path { get; init; }
}

/// <summary>
/// 驱动器卡片（FR-1.1.3）。容量是异步填进来的，所以除路径与盘符外都可变。
/// 卡片先以"读取中"出现、再被填上容量，比"等所有盘都读完再一起出现"更接近
/// 需求里"不阻塞首帧"的意思——用户至少立刻看见有几个盘。
/// </summary>
public sealed class HomeDriveTile : INotifyPropertyChanged, IHomeEntry
{
    private string _capacityText;
    private double _usedRatio;
    private bool _hasCapacity;

    public HomeDriveTile(string path, string name)
    {
        Path = path;
        Name = name;
        _capacityText = Strings.Home_Drive_Loading;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    /// <summary>盘符（<c>C:</c>）或已挂载卷的显示名。</summary>
    public string Name { get; }

    public string Glyph => IconGlyphs.Drive;

    /// <summary>"123 GB 可用，共 456 GB"；读取失败时是"—"（FR-1.1.3）。</summary>
    public string CapacityText
    {
        get => _capacityText;
        private set
        {
            if (_capacityText == value) return;
            _capacityText = value;
            Raise();
        }
    }

    /// <summary>已用比例（0–1），画容量条用。不可读时为 0 且不显示条。</summary>
    public double UsedRatio
    {
        get => _usedRatio;
        private set
        {
            if (Math.Abs(_usedRatio - value) < 0.0001) return;
            _usedRatio = value;
            Raise();
        }
    }

    public bool HasCapacity
    {
        get => _hasCapacity;
        private set
        {
            if (_hasCapacity == value) return;
            _hasCapacity = value;
            Raise();
        }
    }

    public void Apply(long freeBytes, long totalBytes)
    {
        CapacityText = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Home_Drive_Capacity,
            FormatBytes(freeBytes),
            FormatBytes(totalBytes));

        UsedRatio = totalBytes > 0 ? Math.Clamp((double)(totalBytes - freeBytes) / totalBytes, 0, 1) : 0;
        HasCapacity = totalBytes > 0;
    }

    /// <summary>读取失败（网络盘断线、无介质）——按需求显示"—"，不留"读取中"。</summary>
    public void Fail()
    {
        CapacityText = Strings.Home_Drive_Unknown;
        HasCapacity = false;
        UsedRatio = 0;
    }

    /// <summary>
    /// 容量格式化。不用 <see cref="Core.FileSystem.FileEntry.FormatSizeText"/>：
    /// 那个到 GB 就封顶，4TB 的盘会显示成 "4096 GB"，在主页这种一眼看的场景里不好读。
    /// </summary>
    internal static string FormatBytes(long bytes)
    {
        const double Kb = 1024d;
        const double Mb = Kb * 1024;
        const double Gb = Mb * 1024;
        const double Tb = Gb * 1024;

        double value = bytes;
        return value switch
        {
            >= Tb => (value / Tb).ToString("0.##", CultureInfo.CurrentCulture) + " TB",
            >= Gb => (value / Gb).ToString("0.#", CultureInfo.CurrentCulture) + " GB",
            >= Mb => (value / Mb).ToString("0.#", CultureInfo.CurrentCulture) + " MB",
            _ => (value / Kb).ToString("0.#", CultureInfo.CurrentCulture) + " KB",
        };
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>最近访问的一条（FR-1.1.2）。<see cref="IsMissing"/> 为真时灰显且点不开。</summary>
public sealed class HomeRecentTile : INotifyPropertyChanged, IHomeEntry
{
    private bool _isMissing;

    public HomeRecentTile(string path, string name, string parent)
    {
        Path = path;
        Name = name;
        Parent = parent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string Name { get; }

    /// <summary>父目录。同名目录（三份 Documents）靠它区分。</summary>
    public string Parent { get; }

    public string Glyph => IconGlyphs.Folder;

    /// <summary>目录已不存在（U 盘拔了、被删了）。只标记不删除——盘回来它就还能用。</summary>
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (_isMissing == value) return;
            _isMissing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMissing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tooltip)));
        }
    }

    public string Tooltip => IsMissing ? $"{Path}{Environment.NewLine}{Strings.Home_Recent_Missing}" : Path;
}

/// <summary>主页收藏区的一项（FR-1.1.5，与 FR-7 同一份数据）。</summary>
public sealed class HomeBookmarkTile : INotifyPropertyChanged, IHomeEntry
{
    private bool _isMissing;

    public HomeBookmarkTile(BookmarkEntry entry)
    {
        Path = entry.Path;
        Name = BookmarkStore.DisplayName(entry);
        Glyph = entry.Kind == BookmarkKind.Folder ? IconGlyphs.Folder : IconGlyphs.File;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string Name { get; }

    public string Glyph { get; }

    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (_isMissing == value) return;
            _isMissing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMissing)));
        }
    }
}

/// <summary>
/// 主页（FR-1.1）。它是**一种标签页**（<see cref="TabKind.Home"/>），不是独立窗口。
///
/// 这个视图模型刻意不持有任何目录状态：主页不"当前位于"某处，它只是几个入口的集合。
/// 点击卡片后由宿主决定"在当前标签内导航"还是"开新标签"，因此这里只发事件、不做导航——
/// 主页若自己知道窗格的存在，双窗格下"点卡片该开在哪一侧"就又要在两处各判一次。
///
/// 首帧约定：构造函数只读内存（设置里的历史与收藏），驱动器与失效检查都在
/// <see cref="LoadAsync"/> 里异步做（FR-1.1.3"容量异步获取不阻塞首帧"）。
/// </summary>
public sealed class HomeViewModel : INotifyPropertyChanged
{
    /// <summary>驱动器容量读取的超时。断线的映射盘会卡在 IsReady 上几十秒，不能让整排卡片等它。</summary>
    private static readonly TimeSpan DriveProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly VisitHistory _history;
    private readonly BookmarkStore _bookmarks;

    private bool _isBookmarksExpanded;
    private bool _isLoadingDrives;
    private bool _loadStarted;

    public HomeViewModel(VisitHistory history, BookmarkStore bookmarks)
    {
        _history = history;
        _bookmarks = bookmarks;

        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>点了某个入口。<c>newTab</c> 为真表示 Ctrl+点击/中键，要开新标签。</summary>
    public event Action<string, bool>? OpenRequested;

    public event Action? NewTabRequested;

    public event Action? NewFolderRequested;

    /// <summary>主页改动了设置里的数据（清空历史），宿主据此落盘。</summary>
    public event Action? SettingsChanged;

    public ObservableCollection<HomeFolderTile> Folders { get; } = [];

    public ObservableCollection<HomeDriveTile> Drives { get; } = [];

    public ObservableCollection<HomeRecentTile> Recent { get; } = [];

    public ObservableCollection<HomeBookmarkTile> Bookmarks { get; } = [];

    public bool HasFolders => Folders.Count > 0;

    public bool HasDrives => Drives.Count > 0;

    public bool HasRecent => Recent.Count > 0;

    public bool HasBookmarks => Bookmarks.Count > 0;

    public bool IsLoadingDrives
    {
        get => _isLoadingDrives;
        private set
        {
            if (_isLoadingDrives == value) return;
            _isLoadingDrives = value;
            Raise();
        }
    }

    /// <summary>收藏超过 8 项（FR-1.1.5 的"查看全部"）。</summary>
    public bool HasMoreBookmarks => _bookmarks.Count > BookmarkSettings.HomePageLimit;

    /// <summary>收藏区是否展开到全部。展开后就地显示，不跳页——0.2.0 还没有独立的收藏页。</summary>
    public bool IsBookmarksExpanded
    {
        get => _isBookmarksExpanded;
        private set
        {
            if (_isBookmarksExpanded == value) return;
            _isBookmarksExpanded = value;
            Raise();
            Raise(nameof(BookmarksToggleText));
            RebuildBookmarks();
        }
    }

    public string BookmarksToggleText
        => IsBookmarksExpanded ? Strings.Home_Bookmarks_Less : Strings.Home_Bookmarks_More;

    /// <summary>
    /// 重建全部"内存可见"的部分：常用目录、最近访问、收藏。
    /// 不碰驱动器（那是异步的，见 <see cref="LoadAsync"/>）。
    /// </summary>
    public void Refresh()
    {
        RebuildFolders();
        RebuildRecent();
        RebuildBookmarks();
    }

    /// <summary>
    /// 首帧之后的异步部分：驱动器容量（FR-1.1.3）+ 失效标记（FR-7.7 的同一条路子）。
    /// 只在第一次进入主页时跑；切回来再跑一次是为了"刚插的 U 盘要能被看见"。
    /// </summary>
    public async Task LoadAsync()
    {
        _loadStarted = true;
        await Task.WhenAll(LoadDrivesAsync(), CheckAvailabilityAsync()).ConfigureAwait(true);
    }

    /// <summary>清空最近访问（FR-1.1.2 的"一键清空"）。只清历史，不动收藏与会话。</summary>
    public void ClearRecent()
    {
        _history.Clear();
        RebuildRecent();
        SettingsChanged?.Invoke();
    }

    public void ToggleBookmarks() => IsBookmarksExpanded = !IsBookmarksExpanded;

    /// <summary>语言切换后重建文案（卡片上的名字与容量文本都是构造时算好的字符串）。</summary>
    public void RefreshLocalizedText()
    {
        RebuildFolders();
        RebuildRecent();
        RebuildBookmarks();

        foreach (HomeDriveTile drive in Drives)
        {
            // 容量文本是格式化出来的，切语言后要按新语言重排；没有容量的保持"读取中/—"
            if (!drive.HasCapacity) drive.Fail();
        }
    }

    private void RebuildFolders()
    {
        Folders.Clear();
        foreach (KnownFolder folder in KnownFolderService.All)
        {
            (string name, string glyph) = KnownFolderDisplay.Of(folder.Kind);
            Folders.Add(new HomeFolderTile { Name = name, Glyph = glyph, Path = folder.Path });
        }

        Raise(nameof(HasFolders));
    }

    private void RebuildRecent()
    {
        Recent.Clear();
        foreach (HistoryEntry entry in _history.Recent())
        {
            string name = DisplayNameOf(entry.Path);
            Recent.Add(new HomeRecentTile(entry.Path, name, Path.GetDirectoryName(entry.Path) ?? string.Empty));
        }

        Raise(nameof(HasRecent));
    }

    private void RebuildBookmarks()
    {
        Bookmarks.Clear();

        IEnumerable<BookmarkEntry> source = IsBookmarksExpanded
            ? _bookmarks.Items
            : _bookmarks.HomePageItems;

        foreach (BookmarkEntry entry in source) Bookmarks.Add(new HomeBookmarkTile(entry));

        Raise(nameof(HasBookmarks));
        Raise(nameof(HasMoreBookmarks));
    }

    private async Task LoadDrivesAsync()
    {
        IsLoadingDrives = true;
        try
        {
            // GetDrives 本身也可能慢（枚举映射网络盘），整段放到线程池
            string[] roots = await Task.Run(() => DriveInfo.GetDrives().Select(d => d.Name).ToArray())
                .ConfigureAwait(true);

            Drives.Clear();
            foreach (string root in roots)
            {
                Drives.Add(new HomeDriveTile(root, root.TrimEnd(Path.DirectorySeparatorChar)));
            }

            Raise(nameof(HasDrives));
            IsLoadingDrives = false;

            await Task.WhenAll(Drives.Select(FillDriveAsync)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 一台盘都读不出来不该让主页变空白：卡片区显示空态即可
            AppLog.Warn("home.drives", "E9002", $"{ex.GetType().Name}: {ex.Message}");  // i18n-ignore：只进 app.log
            IsLoadingDrives = false;
        }
    }

    /// <summary>
    /// 单个驱动器的容量。超时就标"—"，不等它——一个断线的映射盘会让整排卡片停在"读取中"。
    /// </summary>
    private static async Task FillDriveAsync(HomeDriveTile tile)
    {
        Task<(long Free, long Total)?> probe = Task.Run<(long Free, long Total)?>(() =>
        {
            try
            {
                var info = new DriveInfo(tile.Path);

                // 无介质的读卡器、断线的映射盘：IsReady 为 false 时 TotalSize 会抛
                return info.IsReady ? (info.AvailableFreeSpace, info.TotalSize) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        });

        Task finished = await Task.WhenAny(probe, Task.Delay(DriveProbeTimeout)).ConfigureAwait(true);
        if (!ReferenceEquals(finished, probe))
        {
            tile.Fail();
            return;
        }

        if (await probe.ConfigureAwait(true) is { } sizes) tile.Apply(sizes.Free, sizes.Total);
        else tile.Fail();
    }

    /// <summary>
    /// 逐个检查路径是否还在（最近访问与收藏）。必须异步：UNC 路径不可达时要等几秒，
    /// 同步做会让主页首帧卡住——这正是 FR-7.7 明确要求"不要在渲染前同步检查"的原因。
    /// </summary>
    private async Task CheckAvailabilityAsync()
    {
        HomeRecentTile[] recents = Recent.ToArray();
        HomeBookmarkTile[] bookmarks = Bookmarks.ToArray();

        var results = await Task.Run(() =>
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in recents.Select(r => r.Path).Concat(bookmarks.Select(b => b.Path)))
            {
                if (map.ContainsKey(path)) continue;
                map[path] = SafeExists(path);
            }

            return map;
        }).ConfigureAwait(true);

        foreach (HomeRecentTile tile in recents)
        {
            if (results.TryGetValue(tile.Path, out bool exists)) tile.IsMissing = !exists;
        }

        foreach (HomeBookmarkTile tile in bookmarks)
        {
            if (results.TryGetValue(tile.Path, out bool exists)) tile.IsMissing = !exists;
        }
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return Directory.Exists(path) || File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string DisplayNameOf(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>主页是否已经跑过异步加载。切回主页时据此决定要不要再刷一次驱动器。</summary>
    public bool HasLoaded => _loadStarted;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
