using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Jianlan.App.ViewModels;

namespace Jianlan.App.Views;

/// <summary>
/// 空格预览浮层（FR-9.1.7）。内容由独立的 <see cref="PreviewViewModel"/> 提供，
/// 与预览窗格各用一份——共用一份会在开合、切选中时互相取消在途任务，表现为内容闪一下或空白。
///
/// 三条窗口级约束，缺一条体验就塌：
/// <list type="number">
/// <item><b>不激活</b>（<c>WS_EX_NOACTIVATE</c>）：<c>ShowActivated=false</c> 只挡住首次激活，
///       之后的鼠标/键盘路径仍可能把它拉起来；扩展样式才是硬的。</item>
/// <item><b>鼠标穿透</b>（<c>WS_EX_TRANSPARENT</c>）：所有点击落到下面的主窗，
///       于是"点别处就收起"不需要额外监听——主窗收到点击，由它关浮层。</item>
/// <item><b>不进 Alt+Tab</b>（<c>WS_EX_TOOLWINDOW</c>）：它是主窗的一部分，不该被单独切出来。</item>
/// </list>
/// </summary>
public partial class QuickLookWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    /// <summary>浮层最小可用尺寸。再小就看不清缩略图，不如不给。</summary>
    private const double MinWidthDip = 320;
    private const double MinHeightDip = 240;

    /// <summary>宽度占主窗的比例与上限。上限来自预览解码宽度（1024），再宽只会拉糊。</summary>
    private const double WidthRatio = 0.62;
    private const double MaxWidthDip = 1040;

    private const double HeightRatio = 0.72;
    private const double MaxHeightDip = 760;

    private Window? _owner;

    public QuickLookWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyExtendedStyles();
    }

    /// <summary>
    /// 挂到主窗上并摆好位置。尺寸取主窗的六成、且不超过主窗本身：
    /// 这样它一定落在主窗所在的那块屏里，不需要再查一次显示器工作区
    /// （多屏 + 不同缩放时那段换算最容易算错）。
    /// </summary>
    public void AttachTo(Window owner)
    {
        if (!ReferenceEquals(_owner, owner))
        {
            Detach();

            _owner = owner;
            Owner = owner;
            owner.LocationChanged += OnOwnerBoundsChanged;
            owner.SizeChanged += OnOwnerBoundsChanged;
            Closed += OnWindowClosed;
        }

        PlaceOver(owner);
    }

    private void OnOwnerBoundsChanged(object? sender, EventArgs e)
    {
        if (_owner is not null) PlaceOver(_owner);
    }

    private void PlaceOver(Window owner)
    {
        double availableWidth = Math.Max(MinWidthDip, owner.ActualWidth - 96);
        double availableHeight = Math.Max(MinHeightDip, owner.ActualHeight - 120);

        Width = Math.Min(MaxWidthDip, Math.Min(availableWidth, owner.ActualWidth * WidthRatio));
        Height = Math.Min(MaxHeightDip, Math.Min(availableHeight, owner.ActualHeight * HeightRatio));

        // 居中偏上 20px：视觉重心略高于几何中心，与"翻一眼"的用法相称
        double left = owner.Left + (owner.ActualWidth - Width) / 2;
        double top = owner.Top + (owner.ActualHeight - Height) / 2 - 20;

        // 主窗贴到屏幕边缘时不要跑出窗外
        Left = Math.Max(owner.Left + 8, left);
        Top = Math.Max(owner.Top + 8, top);
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Detach();

    private void Detach()
    {
        if (_owner is null) return;

        _owner.LocationChanged -= OnOwnerBoundsChanged;
        _owner.SizeChanged -= OnOwnerBoundsChanged;
        _owner = null;

        Closed -= OnWindowClosed;
    }

    private void ApplyExtendedStyles()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(
            hwnd, GwlExStyle,
            style | WsExNoActivate | WsExTransparent | WsExToolWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
