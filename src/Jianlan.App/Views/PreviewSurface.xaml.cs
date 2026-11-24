using System.Windows;
using System.Windows.Controls;

namespace Jianlan.App.Views;

/// <summary>
/// 预览内容层（FR-9.1.3）。预览窗格与空格浮层共用，DataContext 为
/// <see cref="ViewModels.PreviewViewModel"/>。
///
/// 唯一需要往外转发的交互是「立即预览」（FR-9.2.8）：延后判定与重试都归视图模型，
/// 这个控件不认识视图模型的方法，只发一个意图，由宿主决定能不能重试。
/// </summary>
public partial class PreviewSurface : UserControl
{
    public PreviewSurface() => InitializeComponent();

    /// <summary>用户点了「立即预览」。宿主转给视图模型的 <c>Retry()</c>。</summary>
    public event EventHandler? RetryRequested;

    private void OnRetryClick(object sender, RoutedEventArgs e)
        => RetryRequested?.Invoke(this, EventArgs.Empty);
}
