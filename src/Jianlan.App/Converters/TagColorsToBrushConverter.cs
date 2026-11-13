using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

using Jianlan.Core.Tagging;

namespace Jianlan.App.Converters;

/// <summary>
/// 标签颜色 → 画刷（FR-4.1.4）。行模板里那个 8px 圆点用它上色。
///
/// 颜色是**固定的**，不跟主题走：用户点的是"红色"，深色主题下必须还是那个红，
/// 换一档亮红会让人觉得"我打的标记怎么变了"。"黑"在深色背景下看不见的问题
/// 由圆点的一圈描边解决（见 <c>RowTemplates.xaml</c>），不是把它偷偷换成灰。
///
/// 返回值缓存且冻结：行模板在滚动时会被反复求值，每次都 new 一个 SolidColorBrush
/// 等于给滚动加垃圾回收压力（NF-05 的教训）。
/// </summary>
public sealed class TagColorsToBrushConverter : IValueConverter
{
    public static TagColorsToBrushConverter Instance { get; } = new();

    private static readonly Dictionary<TagColors, SolidColorBrush> Cache = [];

    private static readonly object Gate = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TagColors colors) return null;

        TagColors primary = TagPalette.Primary(colors);

        // None：不返回透明画刷而是 null——圆点连填充都没有，等于不存在，
        // 不必再为它绑一个 Visibility
        if (primary == TagColors.None) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(primary, out SolidColorBrush? cached)) return cached;

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(TagPalette.Hex(primary)));
            brush.Freeze();
            Cache[primary] = brush;
            return brush;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
