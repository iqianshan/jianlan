using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lingpane.App.Diagnostics;
using Lingpane.App.ViewModels;
using Lingpane.Core.Localization;
using Lingpane.Core.Navigation;
using Lingpane.Core.Settings;

namespace Lingpane.App.Views;

/// <summary>
/// 导航栏的窗口侧接线：宽度拖动（FR-1.2.4）、中键开新标签（FR-1.2.6）、
/// 以及系统命名空间的交接（FR-1.2.3 的回收站 / 网络）。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 导航栏分隔条的拖动热区宽度（列宽）。与 MainWindow.xaml 里 NavSplitterColumn 的初始值一致。
    /// 写成常量而不是读 ActualWidth：导航栏隐藏时那一列是 0，再读回来会把热区一起关掉。
    /// </summary>
    private const double NavSplitterWidth = 5;

    /// <summary>
    /// 按设置排布导航栏：显示/隐藏、宽度、分隔条。
    ///
    /// 宽度从设置读、按上下限夹取：手改过 settings.json 的值（或旧版本没有 navWidth）
    /// 都收在这个函数里，界面各处不必各自判断一遍。
    /// </summary>
    private void ApplyNavLayout()
    {
        bool visible = App.Settings.Appearance.ShowNavPane;

        // 列上的 Min/Max 必须跟着一起改：只把 Width 设成 0，MinWidth 仍会把它撑到 160
        NavColumn.MinWidth = visible ? AppSettings.MinNavWidth : 0;
        NavColumn.MaxWidth = visible ? AppSettings.MaxNavWidth : 0;

        NavColumn.Width = visible
            ? new GridLength(Math.Clamp(
                App.Settings.Layout.NavWidth, AppSettings.MinNavWidth, AppSettings.MaxNavWidth))
            : new GridLength(0);

        // 分隔条与那条竖线一起收起：只把导航栏宽度归零，会在窗口左缘留下一条能拖的 5px 空白，
        // 拖它还会把看不见的导航栏重新拉出来。
        NavSplitterColumn.Width = new GridLength(visible ? NavSplitterWidth : 0);
        NavSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NavDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        QuickAccessList.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 拖完分隔条才落盘（与预览分隔条同一口径）：拖动过程中每个像素都写一次设置文件是不可行的。
    /// 拖动期间 GridSplitter 已经按列的 Min/Max 夹过了，这里再夹一次是防"手改过上下限"的情况。
    /// </summary>
    private void OnNavSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (NavColumn.ActualWidth <= 0) return;

        double width = Math.Clamp(NavColumn.ActualWidth, AppSettings.MinNavWidth, AppSettings.MaxNavWidth);
        if (Math.Abs(width - App.Settings.Layout.NavWidth) < 0.5) return;

        App.Settings.Layout.NavWidth = width;
        App.SaveSettings();
    }

    /// <summary>双击分隔条回默认宽度 184（与预览那边"双击回 320"一致）。</summary>
    private void OnNavSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        App.Settings.Layout.NavWidth = AppSettings.DefaultNavWidth;
        ApplyNavLayout();
        App.SaveSettings();
        e.Handled = true;
    }

    /// <summary>
    /// 导航栏中键（FR-1.2.6）：在地点/标签上等同于"在新标签打开"，
    /// 与列表区、收藏项的中键手势一致。中键不改变选中项，所以行是从命中元素反查的，
    /// 不能读 <c>SelectedItem</c>——那是上一次点过的行。
    /// </summary>
    private void OnNavMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        // 收藏项有自己的处理器（OnBookmarkMouseDown）并且已经吃掉了事件，走不到这里；
        // 这里只管系统节点、标签与工作区。
        switch (FindNavRow(e.OriginalSource as DependencyObject))
        {
            case NavItem { Kind: NavItemKind.Folder } item when !string.IsNullOrEmpty(item.Path):
                e.Handled = true;
                OpenInNewTab(ActiveView, item.Path, background: false);
                break;

            // 驱动器（FR-1.2.2）与普通目录同等对待：中键在新标签打开
            case NavDriveRow drive:
                e.Handled = true;
                OpenInNewTab(ActiveView, drive.Root, background: false);
                break;

            case NavItem item when item.IsShellNamespace:
                e.Handled = true;
                OpenShellLocation(item.Kind, item.Name);
                break;

            case NavTagRow tag:
                e.Handled = true;
                OpenTagResults(tag.Color, tag.Name, newTab: true);
                break;

            case NavWorkspaceRow workspace:
                // 工作区本身就是"一整套标签"，再套一层新标签没有意义：与左键同一个动作
                e.Handled = true;
                OpenWorkspace(workspace.Workspace);
                break;
        }
    }

    /// <summary>从命中的元素向上找到最近一行导航项。行模板内部全是 TextBlock/Border，DataContext 才是行。</summary>
    private static object? FindNavRow(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: INavRow row }) return row;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>
    /// 打开系统命名空间（回收站 / 网络，FR-1.2.3）。
    ///
    /// 为什么交给资源管理器而不是自己列：
    ///   · 回收站的内容存在 <c>$Recycle.Bin</c>，还原/清空要靠 shell 动词，本程序的
    ///     文件操作层只认路径，做一个"能看见但点不动"的列表比不做更糟；
    ///   · "网络"是 Win32 网络命名空间（要 WNet 枚举 + 凭据 UI），也不是路径。
    /// 这两项本来就是"系统的"，交给系统自己的界面是诚实的选择，也顺带避开了 3 秒超时——
    /// 真正会卡住用户的是在地址栏输入一个断线的 <c>\\服务器\共享</c>，那条路由
    /// <see cref="Lingpane.Core.FileSystem.SlowLocations"/> 兜（3 秒后提示"无法访问"）。
    /// </summary>
    private void OpenShellLocation(NavItemKind kind, string name)
    {
        string? uri = kind switch
        {
            NavItemKind.RecycleBin => "shell:RecycleBinFolder",
            NavItemKind.Network => "shell:NetworkPlacesFolder",
            _ => null,
        };

        if (uri is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            Vm.SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Status_OpenedInExplorer, name));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn("nav.shellopen", "E2004", $"{uri} · {ex.GetType().Name}: {ex.Message}");
            Vm.SetStatus(Strings.Status_ShellOpenFailed);
        }

        // 高亮还给当前目录：这两项不是"当前位置"，选中态留在它们上面会撒谎
        SyncNavSelection(Vm.CurrentPath);
    }

    /// <summary>
    /// 「位置」标题行尾的齿轮 → 打开勾选菜单（FR-1.2.9）。
    ///
    /// 菜单挂在按钮自己的 ContextMenu 上而不是新建 Popup：这样右键也能开出同一份，
    /// 且与明细视图的列配置菜单共用一套样式和勾选列排版，不必再造一份"看起来像菜单"的东西。
    /// </summary>
    private void OnNavPlacesMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// 菜单打开瞬间按当前设置刷新勾选态。
    ///
    /// 不在打开之后维持双向绑定：设置是唯一真相，菜单只是它的一次投影。
    /// 每次开都重算，就不存在"菜单开着时设置被别处改了、关掉再开还是旧状态"的问题。
    /// </summary>
    private void OnNavPlacesMenuOpening(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        IReadOnlyList<string> hidden = App.Settings.Layout.NavHiddenPlaces;

        foreach (object entry in menu.Items)
        {
            if (entry is not MenuItem item) continue;

            // 带 Tag 的是九项位置；不带 Tag 的是「恢复全部显示」——它只在真隐藏过东西时可用。
            // 全部显示着的时候点它什么也不会发生，留着可点等于在菜单里放一个坏按钮。
            if (item.Tag is string key) item.IsChecked = !NavPlaces.IsHidden(hidden, key);
            else item.IsEnabled = hidden.Count > 0;
        }
    }

    /// <summary>勾选/取消一项位置。勾选 = 显示，取消 = 隐藏。</summary>
    private void OnToggleNavPlace(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;

        // 菜单项自己已经把 IsChecked 翻过面了，这里只跟设置对齐，不再读 IsChecked——
        // 免得两处各翻一次，变成"点一下没变、点两下才对"。
        List<string> hidden = App.Settings.Layout.NavHiddenPlaces;
        if (!hidden.Remove(key)) hidden.Add(key);

        ApplyNavPlaces();
    }

    /// <summary>「恢复全部显示」：清空隐藏清单。</summary>
    private void OnRestoreAllNavPlaces(object sender, RoutedEventArgs e)
    {
        App.Settings.Layout.NavHiddenPlaces.Clear();
        ApplyNavPlaces();
    }

    /// <summary>
    /// 隐藏清单变了之后的三件事：归一后落盘、重建「位置」这一段、把高亮还给当前目录。
    ///
    /// 归一不能省：用户勾选的顺序是随机的，落盘前按显示顺序排好，settings.json 才读得懂；
    /// 顺带丢掉认不出的键（旧版本残留、手改的错字），不让它被下一次保存再写回去。
    ///
    /// 重建后要重新同步高亮：被隐掉的可能正是当前目录那一项，那时该没有高亮，
    /// 而不是让高亮停在一条已经不在列表里的行上。
    /// </summary>
    private void ApplyNavPlaces()
    {
        App.Settings.Layout.NavHiddenPlaces = [.. NavPlaces.Normalize(App.Settings.Layout.NavHiddenPlaces)];
        App.SaveSettings();

        _nav.RefreshLocationRows();
        SyncNavSelection(Vm.CurrentPath);
    }
}
