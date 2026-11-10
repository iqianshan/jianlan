using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lingpane.Core.FileSystem;

namespace Lingpane.App.Views;

/// <summary>
/// 框选多选（FR-6.7）：在列表的**空白处**按住左键拖出一个矩形，落在矩形里的条目被选中。
///
/// 为什么必须区分"按在条目上"与"按在空白处"：同一个左键手势在资源管理器里有两个意思——
/// 按在文件上拖 = 把文件拖出去，按在空白处拖 = 框选。两者不能共存于一条代码路径，
/// 所以判定放在按下那一刻（见 <see cref="OnFileDragStart"/>），之后各走各的。
///
/// 为什么不自己捕获鼠标：ListBox 在 MouseDown 时已经捕获了，拖到列表外仍能收到 MouseMove，
/// 再捕获一次只会和它抢，表现为"拖到滚动条上就断了"。
/// </summary>
public partial class PaneView
{
    /// <summary>是否处于"空白处按下、可能开始框选"的状态。</summary>
    private bool _bandArmed;

    /// <summary>框已经拉出来了（超过拖拽阈值）。用于区分"点空白"与"框选"。</summary>
    private bool _bandVisible;

    /// <summary>按下时的选中项。Ctrl+框选是在这批之上追加，而不是替换。</summary>
    private List<FileEntry> _bandBase = [];

    /// <summary>Ctrl 是否按下。松手前不重读键盘状态——中途松 Ctrl 不该改变这次框选的语义。</summary>
    private bool _bandAdditive;

    private Point _bandOrigin;

    /// <summary>上一帧命中的条目。命中集合没变就不动选中项，见 <see cref="ApplyBandSelection"/>。</summary>
    private List<FileEntry> _bandHits = [];

    /// <summary>空白处按下：记下起点与基线选中，等移动超过阈值再真正开始拉框。</summary>
    private void BeginRubberBand(MouseButtonEventArgs e)
    {
        _bandOrigin = e.GetPosition(FileList);
        _bandBase = [.. FileList.SelectedItems.OfType<FileEntry>()];
        _bandAdditive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _bandArmed = true;
        _bandVisible = false;
        _bandHits = [];
    }

    /// <summary>
    /// 拖动中更新选框。真的动起来了才显示矩形——
    /// 否则"在空白处点一下"会先闪出一条 0×0 的框，看起来像界面出了毛病。
    /// </summary>
    private void UpdateRubberBand(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndRubberBand();
            return;
        }

        Point now = e.GetPosition(FileList);

        if (!_bandVisible)
        {
            if (Math.Abs(now.X - _bandOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - _bandOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _bandVisible = true;
            RubberBand.Visibility = Visibility.Visible;
        }

        double left = Math.Min(_bandOrigin.X, now.X);
        double top = Math.Min(_bandOrigin.Y, now.Y);
        double width = Math.Abs(now.X - _bandOrigin.X);
        double height = Math.Abs(now.Y - _bandOrigin.Y);

        Canvas.SetLeft(RubberBand, left);
        Canvas.SetTop(RubberBand, top);
        RubberBand.Width = width;
        RubberBand.Height = height;

        ApplyBandSelection(new Rect(left, top, width, height));
    }

    /// <summary>结束框选：收起矩形、清状态。选中项保持原样（那是用户刚圈出来的结果）。</summary>
    private void EndRubberBand()
    {
        _bandArmed = false;
        _bandVisible = false;
        _bandHits = [];
        _bandBase = [];
        RubberBand.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 把与矩形相交的条目设为选中。
    ///
    /// 两点讲究：
    ///   1. 命中集合与上一帧相同就直接返回——拖动过程中每帧改一次 SelectedItems，
    ///      会让 SelectionChanged（进而预览、状态栏、命令可用性）每帧重算一遍；
    ///   2. 只在生成出来的容器里找。虚拟化下屏幕外的行没有容器，
    ///      而"框选能选到还没渲染的行"本来也无从谈起——资源管理器同样如此。
    /// </summary>
    private void ApplyBandSelection(Rect band)
    {
        var hits = new List<FileEntry>();

        foreach (ListBoxItem item in VisibleItemContainers())
        {
            if (item.DataContext is not FileEntry entry) continue;
            if (!TryGetItemBounds(item, out Rect bounds)) continue;
            if (band.IntersectsWith(bounds)) hits.Add(entry);
        }

        if (SameHits(hits)) return;
        _bandHits = hits;

        _restoringState = true;
        try
        {
            FileList.SelectedItems.Clear();

            if (_bandAdditive)
            {
                foreach (FileEntry entry in _bandBase) FileList.SelectedItems.Add(entry);
            }

            foreach (FileEntry entry in hits) FileList.SelectedItems.Add(entry);
        }
        finally
        {
            _restoringState = false;
        }

        // 逐项增删的中间状态不结算，这里统一算一次（与 RestoreSelection 同一口径）
        CommitSelection();
    }

    /// <summary>两批命中是否等价。顺序由可视树决定，这里不看顺序。</summary>
    private bool SameHits(List<FileEntry> hits)
    {
        if (hits.Count != _bandHits.Count) return false;

        for (int i = 0; i < hits.Count; i++)
        {
            if (!ReferenceEquals(hits[i], _bandHits[i])) return false;
        }

        return true;
    }

    /// <summary>条目在列表坐标系里的矩形。容器刚回收、还没接进树时拿不到，按"不在框里"处理。</summary>
    private bool TryGetItemBounds(ListBoxItem item, out Rect bounds)
    {
        bounds = Rect.Empty;

        try
        {
            if (!item.IsVisible) return false;

            bounds = item.TransformToAncestor(FileList).TransformBounds(new Rect(item.RenderSize));
            return true;
        }
        catch (InvalidOperationException)
        {
            // 不在同一棵可视树里（正在被回收复用），这一帧跳过
            return false;
        }
    }

    /// <summary>可视树里现存的 ListBoxItem（只含已生成的行）。</summary>
    private IEnumerable<ListBoxItem> VisibleItemContainers()
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(FileList);

        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            int count = VisualTreeHelper.GetChildrenCount(node);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);

                if (child is ListBoxItem item)
                {
                    yield return item;
                    continue;
                }

                stack.Push(child);
            }
        }
    }

    /// <summary>
    /// 这个按下点是否落在"空白处"——不在任何一行上，也不在滚动条上。
    /// 拖滚动条不能被当成框选：那是用户想滚动列表，框选会把它变成一次误选。
    /// </summary>
    private static bool IsBlankArea(DependencyObject? source)
    {
        if (source is null) return false;
        if (FindAncestor<ListBoxItem>(source) is not null) return false;
        if (FindAncestor<ScrollBar>(source) is not null) return false;

        return true;
    }
}
