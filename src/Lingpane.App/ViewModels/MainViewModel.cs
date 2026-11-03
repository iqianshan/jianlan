using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Lingpane.App.Shell;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Navigation;

namespace Lingpane.App.ViewModels;

/// <summary>左侧导航项。显示名与字形在 UI 层映射，Core 只给路径。</summary>
public sealed record NavItem(string Name, string Glyph, string Path);

/// <summary>
/// M0 主视图模型：只负责"打开一个目录并把结果交给列表"。
/// 不含标签页、双窗格、彩标——这些在 M2 接入。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<FileEntry> _entries = [];
    private string _currentPath = string.Empty;
    private string _statusText = "就绪";
    private bool _isLoading;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 直接暴露 IReadOnlyList 而不批量灌 ObservableCollection：
    /// 万项逐条 Add 会触发 N 次 CollectionChanged，切换目录直接卡住。
    /// </summary>
    public IReadOnlyList<FileEntry> Entries
    {
        get => _entries;
        private set => Set(ref _entries, value);
    }

    public IReadOnlyList<NavItem> QuickAccess { get; } = BuildQuickAccess();

    private static List<NavItem> BuildQuickAccess()
    {
        var list = new List<NavItem>(7);
        foreach (var folder in KnownFolders.All)
        {
            var (name, glyph) = folder.Kind switch
            {
                KnownFolderKind.Desktop => ("桌面", IconGlyphs.Desktop),
                KnownFolderKind.Documents => ("文档", IconGlyphs.Documents),
                KnownFolderKind.Downloads => ("下载", IconGlyphs.Download),
                KnownFolderKind.Pictures => ("图片", IconGlyphs.Pictures),
                KnownFolderKind.Music => ("音乐", IconGlyphs.Music),
                KnownFolderKind.Videos => ("视频", IconGlyphs.Video),
                _ => ("此电脑", IconGlyphs.Account),
            };

            list.Add(new NavItem(name, glyph, folder.Path));
        }

        return list;
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set => Set(ref _currentPath, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    /// <summary>最近一次目录枚举耗时，供 M0 性能核对。</summary>
    public long LastEnumerateMs { get; private set; }

    public async Task NavigateAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        IsLoading = true;
        StatusText = "读取中…";

        long started = Stopwatch.GetTimestamp();
        var result = await Task.Run(() =>
        {
            bool ok = DirectoryEnumerator.TryReadSorted(path, out var list, out var error);
            return (Ok: ok, List: list, Error: error);
        }).ConfigureAwait(true);

        LastEnumerateMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (result.Ok)
        {
            Entries = result.List;
            CurrentPath = path;
            StatusText = $"{result.List.Count} 项 · 枚举 {LastEnumerateMs} ms";
        }
        else
        {
            Entries = [];
            CurrentPath = path;
            StatusText = $"无法打开：{result.Error}";
        }

        IsLoading = false;
    }

    /// <summary>把异常转成可展示的状态文本，不让异常冒泡到 UI 线程。</summary>
    public void ReportError(Exception ex)
    {
        IsLoading = false;
        StatusText = "操作失败：" + ex.Message;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
