using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lingpane.App.Diagnostics;
using Lingpane.App.ViewModels;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Localization;
using Lingpane.Core.Navigation;
using Lingpane.Core.Settings;

namespace Lingpane.App.Views;

/// <summary>
/// 收藏的交互（FR-7）：Ctrl+D、导航栏收藏区的点击与右键菜单。
///
/// 状态都落在 <see cref="BookmarkStore"/>（settings.json 的 <c>bookmarks</c> 段）。
/// 每次改完走 <see cref="ApplyBookmarkChange"/>：重建导航栏、刷主页、立刻落盘。
/// 三件事必须一起做——少落盘会在崩溃时丢掉刚加的收藏，少刷主页会出现
/// "导航栏有了、主页的收藏区还是空的"。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Ctrl+D（FR-7.1 / FR-7.2）：有选中项就收藏选中的那些，没选中就收藏当前目录。
    ///
    /// 为什么不弹输入框问名字：收藏是高频轻动作，每次都打断打字的话，用户会改用别的方式。
    /// 默认名取路径末段（显示时本来就是它），想改名字走右键「重命名」。
    /// </summary>
    private void BookmarkCurrent()
    {
        if (Tab.Kind != TabKind.Files)
        {
            // 主页与标签结果页都不"位于"任何目录，收藏它们没有意义（收藏的是路径）
            Vm.SetStatus(Strings.Status_BookmarkHome);
            return;
        }

        IReadOnlyList<FileEntry> selected = Vm.SelectedEntries;
        IReadOnlyList<string> targets = selected.Count > 0
            ? selected.Select(e => e.FullPath).ToArray()
            : string.IsNullOrEmpty(Vm.CurrentPath) ? [] : [Vm.CurrentPath];

        if (targets.Count == 0) return;

        int added = 0;
        bool duplicate = false;
        bool full = false;

        foreach (string path in targets)
        {
            switch (_bookmarks.TryAdd(path))
            {
                case BookmarkAddResult.Added:
                    added++;
                    break;
                case BookmarkAddResult.Duplicate:
                    duplicate = true;
                    break;
                case BookmarkAddResult.Full:
                    full = true;
                    break;
            }
        }

        if (added > 0)
        {
            ApplyBookmarkChange();
            HighlightNavPath(targets[0]);
            Vm.SetStatus(string.Format(
                Strings.Status_BookmarkAdded, targets.Count == 1 ? Display(targets[0]) : added.ToString()));
            return;
        }

        if (full)
        {
            // FR-7.9：到上限必须出声，不能"按了没反应"
            Vm.SetStatus(string.Format(Strings.Status_BookmarkFull, BookmarkSettings.MaxItems));
            AppLog.Warn("bookmark.limit", "E7002", $"收藏已达 {BookmarkSettings.MaxItems} 条，拒绝新增");  // i18n-ignore：只进 app.log
            return;
        }

        if (duplicate)
        {
            // FR-7.1 验收：重复收藏时提示并把那一行高亮出来，而不是什么都不发生
            HighlightNavPath(targets[0]);
            Vm.SetStatus(string.Format(Strings.Status_BookmarkExists, Display(targets[0])));
        }

        static string Display(string path)
        {
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? path : name;
        }
    }

    /// <summary>改完收藏后统一收口：导航栏、主页、磁盘三处一起更新。</summary>
    private void ApplyBookmarkChange()
    {
        _nav.Rebuild();
        _home.Refresh();
        App.SaveSettings();
    }

    // ── 导航栏收藏区的事件 ──────────────────────────────────────────────────

    /// <summary>分区标题与空态提示：吃掉点击，不让它们被 ListBox 选中（选中也没有对应动作）。</summary>
    /// <summary>
    /// 分区标题的鼠标。默认什么都不做（它不可选中），但带管理动作的分区
    /// （收藏、工作区）左键也把菜单打开。
    ///
    /// 原本这两个入口只挂在右键上。工作区那一段在侧栏最底部，窗口不够高时
    /// 连标题都只露半行，右键是唯一入口——等于没有入口。
    /// 「位置」「标签」没有管理动作，<see cref="NavSectionRow.HasMenu"/> 为假，点它们仍然安静。
    /// </summary>
    private void OnNavInertRowClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (sender is not TextBlock { DataContext: NavSectionRow { HasMenu: true } } label) return;
        if (label.ContextMenu is not { } menu) return;

        // 必须指定锚点：菜单项的显隐绑定（IsBookmarks / IsWorkspaces）靠 DataContext，
        // 而 ContextMenu 的 DataContext 是从 PlacementTarget 继承来的。
        // 少了这一句，两个分区的菜单会同时显示全部动作。
        // 定位用 AnchorMenuRight 而不是默认的 MousePoint：见那里的说明，侧栏太窄，
        // 菜单只有"右边缘贴锚点向左铺"才留得下。
        AnchorMenuRight(menu, label);
        menu.IsOpen = true;
    }

    /// <summary>
    /// 侧栏里"菜单挂在元素上、没有代码定位"的那几处（收藏分组行、位置标题、分区标题右键）
    /// 统一在这里补定位。
    ///
    /// 它们原先都靠默认的 <c>MousePoint</c>，菜单跟着鼠标往右下铺，越过侧栏压住文件列表。
    /// 走 <c>ContextMenuOpening</c> 而不是 <c>Opened</c>：后者发生时位置已经算过一轮，
    /// 再改 <c>Placement</c> 会让菜单先闪在旧位置。
    /// </summary>
    private void OnNavMenuOpeningRight(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } anchor) return;

        AnchorMenuRight(menu, anchor);
    }

    /// <summary>
    /// 「位置」「标签」两个分区的标题没有管理动作，右键菜单里一项都不会显示。
    /// 空菜单仍会弹出一个小空框——在 Opened 里直接关掉，比让它闪一下体面。
    /// </summary>
    private void OnNavSectionMenuOpening(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (menu.DataContext is NavSectionRow { HasMenu: false }) menu.IsOpen = false;
    }

    /// <summary>
    /// 收藏项上的鼠标：中键 → 新标签；双击文件 → 交给系统打开（FR-7.2 验收）。
    /// 文件夹的双击不特殊处理——单击已经导航过去了。
    /// </summary>
    private void OnBookmarkMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NavBookmarkRow row }) return;

        if (e.ChangedButton == MouseButton.Left)
        {
            // 记下按下的位置：拖拽要超过系统阈值才开始，否则每一次单击都会被当成拖动
            _bookmarkDragOrigin = e.GetPosition(QuickAccessList);
            _pressOnRowButton = IsInsideRowButton(e.OriginalSource as DependencyObject);
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            OpenBookmark(row, newTab: true);
            return;
        }

        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && row.Entry.Kind != BookmarkKind.Folder)
        {
            e.Handled = true;
            OpenBookmarkFile(row);
        }
    }

    /// <summary>在当前标签打开一条收藏。文件夹是导航，文件是交给系统打开。</summary>
    private void OpenBookmark(NavBookmarkRow row, bool newTab)
    {
        if (row.Entry.Kind == BookmarkKind.Folder)
        {
            if (newTab) OpenInNewTab(ActiveView, row.Path, background: false);
            else _ = NavigatePaneAsync(Vm, row.Path);
            return;
        }

        // 文件收藏："打开"就是执行它——本程序不预览文件，交给系统默认程序
        OpenBookmarkFile(row);
    }

    private void OpenBookmarkFile(NavBookmarkRow row)
    {
        try
        {
            Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Vm.SetStatus(string.Format(Strings.Status_OpenItemFailed, row.Name));
            AppLog.Warn("bookmark.open", "E7003", $"打开收藏失败：{row.Path}（{ex.Message}）");  // i18n-ignore：只进 app.log
        }
    }

    /// <summary>
    /// 预设子菜单里点了某一项：按类型决定改颜色还是改图标（FR-7.5）。
    ///
    /// 预设本身在 <see cref="HeaderedItemsControl.Header"/> 上，不在 DataContext 上：
    /// ItemsSource 生成的容器只被塞了 Header/HeaderTemplate，DataContext 仍是继承来的
    /// 收藏行（这也是行只能从父菜单那一层取的原因）。
    /// </summary>
    private void OnBookmarkPresetPicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: { } preset } item) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is not MenuItem { DataContext: NavBookmarkRow row }) return;

        switch (preset)
        {
            case BookmarkPresets.ColorPreset color:
                _bookmarks.SetColor(row.Path, color.Hex);
                break;
            case BookmarkPresets.IconPreset icon:
                _bookmarks.SetIcon(row.Path, icon.Glyph);
                break;
            default:
                return;
        }

        ApplyBookmarkChange();
    }

    /// <summary>恢复默认外观（清掉自定义颜色与图标）。</summary>
    private void OnBookmarkResetAppearance(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        _bookmarks.SetColor(row.Path, null);
        _bookmarks.SetIcon(row.Path, null);
        ApplyBookmarkChange();
    }

    private void OnBookmarkOpen(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) OpenBookmark(row, newTab: false);
    }

    private void OnBookmarkOpenNewTab(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) OpenBookmark(row, newTab: true);
    }

    private void OnBookmarkRename(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        string? name = TextPromptWindow.Ask(this, Strings.Bookmark_Prompt_Rename,
            Strings.Bookmark_Prompt_Name, row.Name, ValidateName);

        if (name is null) return;

        _bookmarks.Rename(row.Path, name);
        ApplyBookmarkChange();
    }

    /// <summary>
    /// 换分组（FR-7.3）。直接问一个组名，留空即"未分组"；
    /// 输入不存在的组名会顺手建一个——"我就想把它归到这一类"是个完整意图，
    /// 再要求先建组、再移进去，等于把一步拆成三步。
    /// </summary>
    private void OnBookmarkMoveToGroup(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        string? group = TextPromptWindow.Ask(this, Strings.Bookmark_Prompt_Group,
            Strings.Bookmark_Prompt_Group, row.Entry.Group ?? string.Empty);

        if (group is null) return;

        _bookmarks.MoveTo(row.Path, group, int.MaxValue);
        ApplyBookmarkChange();
    }

    private void OnBookmarkMoveUp(object sender, RoutedEventArgs e) => MoveBookmark(sender, -1);

    private void OnBookmarkMoveDown(object sender, RoutedEventArgs e) => MoveBookmark(sender, +1);

    private void MoveBookmark(object sender, int delta)
    {
        if (RowOf(sender) is not { } row) return;

        _bookmarks.Move(row.Path, delta);
        ApplyBookmarkChange();
    }

    private void OnBookmarkRemove(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        _bookmarks.Remove(row.Path);
        ApplyBookmarkChange();
        Vm.SetStatus(string.Format(Strings.Status_BookmarkRemoved, row.Name));
    }

    // ── 分组管理（FR-7.3） ──────────────────────────────────────────────────

    private void OnBookmarkNewGroup(object sender, RoutedEventArgs e)
    {
        string? name = TextPromptWindow.Ask(this, Strings.Bookmark_Prompt_NewGroup,
            Strings.Bookmark_Prompt_Group, string.Empty, value =>
                _bookmarks.ContainsGroup(value) ? Strings.Bookmark_Prompt_GroupExists : ValidateName(value));

        if (name is null) return;

        _bookmarks.AddGroup(name);
        ApplyBookmarkChange();
    }

    /// <summary>
    /// 批量清理失效项（FR-7.7）。检测走的是与标灰同一套异步逻辑，
    /// 但**只删用户确认过的那些**：结果列出来、问一句、再删。
    /// 收藏一个 U 盘上的目录、盘没插时全自动清理，等于把用户的书签全吃掉。
    /// </summary>
    private async void OnBookmarkCleanMissing(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> missing = await _nav.FindMissingAsync();

        if (missing.Count == 0)
        {
            Vm.SetStatus(Strings.Bookmark_CleanNone);
            return;
        }

        if (!ConfirmWindow.Ask(this, Strings.Bookmark_CleanMissing,
                string.Format(Strings.Bookmark_CleanConfirm, missing.Count)))
        {
            return;
        }

        int removed = _bookmarks.RemoveMany(missing);
        ApplyBookmarkChange();
        Vm.SetStatus(string.Format(Strings.Bookmark_CleanDone, removed));
    }

    private void OnBookmarkGroupRename(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NavGroupRow group }) return;
        if (group.IsUngrouped) return;

        string? name = TextPromptWindow.Ask(this, Strings.Bookmark_Prompt_Group,
            Strings.Bookmark_Prompt_Group, group.Name, value =>
                _bookmarks.ContainsGroup(value) && !string.Equals(value, group.Name, StringComparison.OrdinalIgnoreCase)
                    ? Strings.Bookmark_Prompt_GroupExists
                    : ValidateName(value));

        if (name is null) return;

        _bookmarks.RenameGroup(group.Name, name);
        ApplyBookmarkChange();
    }

    private void OnBookmarkGroupRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NavGroupRow group }) return;
        if (group.IsUngrouped) return;

        // 删分组不删收藏：组里的条目落回"未分组"。要先说清楚，否则用户会以为收藏跟着没了。
        if (!ConfirmWindow.Ask(this, Strings.Bookmark_Group_Remove, group.Name)) return;

        _bookmarks.RemoveGroup(group.Name);
        ApplyBookmarkChange();
    }

    private static string? ValidateName(string value)
        => string.IsNullOrWhiteSpace(value) ? Strings.Bookmark_Prompt_Empty : null;

    private static NavBookmarkRow? RowOf(object sender)
        => sender is FrameworkElement { DataContext: NavBookmarkRow row } ? row : null;

    // ── 拖放（FR-7.4 拖入即收藏 / FR-7.6 拖拽排序） ────────────────────────────

    /// <summary>拖动收藏项时用的私有格式。用文件路径做载荷，不用行对象的引用——
    /// 拖的过程中导航栏可能因为失效检测超时重建过一次，旧引用就悬空了。</summary>
    private const string BookmarkDragFormat = "lingpane/bookmark";

    /// <summary>正在拖的收藏路径（内部排序用）；null 表示这次拖的不是收藏项。</summary>
    private string? _bookmarkDragPath;

    /// <summary>按下点在收藏行上的位置，超过系统的拖拽阈值才真的开始拖。</summary>
    private Point _bookmarkDragOrigin;

    /// <summary>这一下按在行尾「⋯」按钮上。置真时不起拖，让按钮把点击收走。</summary>
    private bool _pressOnRowButton;

    /// <summary>
    /// 起点是否落在某个按钮里。只往上走到列表项为止——再往上就是整条侧栏，
    /// 那不是"这一行的按钮"。非可视元素（如 Run）拿不到父级，停在那里当作不是在按钮里。
    /// </summary>
    private static bool IsInsideRowButton(DependencyObject? start)
    {
        for (DependencyObject? node = start; node is Visual visual; node = VisualTreeHelper.GetParent(visual))
        {
            if (node is Button) return true;
            if (node is ListBoxItem) return false;
        }

        return false;
    }

    /// <summary>
    /// 拖拽经过导航栏。三种情况都要给出不同的效果与提示：
    /// 文件拖入 = 收藏（FR-7.4）；收藏项内部拖动 = 排序（FR-7.6）；其他一律拒绝。
    ///
    /// 效果位只能用 Copy/Move：拖拽源（文件列表）声明的是 <c>Copy | Move</c>，
    /// 目标端给一个不在这个集合里的效果（比如 Link），WPF 会判成"不允许放下"。
    /// </summary>
    private void OnNavDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetDataPresent(BookmarkDragFormat))
        {
            e.Effects = DragDropEffects.Move;
            ShowDropIndicator(e);
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            ShowDropIndicator(e);
            NavDropHint(e);
            return;
        }

        e.Effects = DragDropEffects.None;
        HideDropIndicator();
    }

    private void OnNavDragLeave(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        Vm.SetHint(string.Empty);
    }

    private void OnNavDrop(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        Vm.SetHint(string.Empty);
        e.Handled = true;

        if (e.Data.GetDataPresent(BookmarkDragFormat)
            && e.Data.GetData(BookmarkDragFormat) is string dragged)
        {
            (string? group, int index) = DropPosition(e, dragged);
            _bookmarks.MoveTo(dragged, group, index);
            ApplyBookmarkChange();
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        string? target = GroupUnderCursor(e);
        int added = 0;
        foreach (string path in paths)
        {
            if (_bookmarks.TryAdd(path, group: target) == BookmarkAddResult.Added) added++;
        }

        if (added == 0)
        {
            // 全都没加进去：要么重复、要么满额。说一句，别让用户以为拖丢了
            Vm.SetStatus(_bookmarks.IsFull
                ? string.Format(Strings.Status_BookmarkFull, BookmarkSettings.MaxItems)
                : string.Format(Strings.Status_BookmarkExists, Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))));
            return;
        }

        ApplyBookmarkChange();
        Vm.SetStatus(string.Format(Strings.Status_BookmarkDropAdded, added));
    }

    /// <summary>
    /// 收藏项内部拖动（FR-7.6）。用 <c>PreviewMouseMove</c> 而不是 <c>MouseMove</c>：
    /// 列表项自己会吃掉鼠标消息，冒泡到这一层时往往已经被标成 Handled。
    /// </summary>
    private void OnBookmarkDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: NavBookmarkRow row })
        {
            return;
        }

        // 从行尾「⋯」上按下的不算拖动：那一下是"点菜单"。
        // 手抖 5px 就会超过拖拽阈值，把这一下吞成拖拽的话，按钮就变成"点了没反应"。
        if (_pressOnRowButton) return;

        Point now = e.GetPosition(QuickAccessList);
        if (Math.Abs(now.X - _bookmarkDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _bookmarkDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _bookmarkDragPath = row.Path;

        var data = new DataObject(BookmarkDragFormat, row.Path);
        DragDrop.DoDragDrop(QuickAccessList, data, DragDropEffects.Move);
        _bookmarkDragPath = null;
    }

    /// <summary>把插入指示线摆到"将要插进去的那个缝"上。</summary>
    private void ShowDropIndicator(DragEventArgs e)
    {
        if (ItemUnderCursor(e) is not { } container)
        {
            HideDropIndicator();
            return;
        }

        Point top = container.TranslatePoint(new Point(0, 0), QuickAccessList);
        Point cursor = e.GetPosition(QuickAccessList);

        // 落在行的下半部分就插到它下面
        bool below = cursor.Y > top.Y + container.ActualHeight / 2;
        double y = below ? top.Y + container.ActualHeight : top.Y;

        DropIndicator.Margin = new Thickness(4, Math.Max(0, y - 1), 4, 0);
        DropIndicator.Visibility = Visibility.Visible;
    }

    private void HideDropIndicator() => DropIndicator.Visibility = Visibility.Collapsed;

    /// <summary>松手后收藏项该落到哪：分组 + 组内序号。</summary>
    private (string? Group, int Index) DropPosition(DragEventArgs e, string draggedPath)
    {
        if (ItemUnderCursor(e) is not { } container) return (null, int.MaxValue);

        switch (container.DataContext)
        {
            case NavBookmarkRow other
                when !string.Equals(other.Path, draggedPath, StringComparison.OrdinalIgnoreCase):
            {
                // 序号要在"把被拖的那条先摘掉"之后的列表里算：同组内往下拖时，
                // 被拖的那条自己还占着一个位置，不减掉就会落到目标的下一位（差一位）
                List<BookmarkEntry> siblings = _bookmarks.ItemsOf(other.Entry.Group)
                    .Where(b => !string.Equals(b.Path, draggedPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                int at = IndexOf(siblings, other.Path);
                if (at < 0) return (other.Entry.Group, int.MaxValue);

                // 落在目标行的下半部分 = 插到它下面
                Point top = container.TranslatePoint(new Point(0, 0), QuickAccessList);
                bool below = e.GetPosition(QuickAccessList).Y > top.Y + container.ActualHeight / 2;
                return (other.Entry.Group, below ? at + 1 : at);
            }

            case NavGroupRow group when !group.IsUngrouped:
                // 落在分组标题上 = "放进这个分组，排在最前"
                return (group.Name, 0);

            default:
                return (null, int.MaxValue);
        }

        static int IndexOf(IReadOnlyList<BookmarkEntry> list, string path)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
            }

            return -1;
        }
    }

    /// <summary>光标落在哪个收藏分组里（拖文件进来时决定归到哪一组）。</summary>
    private string? GroupUnderCursor(DragEventArgs e) => ItemUnderCursor(e)?.DataContext switch
    {
        NavGroupRow group when !group.IsUngrouped => group.Name,
        NavBookmarkRow row => row.Entry.Group,
        _ => null,
    };

    /// <summary>拖拽时状态栏那一句：让用户看清"放进这里会怎样"。</summary>
    private void NavDropHint(DragEventArgs e)
    {
        string? group = GroupUnderCursor(e);
        Vm.SetHint(group is null
            ? Strings.Bookmark_Hint_Drop
            : string.Format(Strings.Bookmark_Hint_DropToGroup, group));
    }

    /// <summary>光标下的列表行容器。没有（落在空白处）返回 null。</summary>
    private ListBoxItem? ItemUnderCursor(DragEventArgs e)
    {
        DependencyObject? hit = QuickAccessList.InputHitTest(e.GetPosition(QuickAccessList)) as DependencyObject;

        while (hit is not null and not ListBoxItem)
        {
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }

        return hit as ListBoxItem;
    }
}
