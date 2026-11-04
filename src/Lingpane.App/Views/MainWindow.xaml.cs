using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lingpane.App.Commands;
using Lingpane.App.ViewModels;
using Lingpane.Core.Commands;
using Lingpane.Core.Diagnostics;
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

        UpCommand = new CommandAdapter(_commands.Get(CommandIds.GoUp));
        BackCommand = new CommandAdapter(_commands.Get(CommandIds.GoBack));
        ForwardCommand = new CommandAdapter(_commands.Get(CommandIds.GoForward));
        RefreshCommand = new CommandAdapter(_commands.Get(CommandIds.Refresh));
        ThemeCommand = new CommandAdapter(_commands.Get(CommandIds.CycleTheme));
        DetailsCommand = new CommandAdapter(_commands.Get(CommandIds.ViewDetails));
        ListViewCommand = new CommandAdapter(_commands.Get(CommandIds.ViewList));
        SettingsCommand = new CommandAdapter(_commands.Get(CommandIds.OpenSettings));

        // 快捷键只能在这里挂：KeyBinding 不继承 DataContext，写在 XAML 里绑不到命令
        InputBindings.Add(new KeyBinding(RefreshCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(RefreshCommand, Key.R, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(UpCommand, Key.Up, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(BackCommand, Key.Left, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(ForwardCommand, Key.Right, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(ThemeCommand, Key.D, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(DetailsCommand, Key.D1, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(ListViewCommand, Key.D2, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new DelegateCommand(BeginAddressEdit), Key.L, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(SettingsCommand, Key.OemComma, ModifierKeys.Control));

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

        foreach (object item in FileList.SelectedItems)
        {
            if (item is not Core.FileSystem.FileEntry entry) continue;
            count++;
            total += entry.Size;
        }

        _viewModel.SetSelection(count, total);
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
