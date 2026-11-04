using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lingpane.App.Commands;
using Lingpane.App.ViewModels;
using Lingpane.Core.Commands;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.Navigation;

namespace Lingpane.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly CommandRegistry _commands = new();
    private bool _flushed;

    public CommandAdapter UpCommand { get; }
    public CommandAdapter RefreshCommand { get; }
    public CommandAdapter ThemeCommand { get; }

    public MainWindow()
    {
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
            Id = CommandIds.Refresh,
            Title = "刷新",
            Gesture = "F5",
            CanExecute = () => !_viewModel.IsLoading,
            Execute = () => _ = _viewModel.NavigateAsync(_viewModel.CurrentPath),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CycleTheme,
            Title = "主题",
            // 不能用 Ctrl+T / Ctrl+Shift+T：docs/shortcuts.md 已把它们分配给标签页
            Gesture = "Ctrl+Shift+D",
            Execute = () => App.CycleTheme(),
        });

        UpCommand = new CommandAdapter(_commands.Get(CommandIds.GoUp));
        RefreshCommand = new CommandAdapter(_commands.Get(CommandIds.Refresh));
        ThemeCommand = new CommandAdapter(_commands.Get(CommandIds.CycleTheme));

        // 快捷键只能在这里挂：KeyBinding 不继承 DataContext，写在 XAML 里绑不到命令
        InputBindings.Add(new KeyBinding(RefreshCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(RefreshCommand, Key.R, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(UpCommand, Key.Up, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(ThemeCommand, Key.D, ModifierKeys.Control | ModifierKeys.Shift));

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        CompositionTarget.Rendering += OnFirstRender;
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

    private static string ResolveStartPath()
    {
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
