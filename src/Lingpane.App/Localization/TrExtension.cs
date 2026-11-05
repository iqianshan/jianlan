using System.Windows.Data;
using System.Windows.Markup;
using Lingpane.Core.Localization;

namespace Lingpane.App.Localization;

/// <summary>
/// XAML 里的文案标记扩展：<c>Text="{loc:Tr Key=Menu.Open}"</c>。
///
/// 它返回的不是字符串而是 <see cref="Binding"/>，绑到 <see cref="LocalizationService"/> 的索引器。
/// 这是"切换语言不重建窗口"的关键：语言一变，索引器触发 PropertyChanged，所有这类绑定同时刷新
/// （<c>x:Static</c> 拿的是编译期常量，换语言必须重建窗口，满足不了 FR-10.4 的 &lt;200ms）。
///
/// 用法注意：因为返回 Binding，在 <c>Setter.Value</c>、<c>DataTrigger</c> 等位置也能直接用；
/// 但属性的默认值、比较值这类"非绑定"场景不适用。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    /// <summary>资源键，形如 <c>Menu.Open</c>。键不存在时界面显示 <c>!键名!</c>。</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key)) return string.Empty;

        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}
