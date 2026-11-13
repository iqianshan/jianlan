using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Jianlan.App.Shell;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Localization;
using Jianlan.Core.Navigation;
using Jianlan.Core.Settings;
using Jianlan.Core.Tagging;

namespace Jianlan.App.ViewModels;

/// <summary>
/// 导航栏系统节点的种类。它决定点击语义，不只是图标：
/// <see cref="Home"/> 切成应用内主页视图，<see cref="Folder"/> 导航到路径，
/// <see cref="RecycleBin"/> / <see cref="Network"/> 是 shell 命名空间——
/// 它们没有文件系统路径，本程序的列表/标签/预览都按路径工作，所以交给资源管理器打开（FR-1.2.3）。
/// </summary>
public enum NavItemKind
{
    Home,
    Folder,
    RecycleBin,
    Network,

    /// <summary>
    /// 此电脑（FR-1.2.2）。它没有自己的路径，是一个**可展开的容器**：
    /// 展开后列出全部驱动器，点击行本身只折叠/展开，不导航。
    /// 与 <see cref="RecycleBin"/> / <see cref="Network"/> 的区别是它不下沉给资源管理器——
    /// 驱动器就是普通根目录，本程序的列表、标签、预览都能直接用。
    /// </summary>
    ThisPc,
}

/// <summary>
/// 左侧导航项。显示名与字形在 UI 层映射，Core 只给路径。
/// <see cref="Kind"/> 为 <see cref="NavItemKind.Home"/> 的那一项是主页入口（FR-1.2.1）：
/// 它没有路径，点击的语义是"把当前标签切到主页"，不是"导航到某个目录"。
/// </summary>
/// <param name="PlaceKey">
/// 「位置」分区里这一项的稳定键（<see cref="NavPlaces"/>），供"隐藏指定位置"使用（FR-1.2.9）。
/// 主页与将来其它不该被隐藏的节点留空。<see cref="NavItemKind.Folder"/> 一类的节点
/// 光看 Kind 分不出"桌面"和"下载"，隐藏清单只能靠这个键对上号。
/// </param>
public sealed record NavItem(
    string Name,
    string Glyph,
    string Path,
    NavItemKind Kind = NavItemKind.Folder,
    string? Tooltip = null,
    string? PlaceKey = null) : INavRow
{
    public bool IsHome => Kind == NavItemKind.Home;

    /// <summary>系统命名空间（回收站 / 网络）：点击由资源管理器接管，本窗口不列目录。</summary>
    public bool IsShellNamespace => Kind is NavItemKind.RecycleBin or NavItemKind.Network;

    /// <summary>可展开的容器节点（目前只有此电脑）：点击语义是折叠/展开，不是导航。</summary>
    public bool IsExpandable => Kind == NavItemKind.ThisPc;
}

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

    /// <summary>这一份视图模型属于哪一侧窗格（FR-3.7）。决定视图/排序的记忆写到哪里。</summary>
    private readonly PaneSlot _slot;

    /// <summary>
    /// 成功进入一个目录后的回调（FR-1.1.2 最近访问）。用委托注入而不是直接读
    /// <c>App.Settings</c>：视图模型在当前代码里不依赖 App 静态类，测试也能单独构造。
    /// </summary>
    private readonly Action<string>? _onVisited;

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

    /// <summary>
    /// 文件夹大小扫描（FR-6.16）。整个窗格同时只允许一个：
    /// 用户连点两个文件夹时，旧任务取消、旧目标复位，否则它们会永远停在"计算中…"。
    /// </summary>
    private CancellationTokenSource? _sizeScanCts;
    private List<FileEntry> _sizeScanTargets = [];
    private bool _isCalculatingSize;
    private string _sizeProgressText = string.Empty;

    /// <summary>导航序号：用于丢弃过期的异步枚举结果（快速连按时防止旧目录覆盖新目录）。</summary>
    private int _navigationToken;

    // ── 当前目录搜索（FR-6.13） ─────────────────────────────────────────────
    //
    // 搜索只是"对已枚举条目的再过滤"，不读盘、不递归：见 EntryFilter 的说明。
    // 两个数字分开存——列表里最多 1000 项，状态栏要说的是命中总数。

    /// <summary>输入防抖时长。250ms 是"打字停顿时才过滤"与"感觉不到延迟"的交点。</summary>
    private const int SearchDebounceMs = 250;

    private string _searchQuery = string.Empty;
    private int _searchMatched;
    private bool _searchTruncated;

    /// <summary>
    /// 过滤序号：搜索在后台线程跑，回来时用它判断"这批结果还算不算数"。
    /// 用户在 250ms 内连打两个字，第一次的结果绝不能覆盖第二次的。
    /// </summary>
    private int _filterGeneration;

    /// <summary>
    /// 防抖计时器。只取消、不释放：`Task.Delay` 在取消后会自行摘掉注册，
    /// 而提前 Dispose 一个"可能还有等待者"的 CTS 会让那一轮要么抛 ObjectDisposedException、
    /// 要么永远等不到完成。这类 CTS 没有原生句柄，交给 GC 即可。
    /// </summary>
    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>
    /// 彩色标签库（FR-4）。可空：没有它就只是"这个窗格不显示标签"，
    /// 浏览功能一律照常——标签是装饰，不该成为打开目录的前提。
    /// </summary>
    private readonly TagStore? _tags;

    /// <summary>当前目录所在卷的序列号。标签键是 <c>(卷, FileId)</c>，一半在这里。</summary>
    private ulong _volumeSerial;

    public PaneViewModel(AppSettings settings, Action<string>? onVisited = null, TagStore? tags = null,
        PaneSlot slot = PaneSlot.Primary)
    {
        _settings = settings;
        _onVisited = onVisited;
        _tags = tags;
        _slot = slot;

        // 视图与排序的初值按"这一侧上次用过什么"取，没记过才用设置里的默认值（FR-3.7）。
        // 取舍：一旦这一侧自己选过，就由它自己说了算——否则用户在分屏里挑了列表视图，
        // 下次启动又被"默认视图"改回详细信息，等于白挑。
        PaneViewStateSettings state = settings.Layout.GetPaneState(slot);

        DefaultViewKind view = state.View ?? settings.Behavior.DefaultView;
        SortFieldKind sort = state.SortBy ?? settings.Behavior.SortBy;
        bool descending = state.SortDescending ?? settings.Behavior.SortDescending;

        // schema 里允许 grid，但 M1 未实现该视图（见 SetViewMode），启动时回落到列表
        _viewMode = view == DefaultViewKind.Grid ? DefaultViewKind.List : view;

        _sort = new EntrySort(sort, descending, settings.Behavior.FoldersFirst,
            settings.Behavior.NaturalSort, settings.Behavior.CaseSensitiveSort);

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
        // 会话文件里可能是旧版本写下的写法（D:/x）：这里先规范一次，
        // 标签标题与悬浮提示才不会和激活后导航到的路径各写一种。
        string normalized = PathRules.Normalize(path);
        if (normalized.Length == 0) return;

        CurrentPath = normalized;
        AddressText = normalized;
        Breadcrumbs = Breadcrumb.Build(normalized);
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

    /// <param name="recordHistory">是否压入本窗格的后退栈。刷新与前进/后退重导航不压。</param>
    /// <param name="recordVisit">
    /// 是否写进"最近访问"（FR-1.1.2）。与 <paramref name="recordHistory"/> 分开：
    /// 按 F5 刷新不算"去过一个新地方"，但 Alt+← 回到刚才的目录算。
    /// </param>
    public async Task NavigateAsync(string path, bool recordHistory = true, bool recordVisit = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // 入口统一规范化：地址栏手输的 D:/work、D:\\work\\、D:\work\.\sub 都能打开，
        // 但写进当前路径后会被面包屑、后退栈、最近访问、标签、复制路径各自带走一份。
        // 放在这里而不是地址栏，是为了让所有入口（侧栏、双击、拖放、命令行、会话恢复）
        // 共用同一条规则，不依赖每个调用方自己记得整理。
        string target = PathRules.Normalize(path);
        if (target.Length == 0) return;

        int token = ++_navigationToken;

        // 换目录时搜索失效：搜索的范围是"这个目录里的这些条目"，换个目录还留着词
        // 只会得到一个莫名其妙的空列表。同一目录的刷新（F5、文件操作后）则保留，
        // 否则"删除搜到的一项之后搜索框自己空了"，用户还得把词重打一遍。
        if (!string.Equals(target, _currentPath, StringComparison.OrdinalIgnoreCase)) ResetSearch();

        // 已经为首屏读过盘，pending 的使命结束（无论成败）
        HasPendingContent = false;

        // 换目录就把扫描停掉：结果只对当前目录的行有意义，留着跑只是白占 IO
        CancelFolderSizeScan();

        IsLoading = true;
        StatusText = Strings.Status_Loading;

        long started = Stopwatch.GetTimestamp();
        bool withFileIds = _tags is { IsLoaded: true, Count: > 0 };
        Task<(bool Ok, List<FileEntry> List, string? Error, ulong Volume)> read =
            Task.Run(() => ReadDirectory(target, withFileIds));

        // 慢位置（网络 / 可移动设备）给 3 秒等待上限（S-04）。内核在 SMB 重试时是
        // 阻塞在 Win32 调用里的，取消不掉——只能"不再等它"，把结果丢掉。
        // 判据与等待都放在 Core（SlowLocations），那边能单测；这里只按结论走。
        if (SlowLocations.NeedsTimeout(target) &&
            !await SlowLocations.CompletedWithinAsync(read, SlowLocations.Timeout).ConfigureAwait(true))
        {
            // 票号自增：晚到的那次枚举回来时 token 已经对不上，会被下面的判断丢掉。
            // 不这么做的话，20 秒后界面会突然从"无法访问"跳到一屏文件，像自己动了。
            _navigationToken++;
            IsLoading = false;

            _all = [];
            _volumeSerial = 0;
            Entries = [];
            CurrentPath = target;
            AddressText = target;
            Breadcrumbs = Breadcrumb.Build(target);
            LastNavigationError = Strings.Error_Timeout;
            _lastNavigationErrorCode = FsErrors.Timeout;
            StatusText = Strings.Status_AccessTimeout;

            Diagnostics.AppLog.Warn(
                "nav.timeout", FsErrors.Timeout,
                $"{target} · {SlowLocations.Timeout.TotalSeconds:0}s 无响应");  // i18n-ignore：只进 app.log
            return;
        }

        var result = await read.ConfigureAwait(true);

        // 期间用户又导航了一次：丢弃本次结果，否则会把新目录覆盖成旧目录
        if (token != _navigationToken) return;

        LastEnumerateMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (result.Ok)
        {
            LastNavigationError = null;
            _all = result.List;
            _volumeSerial = result.Volume;
            ApplyTagsToEntries();
            if (recordHistory) _history.Push(target);
            if (recordVisit) _onVisited?.Invoke(target);

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
            _volumeSerial = 0;
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

    // ── 彩色标签（FR-4） ────────────────────────────────────────────────────

    /// <summary>
    /// 读一个目录。<paramref name="withFileIds"/> 为真时用 FileId 枚举（标签库非空时才走）：
    /// 一次调用同时给出 FileId，不必为"查标签"再枚举第二遍。
    ///
    /// 失败一律回落普通枚举——卷不支持、网络盘超时、无权限目录都会走到这里，
    /// 结果是"这个目录的标签不显示"，而不是"打不开这个目录"。
    /// </summary>
    private (bool Ok, List<FileEntry> List, string? Error, ulong Volume) ReadDirectory(string path, bool withFileIds)
    {
        if (withFileIds)
        {
            if (DirectoryEnumerator.TryReadWithFileIds(path, _sort, out List<FileEntry> tagged, out ulong volume,
                    out _))
            {
                return (true, tagged, null, volume);
            }

            Diagnostics.AppLog.Warn("tags.enum", "E3006", $"{path} 取不到 FileId，本次回落普通枚举");  // i18n-ignore：只进 app.log
        }

        bool ok = DirectoryEnumerator.TryRead(path, _sort, out List<FileEntry> list, out string? error);
        return (ok, list, error, 0);
    }

    /// <summary>
    /// 把标签库里的颜色写进当前条目（FR-4.2.4）。可视区几十项、整目录上万项都是一次哈希查找，
    /// 单次导航的查询耗时在 1 ms 量级，不需要"只查可视区"这种优化。
    /// </summary>
    private void ApplyTagsToEntries()
    {
        if (_tags is not { IsLoaded: true } tags || _volumeSerial == 0) return;

        foreach (FileEntry entry in _all)
        {
            if (entry.FileId == 0) continue;
            entry.SetTags(tags.Get(_volumeSerial, entry.FileId));
        }
    }

    /// <summary>
    /// 标签库刚就绪时补一次标记（FR-4.2.5）。列表先渲染、库后到位，这一步把两者接上。
    ///
    /// 需要重新取 FileId：库未就绪时那次导航走的是快枚举，条目身上没有 id。
    /// 只补 id 不重建列表——重建会顺手丢掉文件夹大小结果与滚动位置。
    /// </summary>
    public async Task RefreshTagsAsync()
    {
        if (_tags is not { IsLoaded: true } tags || string.IsNullOrEmpty(_currentPath)) return;
        if (_all.Count == 0) return;

        string path = _currentPath;

        if (_volumeSerial != 0 && _all.All(e => e.FileId != 0))
        {
            ApplyTagsToEntries();
            return;
        }

        var scan = await Task.Run(() =>
        {
            bool ok = FileIdEnumerator.TryRead(path, out List<FileIdEntry> raw, out ulong volume, out _);
            return (Ok: ok, Raw: raw, Volume: volume);
        }).ConfigureAwait(true);

        // 期间切走了：这批 id 属于另一个目录，套上去会把标签画到错误的行上
        if (!scan.Ok || !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)) return;

        _volumeSerial = scan.Volume;

        var byName = new Dictionary<string, ulong>(scan.Raw.Count, StringComparer.OrdinalIgnoreCase);
        foreach (FileIdEntry entry in scan.Raw) byName[entry.Name] = entry.FileId;

        foreach (FileEntry entry in _all)
        {
            if (entry.FileId == 0 && byName.TryGetValue(entry.Name, out ulong id)) entry.SetFileId(id);
            if (entry.FileId != 0) entry.SetTags(tags.Get(_volumeSerial, entry.FileId));
        }
    }

    /// <summary>
    /// 当前选中项的颜色并集（FR-4.1.2）。右键菜单用它把"已经打上的颜色"勾上：
    /// 选中多个文件、它们的颜色各不相同时，勾选表示"这一色至少有一项有"，
    /// 点一下就是"给所有选中项统一加上/去掉这一色"。
    /// </summary>
    public TagColors SelectionTagMask
    {
        get
        {
            TagColors mask = TagColors.None;
            foreach (FileEntry entry in SelectedEntries) mask |= entry.Tags;
            return mask;
        }
    }

    /// <summary>
    /// 切换某一色（FR-4.1.2）：选中项**全都有**这一色 → 去掉；否则 → 加上。
    ///
    /// 为什么不是"设成这一色"：多色标签的语义是"一项可以同时是红和蓝"，
    /// 设成单色等于每点一次就把上一色抹掉，用户永远攒不出两个颜色。
    /// 要"只留一色"的场合有「移除标签」先清空，再点目标色，两步完成，不必为它留第三种语义。
    /// </summary>
    public Task ToggleTagAsync(TagColors color) => ApplyTagAsync(color, toggle: true);

    /// <summary>
    /// 给选中项设置（或移除）颜色标签（FR-4.1.1 / FR-4.1.3）。<paramref name="colors"/> 为
    /// <see cref="TagColors.None"/> 即移除。
    ///
    /// 取 FileId 的那一段放后台线程：库里为空时条目身上没有 id，得对每个选中项开一次句柄。
    /// 选中几百个文件时这一步是几十毫秒，放 UI 线程会让右键菜单关得一顿。
    /// </summary>
    public Task ApplyTagAsync(TagColors colors) => ApplyTagAsync(colors, toggle: false);

    private async Task ApplyTagAsync(TagColors colors, bool toggle)
    {
        if (_tags is null) return;

        List<FileEntry> targets = [.. SelectedEntries];
        if (targets.Count == 0) return;

        _tags.EnsureLoaded();

        ulong paneVolume = _volumeSerial;
        List<(FileEntry Entry, TagTarget Target)> resolved =
            await Task.Run(() => ResolveTargets(targets, paneVolume)).ConfigureAwait(true);

        if (resolved.Count == 0)
        {
            // 一个都没解析出 FileId：这卷不支持（exFAT / 部分网络盘），说清楚而不是静默失败
            SetStatus(Strings.Status_Tag_Unsupported);
            return;
        }

        TagSetResult result;

        if (toggle)
        {
            // 每一项算自己的结果颜色：选中项原本的颜色可能不一样，统一"设成 X"会把
            // 别人身上的其它颜色抹掉。按结果颜色分组写，组数通常就是 1–2 组。
            var groups = resolved
                .GroupBy(r => (r.Entry.Tags & colors) != 0
                    ? r.Entry.Tags & ~colors
                    : r.Entry.Tags | colors)
                .ToList();

            result = TagSetResult.Applied;
            foreach (var group in groups)
            {
                TagColors next = group.Key;
                TagSetResult one = next == TagColors.None
                    ? (_tags.RemoveMany([.. group.Select(r => r.Target)]) > 0 ? TagSetResult.Applied : TagSetResult.Invalid)
                    : _tags.SetMany([.. group.Select(r => r.Target)], next);

                if (one != TagSetResult.Applied) result = one;
                foreach ((FileEntry entry, _) in group) entry.SetTags(next);
            }

            // RemoveMany 一组都没删到会把结果写成 Invalid，但"本来就没有、切一下还是没变"不该报错
            if (result == TagSetResult.Invalid) result = TagSetResult.Applied;
        }
        else
        {
            result = colors == TagColors.None
                ? (_tags.RemoveMany([.. resolved.Select(r => r.Target)]) > 0 ? TagSetResult.Applied : TagSetResult.Invalid)
                : _tags.SetMany([.. resolved.Select(r => r.Target)], colors);

            // 属性通知必须在 UI 线程发，所以 id 与颜色在这一侧写回
            foreach ((FileEntry entry, TagTarget target) in resolved)
            {
                entry.SetTags(colors);
            }
        }

        foreach ((FileEntry entry, TagTarget target) in resolved)
        {
            entry.SetFileId(target.FileId);
            if (_volumeSerial == 0 && target.Volume != 0) _volumeSerial = target.Volume;
        }

        ReportTagResult(result, resolved.Count, colors);
    }

    /// <summary>
    /// 给选中项补齐 <c>(卷, FileId, 路径)</c>。条目自己带着 id 时直接用，
    /// 否则单独查一次——只有"用户明确点了这一项"才会走到这里，一次系统调用换一次正确。
    /// </summary>
    private static List<(FileEntry Entry, TagTarget Target)> ResolveTargets(List<FileEntry> targets, ulong paneVolume)
    {
        var list = new List<(FileEntry, TagTarget)>(targets.Count);

        foreach (FileEntry entry in targets)
        {
            if (entry.FileId != 0)
            {
                list.Add((entry, new TagTarget(paneVolume, entry.FileId, entry.FullPath)));
                continue;
            }

            if (FileIdEnumerator.TryIdentify(entry.FullPath, out ulong volume, out ulong fileId))
            {
                list.Add((entry, new TagTarget(volume, fileId, entry.FullPath)));
            }
        }

        return list;
    }

    private void ReportTagResult(TagSetResult result, int count, TagColors colors)
    {
        switch (result)
        {
            case TagSetResult.Applied:
                SetStatus(colors == TagColors.None
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Status_Tag_Removed, count)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Status_Tag_Applied, count));
                break;

            case TagSetResult.NoFileId:
                // 明确告知而不是静默失败：exFAT 的 U 盘、部分网络盘拿不到稳定标识
                SetStatus(Strings.Status_Tag_Unsupported);
                break;

            case TagSetResult.Full:
                SetStatus(string.Format(
                    CultureInfo.CurrentCulture, Strings.Status_Tag_Full, TagStore.MaxRecords));
                break;
        }
    }

    /// <summary>
    /// 把枚举异常文本映射成 logging.md §4 的错误码。认不出来时归到 E1001（路径不可用），
    /// 不为了"看起来精确"而硬猜一个更细的码。
    /// </summary>
    private static string MapFsErrorCode(string? message) => FsErrors.CodeOf(message);

    /// <summary>刷新当前目录。不产生新的历史记录，否则连按 F5 会堆满后退栈。</summary>
    public Task RefreshAsync()
        => string.IsNullOrEmpty(CurrentPath)
            ? Task.CompletedTask
            : NavigateAsync(CurrentPath, recordHistory: false, recordVisit: false);

    /// <summary>
    /// 释放目录内容，只留路径、历史与视图状态（内存预算配套约束，见 performance-gate §9.5）。
    ///
    /// 非活动标签调用它。一个 1 万项目录约 3 MB，12 个标签就是 36 MB，而这些条目在切回来
    /// 之前没有任何人看。释放后 <see cref="HasPendingContent"/> 置位，激活时宿主走
    /// <c>LoadPanesAsync</c> 重新读盘——与"会话恢复的半开标签"共用同一条加载路径，
    /// 不为它另写一套。
    ///
    /// 保留：路径、面包屑、导航栈、滚动锚点、选中路径（全是字符串，合计几 KB）。
    /// 丢弃：原始枚举结果与可见列表。
    ///
    /// 代价：切回来要重新枚举一次。万项目录实测 64 ms 枚举 + 218 ms 首屏（§9.2），
    /// 期间列表显示"加载中"。换来的是"标签开得再多也不按标签数线性涨内存"。
    /// </summary>
    public void ReleaseContent()
    {
        if (_all.Count == 0 && _entries.Count == 0) return;

        // 正在跑的枚举必须作废：它回来时直接给 _all 赋值，会把刚释放的内容又装回去。
        // 少了这一步，释放会呈现"切一次省了、多切两次又回来了"的假象。
        _navigationToken++;

        // 在途的搜索过滤同理：它持有释放前的快照，回来会把结果装进一个已经空掉的窗格
        _filterGeneration++;

        CancelFolderSizeScan();
        _sizeScanTargets = [];
        IsCalculatingSize = false;
        SizeProgressText = string.Empty;

        _all = [];
        Entries = [];
        SelectedEntries = [];

        // 状态栏文字保持原样：切回来会立刻显示"加载中"，
        // 中间那一帧显示"0 项"只会让人以为目录被清空了。
        HasPendingContent = true;
    }

    /// <summary>
    /// 把窗格切到"主页形态"（FR-1.2.1 的"主页"节点）：丢掉目录内容与当前路径，
    /// 但**保留历史栈**——Alt+← 应当回到离开主页前看的那一处。
    ///
    /// 与 <see cref="ReleaseContent"/> 的区别：那个留路径、等下次激活重新读盘；
    /// 这个连路径都不要，因为主页不位于任何目录。共同点是都要先作废在途枚举，
    /// 否则它回来时会把内容装回一个已经"不是目录"的窗格。
    /// </summary>
    public void DetachToHome()
    {
        _navigationToken++;
        CancelFolderSizeScan();
        _sizeScanTargets = [];
        IsCalculatingSize = false;
        SizeProgressText = string.Empty;

        // 主页没有"当前目录"，搜索框再留着一个词就是死状态
        ResetSearch();

        _all = [];
        Entries = [];
        HasPendingContent = false;

        LastNavigationError = null;
        _lastNavigationErrorCode = "E1001";
        CurrentPath = string.Empty;
        AddressText = string.Empty;
        Breadcrumbs = [];
        SelectionText = string.Empty;
        StatusText = Strings.Status_Ready;
        IsLoading = false;
        Raise(nameof(CanGoBack));
        Raise(nameof(CanGoForward));
    }

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
        PersistViewState();
    }

    /// <summary>
    /// 设置页改了排序口径（FR-5.2.3 的文件夹优先 / 自然排序 / 区分大小写）：就地重排，不等下次启动。
    /// 只动"怎么比"，排序列与升降序保持用户当前的选择——在设置页点一个勾选框，
    /// 顺手把用户刚点过的列头反向，是没人想要的结果。
    /// </summary>
    public void ApplySortOptions(bool foldersFirst, bool natural, bool caseSensitive)
    {
        EntrySort next = _sort
            .WithNameOptions(natural, caseSensitive)
            with { FoldersFirst = foldersFirst };

        if (next == _sort) return;

        Sort = next;
        ResortInMemory();
    }

    /// <summary>
    /// 把这一侧当前的视图与排序记回设置（FR-3.7）。只改内存里的设置对象，
    /// 落盘由界面层统一做（<c>PaneView</c> 里那几处 <c>App.SaveSettings()</c>）：
    /// 视图模型不碰文件，也不该知道"现在是不是该写盘"。
    /// </summary>
    private void PersistViewState()
    {
        PaneViewStateSettings state = _settings.Layout.GetPaneState(_slot);
        state.View = _viewMode;
        state.SortBy = _sort.Field;
        state.SortDescending = _sort.Descending;
    }

    /// <summary>
    /// 切换视图模式。记忆在本侧窗格的状态里（FR-3.7），不覆盖设置页的"默认视图"——
    /// 默认值管的是"两侧都还没选过的时候"，用户在这里点过之后就由这一侧说了算。
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
        PersistViewState();
        return true;
    }

    /// <summary>"显示扩展名"开关：逐条通知绑定，不重新枚举磁盘。</summary>
    public void ApplyShowExtensions(bool show) => FileEntry.SetShowExtensions(_all, show);

    public string HintText
    {
        get => _hintText;
        private set => Set(ref _hintText, value);
    }

    // ── 当前目录搜索（FR-6.13） ─────────────────────────────────────────────

    /// <summary>
    /// 搜索框文本。界面双向绑定它（<c>UpdateSourceTrigger=PropertyChanged</c>）。
    ///
    /// 写入即防抖 250ms 后过滤，但**清空是同步生效的**：用户删掉最后一个字符或按 Esc，
    /// 期望的是"马上看到完整列表"，再等 250ms 会让人以为没反应。
    /// </summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            string next = value ?? string.Empty;
            if (!Set(ref _searchQuery, next)) return;

            Raise(nameof(IsSearching));

            if (next.Length == 0)
            {
                _searchDebounceCts?.Cancel();
                ApplyFilter();
                return;
            }

            ScheduleSearch();
        }
    }

    /// <summary>是否处于搜索态。空态提示、清除按钮、Esc 行为都看它。</summary>
    public bool IsSearching => _searchQuery.Length > 0;

    /// <summary>清空搜索并立即恢复完整列表。Esc 与"清除"按钮共用。</summary>
    public void ClearSearch() => SearchQuery = string.Empty;

    /// <summary>只清状态、不重算列表。换目录时用——紧接着的导航会自己 ApplyFilter。</summary>
    private void ResetSearch()
    {
        _searchDebounceCts?.Cancel();
        if (_searchQuery.Length == 0) return;

        _searchQuery = string.Empty;
        Raise(nameof(SearchQuery));
        Raise(nameof(IsSearching));
    }

    private void ScheduleSearch()
    {
        _searchDebounceCts?.Cancel();

        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = DebouncedSearchAsync(cts);
    }

    private async Task DebouncedSearchAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SearchDebounceMs, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 用户又敲了一个字：这一轮不必过滤，等最后一次输入
            return;
        }

        // 被取消过但异常还没轮到执行时，新一轮可能已经在跑；以字段里的那一份为准
        if (!ReferenceEquals(_searchDebounceCts, cts)) return;

        ApplyFilter();
    }

    /// <summary>"显示隐藏文件 / 系统文件"开关与搜索词一起生效，只在内存过滤，不重新枚举。</summary>
    public void ApplyFilter()
    {
        bool showHidden = _settings.Behavior.ShowHidden;
        bool showSystem = _settings.Behavior.ShowSystem;
        string query = _searchQuery;

        if (query.Length == 0)
        {
            // 作废在途的搜索过滤：它回来时不能把完整列表又换回上一次的结果集
            _filterGeneration++;
            _searchMatched = 0;
            _searchTruncated = false;

            if (showHidden && showSystem)
            {
                // 零拷贝：万项目录下这一条省掉一次整表复制，是最常见的路径
                Entries = _all;
            }
            else
            {
                var visible = new List<FileEntry>(_all.Count);
                foreach (var entry in _all)
                {
                    // 两个属性各判一次：带 H+S 的条目（desktop.ini）要两个开关都开才出现，
                    // 不能写成"任一位被允许就显示"。
                    if (!EntryFilter.IsVisible(entry, showHidden, showSystem)) continue;
                    visible.Add(entry);
                }

                Entries = visible;
            }

            UpdateStatus();
            return;
        }

        BeginSearchFilter(showHidden, showSystem, query);
    }

    /// <summary>
    /// 搜索过滤放后台线程（需求写明）。这里先复制一份条目引用的快照：
    /// 遍历期间 UI 线程可能因为排序原地 Sort(_all)，或在导航时整个换掉它，
    /// 直接传 _all 会在万项目录排序时抛"集合被修改"，而那种异常出现在后台任务里
    /// 只会变成一次静默的空白列表。复制一万个引用不到 0.1ms。
    /// </summary>
    private void BeginSearchFilter(bool showHidden, bool showSystem, string query)
    {
        int generation = ++_filterGeneration;
        FileEntry[] snapshot = _all.ToArray();
        _ = SearchFilterAsync(generation, snapshot, showHidden, showSystem, query);
    }

    private async Task SearchFilterAsync(
        int generation, FileEntry[] snapshot, bool showHidden, bool showSystem, string query)
    {
        (List<FileEntry> matched, int total) = await Task.Run(() =>
        {
            var list = new List<FileEntry>(Math.Min(snapshot.Length, EntryFilter.MaxSearchResults));
            int count = EntryFilter.Fill(
                snapshot, showHidden, showSystem, query, list, EntryFilter.MaxSearchResults);
            return (list, count);
        }).ConfigureAwait(true);

        // 期间用户又改了词、换了目录或换了排序：这批结果已经不代表当前状态
        if (generation != _filterGeneration) return;

        _searchMatched = total;
        _searchTruncated = total > EntryFilter.MaxSearchResults;
        Entries = matched;
        UpdateStatus();
    }

    /// <summary>给一次性提示（路径不存在、视图不可用等）用，下一次导航时清空。</summary>
    public void SetHint(string message) => HintText = message;

    private string? _statusHold;
    private DateTime _statusHoldUntil;
    private string _statusHoldPath = string.Empty;

    /// <summary>
    /// 写一条操作结论，并在当前目录上保持一小会儿不被自动刷新覆盖。
    ///
    /// 撤销、删除这类动作自己会刷新一次列表，紧接着目录监视还会再刷一次（防抖 400 ms），
    /// 而刷新会重算摘要。结论刚写下去就被"12 项 · 枚举 3 ms"盖掉，
    /// 用户根本看不出到底撤成了没——这跟没有反馈是一回事。
    ///
    /// 只在这个目录上生效：换了目录，摘要该是什么就是什么，不能让结论跟着漂。
    /// </summary>
    public void SetStatusHold(string text, TimeSpan hold)
    {
        StatusText = text;
        _statusHold = text;
        _statusHoldUntil = DateTime.UtcNow + hold;
        _statusHoldPath = CurrentPath;
    }

    /// <summary>取消保持。结论被新结论取代时用。</summary>
    public void ReleaseStatusHold() => _statusHold = null;

    /// <summary>
    /// 状态栏的条目统计。三种口径：普通、隐藏了若干项、搜索结果。
    /// 搜索态下"耗时"让位给"命中多少"——用户此刻关心的是有没有搜到，不是枚举用了多久。
    /// </summary>
    public void UpdateStatus()
    {
        // 操作结论在保持期内优先。过期或换了目录就自然让位给摘要
        if (_statusHold is not null)
        {
            if (DateTime.UtcNow < _statusHoldUntil &&
                string.Equals(_statusHoldPath, CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                StatusText = _statusHold;
                return;
            }

            _statusHold = null;
        }

        if (_searchQuery.Length > 0)
        {
            StatusText = _searchTruncated
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Status_SearchTruncated,
                    EntryFilter.MaxSearchResults,
                    _searchMatched)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Status_SearchSummary,
                    _entries.Count,
                    _all.Count);
            return;
        }

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

    /// <summary>是否正在算文件夹大小。状态栏据此把"选中统计"换成进度与取消按钮。</summary>
    public bool IsCalculatingSize
    {
        get => _isCalculatingSize;
        private set => Set(ref _isCalculatingSize, value);
    }

    /// <summary>滚动更新的进度文字（已扫描项数 + 累计大小）。</summary>
    public string SizeProgressText
    {
        get => _sizeProgressText;
        private set => Set(ref _sizeProgressText, value);
    }

    /// <summary>取消当前扫描。写盘的是"已扫描的部分"，不是全部——调用方要如实标注。</summary>
    public void CancelFolderSizeScan() => _sizeScanCts?.Cancel();

    /// <summary>
    /// 统计选中文件夹的大小（FR-6.16）。**唯一入口**：只由右键菜单「计算大小」调起，
    /// 没有滚动、选中、计时器会走到这里——需求写明"严禁自动递归"。
    ///
    /// 多个文件夹逐个扫而不是并发：并发在机械盘与网络盘上互相拖慢，
    /// 而用户对"一个一个出结果"的容忍度远高于"全都卡在计算中"。
    /// </summary>
    public async Task CalculateFolderSizesAsync(IReadOnlyList<FileEntry> targets)
    {
        List<FileEntry> folders = [];
        foreach (var entry in targets)
        {
            if (entry.IsDirectory) folders.Add(entry);
        }

        if (folders.Count == 0)
        {
            // 选中的全是文件时大小列本来就有值，这里只是提醒"这命令对文件无意义"
            SetStatus(Strings.Status_SizeNoFolder);
            return;
        }

        // 单实例：只复位"还卡在计算中"的那几个。
        // 不能一律 ClearFolderSize——那样用户先算 A、再算 B，A 刚算出的结果会被一起抹掉，
        // 而"先算一个、再算旁边一个、对着看"正是这个功能最常见的用法。
        CancelFolderSizeScan();
        foreach (var entry in _sizeScanTargets)
        {
            if (entry.FolderSizeState == FolderSizeState.Computing) entry.ClearFolderSize();
        }

        _sizeScanTargets = folders;

        var cts = new CancellationTokenSource();
        _sizeScanCts = cts;
        IsCalculatingSize = true;
        SizeProgressText = string.Empty;

        string scope = folders.Count == 1
            ? folders[0].DisplayName
            : string.Format(CultureInfo.CurrentCulture, Strings.Size_MultiTargets, folders.Count);

        // Progress 在 UI 线程构造，回调才会回到 UI 线程改绑定属性
        var progress = new Progress<FolderSizeProgress>(p =>
        {
            if (!ReferenceEquals(_sizeScanCts, cts)) return;

            SizeProgressText = string.Format(
                CultureInfo.CurrentCulture,
                Strings.Status_SizeCalculating,
                scope,
                p.FileCount + p.FolderCount,
                FileEntry.FormatSizeText(p.Bytes));
        });

        long totalBytes = 0;
        long totalFiles = 0;
        long totalFolders = 0;
        long scannedItems = 0;
        bool completed = true;
        bool canceled = false;

        try
        {
            foreach (var folder in folders)
            {
                folder.MarkFolderSizeComputing();

                if (cts.IsCancellationRequested)
                {
                    completed = false;
                    break;
                }

                string path = folder.FullPath;
                FolderSizeResult result = await Task
                    .Run(() => FolderSizeCalculator.Count(path, cts.Token, progress), cts.Token)
                    .ConfigureAwait(true);

                folder.SetFolderSize(result.Bytes, result.Completed);

                totalBytes = result.Bytes > long.MaxValue - totalBytes ? long.MaxValue : totalBytes + result.Bytes;
                totalFiles += result.FileCount;
                totalFolders += result.FolderCount;
                scannedItems += result.FileCount + result.FolderCount;
                if (!result.Completed) completed = false;
            }
        }
        catch (OperationCanceledException)
        {
            completed = false;
        }
        finally
        {
            // 记在 finally 之前读：cts 马上就要释放，之后只能看这个局部变量
            canceled = cts.IsCancellationRequested;

            // 被取消在"标记完还没开始扫"的那一个行会停在计算中，这里统一收口。
            // 不清 Completed/Partial 的结果——那些是有效数据。
            foreach (var folder in folders)
            {
                if (folder.FolderSizeState == FolderSizeState.Computing) folder.ClearFolderSize();
            }

            // 只有"还是自己"才收尾：被新请求顶掉时，收尾归新任务做
            if (ReferenceEquals(_sizeScanCts, cts))
            {
                _sizeScanCts = null;
                IsCalculatingSize = false;
                SizeProgressText = string.Empty;
            }

            cts.Dispose();
        }

        // 状态栏结论。取消与"算完但有目录打不开"在这里是同一句话的两端：
        // 前者用户主动喊停，后者是环境不给，都要说清"这个数不是全部"
        if (!completed)
        {
            SetStatus(canceled || scannedItems == 0
                ? Strings.Status_SizeCanceled
                : string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Status_SizePartial,
                    scope,
                    scannedItems,
                    FileEntry.FormatSizeText(totalBytes)));
            return;
        }

        SetStatus(string.Format(
            CultureInfo.CurrentCulture,
            Strings.Status_SizeDone,
            scope,
            FileEntry.FormatSizeText(totalBytes),
            totalFiles,
            totalFolders));
    }

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

    /// <summary>
    /// 已知文件夹在后台校验后可能变化（用户重定向了下载夹），此时重建导航项。
    /// 不动当前目录、不动选中项——变的只是侧栏那几条。
    /// </summary>
    public void RefreshQuickAccess() => QuickAccess = BuildQuickAccess();

    private static List<NavItem> BuildQuickAccess()
    {
        var list = new List<NavItem>(10);

        // 主页节点（FR-1.2.1）。排在最前：它是"回到起点"，比任何一个具体目录更像入口
        list.Add(new NavItem(Strings.Nav_Home, IconGlyphs.Home, string.Empty, NavItemKind.Home));

        foreach (var folder in Services.KnownFolderService.All)
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

            list.Add(new NavItem(name, glyph, folder.Path, PlaceKey: NavPlaces.KeyOf(folder.Kind)));
        }

        // 此电脑（FR-1.2.2）。放在已知文件夹之后、回收站之前，与资源管理器侧栏的顺序一致。
        //
        // 它是一个容器而不是一个位置：没有文件系统路径，点击不去导航，而是展开驱动器列表
        // （见 NavPanelViewModel.ToggleThisPc）。展开状态落盘（FR-1.2.7），下次启动仍是这样。
        list.Add(new NavItem(
            Strings.Nav_ThisPc, IconGlyphs.ThisPc, string.Empty,
            NavItemKind.ThisPc, Strings.Nav_ThisPc_Tip, NavPlaces.ThisPc));

        // 回收站与网络（FR-1.2.3）。排在已知文件夹之后，与资源管理器侧栏的顺序一致。
        //
        // 这两项没有文件系统路径：回收站存在 $Recycle.Bin 里、网络是 Win32 网络命名空间，
        // 本程序的列表/标签/预览/文件操作全部按路径工作。给它们做一个"能点开但什么都做不了"
        // 的假目录比不做更糟，所以点击交给资源管理器（见 MainWindow.OnQuickAccessSelectionChanged）。
        // 网络"3 秒无响应"的验收落在导航本身上：地址栏输入 \\服务器\共享 时按 3 秒兜底
        // （SlowLocations / PaneViewModel.NavigateAsync），这才是真正会卡住用户的那条路。
        list.Add(new NavItem(
            Strings.Nav_RecycleBin, IconGlyphs.RecycleBin, string.Empty,
            NavItemKind.RecycleBin, Strings.Nav_RecycleBin_Tip, NavPlaces.RecycleBin));

        list.Add(new NavItem(
            Strings.Nav_Network, IconGlyphs.Network, string.Empty,
            NavItemKind.Network, Strings.Nav_Network_Tip, NavPlaces.Network));

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
