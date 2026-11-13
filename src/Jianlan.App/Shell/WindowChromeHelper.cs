using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Jianlan.App.Shell;

/// <summary>
/// 无边框窗口的统一入口（FR-10.11）。所有窗口的标题栏都是自绘的，
/// 系统只负责阴影、圆角、贴边吸附与最大化动画。
///
/// 为什么是 <see cref="WindowChrome"/> 而不是 <c>AllowsTransparency="True"</c>：
/// 后者会把窗口降级成分层窗口，硬件加速关闭（滚动与缩略图明显变慢）、
/// 系统阴影与贴边吸附全部消失、最大化还会盖住任务栏。WindowChrome 保留全部系统行为。
///
/// 为什么不用手写 <c>DragMove()</c>：CaptionHeight 区域由系统按 HTCAPTION 处理，
/// 拖动、双击最大化、拖到屏幕边缘吸附、Win+方向键都免费拿到。手写 DragMove 这三个全丢。
///
/// 为什么也不自己处理 WM_GETMINMAXINFO：WPF 的 WindowChromeWorker 已经在做这件事
/// （按 DPI 把最大化尺寸钳到当前显示器的工作区）。再挂一个钩子覆盖它，
/// 等于把已经换算好的物理像素按 DPI 又放大一遍——实测副屏 125% 下最大化宽出 640px，
/// 右边缘与底边直接跑出屏幕。这条弯路走过一次，记在这里。
/// </summary>
public static class WindowChromeHelper
{
    /// <summary>标题栏高度。与 <c>Size.TitleBar.Height</c> 的基线值一致。</summary>
    public const double CaptionHeight = 40;

    /// <summary>
    /// 把窗口切成自绘标题栏形态。必须在 <c>Show()</c> 之前调用——
    /// WindowStyle 在窗口创建后不可改，运行期改要重建 HWND。
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="resizable">可缩放窗口留 6px 拉伸手区；对话框给 false，四边不可拖。</param>
    public static void Attach(Window window, bool resizable = true)
    {
        window.WindowStyle = WindowStyle.None;

        var chrome = new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            // 6px 与系统默认拉伸边框一致，再宽会盖住内容边缘的滚动条
            ResizeBorderThickness = resizable ? new Thickness(6) : new Thickness(0),
            // 玻璃边框必须归零：非零时 WindowChrome 会留一圈主题色描边，
            // 与自绘的标题栏底色对不上，深色主题下尤其明显
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };

        WindowChrome.SetWindowChrome(window, chrome);
        window.SourceInitialized += (_, _) => Hook(window);
    }

    private static void Hook(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        if (HwndSource.FromHwnd(handle) is not { } source) return;

        source.RemoveHook(OnWindowMessage);
        source.AddHook(OnWindowMessage);
    }

    private static IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging)
        {
            MaximizeToWorkArea(hwnd, lParam);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 把最大化窗口钉在当前显示器的工作区。
    ///
    /// 为什么必须在 WM_WINDOWPOSCHANGING 里改，而不是 WM_GETMINMAXINFO：
    /// WindowChromeWorker 会在这个消息里把最大化窗口设成"整个显示器 + 8px 隐形边框"，
    /// 覆盖 WM_GETMINMAXINFO 里算好的尺寸。有系统标题栏时那 8px 藏在屏幕外看不出来，
    /// 标题栏自绘后客户区就等于窗口矩形——实测副屏 125% 下底边比工作区多出 68px，
    /// 整个任务栏被盖住。这里在它之后把矩形改回工作区（本 hook 挂在 chrome worker 之后，
    /// 拿到的是它写过一遍的值，改完即最终值）。
    /// </summary>
    private static void MaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref placement)) return;

        // showCmd 已经是目标状态：即将最大化时读到的就是 SW_SHOWMAXIMIZED，
        // 不能用 IsZoomed——那一刻窗口还没切过去
        if (placement.ShowCmd != SwShowMaximized) return;

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        NativeRect work = info.Work;
        var pos = Marshal.PtrToStructure<WindowPos>(lParam);

        // flags 里若带 SWP_NOSIZE / SWP_NOMOVE，说明这次调用不改对应维度，别硬塞
        const uint noMove = 0x0002;
        const uint noSize = 0x0001;

        if ((pos.Flags & noMove) == 0)
        {
            pos.X = work.Left;
            pos.Y = work.Top;
        }

        if ((pos.Flags & noSize) == 0)
        {
            pos.Cx = work.Right - work.Left;
            pos.Cy = work.Bottom - work.Top;
        }

        Marshal.StructureToPtr(pos, lParam, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
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
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private const int WmWindowPosChanging = 0x0046;
    private const int MonitorDefaultToNearest = 0x00000002;
    private const int SwShowMaximized = 3;

    /// <summary>
    /// 标题栏高度变化（字号缩放）后刷新可拖动区域。CaptionHeight 不是依赖属性，
    /// 绑不了 DynamicResource；标题条长高了却不更新，下半截就拖不动窗口。
    /// </summary>
    public static void UpdateCaptionHeight(Window window, double height)
    {
        if (WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            chrome.CaptionHeight = height;
        }
    }
}
