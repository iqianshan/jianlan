using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingpane.App.Shell;
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
    private PreviewHandlerHost? _handlerHost;

    public PreviewPane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => _handlerHost?.Close();
    }

    public void Attach(PreviewViewModel viewModel)
    {
        Hook(viewModel);
        DataContext = viewModel;

        // 每次解码前现算：窗格刚被拖窄时，旧宽度会让解码出来的位图白占一倍内存
        viewModel.DecodeWidthProvider = () => (int)Math.Round(
            Math.Max(1, ActualWidth - 32) * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is PreviewViewModel viewModel) Hook(viewModel);
    }

    private void Hook(PreviewViewModel viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;

        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;

        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreviewViewModel.HasHandler)) return;

        if (_viewModel?.HasHandler == true)
        {
            EnsureHandlerHost();
            return;
        }

        _handlerHost?.Close();
    }

    /// <summary>
    /// 宿主只建一次。处理器要被换掉时走 <see cref="PreviewHandlerHost.Open"/> 内部的重启，
    /// 而不是拆了重建——重建会连带销毁窗口句柄，正在渲染的处理器会在一个死句柄上继续画。
    /// </summary>
    private void EnsureHandlerHost()
    {
        if (_handlerHost is null)
        {
            _handlerHost = new PreviewHandlerHost();

            _handlerHost.Ready += (_, _) => _viewModel?.HandlerReady();
            _handlerHost.Failed += (_, _) => _viewModel?.HandlerFailed();

            // 首次加入可视化树时容器窗口还没建出来，Open 会被跳过；等 Loaded 再补一次
            _handlerHost.Loaded += (_, _) => OpenHandler();

            HandlerLayer.Children.Add(_handlerHost);
        }

        OpenHandler();
    }

    private void OpenHandler()
    {
        if (_viewModel is not { HasHandler: true } viewModel || _handlerHost is null) return;
        if (!_handlerHost.IsBuilt) return;

        _handlerHost.Open(viewModel.HandlerPath, viewModel.HandlerClsid);
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => _viewModel?.Retry();
}
