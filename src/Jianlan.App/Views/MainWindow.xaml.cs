using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Jianlan.App.Commands;
using Jianlan.App.Diagnostics;
using Jianlan.App.Services;
using Jianlan.App.Shell;
using Jianlan.App.ViewModels;
using Jianlan.Core.Commands;
using Jianlan.Core.Diagnostics;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Navigation;
using Jianlan.Core.Settings;

using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// 窗口壳。持有导航栏、命令表、快捷键钩子、系统热键与会话保存，
/// 浏览区（工具栏/地址栏/列头/列表/状态栏）在 <see cref="Pane"/> 里。
///
/// 分工边界：
///   · 窗口级：窗口标题与尺寸、导航栏、快捷键派发、Ctrl+, 热键、设置窗口、启动计时、会话
///   · 窗格级：一切"作用于某个目录"的东西，见 PaneView
///
/// 命令表现阶段仍在这里（按钮用 <c>AncestorType=Window</c> 取命令），
/// 但每个命令的 Execute 都指向 <see cref="Pane"/>，所以语义已经从"窗口的命令"
/// 变成"作用于当前窗格的命令"。下一步搬进 PaneView 时不需要改调用点。
/// </summary>
public partial class MainWindow : Window
{
    private readonly CommandRegistry _commands = new();
    private readonly TabHostViewModel _tabs;
    private bool _flushed;

    /// <summary>最近访问（FR-1.1.2）。与主页、导航栏共用同一份。</summary>
    private readonly VisitHistory _history;

    /// <summary>收藏（FR-7）。主页收藏区与导航栏收藏区同源（FR-1.1.5）。</summary>
    private readonly BookmarkStore _bookmarks;

    /// <summary>主页视图模型。窗口级单例，所有主页标签共用。</summary>
    private readonly HomeViewModel _home;

    /// <summary>标签结果页（FR-4.1.5）。同为主页那种"窗口级单份、多个标签共用"的形态。</summary>
    private readonly TagResultsViewModel _tagResults = new();

    /// <summary>
    /// 导航栏的完整内容（系统节点 + 收藏，FR-1.2 + FR-7.3）。
    /// 系统节点由窗格视图模型提供：已知文件夹的解析与刷新归它管，这里不复制一份。
    /// </summary>
    private readonly NavPanelViewModel _nav;

    /// <summary>本次运行有没有新增过访问记录——决定退出时要不要多写一次设置。</summary>
    private bool _visitedSinceSave;

    /// <summary>
    /// 双窗格是否开启（FR-3.3）。形态是窗口级而不是标签级：开着分屏切标签，
    /// 新标签也该是分屏；否则"切个标签分屏就没了"会被当成 bug。
    /// </summary>
    private bool _splitEnabled;

    /// <summary>
    /// 本次运行是否使用自绘标题栏（FR-10.11）。取值是**启动那一刻**的快照，不是实时读设置：
    /// WindowStyle 建完窗口就改不了，设置页改完只提示重启。若这里跟着设置实时变，
    /// 用户改完开关再切一下标签栏位置，就会得到"标签让位了、系统标题栏还在"的错位。
    /// </summary>
    private bool _immersiveTitleBar;

    /// <summary>
    /// 撤销栈（FR-6.8）。窗口级、单层，两个窗格共用一份。
    /// 回收站还原走 Shell，具体实现放在 <c>Shell.RecycleBin</c>。
    /// </summary>
    private readonly UndoStack _undo = new(new Shell.RecycleBin());

    /// <summary>
    /// 首帧之后才允许读盘（P1 首帧优先）。标签切换时的懒加载要靠它区分
    /// "启动期还没开始"与"运行期用户切过去了"。
    /// </summary>
    private bool _started;

    /// <summary>
    /// 正在按当前目录同步导航栏高亮：此时的选择变化不是用户操作。
    /// </summary>
    private bool _syncingNavSelection;

    /// <summary>
    /// 是不是"在新窗口打开"开出来的副窗口（FR-2.12）。
    ///
    /// 副窗口不写会话、不记窗口位置、不注册系统热键、不弹"上次异常退出"提示：
    /// 那四件事都属于"主窗口代表这次运行"。不区分的话，用户开一个副窗口看一眼再关掉，
    /// 下次启动恢复出来的就只有那个副窗口里的一条目录——原来那十二个标签全没了。
    /// </summary>
    private readonly bool _isSecondaryWindow;

    private string? _initialPath;
    private string? _initialSecondPath;

    // 窗口级命令只有这些。窗格级命令（刷新、文件操作、视图切换）
    // 在 PaneView 自己的表里，见 PaneView.Commands.cs。
    public CommandAdapter ThemeCommand { get; }

    /// <summary>
    /// 收藏命令，交给窗格的右键菜单用（FR-7.1）。
    ///
    /// 命令本身留在窗口：收藏库、导航栏、主页三处要一起刷，这些都属于窗口级状态。
    /// 窗格只拿一份 adapter 用来在菜单里显示标题与 Ctrl+D 手势——菜单的 DataContext
    /// 是窗格自己，所以要有一个窗格可见的属性指向它。
    /// </summary>
    public CommandAdapter BookmarkCommand { get; }

    public MainWindow() : this(null, null)
    {
    }

    /// <summary>
    /// 开一个只装指定目录的窗口（FR-2.12「在新窗口打开」）。
    ///
    /// 为什么不用新进程：单实例守卫会把第二个进程的启动参数转交给已有窗口，
    /// 结果"开新窗口"变成"让已有窗口跳过去"。所以副窗口必须是同进程的第二个窗口。
    /// 两个参数都为空时就是主窗口，走平时的会话恢复。
    /// </summary>
    /// <summary>
    /// 任务栏与标题栏的图标，直接从 exe 的图标资源取，不走 WPF 的 ImageSource→HICON 那条路。
    ///
    /// 为什么要绕：设了 <c>Window.Icon</c> 之后，窗口自己 WM_GETICON 查得到句柄、取出来的位图
    /// 也确实是品牌蓝（实测 72% 像素是 #245EEB 系），但任务栏按钮画的仍是系统默认图。
    /// 换成纯红色块再试，任务栏依旧不变；同一台机器上记事本这类原生程序显示正常。
    /// 也就是说任务栏没有接受 WPF 生成的那个 HICON。
    ///
    /// 改成用 LoadImage 从 exe 自己的 RT_GROUP_ICON（csproj 的 ApplicationIcon 生成在 32512）
    /// 取句柄，这是标准的图标句柄，标题栏和任务栏都认。取不到就保持 WPF 的行为不动。
    /// </summary>
    private void ApplyIconFromExeResources()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        IntPtr instance = GetModuleHandle(null);

        // 按当前 DPI 要尺寸：150% 缩放下任务栏按钮是 48，给 32 会被放大发虚
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int bigSize = (int)Math.Round(32 * scale);
        int smallSize = (int)Math.Round(16 * scale);

        IntPtr big = LoadImage(instance, IconResourceId, ImageIcon, bigSize, bigSize, 0);
        IntPtr small = LoadImage(instance, IconResourceId, ImageIcon, smallSize, smallSize, 0);

        if (big != IntPtr.Zero) _ = SendMessage(hwnd, WmSetIcon, IconBig, big);
        if (small != IntPtr.Zero) _ = SendMessage(hwnd, WmSetIcon, IconSmall, small);
    }

    /// <summary>csproj 里 <c>ApplicationIcon</c> 生成的组图标编号，即标准 IDI_APPLICATION。</summary>
    private const string IconResourceId = "#32512";

    private const uint ImageIcon = 1;
    private const int WmSetIcon = 0x0080;

    private static readonly IntPtr IconBig = new(1);
    private static readonly IntPtr IconSmall = IntPtr.Zero;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    public MainWindow(string? initialPath, string? initialSecondPath)
    {
        _isSecondaryWindow = !string.IsNullOrEmpty(initialPath);
        _initialPath = initialPath;
        _initialSecondPath = initialSecondPath;

        InitializeComponent();

        // 自绘标题栏（FR-10.11）。必须在首帧前定：WindowStyle 一旦建了窗口就改不了，
        // 运行期切换要重建 HWND，所以设置页改完只提示"重启生效"。
        ApplyWindowChrome();

        // 导航栏宽度取设置里的值（FR-1.2.4）。必须在首帧前套上：放在 Loaded 里会让
        // 侧栏先按默认宽度画一帧、再跳到用户拖过的宽度，看起来像闪了一下。
        ApplyNavLayout();

        // 标签集合归窗口：两个窗格共用同一排标签（一个标签 = 一对窗格），
        // 标签不能属于某一边。浏览区的视图模型由它创建、由窗格 Attach。
        _splitEnabled = App.Settings.Layout.SplitEnabled;

        // 最近访问与收藏都是设置文件里的一段，各只建一个实例：
        // 主页、导航栏、窗格的访问回调必须读写同一份，多建一个就会"这边加完那边看不见"。
        _history = new VisitHistory(App.Settings.History, () => App.Settings.Privacy.HistoryLimit);
        _bookmarks = new BookmarkStore(App.Settings.Bookmarks);
        _home = new HomeViewModel(_history, _bookmarks);

        // 导航栏：系统节点从窗格视图模型取（它管已知文件夹的解析），收藏从本窗口的存储取，
        // 标签从应用级标签库取（同一次运行里所有窗口共用一份）。
        // 用委托而不是直接传 QuickAccess，因为此刻还没有活动标签，Vm 取不到东西。
        _nav = new NavPanelViewModel(_bookmarks,
            () => _tabs.Active is { } t ? t.ActivePane.QuickAccess : [], App.Tags,
            thisPcExpanded: App.Settings.Layout.ThisPcExpanded,
            onThisPcExpandedChanged: expanded =>
            {
                // FR-1.2.7：节点展开状态属于"用户调过一次就要记住"的那类设置
                App.Settings.Layout.ThisPcExpanded = expanded;
                App.SaveSettings();
            },
            // FR-1.2.9：勾掉的位置不进列表。传委托而不是传当时的快照，
            // 每次重建都问一次最新值，勾完立刻生效、不必重启。
            hiddenPlaces: () => App.Settings.Layout.NavHiddenPlaces);
        QuickAccessList.ItemsSource = _nav.Rows;

        // 新建标签走「设置 → 新标签页默认目录」，没设就是主页（FR-2.1）；关掉最后一个标签
        // 仍回"上次的目录"（FR-2.8 的语义，不该被这条新设置顺手改掉）
        _tabs = new TabHostViewModel(App.Settings, ResolveNewTabPath, ResolveStartPath, RecordVisit, App.Tags);
        _tabs.ActiveChanged += OnActiveTabChanged;
        _tabs.TabKindChanged += OnTabKindChanged;
        _tabs.CloseWindowRequested += Close;
        TabBar.Host = _tabs;
        TabBar.OpenDropped += OnTabsDropped;   // 拖到标签页打开（FR-6.11）
        TabBar.OpenInNewWindowRequested += OpenTabInNewWindow;   // 在新窗口打开（FR-2.12）

        TagResults.Attach(_tagResults);
        TagResults.ItemActivated += OnTagResultActivated;

        if (_isSecondaryWindow) OpenInitialTab();
        else RestoreTabs();

        // 导航栏的行要等系统节点（QuickAccess）建好之后才能拼：收藏区排在它们下面。
        _nav.Rebuild();
        StartupTrace.Mark("vm");
        StartupTrace.Mark("xaml");

        // 窗口级命令。窗格级的那二十条在 PaneView.Commands.cs 里，
        // 因为它们读的是"当前目录"与"当前选中项"——双窗格下这两个状态各有一份。
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CycleTheme,
            TitleFactory = () => Strings.Command_CycleTheme,
            // 不能用 Ctrl+T / Ctrl+Shift+T：docs/shortcuts.md 已把它们分配给标签页
            Gesture = "Ctrl+Shift+D",
            Execute = () => App.CycleTheme(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.OpenSettings,
            TitleFactory = () => Strings.Command_OpenSettings,
            Gesture = "Ctrl+,",
            Execute = OpenSettings,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Bookmark,
            TitleFactory = () => Strings.Command_Bookmark,
            Gesture = "Ctrl+D",
            Execute = BookmarkCurrent,
        });

        ThemeCommand = new CommandAdapter(_commands.Get(CommandIds.CycleTheme));
        BookmarkCommand = new CommandAdapter(_commands.Get(CommandIds.Bookmark));

        RegisterTabCommands();
        RegisterSplitCommands();
        RegisterPreviewCommands();
        InitializePreview();

        // 分屏开关：工具栏那颗按钮在窗格里，真正的开关在窗口（FR-3.3）。
        // 只有主窗格显示按钮——分屏是窗口形态，两颗按钮会看起来像"两个窗格各分各的"。
        Pane.SplitToggleRequested += ToggleSplit;
        Pane2.ShowSplitButton = false;

        // 右键菜单里的「收藏」：命令在窗口（要一起刷导航栏与主页），
        // 但菜单的 DataContext 是窗格，所以两个窗格各拿一份 adapter 指过去。
        Pane.BookmarkCommand = BookmarkCommand;
        Pane2.BookmarkCommand = BookmarkCommand;

        // 第二窗格带自己的槽位（FR-3.7）：列配置与视图/排序按"右侧"各存一份
        Pane2.Slot = PaneSlot.Secondary;

        // 撤销栈是窗口级的，两侧共用（FR-6.8）：撤销的是"最近一次操作"，
        // 不是"这个窗格的操作"。两个窗格各持一份会让 Ctrl+Z 的结果随焦点漂移。
        Pane.Undo = _undo;
        Pane2.Undo = _undo;

        // 槽位是构造之后才设的（XAML 要求无参构造），而 PaneView 构造时已经按"第一窗格"
        // 套过一次列宽。这里立刻重套一遍，否则第二窗格要等到第一次尺寸变化才对上配置。
        Pane2.ApplyAppearance(App.Settings);

        // 哪个窗格的列表拿到焦点，哪个就是活动窗格（FR-3.5）。
        Pane.Activated += OnPaneActivated;
        Pane2.Activated += OnPaneActivated;

        // 主页（FR-1.1）：卡片点击只发意图，导航归窗口——主页不知道自己在哪个标签里。
        Home.OpenRequested += OpenFromHome;
        Home.NewTabRequested += NewTab;
        Home.NewFolderRequested += NewFolderFromHome;
        Home.WorkspaceRequested += ws => OpenWorkspace(ws);
        Home.WorkspaceCommandRequested += OnHomeWorkspaceCommand;
        Home.SaveWorkspaceRequested += SaveCurrentWorkspace;
        Home.Attach(_home);

        // 主页改了设置里的数据（清空历史）要立刻落盘：等退出再写的话，中途崩溃就白清了
        _home.SettingsChanged += () => App.SaveSettings();

        // 已知文件夹后台校验可能改了下载夹指向（用户重定向）。侧栏与主页都要跟着换，
        // 否则会出现"标题栏写着新下载夹、侧栏还指着旧的"。
        KnownFolderService.Changed += OnKnownFoldersChanged;

        // 中键 / Ctrl+左键点文件夹 → 新标签（FR-2.10）。窗格只发意图，新建标签归窗口；
        // 带着"是哪个窗格发的"一起转交——到达上限时要退回到发起的那一侧导航。
        Pane.OpenInNewTabRequested += (path, background) => OpenInNewTab(Pane, path, background);
        Pane2.OpenInNewTabRequested += (path, background) => OpenInNewTab(Pane2, path, background);

        // 分屏的行列先摆好：此时还没有标签，只是把两列/两行的宽度归位
        ApplySplitLayout();
        ApplyPreviewLayout();

        // 分屏状态要立刻写进两个窗格：窗格自己拿它决定"进目录是导航还是开新标签"
        // （见 PaneView.OpenEntry）。等到 OnLoaded 才同步的话，首帧之前的那次双击会走错分支。
        SyncSplitButton();
        ApplyTabStripPosition();
        StartupTrace.Mark("cmd");

        // 快捷键不用 Window.InputBindings：KeyGesture 对 , 这类 OEM 键的匹配依赖布局，
        // 实测 Ctrl+, 不触发。改为一张显式表 + PreviewKeyDown 派发，见 Input/KeyBindings.cs。
        PreviewKeyDown += OnPreviewKeyDown;

        // 目录监听的订阅在 PaneView.Attach 里——监听的目标是"当前窗格正在看的目录"，
        // 跟着视图模型走，不再由窗口替它挂。

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        CompositionTarget.Rendering += OnFirstRender;

        // 上次没退干净就在首帧里带上一条提示（FR-10.6）。构造期做，首帧即终态。
        // 副窗口不弹：那是"这次运行"的状态，开第二个窗口时已经有人提醒过了。
        if (!_isSecondaryWindow) ShowSessionBannerIfNeeded();

        // 记录最后目录与标签列表供下次启动使用。这个处理器先于 App 的保存处理器执行，
        // 因此退出时写到盘上的是本窗口刚更新的值。
        Closing += (_, _) =>
        {
            // 热键是系统级资源，进程退出前主动交还，避免句柄被回收前的短暂占用
            UnregisterSettingsHotkey();

            // 预览持有的位图与缩略图缓存要主动丢掉（FR-9.2.6）：
            // 进程退出时操作系统会回收，但"退出前的最后一刻内存很高"会让性能走查误判
            _preview.Release();

            // 副窗口不写会话（FR-2.12）：会话是"下次启动开什么"，
            // 由主窗口代表这次运行决定。副窗口也写的话，关掉它就把主窗口那十二个标签顶掉了。
            if (_isSecondaryWindow) return;

            if (!string.IsNullOrEmpty(Vm.CurrentPath))
            {
                App.Settings.Session.LastPath = Vm.CurrentPath;
            }

            // 会话恢复（FR-2.11）：只记路径与活动位置，不记滚动与选中。
            // 三份列表必须一起取：分两次遍历可能因为期间有人关了标签而对不齐。
            List<string> paths = _tabs.SnapshotPaths();
            App.Settings.Session.Tabs = paths;
            App.Settings.Session.TabSecondPaths = _tabs.SnapshotSecondPaths();
            App.Settings.Session.TabPinned = _tabs.SnapshotPinned();
            App.Settings.Session.ActiveTabIndex = Math.Clamp(_tabs.ActiveIndex, 0, Math.Max(0, paths.Count - 1));

            // 最近访问只在这条路径上落盘：导航一次写一次文件纯属浪费，
            // 而 App 的兜底保存只在"窗口尺寸/最后目录/标签列表有变"时才写，
            // 光逛了几个目录就退出的会话会被它判成"什么都没改"。
            if (_visitedSinceSave)
            {
                App.SaveSettings();
                _visitedSinceSave = false;
            }
        };
    }

    /// <summary>当前标签。窗口构造期已有标签，因此这里恒不为 null。</summary>
    private TabViewModel Tab => _tabs.Active
        ?? throw new InvalidOperationException("窗口还没有活动标签：RestoreTabs 必须先跑一次。");  // i18n-ignore：开发者异常，不进界面

    /// <summary>命令与快捷键作用的窗格视图模型 = 当前标签里焦点所在的那个窗格。</summary>
    private PaneViewModel Vm => Tab.ActivePane;

    /// <summary>命令与快捷键作用的视图 = 焦点窗格对应的那个 PaneView。</summary>
    private PaneView ActiveView => ViewFor(Tab.ActivePane);

    /// <summary>某个窗格视图模型对应的视图。第二窗格未开时一律回主窗格。</summary>
    private PaneView ViewFor(PaneViewModel pane)
        => Tab.Secondary is { } second && ReferenceEquals(pane, second) ? Pane2 : Pane;

    // ── 标签页（FR-2） ──────────────────────────────────────────────────────

    /// <summary>
    /// 建立初始标签。会话恢复的全部读盘动作都在这里避开：只给路径、不枚举目录，
    /// 非活动标签一直懒到被激活才读（FR-2.11「不阻塞首帧」的实质）。
    /// 12 个标签的构造成本是 12 个对象 + 每个标签两三次 <c>Directory.Exists</c>。
    /// </summary>
    private void RestoreTabs()
    {
        // FR-8.4：设了"启动时自动恢复"的工作区就按它来，会话恢复让路（设置里互斥）。
        // 这是唯一在首帧读 workspaces.json 的路径，且由用户显式开启。
        if (RestoreStartupWorkspace()) return;

        SessionSettings session = App.Settings.Session;

        // 三份平行的列表（路径 / 第二窗格 / 固定标记）先按索引配对，再逐条筛掉失效路径。
        // 反过来做——先过滤路径、再拿新下标回原列表取——会在"中间某个目录被删了"时整体错位：
        // 第 3 个标签的固定标记落到第 4 个标签上，第 5 个标签凭空多出一个第二窗格。
        var restored = new List<(string Path, string Second, bool Pinned)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (App.Settings.Behavior.RestoreLastSession)
        {
            for (int i = 0; i < session.Tabs.Count; i++)
            {
                string path = session.Tabs[i];

                // 主页标签用哨兵表示（见 TabHostViewModel.HomeMarker）：它没有路径，
                // 不能走 Directory.Exists 校验，否则每次启动主页都会被当成坏数据丢掉。
                // 去重集合里放哨兵本身：同时恢复出两个主页标签没有意义。
                if (path == TabHostViewModel.HomeMarker)
                {
                    if (seen.Add(TabHostViewModel.HomeMarker))
                    {
                        restored.Add((TabHostViewModel.HomeMarker, string.Empty,
                            i < session.TabPinned.Count && session.TabPinned[i]));
                    }

                    continue;
                }

                if (!Directory.Exists(path) || !seen.Add(path)) continue;

                restored.Add((
                    path,
                    i < session.TabSecondPaths.Count ? session.TabSecondPaths[i] : string.Empty,
                    i < session.TabPinned.Count && session.TabPinned[i]));
            }
        }

        if (restored.Count == 0)
        {
            // 没有可恢复的会话，或恢复项全已失效。两条落地路径：
            //   · 命令行带了目录 → 直接开那个目录（用户双击进来的，不该被主页挡一道）
            //   · 否则开主页（FR-1.1.1"启动后展示主页"）
            if (!string.IsNullOrEmpty(App.StartupDirectory))
            {
                _tabs.Activate(_tabs.AddPending(ResolveStartPath()));
            }
            else
            {
                _tabs.Activate(_tabs.AddPendingHome());
            }

            return;
        }

        foreach ((string path, string second, bool pinned) in restored)
        {
            TabViewModel tab = path == TabHostViewModel.HomeMarker
                ? _tabs.AddPendingHome()
                : _tabs.AddPending(path);

            tab.IsPinned = pinned;

            // 第二窗格只记路径：窗格本身等"这个标签被激活且开着分屏"时才建，
            // 否则启动时就把 12 个标签的另一半全建出来，首帧预算立刻见底。
            if (tab.Kind == TabKind.Files && Directory.Exists(second)) tab.PrepareSecondaryPending(second);
        }

        _tabs.ActivateAt(Math.Clamp(session.ActiveTabIndex, 0, restored.Count - 1));
    }

    /// <summary>
    /// 副窗口的初始标签（FR-2.12）：只装"在新窗口打开"时那条目录（分屏时两条）。
    /// 不读会话、不开主页——用户要的是"把这个目录单独拿出来看"。
    /// </summary>
    private void OpenInitialTab()
    {
        string path = _initialPath ?? ResolveStartPath();

        // 带了两条路径就打开分屏：只留一条的话"在新窗口打开"会看起来丢了半个标签。
        // 必须在标签激活之前定下来——激活那一刻就要按这个形态建第二窗格。
        if (!string.IsNullOrEmpty(_initialSecondPath)) _splitEnabled = true;

        // 只建壳、不读盘：与 RestoreTabs 同一条路（首帧不枚举目录）。
        // 这里不能用 Add(activate: true)：那会在窗口构造到一半时触发绑定。
        TabViewModel tab = _tabs.AddPending(path);

        if (!string.IsNullOrEmpty(_initialSecondPath) && Directory.Exists(_initialSecondPath))
        {
            tab.PrepareSecondaryPending(_initialSecondPath);
        }

        _tabs.ActivateAt(0);
    }

    /// <summary>
    /// 在新窗口打开这个标签（FR-2.12）。分屏时两条路径一起带过去——
    /// 只带一条会让"在新窗口打开"看起来像丢了半个标签。
    /// </summary>
    private void OpenTabInNewWindow(TabViewModel tab)
    {
        (string primary, string secondary) = tab.SnapshotPaths();

        if (string.IsNullOrEmpty(primary))
        {
            // 主页标签没有路径：没有"这个目录"可拿出去，直接说明比开了个空窗口好
            Vm.SetStatus(Strings.Status_NewWindowNoPath);
            return;
        }

        var window = new MainWindow(primary, string.IsNullOrEmpty(secondary) ? null : secondary);
        window.Show();
    }

    private void RegisterTabCommands()
    {
        _commands.Register(new AppCommand
        {
            Id = CommandIds.NewTab,
            Gesture = "Ctrl+T",
            Execute = NewTab,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CloseTab,
            Gesture = "Ctrl+W",
            Execute = () => _tabs.Close(_tabs.Active),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ReopenTab,
            Gesture = "Ctrl+Shift+T",
            Execute = _tabs.ReopenClosed,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.NextTab,
            Gesture = "Ctrl+Tab",
            Execute = _tabs.ActivateNext,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.PreviousTab,
            Gesture = "Ctrl+Shift+Tab",
            Execute = _tabs.ActivatePrevious,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.LastTab,
            Gesture = "Ctrl+9",
            Execute = _tabs.ActivateLast,
        });

        // Ctrl+1…8：跳到第 N 个。Ctrl+9 是"最后一个"，不参与这个循环。
        for (int n = 1; n <= 8; n++)
        {
            int index = n - 1;
            _commands.Register(new AppCommand
            {
                Id = CommandIds.GotoTab(n),
                Gesture = $"Ctrl+{n}",
                Execute = () => _tabs.ActivateAt(index),
            });
        }
    }

    /// <summary>
    /// Ctrl+T：新开一个标签（FR-2.1）。
    ///
    /// 到 12 个上限（FR-2.5）时**必须说话**：`+` 按钮此刻只是变灰，键盘用户看不到那一点变化，
    /// 于是"按了没反应"会被直接理解成"标签功能坏了"。状态栏那句提示是这里唯一能给的反馈，
    /// 它会被下一次导航覆盖，但足够回答"我刚才那下为什么没生效"。
    /// </summary>
    private void NewTab()
    {
        // FR-2.1：新标签默认开主页；设置里指定了目录才开那个目录。
        // 判断放在这里而不是 ResolveNewTabPath 里：那个方法要返回一个"能打开的目录"，
        // 让它在"主页"这种非目录形态下返回空串，会把"设置没填"和"设置填错了"混成一种。
        TabViewModel? created = string.IsNullOrWhiteSpace(App.Settings.General.NewTabPath)
            ? _tabs.AddHome()
            : _tabs.Add();

        if (created is not null) return;

        Vm.SetStatus(string.Format(Strings.Status_TabLimit, SessionSettings.MaxTabs));
        AppLog.Warn("tab.limit", "E6002", $"已达 {SessionSettings.MaxTabs} 个标签，Ctrl+T 不新建");  // i18n-ignore：只进 app.log
    }

    /// <summary>
    /// 把某个目录开进新标签（FR-2.10）。<paramref name="background"/> 为 true 时只建不切，
    /// 让用户连续开一批目录而不离开当前位置。
    ///
    /// 分屏时新标签接着"没被点的那一侧"走：标签是一对窗格，若第二窗格照旧回落到主窗格路径，
    /// 从右边点一下文件夹，左边也会跟着变成同一处——看起来就是"两边一起跳了"。
    /// 所以把另一侧的快照带进新标签，并把焦点留在发起的那一侧（从右边点的，焦点还在右边）。
    ///
    /// 到 12 个上限（FR-2.5）时不新建，直接把路径交给**发起的那一侧窗格**导航——
    /// 比"按键没反应"有用，也比弹一句"标签已满"轻。所以这里带着
    /// <paramref name="source"/>：分屏时中键点右窗格，退回导航的必须是右窗格。
    /// </summary>
    private void OpenInNewTab(PaneView source, string path, bool background)
    {
        TabViewModel current = Tab;
        bool fromSecondary = _splitEnabled
                             && current.Secondary is not null
                             && ReferenceEquals(current.ActivePane, current.Secondary);

        string sibling = string.Empty;
        if (_splitEnabled && current.Secondary is { } other)
        {
            sibling = fromSecondary ? current.Primary.CurrentPath : other.CurrentPath;
        }

        TabViewModel? created = fromSecondary
            ? _tabs.Add(sibling, activate: !background, secondaryPath: path, activePane: 1)
            : _tabs.Add(path, activate: !background, secondaryPath: sibling);

        if (created is not null)
        {
            // 切过去了才抢焦点；后台打开时用户还在原处按着 Ctrl 连点，不该被拽走
            if (!background) ViewFor(created.ActivePane).FocusList();
            return;
        }

        // 状态栏必须出声：满额时"双击文件夹没开新标签"与"双击坏了"在用户眼里一样。
        // 提示落在这个窗格自己的状态栏上——分屏时右边点的那一下，提示就该在右边。
        source.ViewModel?.SetStatus(string.Format(Strings.Status_TabLimit, SessionSettings.MaxTabs));
        AppLog.Warn("tab.limit", "E6002", $"已达 {SessionSettings.MaxTabs} 个标签，改为在当前窗格导航：{path}");  // i18n-ignore：只进 app.log
        source.NavigateTo(path);
    }

    /// <summary>
    /// 有文件夹或文件被拖到标签栏上（FR-6.11「拖到标签页/导航栏打开」）。
    ///
    /// 落点决定去哪：落在某个标签上 = 用那个标签打开（用户指着它说话），
    /// 落在空白处 = 开一个新标签。
    ///
    /// 多条路径只有第一条能进目标标签：同一个标签连着导航会只留下最后一个，
    /// 用户看到的是"拖了五个只开了一个"。所以其余各开一个新标签。
    /// 文件交给系统默认程序——拖到标签栏的语义是"打开它"，
    /// 而不是"把它的目录开出来"（那要在地址栏上拖，不是这里）。
    /// </summary>
    private void OnTabsDropped(string[] paths, TabViewModel? target)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            bool isDirectory = Directory.Exists(path);

            if (i == 0 && target is not null && isDirectory)
            {
                _tabs.Activate(target);

                // 目标标签可能是主页或标签结果页：先切回目录形态，
                // 否则导航完成了界面还停在没有列表的那一页
                target.ShowFiles();
                _ = target.ActivePane.NavigateAsync(path);
                continue;
            }

            if (isDirectory)
            {
                if (_tabs.Add(path, activate: i == 0) is null)
                {
                    // 满额时不能静默：拖进来没反应与"拖放坏了"在用户眼里一样
                    Pane.ViewModel?.SetStatus(string.Format(Strings.Status_TabLimit, SessionSettings.MaxTabs));
                    AppLog.Warn("tab.limit", "E6002", $"已达 {SessionSettings.MaxTabs} 个标签，丢弃拖入的：{path}");  // i18n-ignore：只进 app.log
                    return;
                }

                continue;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                Pane.ViewModel?.SetStatus(string.Format(Strings.Status_OpenItemFailed, Path.GetFileName(path)));
            }
        }
    }

    // ── 主页（FR-1.1） ──────────────────────────────────────────────────────

    /// <summary>
    /// 主页上点了某个入口。<paramref name="newTab"/> 为真表示用户按了 Ctrl 或中键。
    /// 当前标签内打开时，标签形态会从主页自动变成"浏览目录"（见 TabViewModel）。
    /// </summary>
    private void OpenFromHome(string path, bool newTab)
    {
        if (newTab)
        {
            // 与列表里的中键/Ctrl+点击走同一条路径：满 12 个标签时会退回当前窗格导航，
            // 而不是点了没反应。
            OpenInNewTab(ActiveView, path, background: false);
            return;
        }

        _ = NavigatePaneAsync(Vm, path);
    }

    /// <summary>
    /// 主页的"新建文件夹"（FR-1.1.4）。主页没有当前目录，落点是"新标签页默认目录"——
    /// 那张卡片就在主页上，用户的心理位置是"在下载里建一个文件夹"，与 Ctrl+T 一致。
    /// </summary>
    private void NewFolderFromHome()
    {
        string target = ResolveNewTabPath();

        TabViewModel? tab = _tabs.Add(target, activate: true);
        if (tab is null)
        {
            Vm.SetStatus(string.Format(Strings.Status_TabLimit, SessionSettings.MaxTabs));
            return;
        }

        // 新标签是懒加载的：等它把目录读完再发命令，否则"新建文件夹"会落在一个空列表上，
        // 用户看到的是一次没有任何反馈的点击。
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await NavigatePaneAsync(tab.Primary, tab.Primary.CurrentPath);
            ViewFor(tab.ActivePane).TryInvoke(CommandIds.NewFolder);
        }), DispatcherPriority.Background);
    }

    /// <summary>成功进入目录 → 记一条最近访问（FR-1.1.2）。历史关闭时什么都不做。</summary>
    private void RecordVisit(string path)
    {
        if (!App.Settings.Privacy.HistoryEnabled) return;

        _history.Record(path);
        _visitedSinceSave = true;

        // 主页正开着就顺带刷一下：用户刚点开一个目录又按 Alt+← 回主页时，
        // "最近访问"里应该已经有刚去的那个。
        if (Tab.IsHome) _home.Refresh();
    }

    /// <summary>
    /// 一键清空历史（FR-5.3.2）。设置页调用。
    /// 只清"最近访问"，不动会话、收藏与工作区——三者的清空入口各自独立，
    /// 一个按钮把它们一起删掉是最容易被当成数据丢失的一类行为。
    /// </summary>
    internal int ClearHistory()
    {
        int count = _history.Count;

        _history.Clear();
        App.SaveSettings();

        // 主页正开着就立刻变空，不用等下次进主页
        _home.Refresh();
        return count;
    }

    /// <summary>按当前上限裁剪历史（FR-5.3.4 把上限调小之后立即生效）。</summary>
    internal void TrimHistory()
    {
        if (_history.Trim() == 0) return;

        App.SaveSettings();
        _home.Refresh();
    }

    private static async Task NavigatePaneAsync(PaneViewModel pane, string path)
    {
        try
        {
            await pane.NavigateAsync(path);
        }
        catch (Exception ex)
        {
            pane.ReportError(ex);
        }
    }

    /// <summary>
    /// 已知文件夹被后台校验改了（用户重定向了下载夹、换了台机器）。重建侧栏与主页卡片，
    /// 不导航、不动当前目录——变的只是"下载指向哪里"。
    /// </summary>
    private void OnKnownFoldersChanged()
    {
        foreach (TabViewModel tab in _tabs.Tabs)
        {
            tab.Primary.RefreshQuickAccess();
            tab.Secondary?.RefreshQuickAccess();
        }

        _nav.Rebuild();
        _home.Refresh();
    }

    /// <summary>双窗格命令（FR-3.3）。开关是窗口级的：它不是某个标签的属性。</summary>
    private void RegisterSplitCommands()
    {
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ToggleSplit,
            TitleFactory = () => Strings.Command_ToggleSplit,
            Gesture = "F3",
            Execute = ToggleSplit,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.FocusOtherPane,
            TitleFactory = () => Strings.Command_FocusOtherPane,
            Gesture = "F6",
            CanExecute = () => _splitEnabled && Tab.Secondary is not null,
            Execute = FocusOtherPane,
        });
    }

    /// <summary>
    /// 活动标签变了：把两个窗格视图重新绑到新标签的那一对视图模型。
    /// 滚动位置与选中项在 <see cref="PaneView.Attach"/> 里前后搬移。
    /// </summary>
    private void OnActiveTabChanged(TabViewModel? previous, TabViewModel? current)
    {
        if (current is null) return;

        BindTab(current);

        // 释放上一个标签的条目集合（内存预算配套约束，见 performance-gate §9.5）。
        // 顺序不能反：BindTab → PaneView.Attach 会先把滚动位置与选中项写回 previous，
        // 早于它释放会把这两样状态连同条目一起丢掉。
        // 关掉的标签不在这里处理——它已经从 Tabs 移除，整份视图模型交给 GC。
        previous?.ReleaseContent();

        // 窗口标题与导航栏的绑定都挂在窗口 DataContext 上，跟着焦点窗格走
        DataContext = current.ActivePane;
        SyncNavSelection(current.ActivePane.CurrentPath);

        // 首帧之前不读盘：启动时由 OnLoaded 开始第一次导航（P1 首帧优先）
        if (_started) _ = LoadPanesAsync(current);
    }

    /// <summary>把一个标签的一到两份视图模型绑到对应的窗格视图上。</summary>
    private void BindTab(TabViewModel tab)
    {
        ApplyTabKind(tab);
        RefreshPaneFocus();
        RefreshPreview();
    }

    /// <summary>
    /// 按标签形态决定 PaneHost 里显示谁：主页标签显示 <see cref="Home"/> 并让两个窗格让位，
    /// 目录标签则把主页藏起来、窗格绑上去。
    ///
    /// 主页标签不参与分屏：主页不"位于"任何目录，右侧没有第二个位置可放。
    /// 分屏是窗口级形态，切回目录标签时仍在（见 ToggleSplit 的注释）。
    /// </summary>
    private void ApplyTabKind(TabViewModel tab)
    {
        // 主页与标签结果页没有"选中的文件"，预览那一栏先收起（不是关掉——意图还留着）
        ApplyPreviewLayout();

        if (tab.IsHome)
        {
            Home.Visibility = Visibility.Visible;
            TagResults.Visibility = Visibility.Collapsed;
            Pane.Visibility = Visibility.Collapsed;
            Pane2.Visibility = Visibility.Collapsed;

            // 每次显示主页都刷一次：刚插的 U 盘、刚访问过的目录都要出现在卡片里。
            // 异步、带超时，不占首帧（FR-1.1.3）。
            _home.Refresh();
            _ = _home.LoadAsync();
            return;
        }

        if (tab.IsTagResults)
        {
            Home.Visibility = Visibility.Collapsed;
            TagResults.Visibility = Visibility.Visible;
            Pane.Visibility = Visibility.Collapsed;
            Pane2.Visibility = Visibility.Collapsed;

            // 同一颜色来回切不重算（Load 内部判重）；换了颜色才重新取一遍
            _tagResults.Load(App.Tags, tab.TagFilter, tab.TagFilterName);
            return;
        }

        Home.Visibility = Visibility.Collapsed;
        TagResults.Visibility = Visibility.Collapsed;
        Pane.Visibility = Visibility.Visible;
        Pane.Attach(tab.Primary);

        if (_splitEnabled)
        {
            // 第二窗格第一次分屏时按"上次的路径 → 主窗格当前目录"建；建完立刻可见
            PaneViewModel second = tab.EnsureSecondary(tab.Primary.CurrentPath);
            Pane2.Attach(second);
            Pane2.Visibility = Visibility.Visible;
        }
        else
        {
            Pane2.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 标签形态在运行中变了（主页点了目录 / 导航栏点了主页）。只切视图，不重新导航——
    /// 触发这次变化的那次导航已经在路上或已经完成。
    /// </summary>
    private void OnTabKindChanged(TabViewModel tab)
    {
        if (!ReferenceEquals(tab, _tabs.Active)) return;

        ApplyTabKind(tab);
        ApplySplitLayout();
        RefreshPaneFocus();
        ApplyPreviewLayout();
        RefreshPreview();
        DataContext = tab.ActivePane;

        // 标签结果页没有路径，高亮要落在侧栏对应的那个颜色节点上，
        // 而不是被空路径带跑到「主页」
        SyncNavSelection(tab.IsTagResults ? _nav.FindTagRow(tab.TagFilter) : null,
            tab.ActivePane.CurrentPath);

        // 不在这里补一次 LoadPanesAsync：引起形态变化的那个动作本身就是一次导航
        // （主页点卡片 / Alt+← 回退），再发一次只会让两次枚举互相作废。
    }

    /// <summary>按需加载：只导航还没读过盘的窗格（半开的第二窗格也算）。</summary>
    private static async Task LoadPanesAsync(TabViewModel tab)
    {
        if (tab.Primary.HasPendingContent) await NavigateTabAsync(tab.Primary);
        if (tab.Secondary is { HasPendingContent: true } second) await NavigateTabAsync(second);
    }

    // ── 双窗格（FR-3） ──────────────────────────────────────────────────────

    /// <summary>
    /// F3 / Ctrl+Shift+E：开关第二窗格（FR-3.3）。
    ///
    /// 开：只为**当前标签**建第二窗格，其余标签等被激活时再建——12 个标签各配一个
    /// 空壳窗格，等于把双窗格的内存预算花在看不见的地方。
    /// 关：反过来，把所有标签的第二窗格都丢掉，只留路径。
    /// </summary>
    private void ToggleSplit()
    {
        if (Tab.Kind != TabKind.Files)
        {
            // 主页与标签结果页都是"没有位置"的形态，分不出第二栏。
            // 给一句话比让 F3 静默失效好——否则用户会以为分屏快捷键坏了。
            Vm.SetStatus(Strings.Status_HomeNoSplit);
            return;
        }

        _splitEnabled = !_splitEnabled;
        App.Settings.Layout.SplitEnabled = _splitEnabled;

        if (_splitEnabled)
        {
            BindTab(Tab);
            ApplySplitLayout();
            if (_started) _ = LoadPanesAsync(Tab);
        }
        else
        {
            foreach (TabViewModel tab in _tabs.Tabs) tab.DropSecondary();

            // Release 而不是只隐藏：视图继续挂着被丢弃的视图模型，内存就白省了
            Pane2.Release();
            Pane2.Visibility = Visibility.Collapsed;
            ApplySplitLayout();

            // 关掉第二窗格后焦点回主窗格（FR-3.3）
            Tab.ActivatePane(0);
            DataContext = Tab.ActivePane;
            SyncNavSelection(Tab.ActivePane.CurrentPath);
            Pane.FocusList();
        }

        RefreshPaneFocus();
        SyncSplitButton();

        // 同上：只按一下 F3 就关窗口时，退出兜底路径会判定"什么都没改"，分屏开关就丢了
        App.SaveSettings();
    }

    /// <summary>
    /// 探针专用入口：进入双窗格形态，但不改设置、不落盘。
    ///
    /// 不能直接调 <see cref="ToggleSplit"/>：它会 `App.Settings.Layout.SplitEnabled = true`
    /// 并立刻 <c>SaveSettings()</c>，测完一次内存就把用户下次启动变成"莫名其妙分屏"。
    /// 探针本来就要跑多次取中位数，这个副作用会被放大成"设置老是自己变"。
    /// </summary>
    internal void EnterProbeSplit()
    {
        _splitEnabled = true;

        PaneViewModel second = Tab.EnsureSecondary(Tab.Primary.CurrentPath);
        Pane2.Attach(second);
        Pane2.Visibility = Visibility.Visible;

        ApplySplitLayout();
        SyncSplitButton();
        RefreshPaneFocus();
    }

    /// <summary>
    /// 把分屏状态同步到工具栏按钮的选中态。
    /// 三个入口（F3 / Ctrl+Shift+E / 工具栏按钮）最后都落到 <see cref="ToggleSplit"/>，
    /// 所以只在那一处收口就够；启动与设置变更时由 <see cref="ApplySettingsToUi"/> 再叫一次。
    /// </summary>
    private void SyncSplitButton()
    {
        Pane.IsSplitActive = _splitEnabled;
        Pane2.IsSplitActive = _splitEnabled;
    }

    /// <summary>
    /// 按当前形态摆 3×3 网格：左右分屏用列 0/1/2，上下分屏用行 0/1/2，不用的一维归零。
    /// 两种形态共用同一套行列定义，分屏条与窗格的 Grid 定位在这里改——
    /// 为左右/上下各写一套 XAML 会让"分隔条拖拽、双击复位"这类逻辑跟着重复一遍。
    /// </summary>
    private void ApplySplitLayout()
    {
        bool vertical = App.Settings.Layout.SplitVertical;
        double ratio = Math.Clamp(
            App.Settings.Layout.SplitRatio, AppSettings.MinSplitRatio, AppSettings.MaxSplitRatio);

        double gutter = ColumnSplitter.Width;
        if (double.IsNaN(gutter) || gutter <= 0) gutter = 6;

        // 先全部归零，再按当前形态铺开：上一种形态留下的宽度不清掉会挤在一起
        SplitterColumn.Width = new GridLength(0);
        SecondColumn.Width = new GridLength(0);
        SplitterRow.Height = new GridLength(0);
        SecondRow.Height = new GridLength(0);

        Grid.SetColumn(Pane, 0);
        Grid.SetRow(Pane, 0);
        Grid.SetColumnSpan(Pane, 1);
        Grid.SetRowSpan(Pane, 1);

        Grid.SetColumn(Pane2, 2);
        Grid.SetRow(Pane2, 0);
        Grid.SetColumnSpan(Pane2, 1);
        Grid.SetRowSpan(Pane2, 1);

        // 主页与标签结果页都横跨整个 PaneHost。放在分屏判断之前：
        // 这两种形态下无论窗口是否"开着分屏"，都只有一个内容区——
        // 否则切过去会看到右边（或下边）留着一条空白的分屏列。
        Grid.SetColumn(Home, 0);
        Grid.SetRow(Home, 0);
        Grid.SetColumn(TagResults, 0);
        Grid.SetRow(TagResults, 0);

        if (Tab.IsHome || Tab.IsTagResults)
        {
            FrameworkElement solo = Tab.IsHome ? Home : TagResults;
            Grid.SetColumnSpan(solo, 3);
            Grid.SetRowSpan(solo, 3);

            FirstColumn.Width = new GridLength(1, GridUnitType.Star);
            FirstRow.Height = new GridLength(1, GridUnitType.Star);
            ColumnSplitter.Visibility = Visibility.Collapsed;
            RowSplitter.Visibility = Visibility.Collapsed;
            return;
        }

        Grid.SetColumnSpan(Home, 1);
        Grid.SetRowSpan(Home, 1);
        Grid.SetColumnSpan(TagResults, 1);
        Grid.SetRowSpan(TagResults, 1);

        if (!_splitEnabled)
        {
            FirstColumn.Width = new GridLength(1, GridUnitType.Star);
            FirstRow.Height = new GridLength(1, GridUnitType.Star);
            ColumnSplitter.Visibility = Visibility.Collapsed;
            RowSplitter.Visibility = Visibility.Collapsed;
            return;
        }

        if (vertical)
        {
            FirstColumn.Width = new GridLength(1, GridUnitType.Star);
            FirstRow.Height = new GridLength(ratio, GridUnitType.Star);
            SplitterRow.Height = new GridLength(gutter);
            SecondRow.Height = new GridLength(1 - ratio, GridUnitType.Star);

            // 上下分屏时两个窗格都横跨三列（列 1、2 宽度为 0，只为复用同一套定义）。
            // 列号必须显式归 0：从左右分屏切过来时它还在列 2 上，而列 2 此刻宽度是 0，
            // 跨列又从 2 开始数，结果就是"第二个窗格有高度、没宽度"，画面一片空白。
            Grid.SetColumn(Pane, 0);
            Grid.SetColumnSpan(Pane, 3);
            Grid.SetColumn(Pane2, 0);
            Grid.SetColumnSpan(Pane2, 3);
            Grid.SetRow(Pane2, 2);

            RowSplitter.Visibility = Visibility.Visible;
            ColumnSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            FirstRow.Height = new GridLength(1, GridUnitType.Star);
            FirstColumn.Width = new GridLength(ratio, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(gutter);
            SecondColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);

            ColumnSplitter.Visibility = Visibility.Visible;
            RowSplitter.Visibility = Visibility.Collapsed;
        }

        if (KeyDebug) LogSplitLayout();
    }

    /// <summary>
    /// 标签栏位置（FR-2.4）：顶部 / 底部，切换即时生效、不重建控件。
    ///
    /// 上下各留一行，只改哪一行有高度、以及分隔线画在哪一边。当初把标签栏固定在行 0，
    /// 现在有了"底部"这个选项，如果改成重建 TabStrip 或准备两份控件，
    /// 标签的鼠标捕获、拖拽与滚动位置都会跟着丢。
    /// </summary>
    private void ApplyTabStripPosition()
    {
        bool bottom = App.Settings.Appearance.TabStripPosition == AppearanceSettings.TabBottom;
        bool immersive = _immersiveTitleBar;

        Grid.SetRow(TabFrame, bottom ? 3 : 0);

        // 沉浸模式下顶部那一行即使标签栏搬到底部也要留着——窗口键还钉在右上角。
        // 非沉浸模式没有窗口键，行高归零，界面回到改造前的样子。
        TabRowTop.Height = bottom && !immersive ? new GridLength(0) : GridLength.Auto;
        TabRowBottom.Height = bottom ? GridLength.Auto : new GridLength(0);

        // 分隔线画在朝向浏览区的那一侧：顶部时在下方，底部时在上方
        TabFrame.BorderThickness = bottom ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1);

        // 顶部模式下标签栏就是标题栏：撑到标题条高度，并给右侧窗口键让出位置。
        // 不让位的话最后一个标签会被三个按钮压住，且只有标签滚到那里才看得出来。
        if (immersive && !bottom)
        {
            TabFrame.Height = Metric("Size.TitleBar.Height");
            TabFrame.Margin = new Thickness(0, 0, Metric("Size.WindowControls.Width"), 0);
        }
        else
        {
            TabFrame.ClearValue(HeightProperty);
            TabFrame.Margin = new Thickness(0);
        }

        // 标签栏在底部时，标题条左侧用窗口标题补位；否则那一条只剩三个按键，空得莫名其妙
        TitleFallback.Visibility = immersive && bottom ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 切换自绘标题栏形态（FR-10.11）。设置项只影响窗口创建这一次：
    /// 运行期改 WindowStyle 要重建 HWND，整棵可视树都得重来，代价与收益不成比例。
    /// </summary>
    private void ApplyWindowChrome()
    {
        _immersiveTitleBar = App.Settings.Appearance.ImmersiveTitleBar;

        if (!_immersiveTitleBar)
        {
            // 系统标题栏已经在，覆盖层不能留：否则标签右侧白让 138px，顶部还多一条空行
            TitleBarHost.Visibility = Visibility.Collapsed;
            return;
        }

        WindowChromeHelper.Attach(this);
        TitleBarHost.Visibility = Visibility.Visible;
        UpdateCaptionHeight();
    }

    /// <summary>
    /// 标题条高度随字号变，可拖动区域（CaptionHeight）必须跟着走。
    /// 不跟的话 24pt 下标题条高 50、只有上面 40 能拖，用户会以为窗口卡住了。
    /// </summary>
    private void UpdateCaptionHeight() =>
        WindowChromeHelper.UpdateCaptionHeight(this, Metric("Size.TitleBar.Height"));

    /// <summary>取一个尺寸令牌。资源缺失直接抛出，不静默回退到魔数。</summary>
    private double Metric(string key) => (double)FindResource(key);

    /// <summary>
    /// 分屏网格的实际几何。构造函数里 ApplySplitLayout 跑在布局之前，ActualWidth 全是 0，
    /// 单看那一行会误判成"分隔条没占位"。首帧渲染后（OnContentRendered）再打一次才有意义。
    /// </summary>
    private void LogSplitLayout()
    {
        WriteKeyLog($"split vertical={App.Settings.Layout.SplitVertical} enabled={_splitEnabled} " +
                    $"ratio={App.Settings.Layout.SplitRatio:0.###} " +
                    $"col={FirstColumn.ActualWidth:0.#}/{ColumnSplitter.ActualWidth:0.#}/{SecondColumn.ActualWidth:0.#} " +
                    $"row={FirstRow.ActualHeight:0.#}/{RowSplitter.ActualHeight:0.#}/{SecondRow.ActualHeight:0.#} " +
                    $"host={PaneHost.ActualWidth:0.#}x{PaneHost.ActualHeight:0.#}");

        // 分隔条中心的**屏幕坐标**：走查脚本要在这里落鼠标。
        // 从截图反推这个点不可靠——DPI 缩放、DWM 隐形边框、PrintWindow 的裁剪
        // 三者叠加后，同一行像素在不同窗口尺寸下换算出的屏幕点能差几十像素。
        foreach ((string name, GridSplitter splitter) in
                 new[] { ("col", ColumnSplitter), ("row", RowSplitter) })
        {
            if (!splitter.IsVisible || splitter.ActualWidth <= 0 || splitter.ActualHeight <= 0) continue;

            Point p = splitter.PointToScreen(
                new Point(splitter.ActualWidth / 2, splitter.ActualHeight / 2));
            WriteKeyLog($"splitter {name} screen={p.X:0},{p.Y:0} " +
                        $"size={splitter.ActualWidth:0.#}x{splitter.ActualHeight:0.#}");
        }
    }

    /// <summary>
    /// 拖动结束才落盘比例：跟每个拖动事件写文件等于每帧一次磁盘 IO（列宽也是同一个道理）。
    /// </summary>
    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        bool vertical = App.Settings.Layout.SplitVertical;
        double first = vertical ? FirstRow.ActualHeight : FirstColumn.ActualWidth;
        double second = vertical ? SecondRow.ActualHeight : SecondColumn.ActualWidth;
        double total = first + second;
        if (total <= 0) return;

        App.Settings.Layout.SplitRatio = Math.Clamp(
            first / total, AppSettings.MinSplitRatio, AppSettings.MaxSplitRatio);

        // 归一成星号比例：留成像素值的话，窗口拉宽只有一侧在长
        ApplySplitLayout();

        // 必须当场落盘，不能等退出时那条"尺寸变了才存"的兜底路径：
        // 用户只拖了一下分隔条就关窗口，窗口尺寸、最后目录、标签都没变，
        // PersistWindowState 会判定"没改过"直接返回，比例就丢了。
        // 一次拖拽一次写，与列宽（OnColumnResizeDone）同一套规矩。
        App.SaveSettings();
    }

    private void OnSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ResetSplitRatio();
    }

    /// <summary>双击分隔条回到 50:50（FR-3.4）。</summary>
    private void ResetSplitRatio()
    {
        App.Settings.Layout.SplitRatio = AppSettings.DefaultSplitRatio;
        ApplySplitLayout();
        App.SaveSettings();
    }

    private void OnSplitHorizontal(object sender, RoutedEventArgs e) => SetSplitOrientation(vertical: false);

    private void OnSplitVertical(object sender, RoutedEventArgs e) => SetSplitOrientation(vertical: true);

    private void OnSplitReset(object sender, RoutedEventArgs e) => ResetSplitRatio();

    /// <summary>左右 ↔ 上下（FR-3.1）。入口挂在分隔条的右键菜单上：不占键位，也在手边。</summary>
    private void SetSplitOrientation(bool vertical)
    {
        App.Settings.Layout.SplitVertical = vertical;
        ApplySplitLayout();
        App.SaveSettings();
    }

    /// <summary>某个窗格的列表拿到焦点：它成为命令目标（FR-3.5）。</summary>
    private void OnPaneActivated(PaneView view)
    {
        if (!_splitEnabled || view.ViewModel is not { } pane)
        {
            RefreshPaneFocus();
            return;
        }

        Tab.SetActivePane(pane);

        // 导航栏与窗口标题跟着焦点窗格走，否则"左窗格选中桌面、标题显示右窗格"会分不清
        DataContext = pane;
        SyncNavSelection(pane.CurrentPath);
        RefreshPaneFocus();

        // 换了焦点窗格 = 换了"我此刻在看什么"，预览要跟着换（FR-9.1）
        RefreshPreview();
    }

    /// <summary>
    /// 焦点包边只在分屏时画：单窗格时全窗口就一个浏览区，包个框只是多一条线。
    /// 颜色是 1px <c>Brush.Border.Strong</c>（ui-guidelines 规则 10.2）。
    /// </summary>
    private void RefreshPaneFocus()
    {
        bool split = _splitEnabled && Tab.Secondary is not null;

        Pane.SetActive(split && ReferenceEquals(Tab.ActivePane, Tab.Primary), outline: split);
        Pane2.SetActive(split && ReferenceEquals(Tab.ActivePane, Tab.Secondary), outline: split);
    }

    /// <summary>Tab 键在两个窗格之间换焦点（FR-3.5）。列表内换选中项走方向键。</summary>
    private void FocusOtherPane()
    {
        if (!_splitEnabled || Tab.Secondary is not { } second) return;

        PaneViewModel target = ReferenceEquals(Tab.ActivePane, second) ? Tab.Primary : second;

        Tab.SetActivePane(target);
        DataContext = target;
        SyncNavSelection(target.CurrentPath);
        RefreshPaneFocus();
        RefreshPreview();
        ViewFor(target).FocusList();
    }

    /// <summary>
    /// 让导航栏的高亮跟上当前目录。
    /// 切标签换了目录，但导航栏的选中项还是上一个标签留下的那一条——
    /// 看起来像是"导航栏指向 A，列表显示 B"。
    /// </summary>
    private void SyncNavSelection(string path) => SyncNavSelection(null, path);

    /// <summary>
    /// 把导航栏高亮挪到 <paramref name="row"/>（非空时优先）或某个路径上。
    /// 标签结果页没有路径可查，只能直接给行。
    /// </summary>
    private void SyncNavSelection(INavRow? row, string path)
    {
        if (!App.Settings.Appearance.ShowNavPane) return;

        _syncingNavSelection = true;
        try
        {
            QuickAccessList.SelectedItem = row ?? _nav.FindRow(path);
        }
        finally
        {
            _syncingNavSelection = false;
        }
    }

    /// <summary>
    /// 把导航栏高亮挪到某个路径上（不触发导航）。
    /// 用在"重复收藏"这种需要"把那一行指出来"的场合（FR-7.1 验收）。
    /// </summary>
    private void HighlightNavPath(string path)
    {
        if (!App.Settings.Appearance.ShowNavPane) return;

        _syncingNavSelection = true;
        try
        {
            QuickAccessList.SelectedItem = _nav.FindRow(path);
            QuickAccessList.ScrollIntoView(QuickAccessList.SelectedItem);
        }
        finally
        {
            _syncingNavSelection = false;
        }
    }

    /// <summary>只读盘、不改历史栈的首次导航（懒加载标签与启动首屏共用）。</summary>
    private static async Task NavigateTabAsync(PaneViewModel tab)
    {
        try
        {
            await tab.NavigateAsync(tab.CurrentPath);
        }
        catch (Exception ex)
        {
            tab.ReportError(ex);
        }
    }

    /// <summary>供单实例转交使用：把当前标签切到指定目录。</summary>
    public void NavigateTo(string path)
    {
        // 首屏还没起来时转交请求先到：直接改当前标签的待办路径
        if (!_started && Vm.HasPendingContent)
        {
            Vm.PreparePendingPath(path);
            return;
        }

        ActiveView.NavigateTo(path);
    }

    // ── 与设置页的接口 ──────────────────────────────────────────────────────
    // 设置页只认 MainWindow，不认 PaneView。字号变化要按"项目索引"还原滚动位置，
    // 因此这里保留两个转发方法，让设置页不必知道窗格的存在。

    /// <inheritdoc cref="PaneView.CaptureScrollAnchor"/>
    public (int Index, double Offset) CaptureScrollAnchor() => ActiveView.CaptureScrollAnchor();

    /// <inheritdoc cref="PaneView.RestoreScrollAnchor"/>
    public void RestoreScrollAnchor((int Index, double Offset) anchor)
        => ActiveView.RestoreScrollAnchor(anchor);

    /// <summary>设置改动后重新套用受影响的界面状态（不重建窗口）。</summary>
    public void ApplySettingsToUi()
    {
        AppSettings s = App.Settings;

        // 导航栏的显示/隐藏与宽度都归 ApplyNavLayout（FR-1.2.4）：宽度现在有两个来源
        // （设置里的像素值、设置页的开关），分在两处写就会出现"关掉侧栏后列宽还是 184"这类错位
        ApplyNavLayout();

        // 设置入口那一行不跟"显示状态栏"走：状态栏是纯展示设置，
        // 不能顺带把唯一的设置入口藏掉（这一行高度固定为状态栏高度，见 MainWindow.xaml）。

        // 标签栏位置（FR-2.4）：设置页改完立即生效，不用重启
        ApplyTabStripPosition();

        // 分屏按钮的选中态跟着设置走（会话里可能本来就是开着的）
        SyncSplitButton();

        // 预览的开关与触发方式（FR-5.2.10）：设置页改完立即生效
        ApplyPreviewSettings();

        // 状态栏与列宽在两个窗格各有一份，都要套一遍
        Pane.ApplyAppearance(s);
        Pane2.ApplyAppearance(s);

        if (KeyDebug)
        {
            WriteKeyLog($"ui statusBar={s.Appearance.ShowStatusBar} " +
                        $"visibility={ActiveView.StatusBarVisibility} " +
                        $"statusText=\"{Vm.StatusText}\"");
        }
    }

    /// <summary>
    /// 把排序口径（文件夹优先 / 自然排序 / 区分大小写）套到所有已打开的窗格（FR-5.2.3）。
    /// 双窗格 × 最多 12 个标签，逐个套一遍；没变的窗格在 <c>ApplySortOptions</c> 里直接返回。
    /// </summary>
    public void ApplySortOptionsToPanes()
    {
        AppSettings s = App.Settings;

        foreach (TabViewModel tab in _tabs.Tabs)
        {
            tab.Primary.ApplySortOptions(s.Behavior.FoldersFirst, s.Behavior.NaturalSort, s.Behavior.CaseSensitiveSort);

            if (tab.Secondary is { } second)
            {
                second.ApplySortOptions(s.Behavior.FoldersFirst, s.Behavior.NaturalSort, s.Behavior.CaseSensitiveSort);
            }
        }
    }

    private void OpenSettings()
    {
        // 单例：重复点"设置"不应叠出多个窗口
        if (Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow { Owner = this };
        window.Show();
    }

    /// <summary>侧栏底部的设置按钮。与 Ctrl+, 走同一条路。</summary>
    private void OnSettingsButtonClick(object sender, RoutedEventArgs e) => OpenSettings();

    // ── 快捷键 ──────────────────────────────────────────────────────────────

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    /// <summary>设备插拔（FR-1.2.2）。U 盘、移动硬盘、光驱的增删都从这里来。</summary>
    private const int WmDeviceChange = 0x0219;

    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    /// <summary>设备节点树变化：插一个 U 盘往往先来这一条、再来一条 Arrival。</summary>
    private const int DbtDevNodesChanged = 0x0007;

    /// <summary>显示器配置变化（FR-10.2）：分辨率、缩放、拔插外接屏都会发它。</summary>
    private const int WmDisplayChange = 0x007E;

    /// <summary>鼠标侧键按下（FR-6.2）。</summary>
    private const int WmXButtonDown = 0x020B;

    private const ModifierKeys RelevantModifiers =
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    /// <summary>
    /// 按键诊断开关：`--keydebug` 命令行参数或环境变量 <c>JIANLAN_KEYDEBUG=1</c>。
    /// 开启后把每条按键消息写进 logs/keydebug.log，用于排查输入法吃键。
    /// </summary>
    public static bool KeyDebug { get; set; } =
        Environment.GetEnvironmentVariable("JIANLAN_KEYDEBUG") == "1";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        ApplyIconFromExeResources();
        AttachKeyHook();
    }

    /// <summary>
    /// 快捷键的主入口是窗口消息，不是 WPF 的按键事件：
    /// 输入法（TSF/IME）在 WPF 之前改写 Key，Ctrl+, 这类标点组合经常收不到 PreviewKeyDown；
    /// 而物理键码（VK）在同一条 WM_KEYDOWN 里始终准确。
    ///
    /// 两个时机都要挂：<c>OnSourceInitialized</c> 时 HwndSource 已建好，
    /// 但窗口首次 <c>Show()</c> 若触发了 source 重建，早先挂的钩子就留在旧对象上。
    /// <c>RemoveHook</c> 保证重复挂不会执行两次。
    /// </summary>
    private void AttachKeyHook()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source is null) return;

        source.RemoveHook(OnWindowMessage);
        source.AddHook(OnWindowMessage);

        if (KeyDebug) WriteKeyLog($"hook attached hwnd=0x{handle:X} source=0x{source.Handle:X}");
    }

    // ── 窗口热键：Ctrl+, 的最后一道保障 ────────────────────────────────────
    //
    // 实测结论（2026-11-04）：注入 WM_KEYDOWN 能触发，真实按键盘不能。
    // 已排除"其它程序占用了 Ctrl+, 全局热键"（RegisterHotKey 在裸进程里注册成功）。
    // 剩下的可能都在输入链路的下层：输入法 hook、键盘布局、以及 WPF 对 OEM 键的改写。
    // 这些都无法从进程内部断言，所以不再猜——直接改走系统热键通道：
    //
    //   RegisterHotKey 由系统在把按键分发给前台窗口之前处理，命中时投递 WM_HOTKEY
    //   到注册者窗口，按键不再作为普通输入下发。输入法、WPF 的 Key 归一化、
    //   TranslateMessage 全被绕过。
    //
    // 只在窗口激活期间注册，失活立即注销：热键是系统级的，长期持有会把
    // 其它程序的 Ctrl+, 也抢过来。这是"本窗口内生效"与"不打扰别人"的折中。

    private const int HotkeySettings = 0x4C50;          // 任意固定 id，仅本窗口使用
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;            // 长按不重复触发
    private const int VkOemComma = 0xBC;

    private IntPtr _hotkeyWindow;
    private bool _hotkeyRegistered;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RegisterSettingsHotkey();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        UnregisterSettingsHotkey();
        base.OnDeactivated(e);
    }

    private void RegisterSettingsHotkey()
    {
        if (_hotkeyRegistered) return;

        // 探针进程不注册：它是系统级热键，诊断期间抢走用户的 Ctrl+, 属于副作用。
        // 顺带让 AC-05 的探针与用户实例并存时不再刷 E7001（err=1409 已被占用）。
        if (ProbePath is not null) return;

        // 副窗口不注册（FR-2.12）：同一个组合键在进程内只有一个窗口能拿到，
        // 让主窗口持有即可。副窗口里按 Ctrl+, 仍会走窗口内的按键派发，功能不缺。
        if (_isSecondaryWindow) return;

        _hotkeyWindow = new WindowInteropHelper(this).Handle;
        if (_hotkeyWindow == IntPtr.Zero) return;

        if (RegisterHotKey(_hotkeyWindow, HotkeySettings, ModControl | ModNoRepeat, VkOemComma))
        {
            _hotkeyRegistered = true;
            if (KeyDebug) WriteKeyLog("hotkey registered: Ctrl+,");
            return;
        }

        // 注册失败几乎只有两种原因：别的程序已占用该组合，或窗口句柄失效。
        // 这条一定要落盘——它决定了用户报告的"按了没反应"到底怪谁。
        // 成功路径不写盘（每次激活都会走这里），失败才留痕。
        int error = Marshal.GetLastWin32Error();
        Diagnostics.AppLog.Warn("hotkey.register", "E7001", $"Ctrl+, 注册失败 err={error}");  // i18n-ignore：只进 app.log
        if (KeyDebug) WriteKeyLog($"hotkey register FAILED err={error}");
    }

    private void UnregisterSettingsHotkey()
    {
        if (!_hotkeyRegistered) return;

        UnregisterHotKey(_hotkeyWindow, HotkeySettings);
        _hotkeyRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange)
        {
            // 三条消息常常连续到达（插一个 U 盘会先来 DevNodesChanged，再来 Arrival），
            // 合并成一次刷新；否则每插一次盘要重建三四遍侧栏。
            int evt = wParam.ToInt32();
            if (evt is DbtDeviceArrival or DbtDeviceRemoveComplete or DbtDevNodesChanged)
            {
                ScheduleDriveRefresh();
            }

            // 不标 handled：系统的卷管理、其它监听者还要看这条消息
            return IntPtr.Zero;
        }

        if (msg == WmXButtonDown)
        {
            HandleMouseButtonMessage(wParam.ToInt32(), ref handled);
            return IntPtr.Zero;
        }

        if (msg == WmDisplayChange)
        {
            // 拔掉外接屏后窗口可能整个落在可视区之外（FR-10.2）。这里不改窗口大小，
            // 只在"确实不可见"时挪回主屏中央——用户只是在调分辨率不该看到窗口乱跳。
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(EnsureVisibleOnScreen));
            return IntPtr.Zero;
        }

        if (msg == WmHotkey)
        {
            if (wParam.ToInt32() != HotkeySettings) return IntPtr.Zero;

            if (KeyDebug) WriteKeyLog("wm_hotkey -> app.openSettings");
            handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Dispatch(CommandIds.OpenSettings));
            return IntPtr.Zero;
        }

        if (msg is not (WmKeyDown or WmSysKeyDown)) return IntPtr.Zero;

        if ((lParam.ToInt64() & (1L << 30)) != 0) return IntPtr.Zero;   // 长按重复，忽略

        int vk = wParam.ToInt32();
        ModifierKeys modifiers = Keyboard.Modifiers & RelevantModifiers;
        bool listFocused = ActiveView.IsListFocused;

        if (KeyDebug)
        {
            WriteKeyLog($"vk=0x{vk:X2} mods={modifiers} listFocus={listFocused} " +
                        $"focus={Keyboard.FocusedElement?.GetType().Name ?? "null"}");
        }

        if (Keyboard.FocusedElement is TextBoxBase)
        {
            // 输入框里：文本编辑键与所有无修饰键都归输入框，
            // 否则地址栏的 Delete 会变成"删除文件"、Enter 会变成"打开选中项"。
            if (Input.KeyBindings.IsTextEditingKey(vk) || modifiers == ModifierKeys.None)
            {
                return IntPtr.Zero;
            }
        }

        // 分屏时 Tab 在两窗格之间换焦点（FR-3.5）：列表内换选中项用方向键，
        // Tab 让给"换窗格"，否则分屏之后没法纯键盘切换。
        //
        // 这条分支留在钩子里只是兜底：实测真实按键与投递的 WM_KEYDOWN 都到不了这里
        // （见 OnPreviewKeyDown 的说明），真正生效的是那条路径。
        if (_splitEnabled && vk == Input.KeyBindings.Vk.Tab && modifiers == ModifierKeys.None
            && ActiveView.IsListFocused)
        {
            handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusOtherPane);
            return IntPtr.Zero;
        }

        // 无修饰键只在列表有焦点时接管：焦点在按钮上按 Enter 应触发按钮，而不是"打开选中项"。
        // 功能键（F3 分屏）例外——它不产生文本、也不该由某个按钮"拥有"，
        // 焦点停在工具栏按钮上时按 F3 仍然要能分屏。
        bool functionKey = vk is Input.KeyBindings.Vk.F3 or Input.KeyBindings.Vk.F6;
        if (modifiers == ModifierKeys.None && !listFocused && !functionKey) return IntPtr.Zero;

        if (!Input.KeyBindings.TryMatchVirtualKey(vk, out string commandId)) return IntPtr.Zero;

        if (KeyDebug) WriteKeyLog($"  -> {commandId}");

        // 匹配到就吞掉，即使命令此刻不可执行——否则 ListBox 会把它当成"首字母定位"
        handled = true;

        // 消息泵里直接执行可能重入（命令会 Show 窗口），交回 Dispatcher 排队
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Dispatch(commandId));
        return IntPtr.Zero;
    }

    /// <summary>
    /// 快捷键的**实际**入口。
    ///
    /// 设计上主入口是 WM_KEYDOWN 钩子（避开输入法改写 Key），但实测真实按键与
    /// <c>PostMessage</c> 投递的 WM_KEYDOWN 都只走到这里：<c>--keydebug</c> 日志里
    /// 只有 <c>preview key=…</c>、从来没有 <c>vk=…</c>；而同一只钩子收得到
    /// <c>WM_HOTKEY</c>（Ctrl+, 走系统热键，日志正常），所以钩子本身是活的，
    /// 是 WPF 的键盘输入链路在钩子之前把 WM_KEYDOWN 处理掉了。
    ///
    /// 结论：凡是只写在钩子里的键都不会生效——Tab 换窗格就踩过这个坑。
    /// 新键位要么进 KeyBindings（两条路径都认），要么在这里单独处理。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase) return;

        if (KeyDebug)
        {
            WriteKeyLog($"preview key={e.Key} sys={e.SystemKey} ime={e.ImeProcessedKey} " +
                        $"listFocus={ActiveView.IsListFocused} " +
                        $"focus={Keyboard.FocusedElement?.GetType().Name ?? "null"}");
        }

        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        // 标签上的 Enter / Space / ← / → 归标签自己处理（FR-10.7）。
        // 窗口级 PreviewKeyDown 先于元素的 KeyDown 执行，不在这里让路的话，
        // 焦点在标签上按 Enter 会被"打开选中项"先吃掉——表现是"选好标签按 Enter，
        // 结果打开了旁边列表里的文件，标签纹丝不动"。Space 能过是因为它不在键位表里，
        // 两条路径行为不一致本身就是这个 bug 的证据（AC-08 走查发现）。
        if (Keyboard.Modifiers == ModifierKeys.None
            && key is Key.Enter or Key.Space or Key.Left or Key.Right
            && Keyboard.FocusedElement is FrameworkElement { DataContext: TabViewModel })
        {
            return;
        }

        // 分屏时 Tab 在两窗格之间换焦点（FR-3.5）。不给 KeyBindings 注册：
        // 它只在分屏时才有意义，登记成全局键位会让单窗格下的 Tab 也被吞掉。
        if (_splitEnabled && key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None
            && ActiveView.IsListFocused)
        {
            if (KeyDebug) WriteKeyLog("  preview -> pane.focusOther");
            e.Handled = true;
            FocusOtherPane();
            return;
        }

        if (!Input.KeyBindings.TryMatchKey(key, out string commandId)) return;

        if (KeyDebug) WriteKeyLog($"  preview -> {commandId}");

        e.Handled = true;
        Dispatch(commandId);
    }

    /// <summary>
    /// 快捷键派发。**先查窗格、再查窗口。**
    ///
    /// 判据是"这条命令注册在哪张表里"，不是"此刻能不能执行"——
    /// 用可执行性判断会让 F5 在加载中被误判给窗口表（那里根本没有 F5），
    /// 也会让"删除"在没有选中时落到别处。
    /// </summary>
    private void Dispatch(string commandId)
    {
        if (ActiveView.HasCommand(commandId))
        {
            ActiveView.TryInvoke(commandId);
            return;
        }

        _commands.Invoke(commandId);
    }

    internal static void WriteKeyLog(string message)    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "keydebug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 诊断日志写不进去就放弃
        }
    }

    /// <summary>首次渲染回调：比 ContentRendered 更早、更准确反映"画面出现"。</summary>
    private void OnFirstRender(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnFirstRender;
        StartupTrace.Mark("render");
    }

    /// <summary>首帧落屏时间：用户"看到窗口"的时刻。</summary>
    private void OnContentRendered(object? sender, EventArgs e)
    {
        StartupTrace.Mark("firstframe");
        Pane.LogColumnLayout();
        if (KeyDebug && _splitEnabled) LogSplitLayout();
        if (_splitEnabled) Pane2.LogColumnLayout();
        if (KeyDebug) LogWindowAnchors();
        FlushOnce();
    }

    /// <summary>
    /// 走查用：上报窗口级按钮的屏幕中心（设置按钮在侧栏底部，不属于任何窗格）。
    /// 截图反推坐标不可靠——capture 出来的位图与客户区之间存在缩放偏移，
    /// 实测"图像 x=597"对应"客户区 x=657"，差 60px 足以把点击落到隔壁按钮上。
    /// </summary>
    private void LogWindowAnchors()
    {
        Point center = SettingsButton.PointToScreen(
            new Point(SettingsButton.ActualWidth / 2, SettingsButton.ActualHeight / 2));

        WriteKeyLog($"anchor settings screen={center.X:0.#},{center.Y:0.#} " +
                    $"w={SettingsButton.ActualWidth:0.#} h={SettingsButton.ActualHeight:0.#}");
    }

    /// <summary>窗口加载后异步读目录：先出窗口，再出内容（P1 首帧优先）。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        StartupTrace.Mark("loaded");

        // Show() 之后再挂一次：source 若被重建，钩子要落在新对象上
        AttachKeyHook();

        ApplySettingsToUi();
        StartupTrace.Mark("uisetup");

        // 诊断模式接管：测完自行退出，不写启动日志的 normal 路径
        if (ProbePath is not null)
        {
            await RunPerfProbeAsync();
            return;
        }

        // 从这里开始，切换标签才允许读盘
        _started = true;

        try
        {
            // 只导航当前标签（分屏时两个窗格都导航）；其余标签保持 pending，切过去时才枚举
            await LoadPanesAsync(Tab);
            StartupTrace.Mark("firstcontent");
            FlushOnce();

            // 工作区（FR-8.6）：首帧之后才读 workspaces.json，导航栏与主页各补一段。
            // 放在这里而不是构造里，是因为需求要求它不参与冷启动默认路径。
            InitializeWorkspaces();

            // 标签库（FR-4.2.3 / FR-4.2.5）：同样不参与冷启动。列表已经画出来了，
            // 这里在后台把 tags.jsonl 读进内存，再回头给屏幕上的行补圆点。
            InitializeTags();
        }
        catch (Exception ex)
        {
            Vm.ReportError(ex);
        }
    }

    private async void OnQuickAccessSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切标签时同步高亮会走到这里：那是"跟着目录改高亮"，不是"用户点了导航项"
        if (_syncingNavSelection) return;

        // 收藏分组只是开关，不是"位置"：点一下折叠/展开，然后把高亮还给当前目录
        // （FR-7.3）。不给它单独的鼠标事件：行模板带 Margin，点在边距上时事件
        // 不经过模板里的 Grid，那条路会漏。
        if (QuickAccessList.SelectedItem is NavGroupRow group)
        {
            _nav.ToggleGroup(group);
            SyncNavSelection(Vm.CurrentPath);
            return;
        }

        // 分区标题与空态提示没有对应动作，点到了就把高亮还原，别留一个假的"选中"
        if (QuickAccessList.SelectedItem is NavSectionRow or NavEmptyRow or NavThisPcNoteRow)
        {
            SyncNavSelection(Vm.CurrentPath);
            return;
        }

        // 此电脑（FR-1.2.2）：它是一个容器，点击的语义是折叠/展开，不是"去某个目录"。
        // 与收藏分组同一个处理方式——动作做完把高亮还给当前目录。
        if (QuickAccessList.SelectedItem is NavThisPcRow)
        {
            _nav.ToggleThisPc();
            SyncNavSelection(Vm.CurrentPath);
            return;
        }

        // 驱动器（FR-1.2.2）：根目录就是普通可浏览目录，走与"已知文件夹"同一条导航路径。
        // Ctrl+单击与点其它位置一致：在新标签打开。
        if (QuickAccessList.SelectedItem is NavDriveRow driveRow)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenInNewTab(ActiveView, driveRow.Root, background: false);
                HighlightNavPath(Vm.CurrentPath);
                return;
            }

            if (string.Equals(driveRow.Root, Vm.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                await Vm.NavigateAsync(driveRow.Root);
            }
            catch (Exception ex)
            {
                Vm.ReportError(ex);
            }

            return;
        }

        // 标签节点（FR-4.1.5）：在当前标签里换成"这一色的全部条目"。
        // Ctrl+单击与点收藏一致，新开一个标签看，原目录留着不动。
        if (QuickAccessList.SelectedItem is NavTagRow tagRow)
        {
            OpenTagResults(tagRow.Color, tagRow.Name, newTab: Keyboard.Modifiers == ModifierKeys.Control);
            return;
        }

        // Ctrl+单击 / 中键点收藏项 → 新标签（与列表里那套手势一致）。中键在
        // OnBookmarkMouseDown 里已经处理过并且吃掉了事件，这里是 Ctrl+单击那条路。
        if (QuickAccessList.SelectedItem is NavBookmarkRow bookmarkRow)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenBookmark(bookmarkRow, newTab: true);
                HighlightNavPath(Vm.CurrentPath);
                return;
            }

            // 文件夹单击就进去；文件只高亮，双击才打开（FR-7.2 验收）——
            // 单击一条文件收藏就启动程序，误触的代价太大。
            if (bookmarkRow.Entry.Kind == BookmarkKind.Folder) OpenBookmark(bookmarkRow, newTab: false);
            return;
        }

        // 工作区不是"位置"，点它等于换掉整套标签（FR-8.2）。原标签会被替换，
        // 所以这里不还原高亮——还原了反而指向一个即将消失的目录。
        if (QuickAccessList.SelectedItem is NavWorkspaceRow workspaceRow)
        {
            OpenWorkspace(workspaceRow.Workspace);
            return;
        }

        if (QuickAccessList.SelectedItem is not NavItem item) return;

        if (item.IsHome)
        {
            // FR-1.2.1：主页节点在当前标签内打开，而不是另开一个标签
            Tab.ShowHome();
            return;
        }

        // 回收站 / 网络（FR-1.2.3）：shell 命名空间，没有文件系统路径，交给资源管理器
        if (item.IsShellNamespace)
        {
            OpenShellLocation(item.Kind, item.Name);
            return;
        }

        if (string.Equals(item.Path, Vm.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            await Vm.NavigateAsync(item.Path);
        }
        catch (Exception ex)
        {
            Vm.ReportError(ex);
        }
    }

    /// <summary>
    /// 新标签页的默认目录（FR-2.1）：设置里指定的那个，没指定就用系统「下载」。
    /// 每次新建都重新解析，所以设置页改完立刻生效，不需要重启。
    ///
    /// 指定的目录可能不在（移动硬盘没插、网络盘断线），这时回落到下载夹而不是报错：
    /// 用户要的是"开个标签页"，不是"验证我的设置"。设置本身保持原样，盘回来了就恢复。
    /// </summary>
    public static string ResolveNewTabPath()
    {
        string? configured = App.Settings.General.NewTabPath;
        if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured)) return configured;

        string downloads = KnownFolderService.Downloads();
        if (!string.IsNullOrEmpty(downloads) && Directory.Exists(downloads)) return downloads;

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents) && Directory.Exists(documents)) return documents;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : AppContext.BaseDirectory;
    }

    /// <summary>
    /// 启动目录：上次路径优先（session.lastPath），其次"文档"，最后用户目录。
    /// 上次路径可能已被删除，因此必须校验存在性，否则首屏是一片"无法打开"。
    /// </summary>
    private static string ResolveStartPath()
    {
        // 命令行带的目录优先：用户双击文件/快捷方式指定了位置，不该被上次的目录覆盖
        if (!string.IsNullOrEmpty(App.StartupDirectory)) return App.StartupDirectory;

        string? last = App.Settings.Session.LastPath;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents) && Directory.Exists(documents)) return documents;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : AppContext.BaseDirectory;
    }

    private void FlushOnce()
    {
        if (_flushed) return;
        _flushed = true;
        StartupTrace.Flush();
    }
}
