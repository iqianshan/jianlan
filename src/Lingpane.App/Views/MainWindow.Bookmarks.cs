using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        if (Tab.IsHome)
        {
            // 主页不"位于"任何目录，收藏它没有意义（收藏的是路径，主页没有）
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
    private void OnNavInertRowClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>
    /// 收藏项上的鼠标：中键 → 新标签；双击文件 → 交给系统打开（FR-7.2 验收）。
    /// 文件夹的双击不特殊处理——单击已经导航过去了。
    /// </summary>
    private void OnBookmarkMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NavBookmarkRow row }) return;

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
}
