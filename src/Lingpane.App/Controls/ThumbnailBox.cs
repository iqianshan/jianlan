using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingpane.App.Services;
using Lingpane.App.Shell;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Preview;

namespace Lingpane.App.Controls;

/// <summary>
/// 列表行里那个 24×24 的图标槽（FR-6.6）。
///
/// 先显示类型图标，缩略图取到后换上——**不**先占一个空位等缩略图：
/// 一个目录里大部分是文档，等它们全部超时会让整个列表看起来"没图标"。
///
/// 请求与取消都挂在这一行的 <c>Loaded</c>/<c>Unloaded</c> 上，这也是需求里
/// "滚动时不发起请求"最省事的实现方式：虚拟化把行滚出视野时会触发 Unloaded，
/// 在途的请求随之作废；滚回来重新请求（结果多半已在内存缓存里）。
/// </summary>
public sealed class ThumbnailBox : Grid
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(
        nameof(Entry),
        typeof(FileEntry),
        typeof(ThumbnailBox),
        new PropertyMetadata(null, OnEntryChanged));

    /// <summary>大目录简化模式（T-03）：为 true 时一律只显示图标。</summary>
    public static readonly DependencyProperty IsSuppressedProperty = DependencyProperty.Register(
        nameof(IsSuppressed),
        typeof(bool),
        typeof(ThumbnailBox),
        new PropertyMetadata(false, OnSuppressedChanged));

    private readonly Image _image = new() { Stretch = Stretch.None };

    private CancellationTokenSource? _cts;
    private bool _loaded;

    public ThumbnailBox()
    {
        Children.Add(_image);

        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;

        Loaded += (_, _) =>
        {
            _loaded = true;
            Refresh();
        };

        Unloaded += (_, _) =>
        {
            _loaded = false;
            Cancel();
        };
    }

    public FileEntry? Entry
    {
        get => (FileEntry?)GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    public bool IsSuppressed
    {
        get => (bool)GetValue(IsSuppressedProperty);
        set => SetValue(IsSuppressedProperty, value);
    }

    private static void OnEntryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ThumbnailBox)d).Refresh();

    private static void OnSuppressedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ThumbnailBox)d).Refresh();

    /// <summary>回到"只有图标"的状态，并按需重新发起请求。</summary>
    private void Refresh()
    {
        Cancel();

        FileEntry? entry = Entry;
        if (entry is null)
        {
            _image.Source = null;
            return;
        }

        SetIcon(entry);

        if (!_loaded || IsSuppressed) return;
        if (!PreviewClassifier.SupportsThumbnail(entry.FullPath)) return;

        var cts = new CancellationTokenSource();
        _cts = cts;

        _ = LoadAsync(entry, cts);
    }

    private async Task LoadAsync(FileEntry entry, CancellationTokenSource cts)
    {
        try
        {
            // 滚着的时候停在这里，停下再发请求（FR-6.6「滚动时不发起请求」）
            await ThumbnailService.WaitIdleAsync().ConfigureAwait(true);

            if (cts.IsCancellationRequested) return;

            var image = await ThumbnailService.LoadAsync(entry, cts.Token).ConfigureAwait(true);

            // 行已经被回收给别的条目了：这张图不能贴上去
            if (cts.IsCancellationRequested || !ReferenceEquals(cts, _cts)) return;
            if (image is null) return;

            _image.Stretch = Stretch.Uniform;
            _image.Source = image;
        }
        catch (OperationCanceledException)
        {
            // 滚走了
        }
    }

    private void SetIcon(FileEntry entry)
    {
        _image.Source = ShellIconProvider.Get(entry.IconToken);

        // 图标只有 16px 原生尺寸，放大到 24 会糊；缩略图到位后会覆盖这个拉伸方式
        _image.Stretch = Stretch.None;
    }

    private void Cancel()
    {
        if (_cts is null) return;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
        _cts = null;
    }
}
