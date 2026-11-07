using System.ComponentModel;
using Lingpane.Core.Settings;

namespace Lingpane.App.ViewModels;

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

    private PaneViewModel _activePane;
    private bool _isTabActive;
    private bool _isPinned;

    /// <summary>关闭分屏后留下的第二窗格路径，下次开分屏时用。</summary>
    private string? _secondaryPath;

    /// <summary>建第二窗格时要把焦点放过去（1 = 放第二窗格）。见 <see cref="PrepareActivePane"/>。</summary>
    private int _pendingActivePane;

    public TabViewModel(AppSettings settings, string? path)
    {
        _settings = settings;

        Primary = new PaneViewModel(settings);
        Primary.PreparePendingPath(string.IsNullOrWhiteSpace(path) ? string.Empty : path);
        Primary.PropertyChanged += OnPanePropertyChanged;

        _activePane = Primary;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PaneViewModel Primary { get; }

    public PaneViewModel? Secondary { get; private set; }

    /// <summary>焦点所在的窗格。标签标题、命令目标、导航栏高亮都跟着它走。</summary>
    public PaneViewModel ActivePane => _activePane;

    public bool IsSplit => Secondary is not null;

    /// <summary>
    /// 标签标题跟随**活动窗格**，而不是固定取主窗格：用户把焦点放到右边并导航到别处时，
    /// 标签名必须跟着变，否则看起来像"标签没响应"。
    /// </summary>
    public string DisplayName => _activePane.DisplayName;

    /// <summary>分屏时 tooltip 给两条路径，单窗格时与之前一致。</summary>
    public string TabTooltip => Secondary is { } second
        ? $"{Primary.TabTooltip}{Environment.NewLine}{second.TabTooltip}"
        : _activePane.TabTooltip;

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

        var pane = new PaneViewModel(_settings);
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
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
