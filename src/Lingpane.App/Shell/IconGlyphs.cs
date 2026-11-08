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

    /// <summary>
    /// 文件夹（标签页图标、收藏里的文件夹项、最近访问）。
    ///
    /// 这里曾经写的是 <c>E8B7</c>——几乎每份"MDL2 图标表"都把 E8B7 标成 Folder，
    /// 但在 <c>Segoe MDL2 Assets</c> 里它其实是一张**纸**（右下角带折角），
    /// 于是目录的标签页图标一直显示成"文件"。ED25 才是真正的文件夹。
    /// 改码位前先用字体把字形画出来看一眼，别照抄表。
    /// </summary>
    public const string Folder = "\uED25";

    public const string Refresh = "\uE72C";

    // 主页（FR-1.1）与收藏（FR-7）新增
    public const string Home = "\uE80F";
    public const string Drive = "\uEDA2";
    public const string File = "\uE7C3";
    public const string History = "\uE81C";
    public const string Star = "\uE734";
    public const string NewFolder = "\uE8F4";
    public const string ChevronDown = "\uE70D";
    public const string ChevronRight = "\uE76C";

    /// <summary>
    /// 工作区（FR-8）。MDL2 的 MapLayers：三层叠放的菱形，正好表示"多份可切换的集合"。
    /// 码位是先渲染出来看过再定的——别照抄图标表，E8B7 的教训。
    /// </summary>
    public const string Workspace = "\uE81E";
}
