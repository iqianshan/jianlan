using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Lingpane.App.Services;
using Lingpane.App.ViewModels;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Localization;
using Lingpane.Core.Navigation;
using Lingpane.Core.Settings;

namespace Lingpane.App.Views;

/// <summary>
/// 一个浏览区（窗格）。承载工具栏、地址栏、列头、文件列表与状态栏，
/// 以及全部"作用于当前目录"的行为：文件操作、拖放、目录监听、图标解析。
///
/// 为什么要从窗口里拆出来：多标签（FR-2）与双窗格（FR-3）上线后，同一个窗口里
/// 会同时存在一到两个浏览区，它们的路径、选中项、项数统计、滚动位置、Watcher
/// 目标都是独立的。只要这些状态还挂在 Window 上，"这作用于哪个窗格"就得在
/// 每个方法里再判一次，而每加一个功能就多一处需要判的地方。
///
/// 现阶段的边界（与 MainWindow 的分工）：
///   · 窗口级：标签栏、快捷键钩子、系统热键、设置窗口、导航栏、会话保存、启动计时
///   · 窗格级：本文件及 PaneView.*.cs 的全部内容
///
/// 一个 PaneView 在多标签下会被复用：切标签时宿主再调一次 <see cref="Attach"/>，
/// 把视图重新绑到另一份 PaneViewModel。所以本类里凡是从视图模型派生的状态，
/// 都不能假设"整个窗口生命周期内只对应一份"。
/// </summary>
public partial class PaneView : UserControl
{
    private PaneViewModel? _viewModel;

    public PaneView()
    {
        InitializeComponent();
        RegisterCommands();
        HookRowHover();
        InitializeColumns();
        InitializeThumbnails();
        Unloaded += OnPaneUnloaded;
        SizeChanged += OnPaneSizeChanged;
    }

    /// <summary>
    /// 这一侧是第几窗格（FR-3.7）。由宿主在构造时指定：列配置与视图/排序都按槽位各存一份。
    ///
    /// 做成可变属性而不是构造参数：XAML 里无参构造是硬要求，
    /// 而这个值属于"宿主知道、控件不必知道"的那类信息。
    /// </summary>
    public PaneSlot Slot { get; set; } = PaneSlot.Primary;

    /// <summary>本窗格的视图模型。附加前为 null，所有绑定静默为空，不会抛异常。</summary>
    public PaneViewModel? ViewModel => _viewModel;

    /// <summary>
    /// 非空访问器。附加由宿主保证（MainWindow 构造完立即 Attach），
    /// 用显式异常而不是 null 传播，是为了让"忘记 Attach"在第一次交互时就暴露，
    /// 而不是变成一堆莫名其妙的空列表。
    /// </summary>
    private PaneViewModel Vm => _viewModel
        ?? throw new InvalidOperationException("PaneView 尚未附加视图模型：宿主必须先调用 Attach.");  // i18n-ignore：开发者异常，不进界面

    /// <summary>
    /// 绑定视图模型。由宿主（窗口或标签容器）在构造完窗格后调用，
    /// 而不是在构造函数里自己 new——窗格的 VM 由谁创建、创建几个，是宿主的决定。
    ///
    /// **切换标签就是再调一次**：视图只有一份，换的是它绑的那份视图模型。
    /// 因此这里要负责把"属于上一位的"状态收走（滚动位置、选中项、目录监听），
    /// 再把"属于下一位的"放回去。收放不对，切标签就会出现列表跳回顶部、
    /// 监听盯着上一个目录这类问题。
    /// </summary>
    public void Attach(PaneViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (ReferenceEquals(_viewModel, viewModel)) return;

        if (_viewModel is { } previous)
        {
            SaveViewState(previous);
            previous.NavigationCompleted -= StartWatching;
            previous.NavigationCompleted -= ScheduleFolderIcons;
            previous.NavigationCompleted -= RestoreAfterReload;
            previous.PropertyChanged -= OnPanePropertyChanged;
            StopWatching();
        }

        _viewModel = viewModel;
        // 换绑就作废上一次的等待：它属于旧窗格
        _pendingRestore = null;
        DataContext = viewModel;

        // 列配置要在首帧前就位，否则会看到一次"默认宽度 → 记忆宽度"的跳动。
        // 档位先按路径定下来：切标签等于换了目录类型，图片档与下载档的列宽本来就不一样。
        _columnKind = ColumnProfiles.Resolve(viewModel.CurrentPath, KnownFolderService.All);
        ApplyColumnWidths();

        // 监听与图标补充挂在视图模型上：它们的目标是"这个窗格正在看的目录"
        viewModel.NavigationCompleted += StartWatching;
        viewModel.NavigationCompleted += ScheduleFolderIcons;
        viewModel.NavigationCompleted += RestoreAfterReload;
        viewModel.PropertyChanged += OnPanePropertyChanged;

        RestoreViewState(viewModel);
    }

    // ── 切标签时要搬走的视图状态 ────────────────────────────────────────────
    //
    // 只搬两样：滚动位置与选中项。视图模式、排序、列宽都不是"每个标签一份"——
    // 它们来自设置，属于整个应用；标签之间不一致反而会让用户以为设置丢了。

    private bool _restoringState;

    /// <summary>
    /// 内容被释放过、要等重新读盘才能恢复选中与滚动的窗格。
    /// 非活动标签不保留条目（<see cref="PaneViewModel.ReleaseContent"/>），切回来时列表先是空的，
    /// 按路径一条也找不到——此时不能"恢复失败就算了"，得等 <c>NavigationCompleted</c> 再来一次，
    /// 否则用户切回标签会发现选中项没了。
    /// </summary>
    private PaneViewModel? _pendingRestore;

    private void SaveViewState(PaneViewModel viewModel)
    {
        viewModel.ScrollAnchor = CaptureScrollAnchor();

        if (FileList.SelectedItems.Count == 0)
        {
            viewModel.SelectedPaths = [];
            return;
        }

        var paths = new List<string>(FileList.SelectedItems.Count);
        foreach (object item in FileList.SelectedItems)
        {
            if (item is FileEntry entry) paths.Add(entry.FullPath);
        }

        viewModel.SelectedPaths = paths;
    }

    private void RestoreViewState(PaneViewModel viewModel)
    {
        // 条目可能已经被释放（非活动标签不保留内容）。这时按路径一条也找不到，
        // 选中与滚动都只能等重新读盘完成——记下来，交给 RestoreAfterReload。
        bool pending = !RestoreSelection(viewModel, viewModel.SelectedPaths);
        if (pending) _pendingRestore = viewModel;

        // 滚动要等布局跑完：ItemsSource 刚换过，此刻 ScrollViewer 的 extent 还是旧的，
        // 直接 ScrollToVerticalOffset 会被夹到 0，表现为"切回来列表在顶部"。
        // 等着读盘的这一轮先跳过——列表是空的，恢复了也会被夹到 0。
        (int Index, double Offset) anchor = viewModel.ScrollAnchor;
        if (pending || (anchor.Index <= 0 && anchor.Offset <= 0)) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // 期间用户可能又切走了：只还原"当前仍然绑着的那份"的位置
            if (ReferenceEquals(_viewModel, viewModel)) RestoreScrollAnchor(anchor);
        });
    }

    /// <summary>
    /// 重新读盘完成后补做选中与滚动恢复。只对"切回来时内容还没加载"的窗格生效；
    /// 正常导航（用户点了别的目录）不会走到恢复，因为那时没有等待中的窗格。
    /// </summary>
    private void RestoreAfterReload(string path)
    {
        if (_pendingRestore is not { } viewModel || !ReferenceEquals(_viewModel, viewModel)) return;
        _pendingRestore = null;

        RestoreSelection(viewModel, viewModel.SelectedPaths);

        (int Index, double Offset) anchor = viewModel.ScrollAnchor;
        if (anchor.Index <= 0 && anchor.Offset <= 0) return;

        // 刚读完盘、列表刚换过 ItemsSource，同 RestoreViewState 的理由再等一帧布局
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ReferenceEquals(_viewModel, viewModel)) RestoreScrollAnchor(anchor);
        });
    }

    /// <summary>
    /// 条目集合换过之后重试"等读盘"的那次选中恢复。
    ///
    /// 为什么需要它：搜索态下的过滤结果是异步回来的，<c>NavigationCompleted</c> 触发时
    /// 列表还是空的（搜索分支没有同步填 <c>Entries</c>），<c>RestoreAfterReload</c> 必然
    /// 一条也找不到。没有这一步，切回一个正在搜索的标签，选中项会莫名丢掉。
    /// </summary>
    private void RetryPendingRestore()
    {
        if (_pendingRestore is not { } viewModel || !ReferenceEquals(_viewModel, viewModel)) return;
        if (viewModel.SelectedPaths.Count == 0)
        {
            _pendingRestore = null;
            return;
        }

        // 恢复了才清标记；还没恢复就留着，等下一次 Entries 变化（比如过滤完成）
        if (RestoreSelection(viewModel, viewModel.SelectedPaths)) _pendingRestore = null;
    }

    /// <summary>
    /// 按路径恢复选中。返回 <c>false</c> 表示"有路径要选、但一条都没找到"——
    /// 这只发生在内容已被释放、正等着重新读盘的时候。
    /// </summary>
    private bool RestoreSelection(PaneViewModel viewModel, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return true;

        _restoringState = true;
        int found = 0;
        try
        {
            FileList.SelectedItems.Clear();

            foreach (string path in paths)
            {
                if (viewModel.FindByPath(path) is { } entry)
                {
                    FileList.SelectedItems.Add(entry);
                    found++;
                }
            }
        }
        finally
        {
            _restoringState = false;
        }

        // 逐项 Add 的中间状态不结算，这里统一算一次，省掉 N 次命令可用性刷新
        CommitSelection();

        return found > 0;
    }

    /// <summary>
    /// 把当前 <see cref="FileList.SelectedItems"/> 结算给视图模型。
    ///
    /// 为什么单独一个方法：按路径恢复选中（切标签）与框选（FR-6.7）都是"批量改选中，
    /// 改完只结算一次"。两处各写一遍的话，以后给选中加副作用（比如状态栏汇总）
    /// 必然只改一处、漏掉另一处。
    /// </summary>
    private void CommitSelection()
    {
        if (_viewModel is null) return;

        long total = 0;
        var selected = new List<FileEntry>(FileList.SelectedItems.Count);

        foreach (object item in FileList.SelectedItems)
        {
            if (item is not FileEntry entry) continue;
            selected.Add(entry);
            total += entry.Size;
        }

        _viewModel.SetSelection(selected.Count, total);
        _viewModel.SetSelectedEntries(selected);
        CommandManager.InvalidateRequerySuggested();
        SelectionChanged?.Invoke(this);
    }

    /// <summary>窗格被点中（键盘焦点进入列表）时触发，宿主据此切换"活动窗格"。</summary>
    public event Action<PaneView>? Activated;

    /// <summary>
    /// 选中集合变化（含"清空"与"切标签时恢复上次选中"）。预览窗格（FR-9.1.2）据此刷新。
    ///
    /// 由视图发而不是由视图模型发：选中的真值在 <c>ListBox</c> 上，
    /// 视图模型里的 <c>SelectedEntries</c> 只是它的镜像；恢复选中时中间会经历
    /// "先空后有"两个中间态，只有视图知道哪一次才是最终结果。
    /// </summary>
    public event Action<PaneView>? SelectionChanged;

    /// <summary>
    /// 请求把某个目录开进新标签（FR-2.10）。参数：(路径, 是否后台打开)。
    /// 窗格不认识标签集合——新建标签要动窗口级的 <c>TabHost</c>，那是宿主的知识。
    /// </summary>
    public event Action<string, bool>? OpenInNewTabRequested;

    /// <summary>是否为当前活动窗格。分屏时用于区分命令目标与焦点包边。</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// 分屏是否开着。由宿主（MainWindow.ToggleSplit）写入，工具栏上的分屏按钮据此显示选中态。
    ///
    /// 为什么用依赖属性而不是直接改按钮的 Background：按钮的样式里要用 <c>DataTrigger</c> 读它，
    /// 普通 CLR 属性不会通知绑定；而拿代码去改背景色，主题一换（Light/Dark 是 DynamicResource）
    /// 就恢复不了。依赖属性是这两件事同时成立的最短路径。
    /// </summary>
    public static readonly DependencyProperty IsSplitActiveProperty =
        DependencyProperty.Register(
            nameof(IsSplitActive), typeof(bool), typeof(PaneView), new PropertyMetadata(false));

    public bool IsSplitActive
    {
        get => (bool)GetValue(IsSplitActiveProperty);
        set => SetValue(IsSplitActiveProperty, value);
    }

    /// <summary>
    /// 预览窗格是否开着。与 <see cref="IsSplitActive"/> 同源：开关在宿主，
    /// 窗格只负责把状态画成按钮的选中底色。
    ///
    /// 与"此刻是否真的看得见"刻意分开：主页与标签结果页没有可预览的选中项，
    /// 那两种形态下预览栏一律收起，但开关本身仍然是开着的——切回目录标签要自动回来。
    /// 按钮的选中态读这个字段，会让用户在主页也看得出"预览整体是开着的"。
    /// </summary>
    public static readonly DependencyProperty IsPreviewActiveProperty =
        DependencyProperty.Register(
            nameof(IsPreviewActive), typeof(bool), typeof(PaneView), new PropertyMetadata(false));

    public bool IsPreviewActive
    {
        get => (bool)GetValue(IsPreviewActiveProperty);
        set => SetValue(IsPreviewActiveProperty, value);
    }

    /// <summary>
    /// 预览按钮此刻能不能点。非文件标签（主页 / 标签结果 / 工作区详情）下宿主置 false：
    /// 那些形态里预览栏无处安放，按钮点下去什么也不会发生。
    ///
    /// 这里选择"禁用"而不是"点了没反应"：一颗亮着的按钮没反馈，用户只会以为功能坏了；
    /// 而让它照常改写 <c>AppSettings.Appearance.ShowPreview</c> 更糟——用户在主页顺手点两下，
    /// 设置页的勾选就被改掉了，前后状态对不上，且他并不知道自己改过。
    /// </summary>
    public static readonly DependencyProperty IsPreviewToggleEnabledProperty =
        DependencyProperty.Register(
            nameof(IsPreviewToggleEnabled), typeof(bool), typeof(PaneView), new PropertyMetadata(true));

    public bool IsPreviewToggleEnabled
    {
        get => (bool)GetValue(IsPreviewToggleEnabledProperty);
        set => SetValue(IsPreviewToggleEnabledProperty, value);
    }

    /// <summary>
    /// 是否显示工具栏上的分屏按钮。只有主窗格显示：
    /// 开关本身是窗口级的，两颗按钮同时出现会让人以为两个窗格能各分各的。
    /// </summary>
    public bool ShowSplitButton
    {
        get => SplitButton.Visibility == Visibility.Visible;
        set
        {
            Visibility visibility = value ? Visibility.Visible : Visibility.Collapsed;
            SplitButton.Visibility = visibility;
            SplitDivider.Visibility = visibility;
        }
    }

    /// <summary>
    /// 是否显示工具栏上的预览按钮。理由同 <see cref="ShowSplitButton"/>：
    /// 预览是窗口级的一栏，分屏时两颗按钮会让人误以为两栏各有一个预览。
    /// 与分屏按钮不同的是，它不跟着 <c>SplitDivider</c> 走——
    /// 隐藏分屏按钮时那条分隔线要一起收，预览按钮自己不带线。
    /// </summary>
    public bool ShowPreviewButton
    {
        get => PreviewButton.Visibility == Visibility.Visible;
        set => PreviewButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 预览按钮点击。走 Click 而不是 Command：这颗按钮没有可执行性判断要做
    /// （能不能点由宿主写的 <see cref="IsPreviewToggleEnabled"/> 决定，直接绑 IsEnabled），
    /// 再包一层 <c>CommandAdapter</c> 只会让"为什么点不动"多一个排查入口。
    /// </summary>
    private void OnPreviewButtonClick(object sender, RoutedEventArgs e) => PreviewToggleRequested?.Invoke();

    /// <summary>宿主是否要求画焦点包边（只在分屏时）。</summary>
    private bool _outline;

    /// <summary>
    /// 由宿主设置活动态。<paramref name="outline"/> 只在宿主明确传值时更新——
    /// 列表自己拿到焦点时调的是 <c>SetActive(true)</c>，不能因此把包边关掉。
    /// </summary>
    public void SetActive(bool active, bool? outline = null)
    {
        if (outline is bool value) _outline = value;
        OutlineHost.BorderThickness = active && _outline ? new Thickness(1) : new Thickness(0);

        if (IsActive == active) return;

        IsActive = active;
        if (active) Activated?.Invoke(this);
    }

    /// <summary>
    /// 解除与视图模型的绑定。关分屏时用：只把视图 <c>Visibility</c> 设成 Collapsed
    /// 的话，DataContext 还指着那份被丢弃的视图模型，内存省不下来。
    /// </summary>
    public void Release()
    {
        if (_viewModel is { } previous)
        {
            SaveViewState(previous);
            previous.NavigationCompleted -= StartWatching;
            previous.NavigationCompleted -= ScheduleFolderIcons;
            previous.NavigationCompleted -= RestoreAfterReload;
            previous.PropertyChanged -= OnPanePropertyChanged;
            StopWatching();
        }

        _viewModel = null;
        _pendingRestore = null;
        DataContext = null;
        OutlineHost.BorderThickness = new Thickness(0);
    }

    /// <summary>把键盘焦点交给列表（Tab 在两窗格间切换用，FR-3.5）。</summary>
    public void FocusList()
    {
        FileList.Focus();
        if (FileList.SelectedIndex < 0 && FileList.Items.Count > 0) FileList.SelectedIndex = 0;
    }

    private void OnFileListGotFocus(object sender, KeyboardFocusChangedEventArgs e) => SetActive(true);

    /// <summary>设置改动后重新套用窗格内受影响的界面状态（不重建窗格）。</summary>
    public void ApplyAppearance(AppSettings s)
    {
        StatusBarHost.Visibility = s.Appearance.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        ApplyColumnWidths();

        _viewModel?.ApplyShowExtensions(s.Behavior.ShowExtensions);
        _viewModel?.ApplyFilter();
    }

    /// <summary>是否显示状态栏，供宿主查询（单实例时用于诊断日志）。</summary>
    public Visibility StatusBarVisibility => StatusBarHost.Visibility;


    /// <summary>窗格变窄/变宽要重新决定哪些列还放得下（分屏、拖分隔条、拉窗口都会触发）。</summary>
    private void OnPaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 传新宽度而不是让 ApplyColumns 去读 ActualWidth：SizeChanged 触发时
        // ActualWidth 还是旧值，读它会慢一帧，表现为"拖动分隔条时列晚一步折叠"。
        if (!e.WidthChanged) return;

        ApplyColumns(e.NewSize.Width);
        ApplySearchBoxWidth(e.NewSize.Width);
    }

    /// <summary>当前搜索框宽度档位的资源键。只在跨档时改写，避免每次拖窗都刷一遍资源引用。</summary>
    private string _searchWidthKey = "Size.Search.Width";

    public static readonly DependencyProperty IsCompactPaneProperty = DependencyProperty.Register(
        nameof(IsCompactPane),
        typeof(bool),
        typeof(PaneView),
        new PropertyMetadata(false));

    /// <summary>
    /// 窗格窄到连搜索框的装饰都放不下（分屏后的常态）。界面靠它收起放大镜、
    /// 清除按钮与左右留白——都是纯装饰，功能一项不少（Esc 仍能清空、Ctrl+F 仍能聚焦）。
    /// 行内元素通过相对源绑定读它，所以必须是依赖属性。
    /// </summary>
    public bool IsCompactPane
    {
        get => (bool)GetValue(IsCompactPaneProperty);
        private set => SetValue(IsCompactPaneProperty, value);
    }

    public static readonly DependencyProperty IsTightPaneProperty = DependencyProperty.Register(
        nameof(IsTightPane),
        typeof(bool),
        typeof(PaneView),
        new PropertyMetadata(false));

    /// <summary>
    /// 窗格窄到工具栏上所有按钮都摆不下。
    ///
    /// 与 <see cref="IsCompactPane"/> 的分工：压缩档只是把按钮的留白收紧，功能一个不少；
    /// 极窄档则要**真的拿掉一些东西**，否则整条右列会被 Grid 裁掉半颗按钮——
    /// 实测 1280 宽的窗口、预览拖到 475 宽时窗格只剩 310，右列固定部分约 329，
    /// 最后那颗预览图标被切掉一大半，看着像渲染坏了。
    ///
    /// 这一档收起「详细」「列表」两个文字按钮：它们是"视图偏好"，不是"当下要完成的动作"，
    /// 且快捷键（Ctrl+Shift+1 / 2）与列表右键菜单里都还有入口，功能没有真正丢失。
    /// 导航、搜索、分屏、预览这些"动作型"按钮一律保留。
    /// </summary>
    public bool IsTightPane
    {
        get => (bool)GetValue(IsTightPaneProperty);
        private set => SetValue(IsTightPaneProperty, value);
    }

    /// <summary>
    /// 搜索框宽度分三档（FR-6.13）。分屏后每个窗格只有半个窗口，工具栏上还挤着
    /// 四个导航按钮、地址栏与三个视图按钮；搜索框固定 150 会把地址栏压到只剩几个像素，
    /// 面包屑彻底点不到（实测分屏 50/50 时一条路径都看不到）。
    ///
    /// 用三档而不是按比例连续缩放：宽度连续变化时输入框会跟着拖拽一直抖，
    /// 而"切到分屏那一刻变窄一次"是看得懂的。三个宽度都取自 DesignTokens。
    ///
    /// 压缩档只有 40：那个宽度只放得下放大镜，聚焦时临时展开到
    /// <see cref="ExpandedSearchWidth"/>（见 OnSearchBoxGotFocus），
    /// 这样"平时"地址栏拿走全部剩余宽度，"输入时"搜索框接管——两者不必同时挤着。
    /// </summary>
    private void ApplySearchBoxWidth(double paneWidth, bool force = false)
    {
        // 档位阈值 760/620 而不是 720/520：
        // 工具栏右列在预览按钮之后固定占掉约 485（四个导航 144 + 搜索 128 + 详细列表 104
        // + 分隔 13 + 分屏 36 + 预览 36 + 内边距 24），地址栏至少还要留 120 才看得见
        // 一两段面包屑。620 以下的窗格继续用非压缩档会直接把预览按钮裁掉半颗——
        // 实测 1024 宽的窗口开预览后窗格约 536，正好落在那个夹缝里。
        string key = paneWidth switch
        {
            >= 760 => "Size.Search.Width",
            >= 620 => "Size.Search.Width.Narrow",
            _ => "Size.Search.Width.Compact",
        };

        IsCompactPane = key == "Size.Search.Width.Compact";
        IsTightPane = paneWidth < 430;

        // 正在输入时不要把它缩回去：窗口被拖动/分屏切换都可能触发这里，
        // 缩到 40 会让用户看着自己的输入一半不见
        if (IsCompactPane && SearchBox.IsKeyboardFocusWithin)
        {
            SearchHost.Width = ExpandedSearchWidth;
            _searchWidthKey = string.Empty;
            return;
        }

        if (key == _searchWidthKey && !force) return;

        _searchWidthKey = key;
        SearchHost.SetResourceReference(WidthProperty, key);
    }

    /// <summary>压缩态展开时的宽度，与"单窗格"档同值：展开就是临时回到常规宽度。</summary>
    private const double ExpandedSearchWidth = 150;

    private void OnSearchBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 只有压缩态需要展开；其它档位本来就够宽，改宽度只会让工具栏抖一下
        if (!IsCompactPane) return;

        SearchHost.Width = ExpandedSearchWidth;
    }

    private void OnSearchBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsCompactPane) return;

        // 搜索词还在就保持展开：筛选还生效，缩回去会让"东西去哪了"失去线索。
        // 清空搜索（Esc）或没输过就离开时收回去，把宽度还给地址栏。
        if (!string.IsNullOrEmpty(_viewModel?.SearchQuery)) return;

        ApplySearchBoxWidth(ActualWidth, force: true);
    }

    /// <summary>布局诊断：列头实际宽度与可视位置，确认"列被裁掉"还是"列不存在"。</summary>
    internal void LogColumnLayout()
    {
        if (!MainWindow.KeyDebug) return;

        MainWindow.WriteKeyLog($"layout pane={ActualWidth:0}x{ActualHeight:0} list={FileList.ActualWidth:0} " +
                    $"header={HeaderGrid.ActualWidth:0} margin={HeaderGrid.Margin}");

        for (int i = 0; i < HeaderGrid.ColumnDefinitions.Count; i++)
        {
            MainWindow.WriteKeyLog($"layout col{i}={HeaderGrid.ColumnDefinitions[i].ActualWidth:0.#}");
        }

        foreach (Button button in new[] { NameHeaderButton, DateHeaderButton, TypeHeaderButton, SizeHeaderButton })
        {
            if (button is null) continue;

            Point origin = button.TranslatePoint(new Point(0, 0), FileList);
            Point screen = button.PointToScreen(new Point(0, 0));
            MainWindow.WriteKeyLog($"layout {button.Tag}: w={button.ActualWidth:0.#} x={origin.X:0.#} " +
                        $"screenX={screen.X:0.#} screenY={screen.Y:0.#} content=\"{button.Content}\"");
        }

        // 把手的实际屏幕矩形：拖拽验证（模拟输入）必须按这个坐标落点，
        // 按列头坐标推算容易在缩放/边框上差几像素而打空。
        foreach (Thumb thumb in HeaderGrid.Children.OfType<Thumb>())
        {
            Point p = thumb.PointToScreen(new Point(0, 0));
            MainWindow.WriteKeyLog($"layout thumb {thumb.Tag}: x={p.X:0.#} y={p.Y:0.#} " +
                        $"w={thumb.ActualWidth:0.#} h={thumb.ActualHeight:0.#}");
        }

        // 工具栏按钮的屏幕中心。走查脚本点这些按钮时不能靠截图反推：
        // 截图是 DIP 空间的 PrintWindow 位图，注入的是物理像素，中间隔着缩放系数，
        // 差一个百分点就能把"点分屏"变成"点列表"。
        foreach ((string tag, Button button) in new[]
                 {
                     ("details", DetailsButton), ("list", ListButton), ("split", SplitButton),
                 })
        {
            if (button is null) continue;

            Point center = button.PointToScreen(
                new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            MainWindow.WriteKeyLog($"anchor {tag} screen={center.X:0.#},{center.Y:0.#} " +
                        $"w={button.ActualWidth:0.#} visible={button.Visibility}");
        }
    }


    // ── 导航 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 能否返回上级。注意 CurrentPath 在首次导航前是空串，
    /// 而按钮绑定会在窗格加载时就查询 CanExecute——此处必须挡住空值，
    /// 否则 Directory.GetParent("") 直接抛 ArgumentException，启动即崩。
    /// </summary>
    internal bool CanGoUp()
    {
        string path = _viewModel?.CurrentPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            return Directory.GetParent(path) is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal void GoUp()
    {
        if (_viewModel is null || !CanGoUp()) return;

        string parent = Directory.GetParent(_viewModel.CurrentPath)!.FullName;
        _ = _viewModel.NavigateAsync(parent);
    }

    /// <summary>供单实例转交使用：把本窗格切到指定目录。</summary>
    public void NavigateTo(string path) => _ = _viewModel?.NavigateAsync(path);

    // ── 命令可执行性 ────────────────────────────────────────────────────────

    internal bool HasSelection()
        => _viewModel is { IsLoading: false } && _viewModel.SelectedEntries.Count > 0;

    internal bool HasSingleSelection()
        => _viewModel is { IsLoading: false } && _viewModel.SelectedEntries.Count == 1;

    /// <summary>
    /// 「复制完整路径」的可执行条件。它比别的条目命令宽一档：没有选中项时
    /// 复制当前目录本身，所以空白处右键也能用——那正是用户最常想要"这个文件夹的路径"的时机。
    /// 其余条目命令仍只认 <see cref="HasSelection"/>。
    /// </summary>
    internal bool CanCopyPath()
        => _viewModel is { IsLoading: false }
           && (_viewModel.SelectedEntries.Count > 0 || !string.IsNullOrEmpty(_viewModel.CurrentPath));

    /// <summary>
    /// 选中的项里至少有一个目录——「计算大小」的可执行条件。
    /// 全选文件时置灰而不是隐藏：菜单项位置固定，用户才不会以为功能没了。
    /// </summary>
    internal bool HasSelectedFolder()
    {
        if (_viewModel is not { IsLoading: false }) return false;

        foreach (var entry in _viewModel.SelectedEntries)
        {
            if (entry.IsDirectory) return true;
        }

        return false;
    }

    internal bool CanCreateInCurrentDirectory()
        => _viewModel is { IsLoading: false } && !string.IsNullOrEmpty(_viewModel.CurrentPath);

    internal bool CanPaste()
        => _viewModel is { IsLoading: false } && Services.FileClipboard.HasData
           && !string.IsNullOrEmpty(_viewModel.CurrentPath);

    /// <summary>键盘菜单键（VK_APPS）弹出上下文菜单，位置对齐列表。</summary>
    internal void ShowContextMenu()
    {
        if (FileList.ContextMenu is not { } menu) return;

        // 键盘菜单键不经过 ListBox 的 ContextMenuOpening，可见性要在这里单独刷一次，
        // 否则沿用上一次右键留下的布局。
        ApplyMenuScope(HasSelection());

        menu.PlacementTarget = FileList;
        menu.Placement = PlacementMode.Center;
        menu.IsOpen = true;
    }

    /// <summary>WPF 的 ListBox 在 Extended 模式下没有内建 Ctrl+A，需要自己调 SelectAll。</summary>
    internal void SelectAllItems()
    {
        FileList.SelectAll();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>列表已有条目数，供"全选"命令的可执行性判断。</summary>
    internal int ItemCount => FileList.Items.Count;

    /// <summary>列表是否持有键盘焦点。快捷键派发要用它区分"按键归属列表"还是别的控件。</summary>
    internal bool IsListFocused => FileList.IsKeyboardFocusWithin;

    /// <summary>
    /// 记录列表滚动位置。字号/行高变化会导致偏移量失真，
    /// 设置页据此按项目索引还原，避免改字号后列表跳回顶部（FONT-05）。
    /// </summary>
    public (int Index, double Offset) CaptureScrollAnchor()
        => Typography.FontScaleService.CaptureAnchor(FindScrollViewer());

    public void RestoreScrollAnchor((int Index, double Offset) anchor)
        => Typography.FontScaleService.RestoreAnchor(FindScrollViewer(), anchor);

    private ScrollViewer? FindScrollViewer()
    {
        return FindDescendant<ScrollViewer>(FileList);

        static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                if (FindDescendant<T>(child) is { } nested) return nested;
            }

            return null;
        }
    }

    // ── 地址栏 ──────────────────────────────────────────────────────────────

    internal void BeginAddressEdit()
    {
        if (_viewModel is null || string.IsNullOrEmpty(_viewModel.CurrentPath)) return;

        _viewModel.IsAddressEditing = true;

        // 先记快照再赋值：赋值会触发 TextChanged，靠快照判断"用户还没改过内容"，
        // 否则 Ctrl+L 一按就弹出候选把面包屑盖住
        _addressSnapshot = _viewModel.CurrentPath;
        _suppressAutoComplete = true;

        AddressBox.Text = _viewModel.CurrentPath;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndAddressEdit()
    {
        CloseCompletion();
        if (_viewModel is not null) _viewModel.IsAddressEditing = false;
    }

    private async void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;

        // 补全优先吃键：↓↑ 选候选、Tab 接受、Esc 先关候选而不是退出编辑态
        if (TryHandleCompletionKey(e)) return;

        switch (e.Key)
        {
            case Key.Enter:
                // 先规范化再查存在性：Directory.Exists 认 D://work 这种写法，
                // 但提示与后续导航都该用规范形态，否则用户看到的和他输进去的不一致
                string target = PathRules.Normalize(AddressBox.Text);
                EndAddressEdit();

                // 空输入等同取消，不导航也不报错
                if (target.Length == 0)
                {
                    e.Handled = true;
                    break;
                }

                if (!Directory.Exists(target))
                {
                    _viewModel.SetHint(string.Format(Strings.Hint_PathNotFound, target));
                    return;
                }

                await _viewModel.NavigateAsync(target);
                e.Handled = true;
                break;

            case Key.Escape:
                EndAddressEdit();
                e.Handled = true;
                break;
        }
    }

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndAddressEdit();

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && !string.IsNullOrEmpty(path))
        {
            _ = _viewModel?.NavigateAsync(path);
        }
    }

    // ── 当前目录搜索（FR-6.13） ────────────────────────────────────────────

    /// <summary>
    /// Ctrl+F 的出口：聚焦搜索框并全选。
    /// 不切换任何"搜索模式"——搜索框常驻在工具栏上，这里只是把焦点送过去。
    /// </summary>
    internal void BeginSearch()
    {
        if (_viewModel is null) return;

        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearSearch();

        // 点"清除"的人是来改词的，不是来关搜索的：焦点留在输入框里
        SearchBox.Focus();
    }

    /// <summary>
    /// 搜索框里的键。
    ///
    /// 这里能直接处理 Esc，是因为窗口级的 <c>OnPreviewKeyDown</c> 见到
    /// <see cref="System.Windows.Controls.Primitives.TextBoxBase"/> 就提前返回（否则地址栏里
    /// 按 Delete 会变成删除文件）。搜索框自己的语义必须自己收：Esc 清词、回车与 ↓ 落地到列表。
    /// </summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_viewModel is { IsSearching: true })
                {
                    // 第一下清空、第二下回列表——用户按 Esc 十次有九次是想"退出搜索"
                    _viewModel.ClearSearch();
                }
                else
                {
                    FileList.Focus();
                }

                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Down:
                // 资源管理器的习惯：在搜索框里回车/↓ 就跳到结果，选中第一项
                FocusFirstResult();
                e.Handled = true;
                break;
        }
    }

    /// <summary>把焦点交给结果列表并选中第一项；没有结果时只交焦点，不制造一次空选中。</summary>
    private void FocusFirstResult()
    {
        FileList.Focus();

        if (FileList.SelectedIndex < 0 && FileList.Items.Count > 0) FileList.SelectedIndex = 0;
    }

    // ── 列表交互 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 切视图模式并落盘（FR-3.7）：记忆是按窗格分的，所以要写设置。
    /// 与排序同一口径——只有在真有变化时才写一次文件。
    /// </summary>
    internal void ApplyViewMode(DefaultViewKind mode)
    {
        if (_viewModel is not { } vm) return;
        if (!vm.SetViewMode(mode)) return;

        App.SaveSettings();
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field }) return;
        if (!Enum.TryParse<SortFieldKind>(field, ignoreCase: true, out var kind)) return;

        _viewModel?.SortBy(kind);

        // 排序选择按"这一侧"记住（FR-3.7）：视图模型只改内存里的设置对象，
        // 落盘统一在这里做——与列宽拖完才写盘同一个口径，点一次列头不该写一次文件。
        App.SaveSettings();
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 还原选中项时逐项 Add 会触发多次，统一在 RestoreSelection 末尾结算一次
        if (_restoringState || _viewModel is null) return;

        long total = 0;
        int count = 0;
        var selected = new List<FileEntry>(FileList.SelectedItems.Count);

        foreach (object item in FileList.SelectedItems)
        {
            if (item is not FileEntry entry) continue;
            selected.Add(entry);
            count++;
            total += entry.Size;
        }

        _viewModel.SetSelection(count, total);
        _viewModel.SetSelectedEntries(selected);

        // 选中数变化影响"打开/重命名/删除"等命令的可执行性，右键菜单打开前必须已刷新
        CommandManager.InvalidateRequerySuggested();

        // 预览跟着选中走。放在这里而不是绑 ListBox.SelectionChanged：
        // 那个事件在枚举/刷新过程中会连发多次，这里是"这一轮结算完了"的唯一时点。
        SelectionChanged?.Invoke(this);
    }

    /// <summary>
    /// 每次菜单弹出时回填键位提示。
    ///
    /// 必须在 <c>Opened</c> 而不是 <c>Loaded</c> 做：菜单项的命令是经由
    /// <see cref="System.Windows.Controls.Primitives.Popup.PlacementTarget"/> 拿窗口的，
    /// 而 PlacementTarget 只在弹出时才有值。加载阶段调用的结果是一律拿到 null，
    /// 键位提示静默为空——界面上看不出错，只能靠下面的日志兜底。
    /// </summary>
    private void OnContextMenuOpened(object sender, RoutedEventArgs e) => ApplyMenuGestures();

    /// <summary>
    /// 状态栏的「取消」（FR-6.16）。只发取消信号，收尾（复位行状态、给结论文案）
    /// 仍由视图模型在扫描返回后统一做——两处都收尾会出现"取消后又被改回已完成"。
    /// </summary>
    private void OnCancelFolderSize(object sender, RoutedEventArgs e) => _viewModel?.CancelFolderSizeScan();

    /// <summary>
    /// 列表上的 Esc：正在算文件夹大小时取消（FR-6.16 的键盘入口），
    /// 处于搜索态时退出搜索（FR-6.13）。两者都不满足才不吃这个键——平时 Esc
    /// 在文件列表上没有语义，不能顺手吞掉，否则以后给列表加的 Esc 行为会莫名失效。
    ///
    /// 计算中的优先级更高：用户刚点了"计算大小"，此刻按 Esc 想停的多半是那件事，
    /// 而不是清掉搜索词。
    /// </summary>
    private void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (_viewModel is { IsCalculatingSize: true })
        {
            _viewModel.CancelFolderSizeScan();
            e.Handled = true;
            return;
        }

        if (_viewModel is { IsSearching: true })
        {
            _viewModel.ClearSearch();
            e.Handled = true;
        }
    }

    private void ApplyMenuGestures()
    {
        if (FileList.ContextMenu is not { } menu) return;

        foreach (object item in menu.Items)
        {
            if (item is not MenuItem menuItem) continue;

            // 颜色子菜单的项来自标签定义。x:Static 绑定只在首次求值时读一次，
            // 用户在设置里新建或删除了标签，这里必须重设一遍才看得见。
            // 顺手跳过手势检查：它本来就没有 Command（点击走 OnTagColorPicked）。
            if (HasMenuTag(menuItem, MenuTagTagMenu))
            {
                menuItem.ItemsSource = ViewModels.TagPresets.Colors;
                continue;
            }

            if (menuItem.Command is not Commands.CommandAdapter adapter)
            {
                // 绑定名字写错时 Command 为 null，菜单项会静默失效——界面上完全看不出，
                // 只能在这里留痕。正常情况一行都不会写。
                MainWindow.WriteKeyLog($"menu unbound: header=\"{menuItem.Header}\"");
                continue;
            }

            menuItem.InputGestureText = Input.KeyBindings.Gestures.TryGetValue(adapter.Source.Id, out string? text)
                ? text
                : string.Empty;
        }
    }

    // 菜单项的作用域标记。写进 Tag，多个标记用空格分隔：
    //   TagMenu   —— 颜色子菜单，展开前要重设一遍 ItemsSource；
    //   ItemScope —— 针对选中条目，空白处右键时收起。
    private const string MenuTagTagMenu = "TagMenu";
    private const string MenuTagItemScope = "ItemScope";

    private static bool HasMenuTag(MenuItem item, string tag)
        => item.Tag is string text
           && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(tag);

    /// <summary>
    /// 右键落在列表空白处时，把「针对条目」的菜单项收起。
    ///
    /// 收成 Collapsed 而不是置灰：一整排灰项既不解释"为什么不能用"，又把真正可做的
    /// 目录级操作（新建、粘贴）挤到很远。收起之后菜单本身变短，看到的就是"这里能做哪些事"。
    /// 「复制完整路径」不在收起之列——没有选中项时它复制当前目录，见 <see cref="CanCopyPath"/>。
    ///
    /// 顺带收拾空出来的分隔线：开头一条、连着两条、结尾一条都没有意义。
    /// </summary>
    private void ApplyMenuScope(bool hasSelection)
    {
        if (FileList.ContextMenu is not { } menu) return;

        foreach (object item in menu.Items)
        {
            if (item is MenuItem menuItem && HasMenuTag(menuItem, MenuTagItemScope))
            {
                menuItem.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // 分隔线：前面还没有可见项、或上一条可见的就是分隔线，都不留。
        bool sawItem = false;
        bool prevSeparator = false;
        foreach (object item in menu.Items)
        {
            switch (item)
            {
                case MenuItem { Visibility: Visibility.Visible }:
                    sawItem = true;
                    prevSeparator = false;
                    break;

                case Separator separator:
                    bool keep = sawItem && !prevSeparator;
                    separator.Visibility = keep ? Visibility.Visible : Visibility.Collapsed;
                    prevSeparator = keep;
                    break;
            }
        }

        // 收尾：末尾的可见元素是分隔线的话去掉——"更多选项"若被隐藏才会出现，防一手。
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is not UIElement element || element.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (element is Separator) element.Visibility = Visibility.Collapsed;
            break;
        }
    }

    /// <summary>右键先选中行再弹菜单：不选中的话菜单里的操作对象是上一批选中项。</summary>
    private void OnFileRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source) is not { } item) return;

        if (!item.IsSelected)
        {
            FileList.SelectedItems.Clear();
            item.IsSelected = true;
        }

        // Shift+右键直接给系统菜单（FR-6.9 的显式入口之一）。
        // 位置取本次点击的屏幕点，不用鼠标当前位置——后者在长任务里可能已经飘走。
        // 弹出要推迟到本次鼠标事件处理完之后，否则右键松开这一下会落到系统菜单上，
        // 被它当成"点了别处"关掉。
        //
        // 只把 Down 标成 Handled 拦不住自建菜单：自建菜单是右键**抬起**时才弹的，
        // 具体时机由内部的 PopupControlService 决定。所以另置一个一次性标记，
        // 等 ContextMenuOpening 真的来了再取消它（见 ShellMenu.cs）。
        _shellMenuPending = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (_shellMenuPending)
        {
            e.Handled = true;
            Point screen = PointToScreen(e.GetPosition(this));
            QueueShellContextMenu((int)screen.X, (int)screen.Y, extendedVerbs: true);
            return;
        }

        // 自建菜单的「更多选项…」要用同一个点，才能弹在用户右键的位置
        _contextMenuScreenPoint = PointToScreen(e.GetPosition(this));
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source) is not { DataContext: FileEntry entry }) return;

        OpenEntry(entry);
        e.Handled = true;
    }

    /// <summary>
    /// 中键点文件夹 → 新标签打开（FR-2.10）。与资源管理器一致：
    /// 中键在列表里表示"另开一处看它"，不改变当前选中项。
    /// </summary>
    private void OnFileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        if (HitFolder(e.OriginalSource) is not { } folder) return;

        e.Handled = true;
        RequestNewTab(folder);
    }

    /// <summary>
    /// `Ctrl+左键` 点文件夹 → 新标签打开（FR-2.10）。挂在**松开**上而不是按下：
    /// `Ctrl+拖拽` 是"强制复制"，按下就开标签会让每一次 Ctrl 拖拽都多出一个标签。
    /// 判据是 <see cref="_dragArmed"/> —— 只要中途真的拖起来了，它就被清掉了。
    ///
    /// 代价：文件夹不能再用 `Ctrl+点击` 多选（文件不受影响）。要选多个文件夹用 `Shift+点击`
    /// 或 `Ctrl+A`，见 docs/shortcuts.md §1。选中动作在按下时就被拦掉了
    /// （见 <see cref="OnFileDragStart"/>），所以不会出现"选中了一个、同时冒出个标签"。
    /// </summary>
    private void OnFileLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 框选收尾（FR-6.7）。选中结果在拖动过程中已经实时给过了，
        // 这里只收起矩形；不能继续往下走"单击打开"——那次按下的起点是空白，
        // 没有"打开哪一项"这回事。
        if (_bandArmed)
        {
            // 空白处单击（没拉出框）= 取消选择。ListBox 自己不做这件事：
            // 点空白命中的是 ScrollViewer / ItemsPresenter，Selector 只在命中
            // ListBoxItem 时才改选中，所以这里必须显式清一次。
            // Ctrl+点空白保留原选中——那是"接着加选"的准备动作，清掉反而碍事。
            bool dragged = _bandVisible;
            EndRubberBand();

            if (!dragged && (Keyboard.Modifiers & ModifierKeys.Control) == 0 &&
                FileList.SelectedItems.Count > 0)
            {
                FileList.UnselectAll();
            }

            e.Handled = true;
            return;
        }

        bool wasClick = _dragArmed;
        _dragArmed = false;

        if (!wasClick) return;

        // Ctrl+点击文件夹 → 新标签打开（判据见上面那段）
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (HitFolder(e.OriginalSource) is not { } folder) return;

            e.Handled = true;
            RequestNewTab(folder);
            return;
        }

        // 单击打开（FR-5.2.5）。挂在"松开"而不是"按下"：拖拽起手时 _dragArmed
        // 已被清掉，不会出现"拖到一半顺手把源文件打开了"。
        if (TryOpenOnSingleClick(e)) e.Handled = true;
    }

    private static FileEntry? HitFolder(object? source)
        => source is DependencyObject node
           && FindAncestor<ListBoxItem>(node) is { DataContext: FileEntry { IsDirectory: true } folder
           }
            ? folder
            : null;

    private void RequestNewTab(FileEntry folder)
        => OpenInNewTabRequested?.Invoke(folder.FullPath, App.Settings.Behavior.OpenNewTabInBackground);

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }
}
