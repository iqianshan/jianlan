using System.Windows;
using System.Windows.Controls;

using Lingpane.App.ViewModels;
using Lingpane.Core.Tagging;

namespace Lingpane.App.Views;

/// <summary>
/// 窗格的彩色标签入口（FR-4.1.1 / FR-4.1.3 / FR-4.1.2）。
///
/// 为什么整段逻辑放在窗格而不是窗口：标签作用于"这个窗格选中的项"，
/// 双窗格下选中项各有一份。放窗口上，每个处理器都要先问"活动窗格是谁"。
/// </summary>
public partial class PaneView
{
    /// <summary>
    /// 颜色子菜单每次展开时按当前选中项刷一次勾选（FR-4.1.2）。
    ///
    /// 为什么不在 <c>ContextMenuOpening</c> 里做：那一刻子菜单的容器还没生成，
    /// <c>ContainerFromItem</c> 全是 null，勾选状态会一片空白。子菜单展开时容器才齐。
    /// </summary>
    private void OnTagMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem parent || _viewModel is not { } vm) return;

        TagColors mask = vm.SelectionTagMask;

        foreach (object item in parent.Items)
        {
            if (item is not TagPresets.ColorPreset preset) continue;
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not MenuItem container) continue;

            container.IsChecked = (mask & preset.Value) != 0;
        }
    }

    /// <summary>
    /// 颜色子菜单里的某一条被点了。
    ///
    /// 语义是**切换**而不是"设成这一色"（FR-4.1.2）：一项可以同时是红和蓝，
    /// "设成"会让每次点击都抹掉上一色，多色永远攒不出来。已勾上的再点一次即去掉这一色。
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
            await vm.ToggleTagAsync(colors);
        }
        catch (Exception ex)
        {
            // 打标签失败不该变成异常对话框：写盘失败已在 TagStore 里降级为只读 + 日志
            vm.ReportError(ex);
        }
    }

    /// <summary>
    /// 「移除标签」：把选中项身上的颜色标签清空（FR-4.1.3）。
    ///
    /// 必须直设 <see cref="TagColors.None"/>，不能借道 <see cref="ApplyTagAsync"/> 的切换语义。
    /// 切换的实现是"mask 里有这一色就去掉、没有就加上"，传 <c>None</c>（全 0）进去异或的
    /// 结果恒等于原值——点了菜单一点动静都没有，看上去就是"这一项坏了"。
    /// </summary>
    private async Task ClearTagsAsync()
    {
        if (_viewModel is not { } vm) return;

        try
        {
            await vm.ApplyTagAsync(TagColors.None);
        }
        catch (Exception ex)
        {
            vm.ReportError(ex);
        }
    }
}
