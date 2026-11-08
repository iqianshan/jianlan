using Lingpane.Core.Localization;
using Lingpane.Core.Tagging;

namespace Lingpane.App.ViewModels;

/// <summary>
/// 标签颜色的菜单项（FR-4.1.1）。
///
/// 与 <see cref="BookmarkPresets"/> 同一个理由：给预设而不是取色器——任意颜色在深浅两套主题下
/// 很容易选到读不出来的值，而"红/橙/黄/绿/蓝/紫/灰/黑"这八色用户本来就有共识。
/// 颜色名要跟着界面语言走，所以每次访问都重新拼一遍（切成英文后重开右键菜单就是英文的）。
/// </summary>
public static class TagPresets
{
    public sealed record ColorPreset(TagColors Value, string Name, string Hex);

    /// <summary>八色，顺序与 <see cref="TagPalette.All"/> 一致（位序即顺序）。</summary>
    public static IReadOnlyList<ColorPreset> Colors { get; } = Build();

    private static List<ColorPreset> Build()
    {
        var list = new List<ColorPreset>(TagPalette.All.Count);

        foreach (TagColors color in TagPalette.All)
        {
            string name = color switch
            {
                TagColors.Red => Strings.Tag_Color_Red,
                TagColors.Orange => Strings.Tag_Color_Orange,
                TagColors.Yellow => Strings.Tag_Color_Yellow,
                TagColors.Green => Strings.Tag_Color_Green,
                TagColors.Blue => Strings.Tag_Color_Blue,
                TagColors.Purple => Strings.Tag_Color_Purple,
                TagColors.Gray => Strings.Tag_Color_Gray,
                _ => Strings.Tag_Color_Black,
            };

            list.Add(new ColorPreset(color, name, TagPalette.Hex(color)));
        }

        return list;
    }
}
