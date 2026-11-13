using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jianlan.App.ViewModels;

namespace Jianlan.App.Views;

/// <summary>
/// 主页视图（FR-1.1）。它只负责"把卡片画出来、把点击转成意图"，
/// 导航与新建都由窗口处理——主页不知道自己在哪个窗格里，也不该知道。
///
/// 与 <see cref="PaneView"/> 一样只有一份实例：主页视图模型是窗口级单例，
/// 多个主页标签共用（最近访问与收藏本来就是全局数据，各存一份只会不一致）。
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>点了某个入口。<c>newTab</c> 为真表示要新开标签（Ctrl+点击或中键）。</summary>
    public event Action<string, bool>? OpenRequested;

    public event Action? NewTabRequested;

    public event Action? NewFolderRequested;

    /// <summary>点了某份工作区。</summary>
    public event Action<Core.Workspaces.Workspace>? WorkspaceRequested;

    /// <summary>
    /// 在主页的工作区卡片上选了管理动作（FR-8.3）：打开 / 覆盖 / 重命名 / 启动恢复 / 删除。
    /// 与侧栏行开的是同一份菜单，动作本身全部由宿主执行——主页不知道工作区存在哪。
    /// </summary>
    public event Action<Core.Workspaces.Workspace, WorkspaceCommand>? WorkspaceCommandRequested;

    /// <summary>点了"保存当前标签…"。</summary>
    public event Action? SaveWorkspaceRequested;

    public HomeViewModel? ViewModel => DataContext as HomeViewModel;

    /// <summary>
    /// 绑定视图模型。可重复调用（切标签时可能再进来），只换 DataContext、不触发加载——
    /// 加载的时机归宿主（"这个主页标签真的被显示出来了"），在这里做会让构造期就开始读盘。
    /// </summary>
    public void Attach(HomeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        viewModel.OpenRequested += OnOpenRequested;
        viewModel.NewTabRequested += OnNewTabRequested;
        viewModel.NewFolderRequested += OnNewFolderRequested;
        viewModel.WorkspaceRequested += w => WorkspaceRequested?.Invoke(w);
        viewModel.SaveWorkspaceRequested += () => SaveWorkspaceRequested?.Invoke();
    }

    private void OnOpenRequested(string path, bool newTab) => OpenRequested?.Invoke(path, newTab);

    private void OnNewTabRequested() => NewTabRequested?.Invoke();

    private void OnNewFolderRequested() => NewFolderRequested?.Invoke();

    /// <summary>点了某份工作区（FR-8.6）。整块标签的替换归宿主做。</summary>
    private void OnWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HomeWorkspaceTile tile })
        {
            WorkspaceRequested?.Invoke(tile.Workspace);
        }
    }

    /// <summary>
    /// 卡片右键菜单里的一项（FR-8.3）。动作名写在 <c>Tag</c> 上：
    /// 五个菜单项共用这一个处理器，免得为了"点的是哪一条"再写五段一模一样的转发。
    /// </summary>
    private void OnWorkspaceMenuCommand(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: HomeWorkspaceTile tile } item) return;
        if (item.Tag is not string name || !Enum.TryParse(name, out WorkspaceCommand command)) return;

        WorkspaceCommandRequested?.Invoke(tile.Workspace, command);
    }

    private void OnSaveWorkspaceClick(object sender, RoutedEventArgs e) => SaveWorkspaceRequested?.Invoke();

    private void OnEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IHomeEntry entry }) return;

        // Ctrl+点击 = 新标签打开，与列表里的行为一致（FR-2.10）
        bool newTab = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        OpenRequested?.Invoke(entry.Path, newTab);
    }

    /// <summary>
    /// 中键新标签。Button 不把中键当 Click，所以只能自己接 PreviewMouseDown；
    /// 不处理的话中键会落到 ScrollViewer 上变成"自动滚动"。
    /// </summary>
    private void OnEntryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (sender is not FrameworkElement { DataContext: IHomeEntry entry }) return;

        e.Handled = true;
        OpenRequested?.Invoke(entry.Path, true);
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e) => NewTabRequested?.Invoke();

    private void OnNewFolderClick(object sender, RoutedEventArgs e) => NewFolderRequested?.Invoke();

    private void OnClearRecentClick(object sender, RoutedEventArgs e) => ViewModel?.ClearRecent();

    private void OnToggleBookmarksClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleBookmarks();
}
