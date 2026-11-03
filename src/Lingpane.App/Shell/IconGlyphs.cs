namespace Lingpane.App.Shell;

/// <summary>
/// 字体图标码位集中定义（界面规则 9.2：XAML 中禁止内联魔法码位）。
/// Win10 用 Segoe MDL2 Assets；Win11 可切 Segoe Fluent Icons，码位兼容。
/// 新增图标必须先在此登记，禁止在 XAML 里直接写 &amp;#xE7F4; 这类值。
/// </summary>
public static class IconGlyphs
{
    public const string Desktop = "\uE7F4";
    public const string Documents = "\uE8A5";
    public const string Download = "\uE896";
    public const string Pictures = "\uE8B9";
    public const string Music = "\uE8D6";
    public const string Video = "\uE714";
    public const string Account = "\uE77B";

    public const string Back = "\uE72B";
    public const string Forward = "\uE72A";
    public const string Up = "\uE74A";
    public const string Folder = "\uE8B7";
    public const string Refresh = "\uE72C";
}
