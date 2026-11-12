using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingpane.App.Shell;

namespace Lingpane.App.Typography;

/// <summary>
/// 字号与字体的唯一入口（FR-5.1.5 ~ FR-5.1.7）。
/// 所有随字号变化的资源键由本类统一写入，界面一律用 DynamicResource 消费，
/// 因此调整字号不需要重建任何控件树（FONT-04）。
/// 必须在 UI 线程调用。
/// </summary>
public static class FontScaleService
{
    /// <summary>设置页字号范围 8–24pt，步进 1（FR-5.1.6）。</summary>
    public const double MinPoints = FontMetrics.MinPoints;
    public const double MaxPoints = FontMetrics.MaxPoints;

    /// <summary>当前生效字体度量。首次 Apply 之前为设计基线值（不读系统，避免进入启动路径）。</summary>
    public static FontMetrics Current { get; private set; } = FontMetrics.FromPixels(FontMetrics.BaseRowPx);

    /// <summary>用户选择的字号（pt）。null = 跟随系统。</summary>
    public static double? SelectedPoints { get; private set; }

    public static bool IsCompact { get; private set; }

    /// <summary>字号或密度变更完成。</summary>
    public static event EventHandler<FontMetrics>? Changed;

    /// <summary>
    /// 应用字号。<paramref name="points"/> 为 null 时跟随系统字号。
    /// 字体族不在本方法处理，见 <see cref="ApplyFontFamily"/>。
    /// </summary>
    public static void Apply(double? points, bool compact = false)
    {
        var app = Application.Current;
        if (app is null) return;

        double px = points is null
            ? SystemFonts.MessageFontSize
            : FontMetrics.PointsToPixels(Math.Clamp(points.Value, MinPoints, MaxPoints));

        var m = FontMetrics.FromPixels(px, compact);
        var res = app.Resources;

        res["Font.Scale"] = m.Scale;

        res["Font.Size.Caption"] = m.CaptionSize;
        res["Font.Size.Body"] = m.BodySize;
        res["Font.Size.Row"] = m.RowPx;
        res["Font.Size.Subtitle"] = m.SubtitleSize;
        res["Font.Size.Title"] = m.TitleSize;

        res["Font.LineHeight.Caption"] = m.LineHeightCaption;
        res["Font.LineHeight.Body"] = m.LineHeightBody;
        res["Font.LineHeight.Row"] = m.LineHeightRow;

        // 行高：两个密度档各算一份，Active 为当前生效值，列表与导航统一绑 Active
        res["Size.Row.Comfortable"] = FontMetrics.FromPixels(px, false).RowHeight;
        res["Size.Row.Compact"] = FontMetrics.FromPixels(px, true).RowHeight;
        res["Size.Row.Active"] = m.RowHeight;

        res["Size.Row.Header"] = m.HeaderHeight;
        res["Size.Nav.Item.Height"] = m.NavItemHeight;
        res["Size.Tab.Height"] = m.TabHeight;
        res["Size.Tab.MinWidth"] = m.TabMinWidth;
        res["Size.Tab.MaxWidth"] = m.TabMaxWidth;
        res["Size.Tab.Title.MaxWidth"] = m.TabTitleMaxWidth;
        res["Size.TitleBar.Height"] = m.TitleBarHeight;
        res["Size.Toolbar.Height"] = m.ToolbarHeight;
        res["Size.StatusBar.Height"] = m.StatusBarHeight;

        res["Column.Name.MinWidth"] = m.ColumnNameMinWidth;
        res["Column.Date.Width"] = m.ColumnDateWidth;
        res["Column.Type.Width"] = m.ColumnTypeWidth;
        res["Column.Size.Width"] = m.ColumnSizeWidth;

        // 表单标签列与紧随其后的提示缩进：一起缩放，否则 24pt 会截断、9pt 会拉开空隙
        res["Size.FieldLabel.Width"] = m.FieldLabelWidth;
        // 下拉框基准宽度：内容宽度跟着字号走，容器也必须跟着走
        res["Size.Combo.Width"] = m.ComboWidth;
        res["Size.Combo.Width.Narrow"] = m.ComboWidthNarrow;
        res["Size.FieldHint.Margin"] = new Thickness(m.FieldLabelWidth + 8, 4, 0, 0);
        res["Size.ComboContent.Margin"] = new Thickness(m.ComboPaddingLeft, 0, m.ComboArrowWidth, 0);
        res["Size.ComboArrow.Margin"] = new Thickness(0, 0, m.ComboArrowRight, 0);

        // Size.Icon.* 有意不写入：FONT-06 要求图标尺寸不随字号变化

        // 自绘标题条（FR-10.11）的可拖动区域跟着标题条长高。
        // WindowChrome.CaptionHeight 不是依赖属性，绑不了 DynamicResource，只能在这里推一次；
        // 不推的话 24pt 下标题条高 50、只有上面 40 能拖，下半截按下去毫无反应。
        foreach (Window window in app.Windows)
        {
            if (res["Size.TitleBar.Height"] is double caption) WindowChromeHelper.UpdateCaptionHeight(window, caption);
        }

        SelectedPoints = points is null ? null : Math.Round(m.Points, 2);
        IsCompact = compact;
        Current = m;
        Changed?.Invoke(null, m);
    }

    /// <summary>
    /// 切换界面字体族（FR-5.1.5）。传 null 回落默认字体栈。
    /// 只改字形，不改任何尺寸；行高由新字体度量决定，不做硬编码换算。
    /// </summary>
    public static void ApplyFontFamily(FontFamily? family)
    {
        var app = Application.Current;
        if (app is null) return;

        // 首次调用时记住 token 里的默认栈，供"恢复默认"回落，避免重复定义字体字符串
        _defaultUiFamily ??= app.Resources["Font.Family.UI"] as FontFamily;

        // 字体不可用时保持原字体并提示（FONT-03 / FONT-08），不做静默切换
        app.Resources["Font.Family.UI"] = family ?? _defaultUiFamily;
        UiFontChanged?.Invoke(null, (FontFamily?)app.Resources["Font.Family.UI"]);
    }

    private static FontFamily? _defaultUiFamily;

    /// <summary>
    /// 按已保存的字体族名应用（启动路径专用，不枚举系统字体）。
    /// 名字在本机不存在时 WPF 会按字体栈自行回落，这里不做判断；
    /// 设置页 Resolve 时查不到会给 FONT-03 的回落提示。
    /// </summary>
    public static void ApplyFontFamilyByName(string? source) =>
        ApplyFontFamily(string.IsNullOrWhiteSpace(source) ? null : new FontFamily(source));

    public static event EventHandler<FontFamily?>? UiFontChanged;

    /// <summary>恢复默认字体与字号（FR-5.1.7）。</summary>
    public static void ResetToDefault()
    {
        ApplyFontFamily(null);
        Apply(null, IsCompact);
    }

    /// <summary>当前生效的行高，用于滚动锚定计算。</summary>
    public static double ActiveRowHeight =>
        Application.Current?.Resources["Size.Row.Active"] is double d && d > 0 ? d : 32d;

    /// <summary>字号变更前调用：记录首个可见项索引与行内偏移。</summary>
    public static (int Index, double Offset) CaptureAnchor(ScrollViewer? scroll)
    {
        if (scroll is null) return (0, 0d);
        double row = ActiveRowHeight;
        int index = (int)(scroll.VerticalOffset / row);
        return (index, scroll.VerticalOffset - index * row);
    }

    /// <summary>字号变更后调用：按项目索引恢复，保证视口顶部条目不变（FONT-05）。</summary>
    public static void RestoreAnchor(ScrollViewer? scroll, (int Index, double Offset) anchor)
    {
        if (scroll is null) return;
        scroll.ScrollToVerticalOffset(anchor.Index * ActiveRowHeight + anchor.Offset);
    }
}
