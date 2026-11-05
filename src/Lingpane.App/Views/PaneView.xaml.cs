using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Lingpane.App.ViewModels;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Localization;
using Lingpane.Core.Settings;

namespace Lingpane.App.Views;

/// <summary>
/// 一个浏览区（窗格）。承载工具栏、地址栏、列头、文件列表与状态栏，
/// 以及全部"作用于当前目录"的行为：文件操作、拖放、目录监听、图标解析。
///
/// 为什么要从窗口里拆出来：多标签（FR-2）与双窗格（FR-3）上线后，同一个窗口里
/// 会同时存在一到两个浏览区，它们的路径、选中项、项数统计、滚动位置、Watcher
/// 目标都是独立的。只要这些状态还挂在 Window 上，"这作用于哪个窗格"就得在
/// 每个方法里再判一次，而每加一个功能就多一处需要判的地方。
///
/// 现阶段的边界（与 MainWindow 的分工）：
///   · 窗口级：快捷键钩子、系统热键、设置窗口、导航栏、会话保存、启动计时
///   · 窗格级：本文件及 PaneView.*.cs 的全部内容
/// 命令表暂时还在窗口上（PaneView 的按钮用 AncestorType=Window 取命令），
/// 下一步会随"焦点窗格"概念一起搬进来，那时命令天然作用于自己所属的窗格。
/// </summary>
public partial class PaneView : UserControl
{
    private PaneViewModel? _viewModel;

    public PaneView()
    {
        InitializeComponent();
        Unloaded += OnPaneUnloaded;
    }

    /// <summary>本窗格的视图模型。附加前为 null，所有绑定静默为空，不会抛异常。</summary>
    public PaneViewModel? ViewModel => _viewModel;

    /// <summary>
    /// 非空访问器。附加由宿主保证（MainWindow 构造完立即 Attach），
    /// 用显式异常而不是 null 传播，是为了让"忘记 Attach"在第一次交互时就暴露，
    /// 而不是变成一堆莫名其妙的空列表。
    /// </summary>
    private PaneViewModel Vm => _viewModel
        ?? throw new InvalidOperationException("PaneView 尚未附加视图模型：宿主必须先调用 Attach.");  // i18n-ignore：开发者异常，不进界面

    /// <summary>
    /// 绑定视图模型。由宿主（窗口或标签容器）在构造完窗格后调用，
    /// 而不是在构造函数里自己 new——窗格的 VM 由谁创建、创建几个，是宿主的决定。
    /// </summary>
    public void Attach(PaneViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        DataContext = viewModel;

        // 列宽要在首帧前就位，否则会看到一次"默认宽度 → 记忆宽度"的跳动
        ApplyColumnWidths();
    }

    /// <summary>窗格被点中（键盘焦点进入列表）时触发，宿主据此切换"活动窗格"。</summary>
    public event Action<PaneView>? Activated;

    /// <summary>是否为当前活动窗格。双窗格上线后用于区分焦点与命令目标。</summary>
    public bool IsActive { get; private set; }

    /// <summary>由宿主设置活动态。单窗格阶段始终为 true。</summary>
    public void SetActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;
        if (active) Activated?.Invoke(this);
    }

    private void OnFileListGotFocus(object sender, KeyboardFocusChangedEventArgs e) => SetActive(true);

    /// <summary>设置改动后重新套用窗格内受影响的界面状态（不重建窗格）。</summary>
    public void ApplyAppearance(AppSettings s)
    {
        StatusBarHost.Visibility = s.Appearance.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        ApplyColumnWidths();

        _viewModel?.ApplyShowExtensions(s.Behavior.ShowExtensions);
        _viewModel?.ApplyFilter();
    }

    /// <summary>是否显示状态栏，供宿主查询（单实例时用于诊断日志）。</summary>
    public Visibility StatusBarVisibility => StatusBarHost.Visibility;

    // ── 列宽 ────────────────────────────────────────────────────────────────
    //
    // 列宽不放 ViewModel：它只影响显示，且行模板与列头共用同三个 DynamicResource
    // 令牌（Column.Date/Type/Size.Width），改令牌即生效，不需要重建列表。
    // 名称列是星号列，宽度随窗口伸缩，因此不参与拖拽。
    //
    // 令牌写在窗格自己的 Resources 里而不是 App 级：双窗格时两个窗格的可用宽度
    // 不同，各自持有一份可以避免一格的拖拽影响另一格。

    private void ApplyColumnWidths()
    {
        AppSettings s = App.Settings;
        Resources["Column.Date.Width"] = s.Layout.ColumnDateWidth;
        Resources["Column.Type.Width"] = s.Layout.ColumnTypeWidth;
        Resources["Column.Size.Width"] = s.Layout.ColumnSizeWidth;

        if (MainWindow.KeyDebug)
        {
            MainWindow.WriteKeyLog($"columns in={s.Layout.ColumnDateWidth}/{s.Layout.ColumnTypeWidth}/{s.Layout.ColumnSizeWidth}");
        }
    }

    /// <summary>布局诊断：列头实际宽度与可视位置，确认"列被裁掉"还是"列不存在"。</summary>
    internal void LogColumnLayout()
    {
        if (!MainWindow.KeyDebug) return;

        MainWindow.WriteKeyLog($"layout pane={ActualWidth:0}x{ActualHeight:0} list={FileList.ActualWidth:0} " +
                    $"header={HeaderGrid.ActualWidth:0} margin={HeaderGrid.Margin}");

        for (int i = 0; i < HeaderGrid.ColumnDefinitions.Count; i++)
        {
            MainWindow.WriteKeyLog($"layout col{i}={HeaderGrid.ColumnDefinitions[i].ActualWidth:0.#}");
        }

        foreach (Button button in new[] { NameHeaderButton, DateHeaderButton, TypeHeaderButton, SizeHeaderButton })
        {
            if (button is null) continue;

            Point origin = button.TranslatePoint(new Point(0, 0), FileList);
            Point screen = button.PointToScreen(new Point(0, 0));
            MainWindow.WriteKeyLog($"layout {button.Tag}: w={button.ActualWidth:0.#} x={origin.X:0.#} " +
                        $"screenX={screen.X:0.#} screenY={screen.Y:0.#} content=\"{button.Content}\"");
        }

        // 把手的实际屏幕矩形：拖拽验证（模拟输入）必须按这个坐标落点，
        // 按列头坐标推算容易在缩放/边框上差几像素而打空。
        foreach (Thumb thumb in HeaderGrid.Children.OfType<Thumb>())
        {
            Point p = thumb.PointToScreen(new Point(0, 0));
            MainWindow.WriteKeyLog($"layout thumb {thumb.Tag}: x={p.X:0.#} y={p.Y:0.#} " +
                        $"w={thumb.ActualWidth:0.#} h={thumb.ActualHeight:0.#}");
        }
    }

    // 名称列（星号列）的最小可视宽度，与 DesignTokens 的 Column.Name.MinWidth 对齐。
    // 拖宽其他列时不能把名称列压到这条线以下，否则文件名不可读、
    // 且星号列被压到 0 后 Grid 会整体溢出，把最右侧的列挤出可视区。
    private const double MinNameWidth = 160;

    private void OnColumnResize(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string token }) return;

        double current = ReadColumnWidth(token);

        // 把手挂在列的左边缘：向右拖（HorizontalChange > 0）= 把分隔线右推，
        // 本列变窄、左邻列变宽；向左拖则相反。取反后分隔线始终跟着鼠标，
        // 而不是"列宽变了、边界却不动"。
        double next = current - e.HorizontalChange;

        // 只有本列变宽时受限：最多吃掉名称列让出的富余宽度，避免溢出
        if (next > current)
        {
            double nameSlack = HeaderGrid.ColumnDefinitions[2].ActualWidth - MinNameWidth;
            next = Math.Min(next, current + Math.Max(0, nameSlack));
        }

        next = Math.Clamp(next, AppSettings.MinColumnWidth, AppSettings.MaxColumnWidth);

        if (Math.Abs(next - current) < 0.5) return;

        WriteColumnWidth(token, next);
        Resources[token] = next;
    }

    private void OnColumnResizeDone(object sender, DragCompletedEventArgs e)
    {
        if (sender is not Thumb thumb) return;

        // 一次拖拽只落盘一次，不跟随每个 DragDelta 写文件
        App.SaveSettings();

        if (MainWindow.KeyDebug)
        {
            AppSettings s = App.Settings;
            MainWindow.WriteKeyLog($"columns done {thumb.Tag} delta={e.HorizontalChange:0.#} " +
                        $"out={s.Layout.ColumnDateWidth:0.#}/{s.Layout.ColumnTypeWidth:0.#}/{s.Layout.ColumnSizeWidth:0.#}");
        }
    }

    private static double ReadColumnWidth(string token) => token switch
    {
        "Column.Date.Width" => App.Settings.Layout.ColumnDateWidth,
        "Column.Type.Width" => App.Settings.Layout.ColumnTypeWidth,
        _ => App.Settings.Layout.ColumnSizeWidth,
    };

    private static void WriteColumnWidth(string token, double value)
    {
        switch (token)
        {
            case "Column.Date.Width":
                App.Settings.Layout.ColumnDateWidth = value;
                break;
            case "Column.Type.Width":
                App.Settings.Layout.ColumnTypeWidth = value;
                break;
            default:
                App.Settings.Layout.ColumnSizeWidth = value;
                break;
        }
    }

    // ── 导航 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 能否返回上级。注意 CurrentPath 在首次导航前是空串，
    /// 而按钮绑定会在窗格加载时就查询 CanExecute——此处必须挡住空值，
    /// 否则 Directory.GetParent("") 直接抛 ArgumentException，启动即崩。
    /// </summary>
    internal bool CanGoUp()
    {
        string path = _viewModel?.CurrentPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            return Directory.GetParent(path) is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal void GoUp()
    {
        if (_viewModel is null || !CanGoUp()) return;

        string parent = Directory.GetParent(_viewModel.CurrentPath)!.FullName;
        _ = _viewModel.NavigateAsync(parent);
    }

    /// <summary>供单实例转交使用：把本窗格切到指定目录。</summary>
    public void NavigateTo(string path) => _ = _viewModel?.NavigateAsync(path);

    // ── 命令可执行性 ────────────────────────────────────────────────────────

    internal bool HasSelection()
        => _viewModel is { IsLoading: false } && _viewModel.SelectedEntries.Count > 0;

    internal bool HasSingleSelection()
        => _viewModel is { IsLoading: false } && _viewModel.SelectedEntries.Count == 1;

    internal bool CanCreateInCurrentDirectory()
        => _viewModel is { IsLoading: false } && !string.IsNullOrEmpty(_viewModel.CurrentPath);

    internal bool CanPaste()
        => _viewModel is { IsLoading: false } && Services.FileClipboard.HasData
           && !string.IsNullOrEmpty(_viewModel.CurrentPath);

    /// <summary>键盘菜单键（VK_APPS）弹出上下文菜单，位置对齐列表。</summary>
    internal void ShowContextMenu()
    {
        if (FileList.ContextMenu is not { } menu) return;

        menu.PlacementTarget = FileList;
        menu.Placement = PlacementMode.Center;
        menu.IsOpen = true;
    }

    /// <summary>WPF 的 ListBox 在 Extended 模式下没有内建 Ctrl+A，需要自己调 SelectAll。</summary>
    internal void SelectAllItems()
    {
        FileList.SelectAll();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>列表已有条目数，供"全选"命令的可执行性判断。</summary>
    internal int ItemCount => FileList.Items.Count;

    /// <summary>列表是否持有键盘焦点。快捷键派发要用它区分"按键归属列表"还是别的控件。</summary>
    internal bool IsListFocused => FileList.IsKeyboardFocusWithin;

    /// <summary>
    /// 记录列表滚动位置。字号/行高变化会导致偏移量失真，
    /// 设置页据此按项目索引还原，避免改字号后列表跳回顶部（FONT-05）。
    /// </summary>
    public (int Index, double Offset) CaptureScrollAnchor()
        => Typography.FontScaleService.CaptureAnchor(FindScrollViewer());

    public void RestoreScrollAnchor((int Index, double Offset) anchor)
        => Typography.FontScaleService.RestoreAnchor(FindScrollViewer(), anchor);

    private ScrollViewer? FindScrollViewer()
    {
        return FindDescendant<ScrollViewer>(FileList);

        static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
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
    }

    // ── 地址栏 ──────────────────────────────────────────────────────────────

    internal void BeginAddressEdit()
    {
        if (_viewModel is null || string.IsNullOrEmpty(_viewModel.CurrentPath)) return;

        _viewModel.IsAddressEditing = true;
        AddressBox.Text = _viewModel.CurrentPath;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndAddressEdit()
    {
        if (_viewModel is not null) _viewModel.IsAddressEditing = false;
    }

    private async void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;

        switch (e.Key)
        {
            case Key.Enter:
                string target = AddressBox.Text;
                EndAddressEdit();
                if (!string.IsNullOrWhiteSpace(target) && !Directory.Exists(target))
                {
                    _viewModel.SetHint(string.Format(Strings.Hint_PathNotFound, target));
                    return;
                }

                await _viewModel.NavigateAsync(target);
                e.Handled = true;
                break;

            case Key.Escape:
                EndAddressEdit();
                e.Handled = true;
                break;
        }
    }

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndAddressEdit();

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && !string.IsNullOrEmpty(path))
        {
            _ = _viewModel?.NavigateAsync(path);
        }
    }

    // ── 列表交互 ────────────────────────────────────────────────────────────

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field }) return;
        if (!Enum.TryParse<SortFieldKind>(field, ignoreCase: true, out var kind)) return;

        _viewModel?.SortBy(kind);
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;

        long total = 0;
        int count = 0;
        var selected = new List<FileEntry>(FileList.SelectedItems.Count);

        foreach (object item in FileList.SelectedItems)
        {
            if (item is not FileEntry entry) continue;
            selected.Add(entry);
            count++;
            total += entry.Size;
        }

        _viewModel.SetSelection(count, total);
        _viewModel.SetSelectedEntries(selected);

        // 选中数变化影响"打开/重命名/删除"等命令的可执行性，右键菜单打开前必须已刷新
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// 每次菜单弹出时回填键位提示。
    ///
    /// 必须在 <c>Opened</c> 而不是 <c>Loaded</c> 做：菜单项的命令是经由
    /// <see cref="System.Windows.Controls.Primitives.Popup.PlacementTarget"/> 拿窗口的，
    /// 而 PlacementTarget 只在弹出时才有值。加载阶段调用的结果是一律拿到 null，
    /// 键位提示静默为空——界面上看不出错，只能靠下面的日志兜底。
    /// </summary>
    private void OnContextMenuOpened(object sender, RoutedEventArgs e) => ApplyMenuGestures();

    private void ApplyMenuGestures()
    {
        if (FileList.ContextMenu is not { } menu) return;

        foreach (object item in menu.Items)
        {
            if (item is not MenuItem menuItem) continue;

            if (menuItem.Command is not Commands.CommandAdapter adapter)
            {
                // 绑定名字写错时 Command 为 null，菜单项会静默失效——界面上完全看不出，
                // 只能在这里留痕。正常情况一行都不会写。
                MainWindow.WriteKeyLog($"menu unbound: header=\"{menuItem.Header}\"");
                continue;
            }

            menuItem.InputGestureText = Input.KeyBindings.Gestures.TryGetValue(adapter.Source.Id, out string? text)
                ? text
                : string.Empty;
        }
    }

    /// <summary>右键先选中行再弹菜单：不选中的话菜单里的操作对象是上一批选中项。</summary>
    private void OnFileRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source) is not { } item) return;

        if (!item.IsSelected)
        {
            FileList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source) is not { DataContext: FileEntry entry }) return;

        OpenEntry(entry);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
    }
}
