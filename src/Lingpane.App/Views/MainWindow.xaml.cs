using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lingpane.App.Commands;
using Lingpane.App.Diagnostics;
using Lingpane.App.Services;
using Lingpane.App.ViewModels;
using Lingpane.Core.Commands;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.Settings;

using Lingpane.Core.Localization;

namespace Lingpane.App.Views;

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

    /// <summary>
    /// 双窗格是否开启（FR-3.3）。形态是窗口级而不是标签级：开着分屏切标签，
    /// 新标签也该是分屏；否则"切个标签分屏就没了"会被当成 bug。
    /// </summary>
    private bool _splitEnabled;

    /// <summary>
    /// 首帧之后才允许读盘（P1 首帧优先）。标签切换时的懒加载要靠它区分
    /// "启动期还没开始"与"运行期用户切过去了"。
    /// </summary>
    private bool _started;

    /// <summary>正在按当前目录同步导航栏高亮：此时的选择变化不是用户操作。</summary>
    private bool _syncingNavSelection;

    // 窗口级命令只有这些。窗格级命令（刷新、文件操作、视图切换）
    // 在 PaneView 自己的表里，见 PaneView.Commands.cs。
    public CommandAdapter ThemeCommand { get; }

    public MainWindow()
    {
        InitializeComponent();

        // 标签集合归窗口：两个窗格共用同一排标签（一个标签 = 一对窗格），
        // 标签不能属于某一边。浏览区的视图模型由它创建、由窗格 Attach。
        _splitEnabled = App.Settings.Layout.SplitEnabled;

        _tabs = new TabHostViewModel(App.Settings, ResolveStartPath);
        _tabs.ActiveChanged += OnActiveTabChanged;
        _tabs.CloseWindowRequested += Close;
        TabBar.Host = _tabs;

        RestoreTabs();
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

        ThemeCommand = new CommandAdapter(_commands.Get(CommandIds.CycleTheme));

        RegisterTabCommands();
        RegisterSplitCommands();

        // 分屏开关：工具栏那颗按钮在窗格里，真正的开关在窗口（FR-3.3）。
        // 只有主窗格显示按钮——分屏是窗口形态，两颗按钮会看起来像"两个窗格各分各的"。
        Pane.SplitToggleRequested += ToggleSplit;
        Pane2.ShowSplitButton = false;

        // 哪个窗格的列表拿到焦点，哪个就是活动窗格（FR-3.5）。
        Pane.Activated += OnPaneActivated;
        Pane2.Activated += OnPaneActivated;

        // 中键 / Ctrl+左键点文件夹 → 新标签（FR-2.10）。窗格只发意图，新建标签归窗口；
        // 带着"是哪个窗格发的"一起转交——到达上限时要退回到发起的那一侧导航。
        Pane.OpenInNewTabRequested += (path, background) => OpenInNewTab(Pane, path, background);
        Pane2.OpenInNewTabRequested += (path, background) => OpenInNewTab(Pane2, path, background);

        // 分屏的行列先摆好：此时还没有标签，只是把两列/两行的宽度归位
        ApplySplitLayout();
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

        // 记录最后目录与标签列表供下次启动使用。这个处理器先于 App 的保存处理器执行，
        // 因此退出时写到盘上的是本窗口刚更新的值。
        Closing += (_, _) =>
        {
            // 热键是系统级资源，进程退出前主动交还，避免句柄被回收前的短暂占用
            UnregisterSettingsHotkey();

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
                if (!Directory.Exists(path) || !seen.Add(path)) continue;

                restored.Add((
                    path,
                    i < session.TabSecondPaths.Count ? session.TabSecondPaths[i] : string.Empty,
                    i < session.TabPinned.Count && session.TabPinned[i]));
            }
        }

        if (restored.Count == 0)
        {
            // 没有可恢复的会话，或恢复项全已失效：开一个默认目录的标签
            _tabs.Activate(_tabs.AddPending(ResolveStartPath()));
            return;
        }

        foreach ((string path, string second, bool pinned) in restored)
        {
            TabViewModel tab = _tabs.AddPending(path);
            tab.IsPinned = pinned;

            // 第二窗格只记路径：窗格本身等"这个标签被激活且开着分屏"时才建，
            // 否则启动时就把 12 个标签的另一半全建出来，首帧预算立刻见底。
            if (Directory.Exists(second)) tab.PrepareSecondaryPending(second);
        }

        _tabs.ActivateAt(Math.Clamp(session.ActiveTabIndex, 0, restored.Count - 1));
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
        if (_tabs.Add() is not null) return;

        Vm.SetStatus(string.Format(Strings.Status_TabLimit, SessionSettings.MaxTabs));
        AppLog.Warn("tab.limit", "E6002", $"已达 {SessionSettings.MaxTabs} 个标签，Ctrl+T 不新建");  // i18n-ignore：只进 app.log
    }

    /// <summary>
    /// 把某个目录开进新标签（FR-2.10）。<paramref name="background"/> 为 true 时只建不切，
    /// 让用户连续开一批目录而不离开当前位置。
    ///
    /// 到 12 个上限（FR-2.5）时不新建，直接把路径交给**发起的那一侧窗格**导航——
    /// 比"按键没反应"有用，也比弹一句"标签已满"轻。所以这里带着
    /// <paramref name="source"/>：分屏时中键点右窗格，退回导航的必须是右窗格。
    /// </summary>
    private void OpenInNewTab(PaneView source, string path, bool background)
    {
        if (_tabs.Add(path, activate: !background) is not null) return;

        // 状态栏必须出声：满额时"双击文件夹没开新标签"与"双击坏了"在用户眼里一样。
        // 提示落在这个窗格自己的状态栏上——分屏时右边点的那一下，提示就该在右边。
        source.ViewModel?.SetStatus(string.Format(Strings.Status_TabLimit, SessionSettings.MaxTabs));
        AppLog.Warn("tab.limit", "E6002", $"已达 {SessionSettings.MaxTabs} 个标签，改为在当前窗格导航：{path}");  // i18n-ignore：只进 app.log
        source.NavigateTo(path);
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

        // 窗口标题与导航栏的绑定都挂在窗口 DataContext 上，跟着焦点窗格走
        DataContext = current.ActivePane;
        SyncNavSelection(current.ActivePane.CurrentPath);

        // 首帧之前不读盘：启动时由 OnLoaded 开始第一次导航（P1 首帧优先）
        if (_started) _ = LoadPanesAsync(current);
    }

    /// <summary>把一个标签的一到两份视图模型绑到对应的窗格视图上。</summary>
    private void BindTab(TabViewModel tab)
    {
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

        RefreshPaneFocus();
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

        Grid.SetRow(TabFrame, bottom ? 3 : 0);
        TabRowTop.Height = bottom ? new GridLength(0) : GridLength.Auto;
        TabRowBottom.Height = bottom ? GridLength.Auto : new GridLength(0);

        // 分隔线画在朝向浏览区的那一侧：顶部时在下方，底部时在上方
        TabFrame.BorderThickness = bottom ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1);
    }

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
        ViewFor(target).FocusList();
    }

    /// <summary>
    /// 让导航栏的高亮跟上当前目录。
    /// 切标签换了目录，但导航栏的选中项还是上一个标签留下的那一条——
    /// 看起来像是"导航栏指向 A，列表显示 B"。
    /// </summary>
    private void SyncNavSelection(string path)
    {
        if (!App.Settings.Appearance.ShowNavPane) return;

        _syncingNavSelection = true;
        try
        {
            QuickAccessList.SelectedItem = QuickAccessList.Items
                .OfType<NavItem>()
                .FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
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

        NavColumn.Width = s.Appearance.ShowNavPane
            ? new GridLength(1, GridUnitType.Auto)
            : new GridLength(0);
        QuickAccessList.Visibility = s.Appearance.ShowNavPane ? Visibility.Visible : Visibility.Collapsed;

        // 导航栏底部的占位条跟随状态栏：状态栏隐藏时它也不该留着一条空横线
        NavStatusSpacer.Visibility = s.Appearance.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;

        // 标签栏位置（FR-2.4）：设置页改完立即生效，不用重启
        ApplyTabStripPosition();

        // 分屏按钮的选中态跟着设置走（会话里可能本来就是开着的）
        SyncSplitButton();

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
    private const ModifierKeys RelevantModifiers =
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    /// <summary>
    /// 按键诊断开关：`--keydebug` 命令行参数或环境变量 <c>LINGPANE_KEYDEBUG=1</c>。
    /// 开启后把每条按键消息写进 logs/keydebug.log，用于排查输入法吃键。
    /// </summary>
    public static bool KeyDebug { get; set; } =
        Environment.GetEnvironmentVariable("LINGPANE_KEYDEBUG") == "1";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
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

        if (QuickAccessList.SelectedItem is not NavItem item) return;
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
