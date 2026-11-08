using Lingpane.App.Shell;
using Lingpane.Core.Localization;

namespace Lingpane.App.ViewModels;

/// <summary>
/// 收藏项的自定义外观预设（FR-7.5「设置图标 / 设置颜色」）。
///
/// 为什么是预设而不是取色器 / 图标浏览器：
///   · 颜色要能进 settings.json（<c>#RRGGBB</c>）、要在深浅两套主题下都可读，
///     给一个任意取色器，用户很容易选到和背景同色的值；
///   · 图标只认 <c>Segoe MDL2 Assets</c> 的字形码位，开放输入等于把字体细节泄露给用户。
/// 每条都带一个本地化名字：菜单里显示名字而不是光秃秃的色块——色块对色弱用户没有信息量。
///
/// 列表每次访问都重新拼：名字取自当前语言，切成英文之后重新打开右键菜单就是英文的。
/// </summary>
public static class BookmarkPresets
{
    public sealed record ColorPreset(string Name, string Hex);

    public sealed record IconPreset(string Name, string Glyph);

    /// <summary>六个颜色，取自 Fluent 的强调色阶，深浅主题下都够亮。</summary>
    public static IReadOnlyList<ColorPreset> Colors { get; } =
    [
        new(Strings.Bookmark_Color_Red, "#E81123"),
        new(Strings.Bookmark_Color_Orange, "#F7630C"),
        new(Strings.Bookmark_Color_Green, "#107C10"),
        new(Strings.Bookmark_Color_Blue, "#0078D4"),
        new(Strings.Bookmark_Color_Purple, "#8764B8"),
        new(Strings.Bookmark_Color_Gray, "#8A8A8E"),
    ];

    /// <summary>六个图标。字形码位集中在 <see cref="IconGlyphs"/>，这里只挑出来。</summary>
    public static IReadOnlyList<IconPreset> Icons { get; } =
    [
        new(Strings.Bookmark_Icon_Folder, IconGlyphs.Folder),
        new(Strings.Bookmark_Icon_Star, IconGlyphs.Star),
        new(Strings.Bookmark_Icon_Home, IconGlyphs.Home),
        new(Strings.Bookmark_Icon_Drive, IconGlyphs.Drive),
        new(Strings.Bookmark_Icon_Download, IconGlyphs.Download),
        new(Strings.Bookmark_Icon_Pictures, IconGlyphs.Pictures),
    ];
}
