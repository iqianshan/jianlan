using System.Windows;

namespace Jianlan.App.Services;

/// <summary>
/// 窗口位置的计算（FR-10.2）。
///
/// 需求要的是"重启后还原；多显示器拔插后位置越界时自动居中"。
/// 两句话对应这里的两个方法：一个判断存下来的矩形现在还看不看得见，
/// 一个把看不见的矩形挪回主屏正中。
///
/// 为什么用"虚拟屏矩形"而不是逐台显示器做命中测试：
///   · 恢复位置时窗口还不存在，拿不到句柄，<c>MonitorFromWindow</c> 用不了；
///   · 虚拟屏是全部显示器的最小外接矩形，判"完全在外面"足够准。
/// 代价是显示器之间的**空隙**（L 形排布时那块死角）会被当成可见区域——
/// 窗口正好卡在空隙里的概率极低，而为此写一套显示器枚举+逐台相交，
/// 换来的是每次启动多一次外壳调用。不值得。
/// </summary>
internal static class WindowPlacement
{
    /// <summary>
    /// 判定"还看得见"的最小露出量。露出 10px 边角等于找不到窗口——
    /// 用户会以为程序没启动，而不是去拖一个看不见的标题栏。
    /// </summary>
    private const double MinVisibleWidth = 120;
    private const double MinVisibleHeight = 40;

    /// <summary>虚拟屏矩形（全部显示器的外接矩形）。</summary>
    public static Rect VirtualScreen => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// 这个位置的窗口是否还落在可视区里。拔掉外接屏、或把分辨率调小之后，
    /// 上次存的坐标可能整个跑到屏幕外面。
    /// </summary>
    public static bool IsSufficientlyVisible(Rect window)
    {
        Rect overlap = Rect.Intersect(window, VirtualScreen);
        if (overlap.IsEmpty) return false;

        return overlap.Width >= Math.Min(MinVisibleWidth, window.Width)
            && overlap.Height >= Math.Min(MinVisibleHeight, window.Height);
    }

    /// <summary>
    /// 居中到主屏工作区。窗口比工作区还大时先缩到放得下——
    /// 否则居中之后标题栏会被顶到屏幕外，用户依然拖不动它。
    /// </summary>
    public static Rect CenterOnPrimary(Rect window)
    {
        Rect work = SystemParameters.WorkArea;

        double width = Math.Min(window.Width, work.Width);
        double height = Math.Min(window.Height, work.Height);

        return new Rect(
            work.Left + ((work.Width - width) / 2),
            work.Top + ((work.Height - height) / 2),
            width,
            height);
    }
}
