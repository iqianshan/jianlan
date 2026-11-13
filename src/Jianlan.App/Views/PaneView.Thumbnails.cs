using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Jianlan.App.Services;

namespace Jianlan.App.Views;

/// <summary>
/// 行内缩略图（FR-6.6）在窗格这一侧的三件事：滚动闸门、大目录简化模式、把状态传给行模板。
/// </summary>
public partial class PaneView
{
    /// <summary>超过这个条目数就进入简化模式，一律不出缩略图（T-03）。</summary>
    private const int LargeFolderItems = 5000;

    /// <summary>滚动停下多久才算"停"。太短会让惯性滚动被切成一段段请求，太长会有明显的空窗。</summary>
    private static readonly TimeSpan ScrollIdleDelay = TimeSpan.FromMilliseconds(160);

    public static readonly DependencyProperty IsThumbnailSuppressedProperty = DependencyProperty.Register(
        nameof(IsThumbnailSuppressed),
        typeof(bool),
        typeof(PaneView),
        new PropertyMetadata(false));

    private DispatcherTimer? _scrollIdle;

    /// <summary>
    /// 本窗格当前是否处于简化模式。行模板通过相对源绑定读它，因此必须是依赖属性。
    /// </summary>
    public bool IsThumbnailSuppressed
    {
        get => (bool)GetValue(IsThumbnailSuppressedProperty);
        private set => SetValue(IsThumbnailSuppressedProperty, value);
    }

    private void InitializeThumbnails()
    {
        // 条目集合本身是 ObservableCollection：换目录、增删文件都会走到这里，
        // 比在视图模型的属性变更里再判一次"是不是 Entries"更直接
        if (FileList.Items is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += (_, _) =>
                IsThumbnailSuppressed = FileList.Items.Count > LargeFolderItems;
        }
    }

    /// <summary>
    /// 滚动时暂停缩略图请求（FR-6.6「滚动时不发起请求」）。
    ///
    /// 闸门是全局的：两个窗格共用一个 <see cref="ThumbnailService"/>。严格按需求可以
    /// 只暂停"正在滚的那个窗格"，但两个窗格同时滚的情况几乎不存在，而全局闸门
    /// 少一层"哪个窗格在滚"的状态，出错面更小。
    /// </summary>
    private void OnFileListScroll(object sender, ScrollChangedEventArgs e)
    {
        // 只有真正发生位移才算滚动：内容变化也会触发 ScrollChanged，那种情况不该暂停
        if (e.VerticalChange == 0 && e.HorizontalChange == 0 && e.ExtentHeightChange == 0) return;

        ThumbnailService.Pause();

        _scrollIdle ??= CreateScrollIdleTimer();
        _scrollIdle.Stop();
        _scrollIdle.Start();
    }

    private DispatcherTimer CreateScrollIdleTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ScrollIdleDelay };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ThumbnailService.Resume();
        };

        return timer;
    }
}
