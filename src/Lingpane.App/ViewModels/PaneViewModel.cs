using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Lingpane.App.Shell;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Localization;
using Lingpane.Core.Navigation;
using Lingpane.Core.Settings;

namespace Lingpane.App.ViewModels;

/// <summary>左侧导航项。显示名与字形在 UI 层映射，Core 只给路径。</summary>
public sealed record NavItem(string Name, string Glyph, string Path);

/// <summary>
/// 单个浏览区（窗格）的视图模型：负责"打开目录 → 过滤 → 排序 → 交给列表"，
/// 以及导航栈、地址栏状态与状态栏文案。不含标签页、双窗格、彩标——这些在 M2 接入。
///
/// 一个 PaneView 对应一个 PaneViewModel。双窗格就是两份，互不共享导航栈与选中项。
///
/// 内存与响应性约定：
///   · <c>_all</c> 保存磁盘枚举的原始结果，切换"显示隐藏文件"只在内存里过滤，不重新枚举（FR-5.2.4）
///   · 对外只暴露整体替换的 IReadOnlyList，避免逐条 CollectionChanged（ADR-0003）
/// </summary>
public sealed class PaneViewModel : INotifyPropertyChanged
{
    private readonly NavigationHistory _history = new();
    private readonly AppSettings _settings;

    private List<FileEntry> _all = [];
    private IReadOnlyList<FileEntry> _entries = [];
    private IReadOnlyList<BreadcrumbSegment> _breadcrumbs = [];
    private string _currentPath = string.Empty;
    private string _addressText = string.Empty;
    private string _statusText = string.Empty;
    private string _selectionText = string.Empty;
    private string _hintText = string.Empty;
    private string? _lastNavigationError;
    private bool _isLoading;
    private bool _isAddressEditing;
    private DefaultViewKind _viewMode;
    private EntrySort _sort;
    private IReadOnlyList<NavItem> _quickAccess = [];

    // 语言切换要把状态栏/选中统计按当前语言重算，得记住上次的入参
    private string _lastNavigationErrorCode = "E1001";
    private int _lastSelectionCount;
    private long _lastSelectionSize;

    /// <summary>导航序号：用于丢弃过期的异步枚举结果（快速连按时防止旧目录覆盖新目录）。</summary>
    private int _navigationToken;

    public PaneViewModel(AppSettings settings)
    {
        _settings = settings;
        // schema 里允许 grid，但 M1 未实现该视图（见 SetViewMode），启动时回落到列表
        _viewMode = settings.Behavior.DefaultView == DefaultViewKind.Grid
            ? DefaultViewKind.List
            : settings.Behavior.DefaultView;
        _sort = new EntrySort(settings.Behavior.SortBy, settings.Behavior.SortDescending, settings.Behavior.FoldersFirst);
        _quickAccess = BuildQuickAccess();
        _statusText = Strings.Status_Ready;

        // 切换语言后，视图模型里"算出来的"文本不会自己变（列头带排序箭头、状态栏是快照字符串）。
        // 界面上的 {loc:Tr} 绑定由 LocalizationService 自己刷新，不经过这里。
        LocalizationService.Instance.PropertyChanged += (_, _) => RefreshLocalizedText();
    }

    /// <summary>语言切换后重算所有算好的文案。只重算字符串，不重新枚举磁盘。</summary>
    private void RefreshLocalizedText()
    {
        Raise(nameof(TitleText));
        Raise(nameof(NameHeader));
        Raise(nameof(DateHeader));
        Raise(nameof(TypeHeader));
        Raise(nameof(SizeHeader));
        QuickAccess = BuildQuickAccess();

        // 类型列是按语言算出来的文案，绑定不会自己重取，必须逐条通知
        FileEntry.RefreshTypeText(_entries);

        StatusText = HasNavigationError
            ? string.Format(Strings.Error_CannotOpen, DescribeNavigationError(_lastNavigationErrorCode))
            : StatusText;

        UpdateStatus();
        SetSelection(_lastSelectionCount, _lastSelectionSize);

        if (ViewMode == DefaultViewKind.Grid) HintText = Strings.Hint_IconViewUnavailable;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 成功进入某个目录后触发（含刷新）。目录监听、选中恢复都挂在这里，
    /// 避免各处导航入口各写一遍收尾逻辑。
    /// </summary>
    public event Action<string>? NavigationCompleted;

    /// <summary>
    /// 直接暴露 IReadOnlyList 而不批量灌 ObservableCollection：
    /// 万项逐条 Add 会触发 N 次 CollectionChanged，切换目录直接卡住。
    /// </summary>
    public IReadOnlyList<FileEntry> Entries
    {
        get => _entries;
        private set => Set(ref _entries, value);
    }

    /// <summary>侧栏常用位置。显示名是文案，切换语言后要重建（见 RefreshLocalizedText）。</summary>
    public IReadOnlyList<NavItem> QuickAccess
    {
        get => _quickAccess;
        private set => Set(ref _quickAccess, value);
    }

    public IReadOnlyList<BreadcrumbSegment> Breadcrumbs
    {
        get => _breadcrumbs;
        private set => Set(ref _breadcrumbs, value);
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (!Set(ref _currentPath, value)) return;
            Raise(nameof(TitleText));
            Raise(nameof(DisplayName));
            Raise(nameof(TabTooltip));
        }
    }

    /// <summary>
    /// 标签页标题（FR-2.7）：只取当前目录名，不带应用名。
    /// 没有目录时（启动瞬间、标签刚建还没导航）显示应用名，避免标签上是空白。
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(_currentPath)) return Strings.Application_Name;

            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(_currentPath));
            return string.IsNullOrEmpty(name) ? _currentPath : name;
        }
    }

    /// <summary>
    /// 标签页悬浮提示：**整条路径**，不是"上级目录"。
    /// 需求（FR-2.7）只要求同名目录能区分，给全路径是同一种信息且更直接；
    /// 用户真正想确认的往往是"这是 C 盘那份还是 D 盘那份"。
    /// </summary>
    public string TabTooltip => string.IsNullOrEmpty(_currentPath) ? Strings.Application_Name : _currentPath;

    /// <summary>
    /// 会话恢复出"只有路径、还没读盘"的窗格（FR-2.11）。
    /// 非活动标签不枚举目录：十二个标签各读一万项，启动就不是 700ms 的事了。
    /// 激活时由宿主看这个标记决定要不要真的导航。
    /// </summary>
    public bool HasPendingContent { get; private set; }

    /// <summary>只设置路径与标题，不读盘。激活这个标签时才会真正枚举。</summary>
    public void PreparePendingPath(string path)
    {
        CurrentPath = path;
        AddressText = path;
        Breadcrumbs = Breadcrumb.Build(path);
        HasPendingContent = true;
    }

    /// <summary>视图切换出去时要保住的两样东西：滚动位置与选中项。</summary>
    public (int Index, double Offset) ScrollAnchor { get; set; }

    public IReadOnlyList<string> SelectedPaths { get; set; } = [];

    /// <summary>
    /// 窗口标题：显示当前目录名，任务栏与 Alt+Tab 里才分得清开了哪个目录。
    /// 同时它也是自动化验证导航是否生效的观察点（标题会随目录变化）。
    /// </summary>
    public string TitleText
    {
        get
        {
            if (string.IsNullOrEmpty(_currentPath)) return Strings.Application_Name;

            string name = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(_currentPath));

            // 盘符根目录取不到名称，直接显示 "C:\"
            return string.IsNullOrEmpty(name) ? _currentPath : string.Format(Strings.Window_Title, name);
        }
    }

    /// <summary>地址栏编辑态的文本，进入编辑时等于完整路径。</summary>
    public string AddressText
    {
        get => _addressText;
        set => Set(ref _addressText, value);
    }

    /// <summary>true = 显示可编辑地址栏；false = 显示面包屑。</summary>
    public bool IsAddressEditing
    {
        get => _isAddressEditing;
        set => Set(ref _isAddressEditing, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SelectionText
    {
        get => _selectionText;
        private set => Set(ref _selectionText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public DefaultViewKind ViewMode
    {
        get => _viewMode;
        private set
        {
            if (!Set(ref _viewMode, value)) return;
            Raise(nameof(IsDetailsView));
            Raise(nameof(IsListView));
        }
    }

    public bool IsDetailsView => _viewMode == DefaultViewKind.Details;

    public bool IsListView => _viewMode == DefaultViewKind.List;

    public EntrySort Sort
    {
        get => _sort;
        private set
        {
            if (!Set(ref _sort, value)) return;
            Raise(nameof(IsSortByName));
            Raise(nameof(IsSortByDate));
            Raise(nameof(IsSortBySize));
            Raise(nameof(IsSortByType));
            Raise(nameof(SortArrow));
            Raise(nameof(NameHeader));
            Raise(nameof(DateHeader));
            Raise(nameof(TypeHeader));
            Raise(nameof(SizeHeader));
        }
    }

    // 列头文本带方向箭头，只给当前排序列加。放在视图模型里算，
    // 界面就不必为四个列头各写一组 DataTrigger。
    public string NameHeader => HeaderText(Strings.Column_Name, SortFieldKind.Name);

    public string DateHeader => HeaderText(Strings.Column_DateModified, SortFieldKind.Date);

    public string TypeHeader => HeaderText(Strings.Column_Type, SortFieldKind.Type);

    public string SizeHeader => HeaderText(Strings.Column_Size, SortFieldKind.Size);

    private string HeaderText(string label, SortFieldKind field)
        => _sort.Field == field ? $"{label} {SortArrow}" : label;

    public bool IsSortByName => _sort.Field == SortFieldKind.Name;

    public bool IsSortByDate => _sort.Field == SortFieldKind.Date;

    public bool IsSortBySize => _sort.Field == SortFieldKind.Size;

    public bool IsSortByType => _sort.Field == SortFieldKind.Type;

    /// <summary>列头上的方向指示：升序 ▲ / 降序 ▼。</summary>
    public string SortArrow => _sort.Descending ? "▼" : "▲";

    public bool CanGoBack => _history.CanGoBack;

    public bool CanGoForward => _history.CanGoForward;

    /// <summary>最近一次目录枚举耗时，供性能核对。</summary>
    public long LastEnumerateMs { get; private set; }

    /// <summary>
    /// 最近一次导航失败的原因；成功导航或开始新导航时清空。
    /// 界面只在状态栏显示，验收脚本（--perf-probe）也读它判断"是空目录还是打不开"。
    /// </summary>
    public string? LastNavigationError
    {
        get => _lastNavigationError;
        private set
        {
            if (Set(ref _lastNavigationError, value)) Raise(nameof(HasNavigationError));
        }
    }

    /// <summary>列表为空时用来区分"真空目录"与"打不开"，避免空态文案误导用户。</summary>
    public bool HasNavigationError => !string.IsNullOrEmpty(_lastNavigationError);

    /// <summary>当前目录条目总数（未过滤），用于状态栏与测试。</summary>
    public int TotalCount => _all.Count;

    public async Task NavigateAsync(string path, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string target = path.Trim().Trim('"');
        int token = ++_navigationToken;

        // 已经为首屏读过盘，pending 的使命结束（无论成败）
        HasPendingContent = false;

        IsLoading = true;
        StatusText = Strings.Status_Loading;

        long started = Stopwatch.GetTimestamp();
        var result = await Task.Run(() =>
        {
            bool ok = DirectoryEnumerator.TryRead(target, _sort, out var list, out var error);
            return (Ok: ok, List: list, Error: error);
        }).ConfigureAwait(true);

        // 期间用户又导航了一次：丢弃本次结果，否则会把新目录覆盖成旧目录
        if (token != _navigationToken) return;

        LastEnumerateMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (result.Ok)
        {
            LastNavigationError = null;
            _all = result.List;
            if (recordHistory) _history.Push(target);

            CurrentPath = target;
            AddressText = target;
            Breadcrumbs = Breadcrumb.Build(target);
            SelectionText = string.Empty;
            ApplyFilter();
            Raise(nameof(CanGoBack));
            Raise(nameof(CanGoForward));

            // 监听与选中恢复挂在这里：导航/刷新走同一条收尾路径
            NavigationCompleted?.Invoke(target);
        }
        else
        {
            // 原始消息保留给日志与诊断（探针读它判断是否失败），
            // 界面只显示人话——.NET 的异常文本是英文且带路径，直接贴出来不合格
            // （docs/logging.md §6：用户提示不得出现堆栈、错误码、英文异常消息）。
            LastNavigationError = result.Error ?? Strings.Error_Unknown;

            string code = MapFsErrorCode(result.Error);
            _lastNavigationErrorCode = code;
            Diagnostics.AppLog.Warn("nav.open", code, $"{target} · {result.Error}");

            _all = [];
            Entries = [];
            CurrentPath = target;
            AddressText = target;
            Breadcrumbs = Breadcrumb.Build(target);
            StatusText = string.Format(Strings.Error_CannotOpen, DescribeNavigationError(code));
        }

        IsLoading = false;
    }

    /// <summary>错误码 → 用户可读原因。文案要能被 i18n 抽走，不拼接原始异常文本。</summary>
    private static string DescribeNavigationError(string code) => FsErrors.DescribeForNavigation(code);

    /// <summary>
    /// 把枚举异常文本映射成 logging.md §4 的错误码。认不出来时归到 E1001（路径不可用），
    /// 不为了"看起来精确"而硬猜一个更细的码。
    /// </summary>
    private static string MapFsErrorCode(string? message) => FsErrors.CodeOf(message);

    /// <summary>刷新当前目录。不产生新的历史记录，否则连按 F5 会堆满后退栈。</summary>
    public Task RefreshAsync()
        => string.IsNullOrEmpty(CurrentPath) ? Task.CompletedTask : NavigateAsync(CurrentPath, recordHistory: false);

    public Task<bool> GoBackAsync() => GoAsync(back: true);

    public Task<bool> GoForwardAsync() => GoAsync(back: false);

    private async Task<bool> GoAsync(bool back)
    {
        bool moved = back ? _history.TryGoBack(out string path) : _history.TryGoForward(out path);
        if (!moved) return false;

        // 历史栈已经在 TryGoXxx 内移动过，导航时不能再 Push 一次
        await NavigateAsync(path, recordHistory: false).ConfigureAwait(true);
        Raise(nameof(CanGoBack));
        Raise(nameof(CanGoForward));
        return true;
    }

    /// <summary>点击列头：同一列切换升降序，换列改为升序。排序在内存中完成，不重新枚举磁盘。</summary>
    public void SortBy(SortFieldKind field)
    {
        Sort = _sort.Toggle(field);
        ResortInMemory();
    }

    /// <summary>
    /// 切换视图模式。仅本次会话有效，不覆盖设置里的"默认视图"。
    /// M1 只提供"详细信息"与"列表"两种：两者都能走 VirtualizingStackPanel，
    /// 大图标/磁贴需要虚拟化 WrapPanel，否则万项目录会创建一万个容器，排到批 3 补。
    /// </summary>
    /// <returns>false 表示该视图暂不可用，调用方应保持原视图。</returns>
    public bool SetViewMode(DefaultViewKind mode)
    {
        if (mode == _viewMode) return true;

        if (mode == DefaultViewKind.Grid)
        {
            HintText = Strings.Hint_IconViewUnavailable;
            return false;
        }

        HintText = string.Empty;
        ViewMode = mode;
        return true;
    }

    /// <summary>"显示扩展名"开关：逐条通知绑定，不重新枚举磁盘。</summary>
    public void ApplyShowExtensions(bool show) => FileEntry.SetShowExtensions(_all, show);

    public string HintText
    {
        get => _hintText;
        private set => Set(ref _hintText, value);
    }

    /// <summary>给一次性提示（路径不存在、视图不可用等）用，下一次导航时清空。</summary>
    public void SetHint(string message) => HintText = message;

    /// <summary>"显示隐藏文件"开关：只在内存过滤，不重新枚举。</summary>
    public void ApplyFilter()
    {
        if (_settings.Behavior.ShowHidden)
        {
            Entries = _all;
        }
        else
        {
            var visible = new List<FileEntry>(_all.Count);
            foreach (var entry in _all)
            {
                if (!entry.IsHidden) visible.Add(entry);
            }

            Entries = visible;
        }

        UpdateStatus();
    }

    /// <summary>显示隐藏文件之后，需要重算状态栏的条目数。</summary>
    public void UpdateStatus()
    {
        int visible = _entries.Count;
        StatusText = _all.Count == visible
            ? string.Format(Strings.Status_Summary, visible, LastEnumerateMs)
            : string.Format(Strings.Status_SummaryHidden, visible, _all.Count - visible, LastEnumerateMs);
    }

    public void SetSelection(int count, long totalSize)
    {
        _lastSelectionCount = count;
        _lastSelectionSize = totalSize;

        SelectionText = count switch
        {
            0 => string.Empty,
            1 => Strings.Status_SelectedOne,
            _ => totalSize > 0
                ? string.Format(Strings.Status_SelectedWithSize, count, FileEntry.FormatSizeText(totalSize))
                : string.Format(Strings.Status_SelectedCount, count),
        };
    }

    /// <summary>当前选中项。命令的可执行性判断与文件操作都用它，避免各处再从 ListBox 里捞。</summary>
    public IReadOnlyList<FileEntry> SelectedEntries { get; private set; } = [];

    public void SetSelectedEntries(IReadOnlyList<FileEntry> entries) => SelectedEntries = entries;

    /// <summary>按完整路径取当前列表里的条目，用于刷新后恢复选中。</summary>
    public FileEntry? FindByPath(string fullPath)
    {
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return entry;
        }

        return null;
    }

    /// <summary>状态栏提示。文件操作的结果文本走这里，不走 HintText（HintText 会被下次导航清空）。</summary>
    public void SetStatus(string text) => StatusText = text;

    private void ResortInMemory()
    {
        if (_all.Count == 0)
        {
            UpdateStatus();
            return;
        }

        _all.Sort(_sort.ToComparison());
        ApplyFilter();
    }

    /// <summary>把异常转成可展示的状态文本，不让异常冒泡到 UI 线程。</summary>
    public void ReportError(Exception ex)
    {
        IsLoading = false;

        // 异常原文跟随系统语言，不能进界面（见 FsErrors 的说明），只写日志
        string code = FsErrors.CodeOf(ex);
        Diagnostics.AppLog.Warn("ops.report", code, $"{ex.GetType().Name}: {ex.Message}");
        StatusText = string.Format(Strings.Status_OperationFailed, FsErrors.DescribeForOperation(code));
    }

    private static List<NavItem> BuildQuickAccess()
    {
        var list = new List<NavItem>(7);
        foreach (var folder in KnownFolders.All)
        {
            var (name, glyph) = folder.Kind switch
            {
                KnownFolderKind.Desktop => (Strings.Nav_Desktop, IconGlyphs.Desktop),
                KnownFolderKind.Documents => (Strings.Nav_Documents, IconGlyphs.Documents),
                KnownFolderKind.Downloads => (Strings.Nav_Downloads, IconGlyphs.Download),
                KnownFolderKind.Pictures => (Strings.Nav_Pictures, IconGlyphs.Pictures),
                KnownFolderKind.Music => (Strings.Nav_Music, IconGlyphs.Music),
                KnownFolderKind.Videos => (Strings.Nav_Videos, IconGlyphs.Video),
                _ => (Strings.Nav_ThisPc, IconGlyphs.Account),
            };

            list.Add(new NavItem(name, glyph, folder.Path));
        }

        return list;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
