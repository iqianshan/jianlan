using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingpane.App.ViewModels;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.Navigation;

namespace Lingpane.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _flushed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        CompositionTarget.Rendering += OnFirstRender;
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
