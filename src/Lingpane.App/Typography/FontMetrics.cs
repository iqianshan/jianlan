using System;

namespace Lingpane.App.Typography;

/// <summary>
/// 字号 → 派生尺寸的唯一计算入口。
/// 任何按字号缩放的比例、取整、上下限规则都只允许写在这里，
/// 界面 XAML 与其它代码不得再自行计算（规则 4.6 / 15.2）。
/// 内部一律以 px 计算；pt 只出现在设置页与持久化层。
/// </summary>
public sealed record FontMetrics(
    double RowPx,
    double Points,
    double Scale,
    double CaptionSize,
    double BodySize,
    double SubtitleSize,
    double TitleSize,
    double LineHeightCaption,
    double LineHeightBody,
    double LineHeightRow,
    double RowHeight,
    double HeaderHeight,
    double NavItemHeight,
    double TabHeight,
    double TitleBarHeight,
    double ToolbarHeight,
    double StatusBarHeight,
    double ColumnNameMinWidth,
    double ColumnDateWidth,
    double ColumnTypeWidth,
    double ColumnSizeWidth,
    double TabMinWidth,
    double TabMaxWidth,
    double TabTitleMaxWidth,
    double FieldLabelWidth,
    double ComboWidth,
    double ComboWidthNarrow,
    double ComboPaddingLeft,
    double ComboArrowWidth,
    double ComboArrowRight)
{
    /// <summary>字号范围 8–24pt（FR-5.1.6）。</summary>
    public const double MinPoints = 8d;
    public const double MaxPoints = 24d;

    /// <summary>设计基线：行文字 13px = 9.75pt。</summary>
    public const double BaseRowPx = 13d;

    /// <summary>行高基准：舒适 32 / 紧凑 26，只作下限，不缩放字号。</summary>
    public const double ComfortableRowHeight = 32d;
    public const double CompactRowHeight = 26d;

    public const double PointsPerPixel = 0.75d;
    public const double PixelsPerPoint = 4d / 3d;

    public static double PointsToPixels(double pt) => pt * PixelsPerPoint;

    public static double PixelsToPoints(double px) => px * PointsPerPixel;

    /// <summary>按 pt 生成（设置页入口）。</summary>
    public static FontMetrics FromPoints(double points, bool compact = false) =>
        FromPixels(PointsToPixels(points), compact);

    /// <summary>按行文字 px 生成（内部 / 跟随系统入口）。</summary>
    public static FontMetrics FromPixels(double rowPx, bool compact = false)
    {
        double basePx = Math.Clamp(rowPx, PointsToPixels(MinPoints), PointsToPixels(MaxPoints));
        double s = basePx / BaseRowPx;

        double caption = Size(basePx, 0.846, 9);
        double body = Size(basePx, 0.923, 10);
        double subtitle = Size(basePx, 1.154, 14);
        double title = Size(basePx, 1.538, 18);

        double lhRow = Math.Round(basePx * 1.538, 1);
        double rowPadding = compact ? 6d : 12d;
        double rowFloor = compact ? CompactRowHeight : ComfortableRowHeight;

        return new FontMetrics(
            RowPx: Math.Round(basePx, 2),
            Points: Math.Round(PixelsToPoints(basePx), 2),
            Scale: Math.Round(s, 4),
            CaptionSize: caption,
            BodySize: body,
            SubtitleSize: subtitle,
            TitleSize: title,
            LineHeightCaption: Math.Round(caption * 1.45, 1),
            LineHeightBody: Math.Round(body * 1.667, 1),
            LineHeightRow: lhRow,
            RowHeight: Math.Max(rowFloor, Even(lhRow + rowPadding)),
            HeaderHeight: Math.Max(28d, Even(lhRow + 10)),
            NavItemHeight: Math.Max(rowFloor, Even(lhRow + rowPadding)),
            // 标签比工具条按钮矮一档，但要装下图标 + 关闭键 + 活动指示线。
            // 12pt 下原来卡在 30，卡片显得扁；抬到 34 后与 40 高的标题条还有 3px 呼吸位。
            // 下限跟 DesignTokens 的 Size.Tab.Height 保持一致——那份 token 只是首帧的初值，
            // 启动后会被这里算出的数覆盖，两处不同步就会出现"改了 token 没反应"。
            TabHeight: Math.Max(34d, Even(lhRow + 14)),
            TitleBarHeight: Math.Max(40d, Even(lhRow + 16)),
            ToolbarHeight: Math.Max(48d, Even(basePx * 2.4 + 16)),
            StatusBarHeight: Math.Max(28d, Even(lhRow + 8)),
            ColumnNameMinWidth: Even(160d * s),
            ColumnDateWidth: Even(110d * s),
            ColumnTypeWidth: Even(110d * s),
            ColumnSizeWidth: Even(90d * s),
            // 标签页宽度同样跟着字号走（ui-guidelines 规则 3.x：Size.Tab.* 随动）。
            // 只放大高度不放大宽度，24pt 下每个标签都是"文…"，等于没有标题。
            TabMinWidth: Math.Max(100d, Even(100d * s)),
            TabMaxWidth: Math.Max(200d, Even(200d * s)),
            // 标题可用宽度 = 标签最大宽度 − 图标 − 关闭按钮 − 内边距。
            // 单独给 TextBlock 而不只给标签容器：横向 StackPanel 测量子项时给的是无限宽，
            // 容器的 MaxWidth 只会把内容硬裁掉（不给省略号），必须让 TextBlock 自己知道上限。
            TabTitleMaxWidth: Math.Max(64d, Even(200d * s) - Even(72d * s)),
            // 设置页左侧标签列。原来是写死的 120，24pt 下"界面语言"被压成"界面…"（AC-06）
            FieldLabelWidth: Even(120d * s),
            // 下拉框宽度：以 12pt（设计基准）为锚点随字号等比放大，只留两档——
            // 常规 300、数值 120。同一页里混用 200/240/300 会让左边缘参差，
            // 而写死任一档在英文 24pt 下都会出事："Follow system"只剩"Follow"、
            // "Interface font"直接贴边（AC-06 走查发现）——文字在变宽，容器却不动。
            ComboWidth: Even(300d / BaselineScale * s),
            ComboWidthNarrow: Even(120d / BaselineScale * s),
            // 下拉框内边距：右侧要留出箭头的位置。写死 10/28 时 24pt 下
            // "简体中文"会顶到 ▾ 上（AC-06 走查发现）
            ComboPaddingLeft: Even(10d * s),
            ComboArrowWidth: Even(34d * s),
            ComboArrowRight: Even(10d * s));
    }

    private static double Size(double basePx, double ratio, double min) =>
        Math.Max(min, Math.Round(basePx * ratio, MidpointRounding.AwayFromZero));

    /// <summary>
    /// 12pt 下的字号倍数（16px ÷ 13px ≈ 1.23）。凡是以 12pt 设计稿的像素值
    /// 为锚点、希望"12pt 时正好等于该值"的尺寸，都要先除掉它再乘 s。
    /// 直接写 <c>Even(200 * s)</c> 会把 12pt 撑成 246，12pt 布局就跟着漂。
    /// </summary>
    private static readonly double BaselineScale = PointsToPixels(12d) / BaseRowPx;

    /// <summary>
    /// 高度与列宽统一取偶数，保证 DPI 缩放下的像素对齐；间距仍走 4px 栅格。
    /// </summary>
    private static double Even(double value) => Math.Ceiling(value / 2d) * 2d;
}
