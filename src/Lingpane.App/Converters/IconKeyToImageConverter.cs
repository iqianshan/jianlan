using System.Globalization;
using System.Windows.Data;
using Lingpane.App.Shell;

namespace Lingpane.App.Converters;

/// <summary>
/// FileEntry.IconToken → ImageSource。命中缓存时只是一次字典查找。
/// 用 x:Static 单例绑定，避免每次解析 DataTemplate 时新建转换器。
///
/// token 有两种形态：<c>.txt</c> 这类扩展名键，和 <c>#12</c> 这类 shell 图标序号
/// （目录在后台解析出真实图标后写入）。
/// </summary>
public sealed class IconKeyToImageConverter : IValueConverter
{
    public static readonly IconKeyToImageConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ShellIconProvider.GetByToken(value as string ?? ".file");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
