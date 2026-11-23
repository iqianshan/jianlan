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
///   · <see cref="CommitRename"/> 提交，**只认 Enter**，改名后按下标刷新；
///   · <see cref="CancelRename"/> 放弃，Esc 与"点了别处"都走这里，磁盘一个字节都不动。
///
/// 为什么离开编辑态一律按取消：敲一半的名字被鼠标顺手点一下就落盘，改错的代价
/// 比丢几个字大得多——尤其这里是文件管理器，没有对话框可以确认。所以只有明确的
/// Enter 才改盘，其余出口（Esc / 点空白 / 点别的行 / 切标签 / 换目录 / 滚出视野 / 关窗）
/// 全部回到原样。
///
/// 为什么提交后是"延后刷新"而不是当场刷新：改名那一拍 ListBox 可能正在处理点击，
/// 当场刷新会把选中结果抹掉。延后一拍再刷，按路径保住的选中就是用户看到的那一行。
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
    /// 编辑框刚打开、内容一个字都没动过。
    ///
    /// 这一状态决定方向键的语义：刚打开时四个方向键都跳到端点（← / ↑ 到最左，
    /// → / ↓ 到最右），符合"名字刚选中、这一下就是要去另一头"的直觉；
    /// 一旦开始敲字、点了编辑框内部、或者已经按过一次方向键，就退回逐字移动
    /// ——否则名字长一点就再也插不进中间。清掉它的地方有三处：
    /// <see cref="OnRenameBoxTextChanged"/>、<see cref="OnRenameBoxPreviewMouseDown"/>
    /// 与 <see cref="OnRenameBoxPreviewKeyDown"/> 里跳完端点那一行。
    /// </summary>
    private bool _renameFresh;

    /// <summary>
    /// 正在弹"要不要改扩展名"的确认窗。
    ///
    /// 模态窗会拿走键盘焦点，编辑框的 <see cref="OnRenameBoxLostKeyboardFocus"/>
    /// 会把编辑态收掉——等用户点完"是/否"回来，编辑框已经没了、敲的字也丢了。
    /// 所以弹窗期间挂起一切"离开即取消"的判定。
    /// </summary>
    private bool _confirming;

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

        // 上一次还没收尾（连按 F2、右键重命名叠在编辑上）：先收成取消，不留两份编辑态。
        // 不按提交，否则"再按一次 F2"会变成把半截名字落盘。
        if (IsRenaming) CancelRename(refocus: false);

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
        //
        // 键盘用 PreviewKeyDown：方向键在 TextBox 的类处理器里就被消化并标成 Handled，
        // 冒泡阶段的 KeyDown 收不到（回车、Esc 恰好没人处理，所以只用 KeyDown 时
        // 看着是好的，方向键却怎么都改不了）。
        box.PreviewKeyDown -= OnRenameBoxPreviewKeyDown;
        box.PreviewKeyDown += OnRenameBoxPreviewKeyDown;
        box.PreviewMouseDown -= OnRenameBoxPreviewMouseDown;
        box.PreviewMouseDown += OnRenameBoxPreviewMouseDown;
        box.TextChanged -= OnRenameBoxTextChanged;
        box.TextChanged += OnRenameBoxTextChanged;
        box.LostKeyboardFocus -= OnRenameBoxLostKeyboardFocus;
        box.LostKeyboardFocus += OnRenameBoxLostKeyboardFocus;

        (int start, int length) = RenameGesturePolicy.StemSelection(entry.Name, entry.IsDirectory);

        box.Focus();
        box.Select(start, length);
        Keyboard.Focus(box);

        // 必须在赋 Text 之后：赋 Text 会触发 TextChanged，那里会把标记清掉
        _renameFresh = true;
    }

    /// <summary>
    /// 编辑框里的键。
    ///
    /// 方向键分两段：刚打开（<see cref="_renameFresh"/>）或整名/词干还选着的时候，
    /// 四个方向键只做"去端点"；已经在改了，还原成 TextBox 原本的逐字移动。
    ///
    /// 为什么不无条件拦截：文件名的词干预选只有扩展名之前那一截，按原生的
    /// "有选区就收到选区边界"处理，→ 会停在扩展名前面那一点，是半截行为；
    /// 而无条件跳到端点又会让长名字没法插字。按"动没动过"分两段，两头都保住了。
    /// </summary>
    private void OnRenameBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        switch (e.Key)
        {
            case Key.Enter:
                // 吞掉：不吞的话回车会继续冒泡给列表，变成"打开选中项"
                e.Handled = true;
                CommitRename(refocus: true);
                return;

            case Key.Escape:
                e.Handled = true;
                CancelRename();
                return;
        }

        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;

        // Shift+方向是选字、Ctrl+方向是整词跳，都有既定语义，不抢
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        // 动过内容又没有选区：这就是正常的逐字移动
        if (!_renameFresh && box.SelectionLength == 0) return;

        bool toEnd = e.Key is Key.Right or Key.Down;
        e.Handled = true;
        box.Select(toEnd ? box.Text.Length : 0, 0);

        // 这一下已经把光标送到端点，之后就是正常的逐字移动。
        // 不清掉标记的话，每次按方向键都会重新跳到端点——名字长一点就永远插不进中间，
        // 表现是"←/→ 只会跳到两头"，正是用户走到这一步时踩到的那条。
        _renameFresh = false;
    }

    /// <summary>点了编辑框内部（挪光标、框一段字）：从此方向键按字符走。</summary>
    private void OnRenameBoxPreviewMouseDown(object sender, MouseButtonEventArgs e) => _renameFresh = false;

    /// <summary>内容变了：已经不是"刚打开"的那一下，方向键按字符走。</summary>
    private void OnRenameBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        _renameFresh = false;
    }

    /// <summary>
    /// 焦点离开编辑框：点了别的地方、Tab 走了、切到另一个窗格。
    ///
    /// 按**取消**处理。原来的写法是"提交"，理由是"敲了一半别白敲"；实测下来
    /// 反了：点一下列表空白处就把半截名字落到盘上，比丢几个字糟糕得多——
    /// 文件管理器没有确认框，改错了要用户自己发现。现在只有 Enter 会改盘。
    ///
    /// <see cref="KeyboardFocusChangedEventArgs.NewFocus"/> 为 null 表示整个窗口失活
    /// （Alt+Tab 出去看一眼），这种不算"点了别处"：保持编辑态，回来还能接着改。
    /// </summary>
    private void OnRenameBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsRenaming) return;

        // 弹"要不要改扩展名"的那一轮要放过：模态窗拿走焦点不等于用户点了别处
        if (_confirming) return;

        if (e.NewFocus is null) return;

        CancelRename(refocus: false);
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

        // 改了扩展名要先问一句：文件类型是靠扩展名认的，改完多半双击打不开，
        // 而这一下改盘没有别的痕迹能回退。否决之后留在编辑态，让用户接着把名字改回去。
        if (RenameExtensionPolicy.ChangesExtension(entry.Name, name, entry.IsDirectory)
            && !ConfirmExtensionChange(entry.Name, name))
        {
            Vm.SetStatus(Strings.Status_RenameExtensionCancelled);
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
    /// <param name="refocus">收尾后是否把焦点交回列表。点别处触发时不能抢。</param>
    internal void CancelRename(bool refocus = true)
    {
        if (!IsRenaming) return;

        string original = _pendingCreate ?? string.Empty;
        EndRename(refocus);
        CompletePendingCreate(original);
    }

    /// <summary>
    /// 鼠标按在编辑框外面：按取消收尾，回到"这一项被选中"的初始状态。
    ///
    /// 为什么不能只靠失焦事件：点列表空白处的那一下被框选逻辑接走了
    /// （<c>OnFileDragStart</c> → <c>BeginRubberBand</c> 把事件标成 Handled），
    /// WPF 默认的"点哪聚焦哪"根本没发生，编辑框一直握着键盘焦点、失焦事件不响，
    /// 表现就是"点了别处还停在编辑态"。所以判定要自己做，而且要在窗口级做——
    /// 工具栏、侧栏、标签栏都在窗格外面，窗格收不到那些按下。
    /// </summary>
    internal void CancelRenameIfClickedOutside(DependencyObject hit)
    {
        if (!IsRenaming) return;

        // 确认窗是独立的 HWND，点它不该被当成"点了别处"。窗口被禁用时本来也收不到，
        // 这一行是给复用窗口的那条路径兜底。
        if (_confirming) return;

        // 点在编辑框自己身上：那是挪光标，不是离开
        if (_renameBox is { } box && IsDescendantOf(hit, box)) return;

        // 点在列表里（空白处、别的行）收完把焦点交回列表，方向和快捷键立刻可用；
        // 点在工具栏、侧栏这类地方就不能抢——用户正要点那边。
        CancelRename(refocus: IsDescendantOf(hit, FileList));
    }

    private void EndRename(bool refocus)
    {
        // 先摘事件再收状态：编辑框是行模板里的、会被虚拟化复用，
        // 留下的处理器会挂到下一行的同一个控件上
        if (_renameBox is { } box)
        {
            box.PreviewKeyDown -= OnRenameBoxPreviewKeyDown;
            box.PreviewMouseDown -= OnRenameBoxPreviewMouseDown;
            box.TextChanged -= OnRenameBoxTextChanged;
            box.LostKeyboardFocus -= OnRenameBoxLostKeyboardFocus;
        }

        RenamingEntry = null;
        _renameTarget = null;
        _renameBox = null;
        _renameFresh = false;
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

    /// <summary>
    /// 扩展名变了，问一句再改。返回 <c>true</c> 表示用户确认要改。
    ///
    /// 为什么拦这一道：文件类型靠扩展名认，`.txt` 改成 `.docx` 之后双击多半打不开，
    /// 而改名是一次性落盘的，撤销要用户自己想起来按 <c>Ctrl+Z</c>。资源管理器也在这里
    /// 拦一下，手感照抄。判断规则在 <see cref="RenameExtensionPolicy"/>，大小写不算改动，
    /// 目录不参与（文件夹名里的点不是扩展名）。
    ///
    /// 弹窗期间挂 <see cref="_confirming"/>：模态窗拿走键盘焦点会触发编辑框的失焦收尾，
    /// 那样等用户点完"是/否"回来，编辑框和敲进去的字都没了。
    /// </summary>
    private bool ConfirmExtensionChange(string oldName, string newName)
    {
        _confirming = true;
        try
        {
            return ConfirmWindow.Ask(
                Window.GetWindow(this),
                Strings.Dialog_RenameExtensionTitle,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Dialog_RenameExtensionBody,
                    oldName,
                    newName));
        }
        finally
        {
            _confirming = false;

            // 模态窗收尾会把焦点还到"弹出前那个元素"，这里再稳一手：
            // 无论点头还是摇头，回来第一件事都是能接着改（摇头时用户多半还想改回去）。
            if (_renameBox is { } box)
            {
                box.Focus();
                box.SelectAll();
                _renameFresh = true;
            }
        }
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

        // 又是"整名选着、还没动过"的状态，方向键回到端点
        _renameFresh = true;
    }

    /// <summary>编辑中的那一行还在可视区里吗。滚出去之后容器会被虚拟化回收，编辑框跟着消失。</summary>
    private bool IsRenameRowVisible()
    {
        if (_renameTarget is not { } entry) return false;

        if (FileList.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement container)
        {
            return false;
        }

        if (!container.IsVisible) return false;

        // 只看 IsVisible 不够：容器可能还在（虚拟化有缓冲），但已经被滚出视口
        Point topLeft = container.TranslatePoint(new Point(0, 0), FileList);
        return new Rect(topLeft, container.RenderSize).IntersectsWith(new Rect(FileList.RenderSize));
    }

    /// <summary><paramref name="node"/> 是不是 <paramref name="root"/> 自己或它的后代。</summary>
    private static bool IsDescendantOf(DependencyObject node, DependencyObject root)
    {
        for (DependencyObject? current = node; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, root)) return true;
        }

        return false;
    }

    /// <summary>
    /// 统一取父级。直接调 <see cref="VisualTreeHelper.GetParent"/> 会在
    /// <see cref="FrameworkContentElement"/>（比如 TextBlock 里的 Run）上抛异常。
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : node is FrameworkContentElement content
                ? content.Parent
                : LogicalTreeHelper.GetParent(node);

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
