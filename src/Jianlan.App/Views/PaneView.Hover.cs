using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using Jianlan.App.Design;
using Jianlan.Core.FileSystem;

namespace Jianlan.App.Views;

/// <summary>
/// 行的悬停反馈（FR-5.2.5）。
///
/// 单击打开模式下，"鼠标扫过"就等于"即将打开"，此时若行跟着闪高亮，整片列表
/// 会在鼠标划过的瞬间抖一遍。所以高亮推迟 600ms：停下来看才有反馈，扫过去没有。
/// 双击模式下延迟为 0，与原生一致。
/// </summary>
public partial class PaneView
{
    private ListBoxItem? _hoverItem;

    private DispatcherTimer? _hoverTimer;

    /// <summary>
    /// 在构造里挂一次。不走 XAML 的 <c>PreviewMouseMove</c>：那里已经被拖拽逻辑占用，
    /// 而一个路由事件在 XAML 里只能绑一个处理器。
    /// </summary>
    private void HookRowHover()
    {
        FileList.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnRowHoverMove),
            handledEventsToo: true);

        FileList.MouseLeave += (_, _) => ClearRowHover();

        // 滚轮滚动后容器会被回收复用，鼠标不动也会落到"另一行"上。
        // 不清掉的话，原本要等 600ms 的高亮会立刻出现在刚滚出来的那一行。
        FileList.PreviewMouseWheel += (_, _) => ClearRowHover();
    }

    private void OnRowHoverMove(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        // ContainerFromElement 走的是 WPF 自己那套映射，虚拟化下也能拿对
        ListBoxItem? item = ItemsControl.ContainerFromElement(FileList, source) as ListBoxItem;
        if (ReferenceEquals(item, _hoverItem)) return;

        ClearRowHover();
        if (item is null) return;

        _hoverItem = item;

        if (App.Settings.Behavior.DoubleClickOpens)
        {
            RowHover.SetIsHovered(item, true);
            return;
        }

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(RowHover.RevealDelayMs),
        };
        _hoverTimer.Tick += OnHoverTimerTick;
        _hoverTimer.Start();
    }

    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        StopHoverTimer();
        if (_hoverItem is { } item) RowHover.SetIsHovered(item, true);
    }

    private void ClearRowHover()
    {
        StopHoverTimer();
        if (_hoverItem is { } item) RowHover.SetIsHovered(item, false);
        _hoverItem = null;
    }

    private void StopHoverTimer()
    {
        if (_hoverTimer is null) return;

        _hoverTimer.Stop();
        _hoverTimer.Tick -= OnHoverTimerTick;
        _hoverTimer = null;
    }

    /// <summary>单击打开（FR-5.2.5）。只在没有修饰键时生效——Shift 留给区间选择。</summary>
    private bool TryOpenOnSingleClick(MouseButtonEventArgs e)
    {
        if (App.Settings.Behavior.DoubleClickOpens) return false;
        if (Keyboard.Modifiers != ModifierKeys.None) return false;
        if (e.OriginalSource is not DependencyObject source) return false;
        if (FindAncestor<ListBoxItem>(source) is not { DataContext: FileEntry entry }) return false;

        OpenEntry(entry);
        return true;
    }
}
