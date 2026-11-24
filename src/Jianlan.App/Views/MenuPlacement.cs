using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace Jianlan.App.Views;

/// <summary>
/// 右键菜单的左右补偿。
///
/// 问题：菜单稳定弹在鼠标左侧（右边缘贴指针、向下展开），而屏幕上明明放得下。
///
/// 成因是系统的“菜单右对齐”标志：WPF 的 <see cref="System.Windows.Controls.Primitives.Popup"/>
/// 在 <c>PlacementMode.MousePoint</c> 下读到它为真时，会把整块菜单摆到指针左侧。
/// 本机这个标志的三个来源互相打架——注册表 <c>MenuDropAlignment</c> 与
/// <c>SPI_GETMENUDROPALIGNMENT</c> 都是 0（右手），只有
/// <c>GetSystemMetrics(SM_MENUDROPALIGNMENT)</c> 返回 1，而 WPF 读的正是后者。
/// 资源管理器用的是自绘菜单，不读这个值，所以看上去“只有我这儿不对”。
///
/// 处理办法：菜单弹出前先量一次尺寸，右侧放得下就补一个自身宽度，把它拉回指针右下；
/// 放不下就不补，仍用 WPF 摆的左侧位置——那种时候左边本来就是它该在的地方。
/// </summary>
internal static class MenuPlacement
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>
    /// 让菜单出现在指针右下。只在系统把菜单判成“右对齐”时才补，
    /// 系统正常（右手）时不做任何事，避免把本该有的位置也挪歪。
    /// </summary>
    /// <param name="owner">用来取 DPI 与所在显示器的可视对象（一般是窗口自身）。</param>
    /// <param name="menu">即将弹出的菜单。</param>
    /// <param name="screenPoint">右键位置的屏幕坐标，物理像素。</param>
    public static void KeepRightOfCursor(Visual owner, ContextMenu menu, Point screenPoint)
    {
        // 只处理鼠标点定位的菜单。Custom 的坐标是调用方自己算出来的（侧栏那套"贴右边缘"），
        // 补一个宽度只会把它推歪，而且那种菜单本来就不受这个系统标志影响。
        if (menu.Placement != PlacementMode.MousePoint) return;

        if (!SystemParameters.MenuDropAlignment)
        {
            menu.HorizontalOffset = 0;
            return;
        }

        // 菜单还没打开，ActualWidth 是 0，只能先量一次拿期望宽度。
        // 这一步顺带把子项容器建出来，所以必须在 ApplyMenuScope 之后调用：
        // 隐藏项不计宽度，先量后改会造成补偿量偏大。
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = menu.DesiredSize.Width;
        if (width <= 0) return;

        DpiScale dpi = VisualTreeHelper.GetDpi(owner);
        double scale = dpi.DpiScaleX;
        if (scale <= 0) return;

        double workRight = WorkAreaRight(owner) / scale;   // 物理 → DIP
        double cursorX = screenPoint.X / scale;

        // WPF 此时已经把菜单摆到了指针左侧（右缘贴指针）。把它右移一个自身宽度，
        // 左缘就落回指针处；右侧容不下时才保留左侧位置。
        menu.HorizontalOffset = cursorX + width <= workRight ? width : 0;
    }

    /// <summary>
    /// 向上找最近的、挂着菜单的元素。命中物通常是行模板里的某个子元素，
    /// 菜单只挂在列表、列头、标签条、侧栏行这几层上。
    /// </summary>
    public static ContextMenu? FindAbove(DependencyObject start)
    {
        for (DependencyObject? node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { ContextMenu: { } menu }) return menu;
        }

        return null;
    }

    /// <summary>窗格所在显示器的可用区右边界（物理像素）。多显示器下必须按当前屏算。</summary>
    private static double WorkAreaRight(Visual owner)
    {
        IntPtr hwnd = IntPtr.Zero;
        if (Window.GetWindow(owner) is { } window)
        {
            hwnd = new WindowInteropHelper(window).Handle;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return SystemParameters.WorkArea.Right;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea.Right;

        return info.Work.Right;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
