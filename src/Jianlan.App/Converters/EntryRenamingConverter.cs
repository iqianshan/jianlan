using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

using Jianlan.Core.FileSystem;

namespace Jianlan.App.Converters;

/// <summary>
/// 行模板里“文字 ↔ 编辑框”的显隐开关：把这一行的条目与窗格正在编辑的那个条目比一比。
///
/// 为什么用 MultiBinding 而不是给每个条目加一个 <c>IsRenaming</c> 属性：
/// 列表是 Recycling 虚拟化，行容器会被复用给别的条目；编辑态若挂在条目上，
/// 滚动、排序、刷新之后很容易出现“编辑框画在了另一行上”。挂在窗格上只有一个真值，
/// 行容器复用到哪里都由同一个比较决定，不会错位。
///
/// <c>ConverterParameter=hide</c> 用于文字那一份：同一个比较，反过来取显隐。
/// </summary>
public sealed class EntryRenamingConverter : IMultiValueConverter
{
    public static EntryRenamingConverter Instance { get; } = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool equal = values.Length == 2 && ReferenceEquals(values[0], values[1]);
        bool hide = string.Equals(parameter as string, "hide", StringComparison.Ordinal);

        return equal != hide ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();  // i18n-ignore：开发者异常，不进界面
}
