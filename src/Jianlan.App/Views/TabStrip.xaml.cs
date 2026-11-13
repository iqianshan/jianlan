using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Jianlan.App.Diagnostics;
using Jianlan.App.ViewModels;
using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// 标签栏的交互：新建、切换、关闭、拖拽排序、右键菜单。
///
/// 视图不做业务判断——"关掉最后一个标签该去哪"这类规则在
/// <see cref="TabHostViewModel"/> 里，这里只把点击翻译成对它的调用。
/// 唯一的例外是拖拽：插入位置要靠可视位置算，只有视图知道每个标签落在哪。
/// </summary>
public partial class TabStrip : UserControl
{
    /// <summary>拖拽判定阈值（像素）。小于它一律当点击，避免手抖把标签顺序改了。</summary>
    private const double DragThreshold = 6;

    private TabHostViewModel? _host;

    private int _dragFrom = -1;
    private Point _dragOrigin;
    private bool _dragging;

    public TabStrip()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 标签集合。由窗口注入，注入后本控件把自己的 DataContext 设为它，
    /// 模板里的绑定直接写 <c>{Binding Tabs}</c> 即可。
    /// </summary>
    public TabHostViewModel? Host
    {
        get => _host;
        set
        {
            if (ReferenceEquals(_host, value)) return;

            if (_host is not null) _host.ActiveChanged -= OnActiveChanged;

            _host = value;
            DataContext = value;

            if (_host is not null) _host.ActiveChanged += OnActiveChanged;
        }
    }

    // ── 活动标签滚入视野 ────────────────────────────────────────────────────
    //
    // FR-2.5 要求标签溢出时横向滚动。Ctrl+Tab 切到第 12 个标签时，
    // 如果它正好在可视区外，用户看到的画面是"什么都没变"——必须主动滚过去。

    private void OnActiveChanged(TabViewModel? previous, TabViewModel? current)
    {
        if (current is null) return;

        // 等布局跑完再滚：刚新建的标签此刻还没生成容器
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FindContainer(current) is { } container) container.BringIntoView();
        });
    }

    private FrameworkElement? FindContainer(TabViewModel tab)
        => TabItems.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement;

    // ── 新建 / 关闭 / 菜单 ──────────────────────────────────────────────────

    private void OnNewTabClick(object sender, RoutedEventArgs e) => _host?.Add();

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: TabViewModel tab }) _host?.Close(tab);
    }

    /// <summary>
    /// 右键菜单弹出前，把"固定 / 取消固定"那条改成当前状态对应的说法（FR-2.9）。
    ///
    /// 用一条菜单项改标题，而不是放两条互斥的菜单项：模板里要因此写两个
    /// 条件相反的 <c>DataTrigger</c>，而这里只是打开前一次字符串赋值。
    /// 每次弹出都重设，所以语言切换后也不会停在旧文案上。
    /// </summary>
    private void OnTabMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu, DataContext: TabViewModel tab }) return;

        foreach (object item in menu.Items)
        {
            if (item is MenuItem { Tag: "pin" } pin)
            {
                pin.Header = tab.IsPinned ? Strings.Tab_Menu_Unpin : Strings.Tab_Menu_Pin;
                return;
            }
        }
    }

    private void OnTabTogglePin(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: TabViewModel tab }) tab.IsPinned = !tab.IsPinned;
    }

    private void OnTabCloseOthers(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: TabViewModel tab }) _host?.CloseOthers(tab);
    }

    private void OnTabCloseRight(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: TabViewModel tab }) _host?.CloseToRight(tab);
    }

    private void OnTabCopyPath(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: TabViewModel tab }) return;

        // 分屏时"复制路径"给两条路径，中间换行：单独给一边的信息不完整，
        // 而用户复制路径多半是要粘到别处做定位，两条都给才够用。
        (string primary, string secondary) = tab.SnapshotPaths();
        if (string.IsNullOrEmpty(primary)) return;

        string text = string.IsNullOrEmpty(secondary)
            ? primary
            : $"{primary}{Environment.NewLine}{secondary}";

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // 剪贴板被别的进程占用是常态，不弹窗也不崩，留痕即可
            AppLog.Warn("tab.copyPath", "E6001", ex.Message);
        }
    }

    // ── 键盘（FR-10.7）──────────────────────────────────────────────────────

    /// <summary>
    /// 标签上的键盘操作。焦点环由模板画（Border 没有 FocusVisualStyle），
    /// 这里只负责把按键翻译成动作。
    ///
    /// 方向键直接切换标签而不是"只移动焦点"：与系统 TabControl 一致，
    /// 少按一次 Enter；不提供 Delete 关闭——误触会把正在用的标签关掉，
    /// 键盘关闭走 Ctrl+W，和鼠标右键菜单是同一个动作。
    /// </summary>
    private void OnTabKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabViewModel tab }) return;

        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
                _host?.Activate(tab);
                e.Handled = true;
                break;

            case Key.Left:
                MoveToSibling(tab, -1);
                e.Handled = true;
                break;

            case Key.Right:
                MoveToSibling(tab, 1);
                e.Handled = true;
                break;
        }
    }

    private void MoveToSibling(TabViewModel tab, int delta)
    {
        if (_host is null) return;

        int index = _host.Tabs.IndexOf(tab);
        if (index < 0) return;

        int next = Math.Clamp(index + delta, 0, _host.Tabs.Count - 1);
        if (next == index) return;

        TabViewModel target = _host.Tabs[next];
        _host.Activate(target);

        // 容器此刻可能还没生成（刚新建的标签），等一轮布局再落焦点
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FindContainer(target) is FrameworkElement container) container.Focus();
        });
    }

    // ── 鼠标：切换 / 中键关闭 / 拖拽排序 ────────────────────────────────────

    private void OnTabMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = -1;

        if (sender is not FrameworkElement { DataContext: TabViewModel tab }) return;

        // 点在关闭按钮上：切换留给按钮自己，这里不抢先
        if (FindAncestor<Button>(e.OriginalSource) is not null) return;

        _host?.Activate(tab);

        _dragOrigin = e.GetPosition(TabItems);
        _dragFrom = _host?.Tabs.IndexOf(tab) ?? -1;
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 中键关闭（FR-2.3）
        if (e.ChangedButton != MouseButton.Middle) return;
        if (sender is not FrameworkElement { DataContext: TabViewModel tab }) return;

        e.Handled = true;
        _host?.Close(tab);
    }

    private void OnStripMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom < 0 || e.LeftButton != MouseButtonState.Pressed) return;

        Point position = e.GetPosition(TabItems);

        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragOrigin.X) < DragThreshold) return;

            _dragging = true;
            TabItems.CaptureMouse();
        }

        ShowDropIndicator(InsertIndexAt(position.X));
    }

    private void OnStripMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging && _host is { } host)
        {
            int insert = InsertIndexAt(e.GetPosition(TabItems).X);

            // 拖到自身左侧时，插入位置就是最终位置；拖到右侧要减一，
            // 因为移除源项之后，它后面所有项的索引都前移了一位。
            int target = insert > _dragFrom ? insert - 1 : insert;
            host.Move(_dragFrom, target);
        }

        EndDrag();
    }

    /// <summary>指针落在第几个插入位（0 = 最前，Count = 最后）。</summary>
    private int InsertIndexAt(double x)
    {
        int count = TabItems.Items.Count;

        for (int i = 0; i < count; i++)
        {
            if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;

            Point origin = container.TranslatePoint(new Point(0, 0), TabItems);
            if (x < origin.X + container.ActualWidth / 2) return i;
        }

        return count;
    }

    private void ShowDropIndicator(int insertIndex)
    {
        double x = TabItems.ActualWidth;

        if (insertIndex < TabItems.Items.Count
            && TabItems.ItemContainerGenerator.ContainerFromIndex(insertIndex) is FrameworkElement next)
        {
            x = next.TranslatePoint(new Point(0, 0), TabItems).X;
        }
        else if (TabItems.Items.Count > 0
                 && TabItems.ItemContainerGenerator.ContainerFromIndex(TabItems.Items.Count - 1) is FrameworkElement last)
        {
            x = last.TranslatePoint(new Point(0, 0), TabItems).X + last.ActualWidth;
        }

        // TabItems 坐标 → 覆盖层坐标（拖动过程中内容可能已经滚动过）
        Point inStrip = TabItems.TranslatePoint(new Point(x, 0), this);
        Canvas.SetLeft(DropIndicator, inStrip.X - DropIndicator.Width / 2);
        Canvas.SetTop(DropIndicator, 0);
        DropIndicator.Visibility = Visibility.Visible;
    }

    private void EndDrag()
    {
        if (TabItems.IsMouseCaptured) TabItems.ReleaseMouseCapture();

        _dragging = false;
        _dragFrom = -1;
        DropIndicator.Visibility = Visibility.Collapsed;
    }

    // ── 拖到标签页打开（FR-6.11）────────────────────────────────────────────
    //
    // 从本程序的列表或资源管理器把文件夹拖到标签栏：落在某个标签上就由那个标签
    // 导航过去，落在空白处就开新标签。视图只负责"落在哪个标签上"，
    // 开标签还是导航由宿主决定——与右键菜单、中键同一个分工。

    /// <summary>
    /// 有路径被拖到标签栏上（FR-6.11）。<c>target</c> 为 null 表示落在空白处。
    ///
    /// 只报"哪里"，不做目录判断：拖来的是文件还是文件夹，要看磁盘，
    /// 那是宿主的活。
    /// </summary>
    public event Action<string[], TabViewModel?>? OpenDropped;

    /// <summary>
    /// 标签菜单里点了「在新窗口打开」（FR-2.12）。视图只报"哪个标签"，
    /// 开窗口归宿主——标签栏不该知道窗口是怎么造出来的。
    /// </summary>
    public event Action<TabViewModel>? OpenInNewWindowRequested;

    private void OnTabOpenWindow(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: TabViewModel tab }) OpenInNewWindowRequested?.Invoke(tab);
    }

    private void OnStripDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            HideDropTarget();
            return;
        }

        // 拖到标签栏是"打开"，不会移动或复制任何文件
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        ShowDropTarget(TabAt(e.GetPosition(TabItems)));
    }

    private void OnStripDragLeave(object sender, DragEventArgs e) => HideDropTarget();

    private void OnStripDrop(object sender, DragEventArgs e)
    {
        HideDropTarget();

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        e.Handled = true;
        OpenDropped?.Invoke(paths, TabAt(e.GetPosition(TabItems)));
    }

    /// <summary>指针下的标签。落点在标签之间的空隙或 <c>+</c> 按钮上时返回 null。</summary>
    private TabViewModel? TabAt(Point position)
    {
        for (int i = 0; i < TabItems.Items.Count; i++)
        {
            if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;

            Point origin = container.TranslatePoint(new Point(0, 0), TabItems);
            if (position.X >= origin.X && position.X <= origin.X + container.ActualWidth)
            {
                return container.DataContext as TabViewModel;
            }
        }

        return null;
    }

    private void ShowDropTarget(TabViewModel? tab)
    {
        if (tab is null || _host is null)
        {
            // 落在空白处：开新标签，没有具体目标可框
            HideDropTarget();
            return;
        }

        int index = _host.Tabs.IndexOf(tab);
        if (index < 0 || TabItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
        {
            HideDropTarget();
            return;
        }

        Point origin = container.TranslatePoint(new Point(0, 0), this);

        Canvas.SetLeft(DropTarget, origin.X);
        Canvas.SetTop(DropTarget, origin.Y);
        DropTarget.Width = container.ActualWidth;
        DropTarget.Height = container.ActualHeight;
        DropTarget.Visibility = Visibility.Visible;
    }

    private void HideDropTarget() => DropTarget.Visibility = Visibility.Collapsed;

    /// <summary>滚轮在标签栏上 = 横向滚动（纵向滚动在标签栏没有意义）。</summary>
    private void OnStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Scroller.ScrollableWidth <= 0) return;

        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        var node = source as DependencyObject;
        while (node is not null and not T)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }
}
