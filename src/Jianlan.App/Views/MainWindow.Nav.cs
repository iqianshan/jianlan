using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Jianlan.App.Diagnostics;
using Jianlan.App.ViewModels;
using Jianlan.Core.Localization;
using Jianlan.Core.Navigation;
using Jianlan.Core.Settings;

namespace Jianlan.App.Views;

/// <summary>
/// 导航栏的窗口侧接线：宽度拖动（FR-1.2.4）、中键开新标签（FR-1.2.6）、
/// 以及系统命名空间的交接（FR-1.2.3 的回收站 / 网络）。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 导航栏分隔条的拖动热区宽度（列宽）。与 MainWindow.xaml 里 NavSplitterColumn 的初始值一致。
    /// 写成常量而不是读 ActualWidth：导航栏隐藏时那一列是 0，再读回来会把热区一起关掉。
    /// </summary>
    private const double NavSplitterWidth = 5;

    /// <summary>上一次"点已选中导航项"补导航的目标与时刻（<see cref="Environment.TickCount64"/>）。</summary>
    private string? _navReclickPath;
    private long _navReclickAt;

    /// <summary>当前跟随侧栏高亮的窗格（焦点窗格）。</summary>
    private PaneViewModel? _navFollow;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();

    /// <summary>
    /// 按设置排布导航栏：显示/隐藏、宽度、分隔条。
    ///
    /// 宽度从设置读、按上下限夹取：手改过 settings.json 的值（或旧版本没有 navWidth）
    /// 都收在这个函数里，界面各处不必各自判断一遍。
    /// </summary>
    private void ApplyNavLayout()
    {
        bool visible = App.Settings.Appearance.ShowNavPane;

        // 列上的 Min/Max 必须跟着一起改：只把 Width 设成 0，MinWidth 仍会把它撑到 160
        NavColumn.MinWidth = visible ? AppSettings.MinNavWidth : 0;
        NavColumn.MaxWidth = visible ? AppSettings.MaxNavWidth : 0;

        NavColumn.Width = visible
            ? new GridLength(Math.Clamp(
                App.Settings.Layout.NavWidth, AppSettings.MinNavWidth, AppSettings.MaxNavWidth))
            : new GridLength(0);

        // 分隔条与那条竖线一起收起：只把导航栏宽度归零，会在窗口左缘留下一条能拖的 5px 空白，
        // 拖它还会把看不见的导航栏重新拉出来。
        NavSplitterColumn.Width = new GridLength(visible ? NavSplitterWidth : 0);
        NavSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NavDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        QuickAccessList.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 拖完分隔条才落盘（与预览分隔条同一口径）：拖动过程中每个像素都写一次设置文件是不可行的。
    /// 拖动期间 GridSplitter 已经按列的 Min/Max 夹过了，这里再夹一次是防"手改过上下限"的情况。
    /// </summary>
    private void OnNavSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (NavColumn.ActualWidth <= 0) return;

        double width = Math.Clamp(NavColumn.ActualWidth, AppSettings.MinNavWidth, AppSettings.MaxNavWidth);
        if (Math.Abs(width - App.Settings.Layout.NavWidth) < 0.5) return;

        App.Settings.Layout.NavWidth = width;
        App.SaveSettings();
    }

    /// <summary>双击分隔条回默认宽度 184（与预览那边"双击回 320"一致）。</summary>
    private void OnNavSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        App.Settings.Layout.NavWidth = AppSettings.DefaultNavWidth;
        ApplyNavLayout();
        App.SaveSettings();
        e.Handled = true;
    }

    /// <summary>
    /// 让侧栏高亮跟着焦点窗格的导航走。
    ///
    /// 原来只在"点侧栏 / 切标签 / 换窗格"时同步，列表里的导航（双击进子目录、地址栏、
    /// 后退、刷新）一次都不管。于是高亮会停在上一次点过的那一项上——明明已经在 Downloads
    /// 的深处，侧栏还亮着上一次点过的「文档」；更糟的是因为"点已选中项不触发
    /// SelectionChanged"，那一项还点不动（<see cref="ReclickSelectedPlace"/> 是另一半修复）。
    /// </summary>
    private void FollowNavPane(PaneViewModel? pane)
    {
        if (ReferenceEquals(_navFollow, pane)) return;

        if (_navFollow is not null) _navFollow.NavigationCompleted -= OnNavFollowArrived;
        _navFollow = pane;
        if (pane is not null) pane.NavigationCompleted += OnNavFollowArrived;
    }

    private void OnNavFollowArrived(string path)
    {
        // 非焦点窗格在后台刷新目录时不改高亮：侧栏回答的是"焦点窗格在哪"
        if (!ReferenceEquals(_navFollow, Tab.ActivePane)) return;

        SyncNavSelection(path);
    }

    /// <summary>
    /// 左键按下先进这一层（隧道），比列表行自己处理得更早。
    ///
    /// 为什么不能用冒泡的 <c>MouseDown</c>：点在一行上时，<c>ListBoxItem</c> 在
    /// <c>MouseLeftButtonDown</c> 里就把事件标成已处理了，挂在 ListBox 上的
    /// <c>MouseDown</c> 收不到——实测"点已选中行"时那个处理器一次都进不来，
    /// 这也是本缺陷藏得深的原因之一。中键仍走冒泡（<see cref="OnNavMouseDown"/>），
    /// 它已有的验收不受影响。
    /// </summary>
    private void OnNavPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        ReclickSelectedPlace(e);
    }

    /// <summary>
    /// 点一个**已经选中**的导航行时补一次导航。
    ///
    /// WPF 的 ListBox 在点击已选中项时不会再触发 SelectionChanged，而这一列的导航只挂在
    /// SelectionChanged 上。后果很具体：在 <c>下载\某个子目录</c> 里，「下载」是高亮的
    /// （它确实是当前位置的祖先，见 <see cref="NavPanelViewModel.FindRow"/>），
    /// 再点它却什么都不会发生——用户看到的就是"点了左上角的「下载」，右边列表没反应"。
    ///
    /// 只在"按下的行正是当前选中项"时接管：
    ///   · 按在别的行上，选中会变，SelectionChanged 会照常导航；这里也去导航等于跑两遍；
    ///   · 目标就是当前目录时不动（已经在那一层了）。
    /// </summary>
    private void ReclickSelectedPlace(MouseButtonEventArgs e)
    {
        object? hit = FindNavRow(e.OriginalSource as DependencyObject);
        if (!ReferenceEquals(hit, QuickAccessList.SelectedItem)) return;

        string? target = QuickAccessList.SelectedItem switch
        {
            NavItem { Kind: NavItemKind.Folder } item when !string.IsNullOrEmpty(item.Path) => item.Path,
            NavDriveRow drive => drive.Root,
            _ => null,
        };

        if (target is null) return;

        // 驱动器上的 Ctrl+单击与"它没被选中时点它"同一个意思：新标签打开。
        // 这条分支在 SelectionChanged 里已经有了（FR-1.2.6），这里不能漏——
        // 否则同一个手势在"这一行刚好已高亮"时突然变成在当前标签里跳。
        if (QuickAccessList.SelectedItem is NavDriveRow && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenInNewTab(ActiveView, target, background: false);
            HighlightNavPath(Vm.CurrentPath);
            return;
        }

        if (string.Equals(target, Vm.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;

        // 双击会送来两次 MouseDown，第二次时上一次的导航还没回来。同一个目标在一个
        // 双击时间内只认一次，免得多绕一趟目录枚举。
        long now = Environment.TickCount64;
        if (string.Equals(target, _navReclickPath, StringComparison.OrdinalIgnoreCase)
            && now - _navReclickAt < GetDoubleClickTime())
        {
            e.Handled = true;
            return;
        }

        _navReclickPath = target;
        _navReclickAt = now;

        e.Handled = true;
        _ = NavigateFromNavAsync(target);
    }

    private async Task NavigateFromNavAsync(string path)
    {
        try
        {
            await Vm.NavigateAsync(path);
        }
        catch (Exception ex)
        {
            Vm.ReportError(ex);
        }
    }

    /// <summary>
    /// 导航栏中键（FR-1.2.6）：在地点/标签上等同于"在新标签打开"，
    /// 与列表区、收藏项的中键手势一致。中键不改变选中项，所以行是从命中元素反查的，
    /// 不能读 <c>SelectedItem</c>——那是上一次点过的行。
    /// </summary>
    private void OnNavMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        // 收藏项有自己的处理器（OnBookmarkMouseDown）并且已经吃掉了事件，走不到这里；
        // 这里只管系统节点、标签与工作区。
        switch (FindNavRow(e.OriginalSource as DependencyObject))
        {
            case NavItem { Kind: NavItemKind.Folder } item when !string.IsNullOrEmpty(item.Path):
                e.Handled = true;
                OpenInNewTab(ActiveView, item.Path, background: false);
                break;

            // 驱动器（FR-1.2.2）与普通目录同等对待：中键在新标签打开
            case NavDriveRow drive:
                e.Handled = true;
                OpenInNewTab(ActiveView, drive.Root, background: false);
                break;

            case NavItem item when item.IsShellNamespace:
                e.Handled = true;
                OpenShellLocation(item.Kind, item.Name);
                break;

            case NavTagRow tag:
                e.Handled = true;
                OpenTagResults(tag.Color, tag.Name, newTab: true);
                break;

            case NavWorkspaceRow workspace:
                // 工作区本身就是"一整套标签"，再套一层新标签没有意义：与左键同一个动作
                e.Handled = true;
                OpenWorkspace(workspace.Workspace);
                break;
        }
    }

    /// <summary>从命中的元素向上找到最近一行导航项。行模板内部全是 TextBlock/Border，DataContext 才是行。</summary>
    private static object? FindNavRow(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: INavRow row }) return row;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>
    /// 打开系统命名空间（回收站 / 网络，FR-1.2.3）。
    ///
    /// 为什么交给资源管理器而不是自己列：
    ///   · 回收站的内容存在 <c>$Recycle.Bin</c>，还原/清空要靠 shell 动词，本程序的
    ///     文件操作层只认路径，做一个"能看见但点不动"的列表比不做更糟；
    ///   · "网络"是 Win32 网络命名空间（要 WNet 枚举 + 凭据 UI），也不是路径。
    /// 这两项本来就是"系统的"，交给系统自己的界面是诚实的选择，也顺带避开了 3 秒超时——
    /// 真正会卡住用户的是在地址栏输入一个断线的 <c>\\服务器\共享</c>，那条路由
    /// <see cref="Jianlan.Core.FileSystem.SlowLocations"/> 兜（3 秒后提示"无法访问"）。
    /// </summary>
    private void OpenShellLocation(NavItemKind kind, string name)
    {
        string? uri = kind switch
        {
            NavItemKind.RecycleBin => "shell:RecycleBinFolder",
            NavItemKind.Network => "shell:NetworkPlacesFolder",
            _ => null,
        };

        if (uri is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            Vm.SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Status_OpenedInExplorer, name));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn("nav.shellopen", "E2004", $"{uri} · {ex.GetType().Name}: {ex.Message}");
            Vm.SetStatus(Strings.Status_ShellOpenFailed);
        }

        // 高亮还给当前目录：这两项不是"当前位置"，选中态留在它们上面会撒谎
        SyncNavSelection(Vm.CurrentPath);
    }

    /// <summary>
    /// 「位置」标题行尾的齿轮 → 打开勾选菜单（FR-1.2.9）。
    ///
    /// 菜单挂在按钮自己的 ContextMenu 上而不是新建 Popup：这样右键也能开出同一份，
    /// 且与明细视图的列配置菜单共用一套样式和勾选列排版，不必再造一份"看起来像菜单"的东西。
    /// </summary>
    private void OnNavPlacesMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;

        AnchorMenuRight(menu, button);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// 菜单打开瞬间按当前设置刷新勾选态。
    ///
    /// 不在打开之后维持双向绑定：设置是唯一真相，菜单只是它的一次投影。
    /// 每次开都重算，就不存在"菜单开着时设置被别处改了、关掉再开还是旧状态"的问题。
    /// </summary>
    private void OnNavPlacesMenuOpening(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        IReadOnlyList<string> hidden = App.Settings.Layout.NavHiddenPlaces;

        foreach (object entry in menu.Items)
        {
            if (entry is not MenuItem item) continue;

            // 带 Tag 的是九项位置；不带 Tag 的是「恢复全部显示」——它只在真隐藏过东西时可用。
            // 全部显示着的时候点它什么也不会发生，留着可点等于在菜单里放一个坏按钮。
            if (item.Tag is string key) item.IsChecked = !NavPlaces.IsHidden(hidden, key);
            else item.IsEnabled = hidden.Count > 0;
        }
    }

    /// <summary>勾选/取消一项位置。勾选 = 显示，取消 = 隐藏。</summary>
    private void OnToggleNavPlace(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;

        // 菜单项自己已经把 IsChecked 翻过面了，这里只跟设置对齐，不再读 IsChecked——
        // 免得两处各翻一次，变成"点一下没变、点两下才对"。
        List<string> hidden = App.Settings.Layout.NavHiddenPlaces;
        if (!hidden.Remove(key)) hidden.Add(key);

        ApplyNavPlaces();
    }

    /// <summary>「恢复全部显示」：清空隐藏清单。</summary>
    private void OnRestoreAllNavPlaces(object sender, RoutedEventArgs e)
    {
        App.Settings.Layout.NavHiddenPlaces.Clear();
        ApplyNavPlaces();
    }

    /// <summary>
    /// 隐藏清单变了之后的三件事：归一后落盘、重建「位置」这一段、把高亮还给当前目录。
    ///
    /// 归一不能省：用户勾选的顺序是随机的，落盘前按显示顺序排好，settings.json 才读得懂；
    /// 顺带丢掉认不出的键（旧版本残留、手改的错字），不让它被下一次保存再写回去。
    ///
    /// 重建后要重新同步高亮：被隐掉的可能正是当前目录那一项，那时该没有高亮，
    /// 而不是让高亮停在一条已经不在列表里的行上。
    /// </summary>
    private void ApplyNavPlaces()
    {
        App.Settings.Layout.NavHiddenPlaces = [.. NavPlaces.Normalize(App.Settings.Layout.NavHiddenPlaces)];
        App.SaveSettings();

        _nav.RefreshLocationRows();
        SyncNavSelection(Vm.CurrentPath);
    }

    // ── 行尾「更多操作」与右键菜单的定位（FR-7.3 / FR-8.3） ────────────────────

    /// <summary>
    /// 侧栏里的菜单一律"右边缘贴侧栏右边界、向下展开"。
    ///
    /// 为什么不用默认的 <c>MousePoint</c>：菜单跟着鼠标在行内的横向位置漂，同一行每次点开
    /// 落在不同地方。
    ///
    /// 为什么不用 <c>Bottom</c>：WPF 的 <c>Bottom</c> 是"下方 + **左边缘**对齐锚点"，
    /// 不是水平居中——这一点和直觉相反。侧栏只有 227 DIP、菜单宽 210 DIP，左边缘贴上去
    /// 就是向右铺，整块压到文件列表上（实测工作区菜单右缘越过侧栏 172 物理像素）。
    ///
    /// 两个必须自己算的偏移：
    /// <list type="number">
    /// <item>行的右边缘**不是**侧栏右边界。ListBox 的内边距与滚动条那一段不在行里，
    ///   实测差 82 DIP；只贴行右边缘的话，菜单会整块被推出窗口左边（只剩右半截）。</item>
    /// <item><c>PlacementMode.Custom</c> 下 WPF **不做越界修正**，坐标给多少就放多少。
    ///   所以左边与下边都要自己 clamp，否则菜单在窗口边缘会被裁掉。</item>
    /// </list>
    /// </summary>
    private static void AnchorMenuRight(ContextMenu menu, FrameworkElement anchor)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popupSize, anchorSize, _) =>
        {
            // 锚点右边缘 → 侧栏右边缘的那一段，补上才等于"贴着侧栏右边"
            double extra = 0;
            double anchorLeft = 0;
            double anchorTop = 0;
            Window? window = Window.GetWindow(anchor);

            if (FindNavScope(anchor) is { } scope)
            {
                try
                {
                    double right = anchor.TransformToAncestor(scope)
                        .Transform(new Point(anchorSize.Width, 0)).X;
                    extra = Math.Max(0, scope.ActualWidth - right);
                }
                catch (InvalidOperationException) { }
            }

            if (window is { IsLoaded: true })
            {
                try
                {
                    Point origin = anchor.TransformToAncestor(window).Transform(new Point(0, 0));
                    anchorLeft = origin.X;
                    anchorTop = origin.Y;
                }
                catch (InvalidOperationException) { }
            }

            double x = anchorSize.Width + extra - popupSize.Width;
            double y = anchorSize.Height;

            // 左边越界就往回收 4px（菜单比侧栏宽的那几像素宁可压一点内容区，
            // 也不能整块跑到窗口外）
            if (window is { IsLoaded: true }) x = Math.Max(x, 4 - anchorLeft);

            // 下方放不下就翻到锚点上方；上方也放不下再压回窗口内
            if (window is { IsLoaded: true } && anchorTop + y + popupSize.Height > window.ActualHeight - 4)
            {
                y = -popupSize.Height;
                if (anchorTop + y < 4) y = 4 - anchorTop;
            }

            return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal)];
        };
    }

    /// <summary>向上找到侧栏的 ListBox：它才是"侧栏右边界"的度量对象。</summary>
    private static ListBox? FindNavScope(DependencyObject? node)
    {
        for (DependencyObject? cur = node; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is ListBox list) return list;
        }

        return null;
    }

    /// <summary>
    /// 行尾「⋯」被点击：打开这一行自己的菜单，锚在按钮左下方向左展开。
    ///
    /// 锚点必须显式指定。菜单挂在行的根 Grid 上（数据模板里只写了一份），
    /// 而 ContextMenu 默认以"鼠标位置"为参考点——按钮是被点出来的，鼠标可能早就不在那儿了，
    /// 菜单就会飘到上一次鼠标所在的角落。
    /// </summary>
    private void OnRowMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        if (FindRowMenu(button) is not { } menu) return;

        AnchorMenuRight(menu, button);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// 右键按下时直接接管：打开菜单，并且**吃掉这次按键**。
    ///
    /// 吃掉这一下不是洁癖，是必须的：<see cref="ListBox"/> 会把右键当成"选中这一行"，
    /// 而选中工作区行／收藏项就等于打开它（<c>OnQuickAccessSelectionChanged</c>）。
    /// 实测右键工作区行会直接弹出"打开工作区"的确认框——用户只是想改个名字，
    /// 却先被问"要不要替换当前标签"。在隧道阶段拦下，行的选中态就不会动。
    /// </summary>
    private void OnRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        if (row.ContextMenu is not { } menu) return;

        e.Handled = true;

        AnchorMenuRight(menu, row);
        menu.IsOpen = true;
    }

    /// <summary>
    /// 键盘打开同一份菜单（Shift+F10 / 菜单键）。鼠标右键走
    /// <see cref="OnRowRightButtonDown"/>，这里只补键盘那条路——定位规则相同，
    /// 都交给 <see cref="AnchorMenuRight"/>。
    /// </summary>
    private void OnRowMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        if (row.ContextMenu is not { } menu) return;

        AnchorMenuRight(menu, row);
    }

    /// <summary>
    /// 从按钮往上找到挂着菜单的那一层（就是行的根 Grid）。
    /// 碰到 <see cref="ListBoxItem"/> 就停：再往上就是别的行，绝不能借到邻居的菜单。
    /// </summary>
    private static ContextMenu? FindRowMenu(DependencyObject? start)
    {
        for (DependencyObject? node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListBoxItem) return null;
            if (node is FrameworkElement { ContextMenu: { } menu }) return menu;
        }

        return null;
    }
}
