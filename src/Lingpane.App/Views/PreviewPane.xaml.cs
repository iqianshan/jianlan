using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingpane.App.ViewModels;

namespace Lingpane.App.Views;

/// <summary>
/// 预览窗格视图（FR-9.1.1）。只做三件事：挂视图模型、把窗格宽度换算成解码宽度、转发「立即预览」。
///
/// 宽度换算放在视图里而不是视图模型里：解码宽度是像素概念，取决于 DPI 缩放，
/// 视图模型拿不到 <c>VisualTreeHelper.GetDpi</c>，硬塞进去会变成"猜一个数字"。
/// </summary>
public partial class PreviewPane : UserControl
{
    private PreviewViewModel? _viewModel;

    public PreviewPane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public void Attach(PreviewViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // 每次解码前现算：窗格刚被拖窄时，旧宽度会让解码出来的位图白占一倍内存
        viewModel.DecodeWidthProvider = () => (int)Math.Round(
            Math.Max(1, ActualWidth - 32) * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is PreviewViewModel viewModel) _viewModel = viewModel;
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => _viewModel?.Retry();
}
