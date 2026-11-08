using System.Windows;
using System.Windows.Controls;

using Lingpane.App.ViewModels;
using Lingpane.Core.Tagging;

namespace Lingpane.App.Views;

/// <summary>
/// 窗格的彩色标签入口（FR-4.1.1 / FR-4.1.3）。
///
/// 为什么整段逻辑放在窗格而不是窗口：标签作用于"这个窗格选中的项"，
/// 双窗格下选中项各有一份。放窗口上，每个处理器都要先问"活动窗格是谁"。
/// </summary>
public partial class PaneView
{
    /// <summary>
    /// 颜色子菜单里的某一条被点了。
    ///
    /// 读 <c>MenuItem.Header</c> 而不是 <c>DataContext</c>：带 <c>ItemsSource</c> 的菜单项，
    /// 容器只被塞进 <c>Header</c>/<c>HeaderTemplate</c>，**不设** <c>DataContext</c>——
    /// 那里读到的是父菜单继承下来的对象（这里是本窗格），拿它当预设会让 switch 静默落空。
    /// FR-7 的"选了颜色没反应"就是这么来的，别再踩一次。
    /// </summary>
    private void OnTagColorPicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: TagPresets.ColorPreset preset }) return;

        _ = ApplyTagAsync(preset.Value);
    }

    private async Task ApplyTagAsync(TagColors colors)
    {
        if (_viewModel is not { } vm) return;

        try
        {
            await vm.ApplyTagAsync(colors);
        }
        catch (Exception ex)
        {
            // 打标签失败不该变成异常对话框：写盘失败已在 TagStore 里降级为只读 + 日志
            vm.ReportError(ex);
        }
    }
}
