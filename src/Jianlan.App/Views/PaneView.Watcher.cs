using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Jianlan.App.Views;

/// <summary>
/// 当前目录的变更监听（FR-4.3）：外部程序新建/删除/改名后，列表自动跟上，
/// 不用手按 F5。
///
/// 两个必须处理的坑：
///   · <see cref="FileSystemWatcher"/> 的事件在线程池线程上触发，碰界面必须切回 UI 线程；
///   · 复制大目录时事件会密集到达，直接每次刷新会把界面拖死——统一去抖 400ms。
/// 程序自己发起操作期间用 <see cref="_suppressWatcher"/> 暂停，操作结束后由调用方刷新一次。
/// </summary>
public partial class PaneView
{
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(400);

    private FileSystemWatcher? _watcher;
    private string? _watchedDirectory;
    private DispatcherTimer? _debounceTimer;
    private bool _suppressWatcher;
    private bool _paneClosed;

    internal void StartWatching(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return;

        if (string.Equals(_watchedDirectory, directory, StringComparison.OrdinalIgnoreCase)
            && _watcher is { EnableRaisingEvents: true })
        {
            return;
        }

        StopWatching();

        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.Size | NotifyFilters.LastWrite,
                // 默认 8KB 在大目录里几秒就会溢出，溢出即漏事件，直接影响"自动刷新"的可靠性
                InternalBufferSize = 32 * 1024,
            };

            watcher.Created += OnWatcherChange;
            watcher.Deleted += OnWatcherChange;
            watcher.Changed += OnWatcherChange;
            watcher.Renamed += OnWatcherRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _watchedDirectory = directory;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // 盘符根目录、无权限目录、网络路径都可能失败：自动刷新失效，但浏览不受影响
            _watchedDirectory = null;
            Diagnostics.AppLog.Warn("watcher.start", "E5001", ex.Message);
        }
    }

    private void StopWatching()
    {
        if (_watcher is null) return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
        _watchedDirectory = null;
    }

    private void OnWatcherChange(object sender, FileSystemEventArgs e) => QueueRefresh();

    private void OnWatcherRenamed(object sender, RenamedEventArgs e) => QueueRefresh();

    /// <summary>缓冲区溢出（事件太多来不及处理）会走这里：宁可整表刷新一次，也不能显示过期内容。</summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Diagnostics.AppLog.Warn("watcher.error", "E5002", e.GetException().Message);
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_suppressWatcher) return;

        // 事件来自线程池，定时器只能在 UI 线程上操作
        Dispatcher.InvokeAsync(() =>
        {
            if (_suppressWatcher) return;
            // 窗格已经收尾还去启定时器，等于给已摘除的视图模型排一次必然失败的刷新
            if (_paneClosed || _viewModel is null) return;

            _debounceTimer ??= CreateDebounceTimer();
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }, DispatcherPriority.Background);
    }

    private DispatcherTimer CreateDebounceTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = WatcherDebounce };
        timer.Tick += OnDebounceElapsed;
        return timer;
    }

    private async void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounceTimer?.Stop();

        if (_suppressWatcher) return;
        // 视图模型可能已经摘除（切换标签、关标签、窗口关闭）。去抖定时器是
        // 400ms 的 DispatcherTimer，摘除与计时器到点可以错开一拍，此时 Vm 访问会抛
        // "PaneView 尚未附加视图模型"。8 小时长跑里出现 9 次，都在标签收尾附近。
        if (_viewModel is null) return;
        if (string.IsNullOrEmpty(Vm.CurrentPath)) return;

        // 外部变化时用户可能正多选，刷新后按路径恢复
        string[] selected = Vm.SelectedEntries.Select(entry => entry.FullPath).ToArray();
        await RefreshAndSelectAsync(selected);
    }

    /// <summary>
    /// 窗格从可视树移除时收尾。原来是 Window.OnClosed，拆成 UserControl 后
    /// 走 Unloaded——窗口关闭会走到这里。
    ///
    /// 注意：**切换标签不会走这里**。标签切换是重新 Attach 另一份视图模型，
    /// 视图本身还在树上；收尾动作在 <see cref="Attach"/> 的开头完成。
    /// 加一次性保护：Unloaded 在重设父子关系时可能重复触发。
    /// </summary>
    private void OnPaneUnloaded(object sender, RoutedEventArgs e)
    {
        if (_paneClosed) return;
        _paneClosed = true;

        _debounceTimer?.Stop();
        StopWatching();
        ReleaseColumns();

        if (_viewModel is not { } vm) return;

        vm.NavigationCompleted -= StartWatching;
        vm.NavigationCompleted -= ScheduleFolderIcons;
        vm.PropertyChanged -= OnPanePropertyChanged;
    }
}
