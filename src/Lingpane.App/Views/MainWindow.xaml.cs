using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Lingpane.App.Commands;
using Lingpane.App.Services;
using Lingpane.App.ViewModels;
using Lingpane.Core.Commands;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Settings;

namespace Lingpane.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CommandRegistry _commands = new();
    private bool _flushed;

    public CommandAdapter UpCommand { get; }
    public CommandAdapter BackCommand { get; }
    public CommandAdapter ForwardCommand { get; }
    public CommandAdapter RefreshCommand { get; }
    public CommandAdapter ThemeCommand { get; }
    public CommandAdapter DetailsCommand { get; }
    public CommandAdapter ListViewCommand { get; }
    public CommandAdapter SettingsCommand { get; }
    public CommandAdapter OpenItemCommand { get; }
    public CommandAdapter NewFolderCommand { get; }
    public CommandAdapter RenameCommand { get; }
    public CommandAdapter DeleteCommand { get; }
    public CommandAdapter DeletePermanentCommand { get; }
    public CommandAdapter CopyItemsCommand { get; }
    public CommandAdapter CutItemsCommand { get; }
    public CommandAdapter PasteCommand { get; }
    public CommandAdapter CopyPathCommand { get; }
    public CommandAdapter PropertiesCommand { get; }

    public MainWindow()
    {
        // 设置必须在创建视图模型前就绪：默认视图、排序规则都来自设置
        _viewModel = new MainViewModel(App.Settings);

        InitializeComponent();
        DataContext = _viewModel;

        // 命令在此集中注册：菜单、快捷键、工具栏按钮都从同一张表取，
        // 避免同一功能在三处各写一份逻辑（ADR-0004 无容器，直接构造即可）。
        _commands.Register(new AppCommand
        {
            Id = CommandIds.GoUp,
            Title = "上级",
            Gesture = "Alt+↑",
            CanExecute = CanGoUp,
            Execute = GoUp,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.GoBack,
            Title = "后退",
            Gesture = "Alt+←",
            CanExecute = () => !_viewModel.IsLoading && _viewModel.CanGoBack,
            Execute = () => _ = _viewModel.GoBackAsync(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.GoForward,
            Title = "前进",
            Gesture = "Alt+→",
            CanExecute = () => !_viewModel.IsLoading && _viewModel.CanGoForward,
            Execute = () => _ = _viewModel.GoForwardAsync(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Refresh,
            Title = "刷新",
            Gesture = "F5",
            CanExecute = () => !_viewModel.IsLoading,
            Execute = () => _ = _viewModel.RefreshAsync(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CycleTheme,
            Title = "主题",
            // 不能用 Ctrl+T / Ctrl+Shift+T：docs/shortcuts.md 已把它们分配给标签页
            Gesture = "Ctrl+Shift+D",
            Execute = () => App.CycleTheme(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ViewDetails,
            Title = "详细信息视图",
            Gesture = "Ctrl+Shift+1",
            Execute = () => _viewModel.SetViewMode(DefaultViewKind.Details),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ViewList,
            Title = "列表视图",
            Gesture = "Ctrl+Shift+2",
            Execute = () => _viewModel.SetViewMode(DefaultViewKind.List),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.OpenSettings,
            Title = "设置",
            Gesture = "Ctrl+,",
            Execute = OpenSettings,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.FocusAddress,
            Title = "定位地址栏",
            Gesture = "Ctrl+L",
            Execute = BeginAddressEdit,
        });

        // 文件操作（M1 批 3）。可执行性由选中状态与当前目录决定，
        // 右键菜单与快捷键共用这一份判断，不各写一遍。
        _commands.Register(new AppCommand
        {
            Id = CommandIds.OpenItem,
            Title = "打开",
            Execute = OpenSelection,
            CanExecute = HasSingleSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.NewFolder,
            Title = "新建文件夹",
            Execute = () => _ = NewFolderAsync(),
            CanExecute = CanCreateInCurrentDirectory,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Rename,
            Title = "重命名",
            Execute = () => _ = RenameSelectionAsync(),
            CanExecute = HasSingleSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Delete,
            Title = "删除",
            Execute = () => _ = DeleteSelectionAsync(permanent: false),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.DeletePermanent,
            Title = "永久删除",
            Execute = () => _ = DeleteSelectionAsync(permanent: true),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CopyItems,
            Title = "复制",
            Execute = () => CopySelection(cut: false),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CutItems,
            Title = "剪切",
            Execute = () => CopySelection(cut: true),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Paste,
            Title = "粘贴",
            Execute = () => _ = PasteAsync(),
            CanExecute = CanPaste,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CopyPath,
            Title = "复制完整路径",
            Execute = CopyPathSelection,
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Properties,
            Title = "属性",
            Execute = ShowProperties,
            CanExecute = HasSelection,
        });

        UpCommand = new CommandAdapter(_commands.Get(CommandIds.GoUp));
        BackCommand = new CommandAdapter(_commands.Get(CommandIds.GoBack));
        ForwardCommand = new CommandAdapter(_commands.Get(CommandIds.GoForward));
        RefreshCommand = new CommandAdapter(_commands.Get(CommandIds.Refresh));
        ThemeCommand = new CommandAdapter(_commands.Get(CommandIds.CycleTheme));
        DetailsCommand = new CommandAdapter(_commands.Get(CommandIds.ViewDetails));
        ListViewCommand = new CommandAdapter(_commands.Get(CommandIds.ViewList));
        SettingsCommand = new CommandAdapter(_commands.Get(CommandIds.OpenSettings));
        OpenItemCommand = new CommandAdapter(_commands.Get(CommandIds.OpenItem));
        NewFolderCommand = new CommandAdapter(_commands.Get(CommandIds.NewFolder));
        RenameCommand = new CommandAdapter(_commands.Get(CommandIds.Rename));
        DeleteCommand = new CommandAdapter(_commands.Get(CommandIds.Delete));
        DeletePermanentCommand = new CommandAdapter(_commands.Get(CommandIds.DeletePermanent));
        CopyItemsCommand = new CommandAdapter(_commands.Get(CommandIds.CopyItems));
        CutItemsCommand = new CommandAdapter(_commands.Get(CommandIds.CutItems));
        PasteCommand = new CommandAdapter(_commands.Get(CommandIds.Paste));
        CopyPathCommand = new CommandAdapter(_commands.Get(CommandIds.CopyPath));
        PropertiesCommand = new CommandAdapter(_commands.Get(CommandIds.Properties));

        // 快捷键不用 Window.InputBindings：KeyGesture 对 , 这类 OEM 键的匹配依赖布局，
        // 实测 Ctrl+, 不触发。改为一张显式表 + PreviewKeyDown 派发，见 Input/KeyBindings.cs。
        PreviewKeyDown += OnPreviewKeyDown;

        // 目录一换就换监听目标
        _viewModel.NavigationCompleted += StartWatching;

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        CompositionTarget.Rendering += OnFirstRender;

        // 记录最后目录供下次启动使用。这个处理器先于 App 的保存处理器执行，
        // 因此退出时写到盘上的是本窗口刚更新的值。
        Closing += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_viewModel.CurrentPath))
            {
                App.Settings.Session.LastPath = _viewModel.CurrentPath;
            }
        };
    }

    /// <summary>
    /// 能否返回上级。注意 CurrentPath 在首次导航前是空串，
    /// 而按钮绑定会在窗口加载时就查询 CanExecute——此处必须挡住空值，
    /// 否则 Directory.GetParent("") 直接抛 ArgumentException，启动即崩。
    /// </summary>
    private bool CanGoUp()
    {
        string path = _viewModel.CurrentPath;
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

    private void GoUp()
    {
        if (!CanGoUp()) return;

        string parent = Directory.GetParent(_viewModel.CurrentPath)!.FullName;
        _ = _viewModel.NavigateAsync(parent);
    }

    /// <summary>供单实例转交使用：把已打开的窗口切到指定目录。</summary>
    public void NavigateTo(string path) => _ = _viewModel.NavigateAsync(path);

    /// <summary>设置改动后重新套用受影响的界面状态（不重建窗口）。</summary>
    public void ApplySettingsToUi()
    {
        AppSettings s = App.Settings;

        NavColumn.Width = s.Appearance.ShowNavPane
            ? new GridLength(1, GridUnitType.Auto)
            : new GridLength(0);
        QuickAccessList.Visibility = s.Appearance.ShowNavPane ? Visibility.Visible : Visibility.Collapsed;

        StatusBarHost.Visibility = s.Appearance.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;

        _viewModel.ApplyShowExtensions(s.Behavior.ShowExtensions);
        _viewModel.ApplyFilter();
    }

    private void OpenSettings()
    {
        // 单例：重复点"设置"不应叠出多个窗口
        if (Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow { Owner = this };
        window.Show();
    }

    // ── 快捷键 ──────────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 文本输入控件里不抢键：地址栏的 Ctrl+C 应该复制文字，而不是复制文件
        if (Keyboard.FocusedElement is TextBoxBase) return;

        if (!Input.KeyBindings.TryMatch(e, out string commandId)) return;

        // 匹配到就吞掉，即使命令此刻不可执行——否则 ListBox 会把它当成"首字母定位"
        e.Handled = true;
        _commands.Invoke(commandId);
    }

    // ── 命令可执行性 ────────────────────────────────────────────────────────

    private bool HasSelection() => !_viewModel.IsLoading && _viewModel.SelectedEntries.Count > 0;

    private bool HasSingleSelection() => !_viewModel.IsLoading && _viewModel.SelectedEntries.Count == 1;

    private bool CanCreateInCurrentDirectory()
        => !_viewModel.IsLoading && !string.IsNullOrEmpty(_viewModel.CurrentPath);

    private bool CanPaste()
        => !_viewModel.IsLoading && FileClipboard.HasData && !string.IsNullOrEmpty(_viewModel.CurrentPath);

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

    private void BeginAddressEdit()
    {
        if (string.IsNullOrEmpty(_viewModel.CurrentPath)) return;

        _viewModel.IsAddressEditing = true;
        AddressBox.Text = _viewModel.CurrentPath;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndAddressEdit() => _viewModel.IsAddressEditing = false;

    private async void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                string target = AddressBox.Text;
                EndAddressEdit();
                if (!string.IsNullOrWhiteSpace(target) && !Directory.Exists(target))
                {
                    _viewModel.SetHint($"路径不存在：{target}");
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
            _ = _viewModel.NavigateAsync(path);
        }
    }

    // ── 列表交互 ────────────────────────────────────────────────────────────

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field }) return;
        if (!Enum.TryParse<SortFieldKind>(field, ignoreCase: true, out var kind)) return;

        _viewModel.SortBy(kind);
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
    /// 右键菜单的键位提示从快捷键表里取，与真实绑定同源。
    /// 写在 XAML 里就会出现"菜单写着 Ctrl+,，实际却是别的键"这类不同步。
    /// </summary>
    private void ApplyMenuGestures()
    {
        if (FileList.ContextMenu is not { } menu) return;

        foreach (object item in menu.Items)
        {
            if (item is not MenuItem { Command: CommandAdapter adapter } menuItem) continue;

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

    /// <summary>首次渲染回调：比 ContentRendered 更早、更准确反映"画面出现"。</summary>
    private void OnFirstRender(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnFirstRender;
        StartupTrace.Mark("render");
    }

    /// <summary>首帧落屏时间：用户"看到窗口"的时刻。</summary>
    private void OnContentRendered(object? sender, EventArgs e)
    {
        StartupTrace.Mark("firstframe");
        FlushOnce();
    }

    /// <summary>窗口加载后异步读目录：先出窗口，再出内容（P1 首帧优先）。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        StartupTrace.Mark("loaded");

        ApplySettingsToUi();
        ApplyMenuGestures();

        // 诊断模式接管：测完自行退出，不写启动日志的 normal 路径
        if (ProbePath is not null)
        {
            await RunPerfProbeAsync();
            return;
        }

        try
        {
            await _viewModel.NavigateAsync(ResolveStartPath());
            StartupTrace.Mark("firstcontent");
            FlushOnce();
        }
        catch (Exception ex)
        {
            _viewModel.ReportError(ex);
        }
    }

    private async void OnQuickAccessSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QuickAccessList.SelectedItem is not NavItem item) return;
        if (string.Equals(item.Path, _viewModel.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            await _viewModel.NavigateAsync(item.Path);
        }
        catch (Exception ex)
        {
            _viewModel.ReportError(ex);
        }
    }

    /// <summary>
    /// 启动目录：上次路径优先（session.lastPath），其次"文档"，最后用户目录。
    /// 上次路径可能已被删除，因此必须校验存在性，否则首屏是一片"无法打开"。
    /// </summary>
    private static string ResolveStartPath()
    {
        string? last = App.Settings.Session.LastPath;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents) && Directory.Exists(documents)) return documents;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : AppContext.BaseDirectory;
    }

    private void FlushOnce()
    {
        if (_flushed) return;
        _flushed = true;
        StartupTrace.Flush();
    }
}
