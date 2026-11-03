using System.Globalization;
using System.Windows.Data;
using Lingpane.App.Shell;

namespace Lingpane.App.Converters;

/// <summary>
/// FileEntry.IconKey → ImageSource。命中缓存时只是一次字典查找。
/// 用 x:Static 单例绑定，避免每次解析 DataTemplate 时新建转换器。
/// </summary>
public sealed class IconKeyToImageConverter : IValueConverter
{
    public static readonly IconKeyToImageConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ShellIconProvider.Get(value as string ?? ".file");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
