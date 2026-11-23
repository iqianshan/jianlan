using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Jianlan.Core.FileSystem;
using Jianlan.Core.Input;
using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// 就地重命名（资源管理器手感）。
///
/// 三条入口共用这一套状态：再点一次名称、F2、右键「重命名」；新建文件夹/文本后
/// 也直接进这里。编辑框长在行模板里（<c>RowTemplates.xaml</c>），谁可见由本类的
/// <see cref="RenamingEntry"/> 决定——不在条目上挂编辑态，因为列表是 Recycling 虚拟化，
/// 状态挂在条目上迟早会画到别的行。
///
/// 生命周期只有三个动作：
///   · <see cref="BeginRename"/> 进入，落焦点、按规则预选一段文字；
///   · <see cref="CommitRename"/> 提交（Enter / 点别处 / 切标签），改名后按下标刷新；
///   · <see cref="CancelRename"/> 放弃（Esc），磁盘一个字节都不动。
///
/// 为什么提交后是"延后刷新"而不是当场刷新：点另一行也会触发提交，那一刻 ListBox 还没
/// 把选中切过去，当场刷新会把这次点击吞掉。延后一拍再刷，按路径保住的选中就是用户刚点的行。
/// </summary>
public partial class PaneView
{
    public static readonly DependencyProperty RenamingEntryProperty = DependencyProperty.Register(
        nameof(RenamingEntry),
        typeof(FileEntry),
        typeof(PaneView),
        new PropertyMetadata(null));

    /// <summary>正在编辑的条目。行模板据此决定显示文字还是编辑框。</summary>
    public FileEntry? RenamingEntry
    {
        get => (FileEntry?)GetValue(RenamingEntryProperty);
        private set => SetValue(RenamingEntryProperty, value);
    }

    public bool IsRenaming => RenamingEntry is not null;

    private TextBox? _renameBox;
    private FileEntry? _renameTarget;

    /// <summary>
    /// 按下时记下的"这一下可能是改名"的候选。按下只是候选，**松开**才决定：
    /// 中间只要真的拖起来了（<c>OnFileDragMove</c> 把 <c>_dragArmed</c> 清掉），
    /// 这里也会被清空——那是在拖文件，不是在改名。
    /// </summary>
    private FileEntry? _renameCandidate;

    /// <summary>
    /// 新建流程留下的"原始路径"：新建后立刻进入改名，整件事要记成一条"新建"。
    /// 用户回车确认时用它算出最终路径记撤销；按 Esc 则记新建时的默认名。
    /// </summary>
    private string? _pendingCreate;

    /// <summary>进入就地编辑。<paramref name="pendingCreate"/> 非空表示这次改名属于新建流程。</summary>
    internal void BeginRename(FileEntry? entry, string? pendingCreate = null)
    {
        if (entry is null) return;
        if (ReferenceEquals(entry, RenamingEntry)) return;

        // 上一次还没收尾（连按 F2、右键重命名叠在编辑上）：先按提交处理，不留两份编辑态
        if (IsRenaming) CommitRename(refocus: false, refresh: false);

        _renameTarget = entry;
        _pendingCreate = pendingCreate;

        // 编辑期间暂停目录监听：外部一有风吹草动就整表刷新，正在输入的那一行会连人带字消失
        _suppressWatcher = true;

        // 条目可能在可视区外（F2 走键盘时常见），先滚进来再画编辑框
        FileList.ScrollIntoView(entry);
        RenamingEntry = entry;

        // 等布局把编辑框画出来：刚改完可见性时容器可能还没实例化，这时取不到 TextBox
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AttachEditor(entry));
    }

    /// <summary>
    /// 找到这一行的编辑框并接管。
    ///
    /// 找不到时会重试两次再放弃：<c>ScrollIntoView</c> 之后容器要到下一次布局才实例化，
    /// 而"放弃"必须是**收掉编辑态**而不是干等——<c>_suppressWatcher</c> 还开着的话，
    /// 这个窗格的自动刷新会一直停摆，看起来就是"外部改了东西列表不动了"。
    /// </summary>
    private void AttachEditor(FileEntry entry, int attempt = 0)
    {
        if (!ReferenceEquals(_renameTarget, entry)) return;

        ListBoxItem? container = FileList.ItemContainerGenerator.ContainerFromItem(entry) as ListBoxItem;
        TextBox? box = container is null ? null : FindDescendant<TextBox>(container);

        if (box is null)
        {
            if (attempt < 2)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    () => AttachEditor(entry, attempt + 1));
                return;
            }

            CancelRename();
            return;
        }

        box.Text = entry.Name;
        box.ClearValue(Control.BorderBrushProperty);   // 上一次校验失败留下的红框
        _renameBox = box;

        // 事件在这里挂而不是写在 XAML 上：行模板是纯资源字典，没有代码后置可以接事件；
        // 先减后加，容器复用同一只 TextBox 时不会叠出多份处理器。
        box.KeyDown -= OnRenameBoxKeyDown;
        box.KeyDown += OnRenameBoxKeyDown;
        box.LostKeyboardFocus -= OnRenameBoxLostKeyboardFocus;
        box.LostKeyboardFocus += OnRenameBoxLostKeyboardFocus;

        (int start, int length) = RenameGesturePolicy.StemSelection(entry.Name, entry.IsDirectory);

        box.Focus();
        box.Select(start, length);
        Keyboard.Focus(box);
    }

    private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                // 吞掉：不吞的话回车会继续冒泡给列表，变成"打开选中项"
                e.Handled = true;
                CommitRename(refocus: true);
                break;

            case Key.Escape:
                e.Handled = true;
                CancelRename();
                break;
        }
    }

    /// <summary>
    /// 焦点离开编辑框：点了另一行、点了侧栏、切到别的窗口。
    ///
    /// 与资源管理器一致，按**提交**处理而不是丢弃——用户已经敲了一半名字，
    /// 点一下别处就白敲，比改错名字更让人恼火。这里不抢回焦点：鼠标已经指到别处了。
    /// </summary>
    private void OnRenameBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsRenaming) return;
        CommitRename(refocus: false);
    }

    /// <summary>
    /// 提交改名。返回是否真的改了名（"没改"与"校验失败"都返回 false，但不必区分）。
    /// </summary>
    /// <param name="refocus">提交后是否把焦点交回列表。键盘提交要，点别处触发时不能抢。</param>
    /// <param name="refresh">是否在提交后刷新列表。切标签/换目录时传 false——那边马上会重读盘。</param>
    internal bool CommitRename(bool refocus, bool refresh = true)
    {
        if (_renameTarget is not { } entry) return false;

        string typed = _renameBox?.Text ?? entry.Name;
        string name = typed.Trim();
        string oldPath = entry.FullPath;

        // 原样提交：当成放弃，但不该报错（用户只是按了回车确认"就是这个名"）
        if (string.Equals(name, entry.Name, StringComparison.Ordinal))
        {
            EndRename(refocus);
            CompletePendingCreate(oldPath);
            return false;
        }

        if (!FileOperations.TryValidateName(name, out string? error))
        {
            RejectRename(error);
            return false;
        }

        if (!FileOperations.TryRename(oldPath, name, out string renamed, out string? renameError))
        {
            RejectRename(renameError);
            return false;
        }

        // 新建后改名整件事只记一条"新建"：记成"新建 + 改名"两条的话，
        // 用户按一次 Ctrl+Z 只把名字改回去，还得再按一次才删掉。
        if (_pendingCreate is null) Undo?.Push(UndoKind.Rename, [new UndoStep(oldPath, renamed)]);

        Vm.SetStatus(string.Format(
            CultureInfo.CurrentCulture, Strings.Status_Renamed, Path.GetFileName(renamed)));

        EndRename(refocus);
        CompletePendingCreate(renamed);

        if (refresh) ScheduleRefreshAfterRename(oldPath, renamed);
        return true;
    }

    /// <summary>放弃编辑。已经新建出来的东西保留默认名，撤销记录仍记"新建"。</summary>
    internal void CancelRename()
    {
        if (!IsRenaming) return;

        string original = _pendingCreate ?? string.Empty;
        EndRename(refocus: true);
        CompletePendingCreate(original);
    }

    private void EndRename(bool refocus)
    {
        RenamingEntry = null;
        _renameTarget = null;
        _renameBox = null;
        _suppressWatcher = false;

        // 焦点交回列表，方向键与后续快捷键立刻可用。点别处触发时不能抢：
        // 用户刚把焦点放到那一行上，抢回来等于点击没生效。
        if (refocus) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FileList.Focus());
    }

    /// <summary>新建流程的收尾：整件事记成一条"新建"，路径取最终名。</summary>
    private void CompletePendingCreate(string path)
    {
        if (_pendingCreate is null) return;

        _pendingCreate = null;
        RecordCreate(path);
    }

    private void RejectRename(string? error)
    {
        // 状态栏给完整句子（"重命名失败：…"）而不是只丢一句"名称无效"：
        // 就地编辑没有对话框可以放标题，失败原因必须自己带出上下文。
        Vm.SetStatus(string.Format(
            CultureInfo.CurrentCulture,
            Strings.Status_RenameFailed,
            error ?? Strings.Error_NameEmpty));

        if (_renameBox is not { } box) return;

        // 红框 + 全选：既能看出"哪一步不对"，也让下一次敲字直接覆盖掉原来的内容。
        // 边框是本地值，下一次进入编辑时在 AttachEditor 里清掉。
        box.BorderBrush = (Brush)FindResource("Brush.Status.Danger");
        box.SelectAll();
        box.Focus();
    }

    /// <summary>
    /// 延后一拍再刷新：提交可能是"点了另一行"触发的，那一刻 ListBox 还没把选中切过去。
    /// 立刻刷新会把那次点击的选中结果抹掉，用户看到的是"点了一下没反应"。
    /// </summary>
    private void ScheduleRefreshAfterRename(string oldPath, string newPath)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => _ = RefreshAfterRenameAsync(oldPath, newPath));
    }

    private async Task RefreshAfterRenameAsync(string oldPath, string newPath)
    {
        if (_paneClosed || _viewModel is null) return;
        if (string.IsNullOrEmpty(Vm.CurrentPath)) return;

        // 被改名的那一项跟着新路径走；此刻若用户选的是别的行，就原样保留。
        string[] keep = Vm.SelectedEntries
            .Select(entry => string.Equals(entry.FullPath, oldPath, StringComparison.OrdinalIgnoreCase)
                ? newPath
                : entry.FullPath)
            .ToArray();

        await RefreshAndSelectAsync(keep);
    }

    /// <summary>在可视树里找第一个指定类型的后代。行模板只有一个 TextBox，按类型找就够。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            if (FindDescendant<T>(child) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>
    /// 按下左键时判断这一下要不要记为改名候选。判定规则本身在
    /// <see cref="RenameGesturePolicy"/> 里（可单测），这里只负责把界面上的事实读出来：
    /// 连击数、修饰键、列表有没有焦点、这一行是否已选中、点的是不是文件名。
    /// </summary>
    private FileEntry? ResolveRenameCandidate(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return null;
        if (FindAncestor<ListBoxItem>(source) is not { } item) return null;
        if (item.DataContext is not FileEntry entry) return null;

        bool onNameCell = IsNameCell(source, item);

        bool arm = RenameGesturePolicy.ShouldArmRename(
            doubleClickOpens: App.Settings.Behavior.DoubleClickOpens,
            clickCount: e.ClickCount,
            hasModifiers: Keyboard.Modifiers != ModifierKeys.None,
            hasFocus: FileList.IsKeyboardFocusWithin,
            isSelected: item.IsSelected,
            selectionCount: FileList.SelectedItems.Count,
            onNameCell: onNameCell,
            alreadyEditing: ReferenceEquals(entry, RenamingEntry));

        return arm ? entry : null;
    }

    /// <summary>
    /// 命中的是不是"文件名"那一块。行模板给名称 TextBlock 打了 <c>Tag="NameCell"</c>，
    /// 图标、色点和名称列右侧的空白都没有这个标记——资源管理器也是这样，
    /// 点图标只会选中，不会进入改名。
    /// </summary>
    private static bool IsNameCell(DependencyObject node, DependencyObject stopAt)
    {
        for (DependencyObject? current = node; current is not null && !ReferenceEquals(current, stopAt);
             current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: "NameCell" }) return true;
        }

        return false;
    }
}
