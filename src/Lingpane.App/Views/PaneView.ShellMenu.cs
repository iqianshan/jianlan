using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Lingpane.App.Diagnostics;
using Lingpane.App.Shell;
using Lingpane.Core.Localization;

namespace Lingpane.App.Views;

/// <summary>
/// 系统 Shell 菜单的接入侧（FR-6.9）。两个入口共用这里：
///   · 右键菜单底部的「更多选项…」；
///   · <c>Shift+右键</c>（拦截在 <c>OnFileRightButtonDown</c> 里）。
///
/// 视图不下判断、只负责凑参数：选中项路径、owner 句柄、屏幕点。
/// 失败时的降级也在这一层——状态栏出一句话，比"右键没反应"有用得多。
/// </summary>
public partial class PaneView
{
    /// <summary>
    /// 最近一次右键的屏幕点。菜单项被点开时鼠标可能已经移开，
    /// 用 <c>Mouse.GetPosition</c> 现取会让菜单弹在手指抬起的位置而不是右键的位置。
    /// </summary>
    private Point? _contextMenuScreenPoint;

    /// <summary>
    /// 本轮右键走的是系统菜单，自建菜单不许弹。每次右键按下时重新赋值，
    /// 由 <see cref="OnFileContextMenuOpening"/> 消费。
    ///
    /// 不能在弹出系统菜单之后就把标记清掉：自建菜单是右键**抬起**时才弹的，
    /// 而系统菜单在按下与抬起之间就已经弹出来了（降级到 Background 的那一下），
    /// 清得太早，抬起时标记已经是 false，两个菜单照样叠在一起。
    /// </summary>
    private bool _shellMenuPending;

    /// <summary>
    /// 自建菜单即将弹出。Shift+右键那一轮直接取消，避免和系统菜单叠在一起。
    /// 消费即清零：这个标记只管一次右键。
    /// </summary>
    private void OnFileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!_shellMenuPending) return;

        e.Handled = true;
        _shellMenuPending = false;
    }

    /// <summary>
    /// 把系统菜单推迟到当前输入事件处理完之后再弹。
    ///
    /// 直接用命令的执行体弹是不行的：命令是在 <c>MenuItem.OnKeyDown</c> 里同步跑的，
    /// 此刻 WPF 的自建菜单**还没关闭**（关闭动作要等这次按键处理完）。两个菜单同时存在时，
    /// 自建菜单关窗会把鼠标捕获收回去，刚弹出来的系统菜单被一起收掉——
    /// 表现就是"点了更多选项，什么都没发生"，既不报错也不留痕。
    /// 降到 <see cref="DispatcherPriority.Background"/> 之后，自建菜单已经收干净了。
    /// </summary>
    private void QueueShellContextMenu(int screenX, int screenY, bool extendedVerbs)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => ShowShellContextMenu(screenX, screenY, extendedVerbs)));
    }

    private void ShowShellContextMenu(int screenX, int screenY, bool extendedVerbs)
    {
        if (_viewModel is null) return;

        var paths = new List<string>(_viewModel.SelectedEntries.Count);
        foreach (var entry in _viewModel.SelectedEntries)
        {
            paths.Add(entry.FullPath);
        }

        // 没选中任何项时 Shift+右键落在空白处：退回当前目录，等于"文件夹背景右键"
        if (paths.Count == 0)
        {
            if (string.IsNullOrEmpty(_viewModel.CurrentPath)) return;
            paths.Add(_viewModel.CurrentPath);
        }

        IntPtr hwnd = IntPtr.Zero;
        if (Window.GetWindow(this) is { } window)
        {
            hwnd = new WindowInteropHelper(window).Handle;
        }

        if (ShellContextMenu.TryShow(hwnd, paths, screenX, screenY, extendedVerbs)) return;

        // 降级：系统菜单打不开也要说一声。静默的话用户只会得到"右键没反应"，
        // 而下一次再试还是没反应——他没有任何线索知道这是系统扩展的问题。
        _viewModel.SetStatus(Strings.Status_ShellMenuUnavailable);
        AppLog.Warn("shell.menu", "E7001", $"系统菜单未能弹出，已降级；项数={paths.Count}");  // i18n-ignore
    }

    /// <summary>「更多选项…」命令的执行体。坐标用打开菜单时记下的那个点。</summary>
    private void ShowShellContextMenuFromMenu()
    {
        Point screen = _contextMenuScreenPoint
            ?? PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        QueueShellContextMenu((int)screen.X, (int)screen.Y, extendedVerbs: false);
    }
}
