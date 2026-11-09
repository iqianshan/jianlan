using System.ComponentModel;
using Lingpane.App.Shell;
using Lingpane.Core.Localization;
using Lingpane.Core.Settings;
using Lingpane.Core.Tagging;

namespace Lingpane.App.ViewModels;

/// <summary>
/// 标签的内容形态（FR-1.1）。主页是"一种标签页"，不是独立窗口——
/// 这样标签栏、Ctrl+Tab、会话恢复全都无需为它开第二条路径。
/// </summary>
public enum TabKind
{
    /// <summary>浏览某个目录。绝大多数标签都是这一种。</summary>
    Files = 0,

    /// <summary>主页（FR-1.1）：常用目录、驱动器、最近访问、收藏。</summary>
    Home = 1,

    /// <summary>
    /// 标签结果（FR-4.1.5）：列出某一颜色下的全部条目，不论它们分散在哪个目录。
    /// 与主页一样是"没有路径"的形态，因此不参与分屏、也不进会话恢复——
    /// 重启后恢复一个"上次看过哪些红标文件"没有意义。
    /// </summary>
    TagResults = 2,
}

/// <summary>
/// 一个标签 = 一对窗格（FR-2 × FR-3）。
///
/// 为什么标签里装窗格，而不是每个窗格各有一条标签栏：窗口顶部只有一条标签栏，
/// 分屏时两列各自拥有一条标签栏的话，Ctrl+1…9 到底切哪一边就无法定义，
/// 窗口一窄两条都挤成"文…"。所以「标签」是位置，窗格是标签内部的分栏——
/// 切标签整对换绑，分屏开关是窗口级形态（切标签不会把分屏切没了）。
///
/// 主窗格（<see cref="Primary"/>）永远存在，第二窗格（<see cref="Secondary"/>）只有在
/// 窗口开分屏时才建。关闭分屏会把第二窗格**丢掉**而只记住路径：双窗格的内存预算
/// 是给"正在用的两个窗格"的，12 个标签各留一份空壳等于把预算花在看不见的地方。
/// </summary>
public sealed class TabViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _settings;

    /// <summary>成功进入目录后的回调（最近访问），两个窗格共用一份。</summary>
    private readonly Action<string>? _onVisited;

    /// <summary>彩色标签库（FR-4），两个窗格共用一份。</summary>
    private readonly TagStore? _tags;

    private TabKind _kind;

    /// <summary>标签结果形态下看的是哪一种颜色（FR-4.1.5）。</summary>
    private TagColors _tagFilter;

    /// <summary>那个颜色的本地化名，切换语言后不会自己变——重建标签时重取。</summary>
    private string _tagFilterName = string.Empty;

    private PaneViewModel _activePane;
    private bool _isTabActive;
    private bool _isPinned;

    /// <summary>关闭分屏后留下的第二窗格路径，下次开分屏时用。</summary>
    private string? _secondaryPath;

    /// <summary>建第二窗格时要把焦点放过去（1 = 放第二窗格）。见 <see cref="PrepareActivePane"/>。</summary>
    private int _pendingActivePane;

    public TabViewModel(AppSettings settings, string? path, Action<string>? onVisited = null,
        TabKind kind = TabKind.Files, TagStore? tags = null)
    {
        _settings = settings;
        _onVisited = onVisited;
        _kind = kind;
        _tags = tags;

        Primary = new PaneViewModel(settings, onVisited, tags);

        // 主页标签不挂路径：主页不"位于"任何目录。不调 PreparePendingPath 是关键——
        // 那个方法会把 HasPendingContent 置真，激活时宿主就会拿空路径去读盘。
        if (kind == TabKind.Files)
        {
            Primary.PreparePendingPath(string.IsNullOrWhiteSpace(path) ? string.Empty : path);
        }

        Primary.PropertyChanged += OnPanePropertyChanged;

        _activePane = Primary;
    }

    /// <summary>这个标签是不是主页（FR-1.1）。</summary>
    public TabKind Kind
    {
        get => _kind;
        private set
        {
            if (_kind == value) return;
            _kind = value;
            Raise(nameof(Kind));
            Raise(nameof(DisplayName));
            Raise(nameof(TabTooltip));
            Raise(nameof(IconGlyph));
            Raise(nameof(IsHome));
            Raise(nameof(IsTagResults));
            KindChanged?.Invoke(this);
        }
    }

    public bool IsHome => _kind == TabKind.Home;

    /// <summary>这个标签是不是某一色的标签结果页（FR-4.1.5）。</summary>
    public bool IsTagResults => _kind == TabKind.TagResults;

    /// <summary>标签结果页看的是哪一色。非该形态时是 <see cref="TagColors.None"/>。</summary>
    public TagColors TagFilter => _kind == TabKind.TagResults ? _tagFilter : TagColors.None;

    /// <summary>那个颜色的名字，头部标题与侧栏高亮都用它。</summary>
    public string TagFilterName => _tagFilterName;

    /// <summary>标签栏上的图标。主页用房子、标签结果用文件夹，其余也是文件夹。</summary>
    public string IconGlyph => IsHome ? IconGlyphs.Home : IconGlyphs.Folder;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 标签形态变了（主页 ⇄ 目录）。宿主据此切换 PaneHost 里显示的是主页还是窗格——
    /// 光靠 <see cref="PropertyChanged"/> 传不出"要换一套视图"这件事。
    /// </summary>
    public event Action<TabViewModel>? KindChanged;

    public PaneViewModel Primary { get; }

    public PaneViewModel? Secondary { get; private set; }

    /// <summary>焦点所在的窗格。标签标题、命令目标、导航栏高亮都跟着它走。</summary>
    public PaneViewModel ActivePane => _activePane;

    public bool IsSplit => Secondary is not null;

    /// <summary>
    /// 标签标题跟随**活动窗格**，而不是固定取主窗格：用户把焦点放到右边并导航到别处时，
    /// 标签名必须跟着变，否则看起来像"标签没响应"。
    /// </summary>
    public string DisplayName => _kind switch
    {
        TabKind.Home => Strings.Tab_Home,
        TabKind.TagResults => string.Format(Strings.Tab_TagResults, _tagFilterName),
        _ => _activePane.DisplayName,
    };

    /// <summary>分屏时 tooltip 给两条路径，单窗格时与之前一致。</summary>
    public string TabTooltip => _kind switch
    {
        TabKind.Home => Strings.Tab_Home_Tip,
        TabKind.TagResults => string.Format(Strings.Tab_TagResults_Tip, _tagFilterName),
        _ => Secondary is { } second
            ? $"{Primary.TabTooltip}{Environment.NewLine}{second.TabTooltip}"
            : _activePane.TabTooltip,
    };

    /// <summary>本标签是否为当前标签。标签栏据此区分活动/非活动配色。</summary>
    public bool IsTabActive
    {
        get => _isTabActive;
        set
        {
            if (_isTabActive == value) return;
            _isTabActive = value;
            Raise(nameof(IsTabActive));
        }
    }

    /// <summary>
    /// 固定标签（FR-2.9）。固定后不参与"关闭其他 / 关闭右侧"，也不再被这两个动作
    /// 顺手带走。语义参考浏览器的固定标签：位置不动、只能单独关。
    /// </summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            Raise(nameof(IsPinned));
        }
    }

    /// <summary>
    /// 建立第二窗格（FR-3.1）。<paramref name="fallbackPath"/> 用于第一次分屏：
    /// 优先用上次关闭分屏时记住的路径，其次用主窗格当前目录（同目录对照是最常见的起点）。
    /// </summary>
    public PaneViewModel EnsureSecondary(string? fallbackPath = null)
    {
        if (Secondary is { } existing) return existing;

        string target = _secondaryPath
                        ?? (string.IsNullOrWhiteSpace(fallbackPath) ? Primary.CurrentPath : fallbackPath);

        var pane = new PaneViewModel(_settings, _onVisited, _tags);
        pane.PreparePendingPath(target);
        pane.PropertyChanged += OnPanePropertyChanged;
        Secondary = pane;

        _secondaryPath = null;
        Raise(nameof(IsSplit));
        Raise(nameof(TabTooltip));

        // 分屏下从第二窗格发起的"在新标签打开"，新标签里焦点仍该在这一侧
        if (_pendingActivePane == 1) SetActivePane(pane);
        _pendingActivePane = 0;

        return pane;
    }

    /// <summary>
    /// 新建标签时声明焦点落在哪一侧（0 主 / 1 第二），在建出第二窗格那一刻生效。
    /// 由 <see cref="TabHostViewModel.Add"/> 在建标签时写入——那时第二窗格还不存在，
    /// 直接调 <see cref="ActivatePane"/> 会被当成"没有第二窗格"而落回主窗格。
    /// </summary>
    public void PrepareActivePane(int index) => _pendingActivePane = index;

    /// <summary>会话恢复：只记第二窗格的路径，不读盘。真正的窗格等窗口开分屏时再建。</summary>
    public void PrepareSecondaryPending(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _secondaryPath = path;
    }

    /// <summary>关掉第二窗格，只留路径。</summary>
    public void DropSecondary()
    {
        if (Secondary is not { } pane) return;

        _secondaryPath = pane.CurrentPath;
        pane.PropertyChanged -= OnPanePropertyChanged;
        Secondary = null;

        if (ReferenceEquals(_activePane, pane)) SetActivePane(Primary);

        Raise(nameof(IsSplit));
        Raise(nameof(TabTooltip));
    }

    /// <summary>焦点在哪个窗格。索引 0 = 主，1 = 第二；没有第二窗格时忽略 1。</summary>
    public void ActivatePane(int index)
    {
        if (index == 1 && Secondary is { } second) SetActivePane(second);
        else SetActivePane(Primary);
    }

    /// <summary>由宿主在点击/F3 之后调用，把"焦点换了"这件事同步到标题与命令目标。</summary>
    public void SetActivePane(PaneViewModel pane)
    {
        if (ReferenceEquals(_activePane, pane)) return;

        _activePane = pane;
        Raise(nameof(ActivePane));
        Raise(nameof(DisplayName));
        Raise(nameof(TabTooltip));
    }

    /// <summary>退出时写会话：主路径与第二路径（没开分屏时第二路径为空串）。</summary>
    public (string Primary, string Secondary) SnapshotPaths()
        => (Primary.CurrentPath, Secondary?.CurrentPath ?? string.Empty);

    /// <summary>
    /// 释放本标签两个窗格的目录内容，只留路径（非活动标签的内存约束）。
    ///
    /// 必须在视图换绑之后调用：<c>PaneView.Attach</c> 会先把滚动位置与选中项写回窗格，
    /// 早于它调用会把这两样状态连同条目一起丢掉。
    ///
    /// 第二窗格通常是 null——关分屏时 <see cref="DropSecondary"/> 已经把它整份丢掉了，
    /// 只有分屏期间它才以实体存在。
    /// </summary>
    public void ReleaseContent()
    {
        Primary.ReleaseContent();
        Secondary?.ReleaseContent();
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 窗格换了目录 → 标签名与 tooltip 一起变。窗格自己会在 CurrentPath 变化时
        // 连发 DisplayName 与 TabTooltip，这里只做一次转发，免得两处各判一遍。
        if (e.PropertyName is nameof(PaneViewModel.DisplayName) or nameof(PaneViewModel.TabTooltip))
        {
            Raise(nameof(DisplayName));
            Raise(nameof(TabTooltip));
            return;
        }

        // 从主页 / 标签结果页点了某个目录：标签形态跟着变成"浏览目录"。反过来不做——
        // 导航到一个真实目录后标签就该显示那个目录，而不是继续叫"主页"或"红标"。
        if (e.PropertyName == nameof(PaneViewModel.CurrentPath) && _kind != TabKind.Files &&
            !string.IsNullOrEmpty(_activePane.CurrentPath))
        {
            Kind = TabKind.Files;
        }
    }

    /// <summary>
    /// 把标签切到主页（FR-1.2.1 的"主页"节点）。窗格的历史栈保留：
    /// Alt+← 应该能回到离开主页前看的那一处，而不是"主页是历史里的一个点"。
    /// </summary>
    public void ShowHome()
    {
        if (IsHome) return;

        DropSecondary();
        Primary.DetachToHome();
        Kind = TabKind.Home;
    }

    /// <summary>
    /// 从非目录形态（主页 / 标签结果）切回目录浏览。
    ///
    /// 宿主在"要跳到一个真实目录"之前先调它，而不是等窗格的 CurrentPath 变化来触发：
    /// 那要经过一次导航完成，此时视图还停在主页上，选中项无处可放。
    /// </summary>
    public void ShowFiles()
    {
        if (_kind == TabKind.Files) return;
        Kind = TabKind.Files;
    }

    /// <summary>
    /// 把标签切到"某一色的标签结果"（FR-4.1.5）。同一个标签再点别的颜色，就换筛选条件、
    /// 不新开标签——用户点颜色是在"换个角度看标记"，不是"再看一份"。
    ///
    /// 窗格只能丢弃：标签结果页没有路径，留着两半窗格既不能显示也没人用得上。
    /// 丢掉的那一半只是丢内容，路径仍在 <see cref="PrepareSecondaryPending"/> 记着，
    /// 之后从结果页双击跳到目录、再开分屏时会带回来。
    /// </summary>
    public void ShowTagResults(TagColors color, string colorName)
    {
        DropSecondary();
        if (_kind != TabKind.TagResults) Primary.DetachToHome();

        _tagFilter = color;
        _tagFilterName = colorName;

        if (_kind == TabKind.TagResults)
        {
            // 形态没变，宿主不会收到 KindChanged，得自己把标题重算一遍
            Raise(nameof(DisplayName));
            Raise(nameof(TabTooltip));
            Raise(nameof(TagFilter));
            KindChanged?.Invoke(this);
            return;
        }

        Kind = TabKind.TagResults;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
