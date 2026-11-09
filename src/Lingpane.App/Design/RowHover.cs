using System.Windows;

namespace Lingpane.App.Design;

/// <summary>
/// 行悬停高亮的延迟门（FR-5.2.5）。
///
/// 单击打开模式下，鼠标扫过列表不该让行一路闪过去——高亮推迟 600ms 出现，
/// 扫过去就没了，只有"停下来看"的行才有反馈。双击模式下延迟为 0，行为与原生一致。
///
/// 为什么做成"额外的一位"而不是改 <c>IsMouseOver</c>：后者是只读的依赖属性，
/// 改不了；而把整块高亮交给本属性又要担心容器复用残留。样式里用
/// <c>IsMouseOver &amp;&amp; IsHovered</c> 两个条件同时成立才画，两个问题一起解决——
/// 复用出来的容器即使残留了 <c>true</c>，鼠标不在上面也不会显示。
///
/// 置位逻辑在 <c>PaneView</c> 里（它才知道列表与设置），本类只管存值。
/// </summary>
public static class RowHover
{
    public static readonly DependencyProperty IsHoveredProperty =
        DependencyProperty.RegisterAttached(
            "IsHovered",
            typeof(bool),
            typeof(RowHover),
            new FrameworkPropertyMetadata(false));

    /// <summary>单击打开模式下延迟显示高亮的毫秒数（需求 600ms，写死不放设置）。</summary>
    public const int RevealDelayMs = 600;

    public static bool GetIsHovered(DependencyObject element) => (bool)element.GetValue(IsHoveredProperty);

    public static void SetIsHovered(DependencyObject element, bool value) => element.SetValue(IsHoveredProperty, value);
}
