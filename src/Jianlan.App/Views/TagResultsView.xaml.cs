using System.Windows.Controls;
using System.Windows.Input;
using Jianlan.App.ViewModels;

namespace Jianlan.App.Views;

/// <summary>
/// 标签结果页（FR-4.1.5）。视图本身不认识导航——双击只把"要看这一条"抛给窗口，
/// 跳转归窗口（它才知道当前标签、分屏形态与选中恢复）。
/// </summary>
public partial class TagResultsView : UserControl
{
    public TagResultsView() => InitializeComponent();

    /// <summary>双击了某一条。窗口据此跳到它所在目录并选中。</summary>
    public event Action<TagResultItem>? ItemActivated;

    public void Attach(TagResultsViewModel viewModel) => DataContext = viewModel;

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultList.SelectedItem is TagResultItem item) ItemActivated?.Invoke(item);
    }
}
