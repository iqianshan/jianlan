namespace Jianlan.App.Shell;

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
    /// 行内「更多操作」（侧栏收藏 / 工作区行）。E712 是三点横排的 More，
    /// 资源管理器侧栏与 Edge 的行尾菜单都用同一个字形——用户不需要认新符号。
    /// </summary>
    public const string More = "\uE712";

    /// <summary>
    /// 工作区（FR-8）。MDL2 的 MapLayers：三层叠放的菱形，正好表示"多份可切换的集合"。
    /// 码位是先渲染出来看过再定的——别照抄图标表，E8B7 的教训。
    /// </summary>
    public const string Workspace = "\uE81E";

    /// <summary>回收站（FR-1.2.3）。MDL2 里没有垃圾桶这个对象本身，E74D 是最接近的"带盖桶"。</summary>
    public const string RecycleBin = "\uE74D";

    /// <summary>网络（FR-1.2.3）。E968 是 MDL2 的 Network：一台机器接着一条线。</summary>
    public const string Network = "\uE968";

    /// <summary>
    /// 此电脑（FR-1.2.2）。E977 是 MDL2 的 PC1：一台主机加一台显示器，
    /// 与资源管理器"此电脑"的语义一致。已渲染确认（EC4E 是纯显示器、E7F8 是笔记本、
    /// EDA2 是单块硬盘，都不如它"这台机器上所有盘"的意思准）。
    /// </summary>
    public const string ThisPc = "\uE977";

    /// <summary>
    /// 齿轮（FR-1.2.9）：侧栏「位置」标题行尾的"选择显示哪些位置"入口。
    /// E713 是 MDL2 的 Setting，就是系统设置里那个齿轮，用户不需要额外学习。
    /// </summary>
    public const string Settings = "\uE713";

    // ── 窗口键（FR-10.11）────────────────────────────────────────────────
    // 这三个是 MDL2 的 Chrome 系列，字形就是 Windows 自己的 10×10 细线，
    // 换成 E8BB 之类的普通图形会在 10px 下糊成一团。

    /// <summary>最小化：一条横线。</summary>
    public const string WindowMinimize = "\uE921";

    /// <summary>最大化：一个空心方框。</summary>
    public const string WindowMaximize = "\uE922";

    /// <summary>还原：两个叠放的方框。最大化态时替换掉最大化图标。</summary>
    public const string WindowRestore = "\uE923";

    /// <summary>关闭：一个叉。</summary>
    public const string WindowClose = "\uE106";
}
