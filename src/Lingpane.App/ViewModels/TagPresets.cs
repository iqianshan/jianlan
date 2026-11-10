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

    /// <summary>
    /// 八色，顺序与 <see cref="TagPalette.All"/> 一致（位序即顺序）。
    ///
    /// 每次访问重新拼：颜色名是本地化文案，缓存成静态字段后切语言就再也拿不到新名字；
    /// 改名（FR-4.1.7）之后同理——下一次打开右键菜单就该是用户写的新名字。
    /// 八项、每项一个 switch，代价可以忽略；真正贵的资源（画刷、图片）才值得缓存。
    /// </summary>
    public static IReadOnlyList<ColorPreset> Colors => Build();

    /// <summary>
    /// 某一色当前显示的名字：用户改过就用改过的，否则用界面语言里的默认名。
    ///
    /// 单独开一个方法给"只有一个颜色名"的地方用（状态栏、结果页标题、侧栏行）——
    /// 那些地方不该为此去建整张表。
    /// </summary>
    public static string NameOf(TagColors color)
    {
        foreach (ColorPreset preset in Colors)
        {
            if (preset.Value == color) return preset.Name;
        }

        return string.Empty;
    }

    /// <summary>
    /// 某一色的**默认**名字（不含自定义名）。设置页要用它判断"用户写的这个名字是不是就是默认值"：
    /// 是的话不写进设置，这样以后切语言，这一色仍然跟着语言走。
    /// </summary>
    public static string DefaultNameOf(TagColors color) => color switch
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

    private static List<ColorPreset> Build()
    {
        IReadOnlyDictionary<string, string> custom = App.Settings.Appearance.TagColorNames;
        var list = new List<ColorPreset>(TagPalette.All.Count);

        foreach (TagColors color in TagPalette.All)
        {
            string name = DefaultNameOf(color);

            // 自定义名（FR-4.1.7）优先。写在最后一步而不是替换 switch：
            // 没改过名的颜色仍要跟着界面语言走，改过名的才固定成用户写的那句话。
            if (custom.TryGetValue(color.ToString(), out string? renamed) && !string.IsNullOrWhiteSpace(renamed))
            {
                name = renamed;
            }

            list.Add(new ColorPreset(color, name, TagPalette.Hex(color)));
        }

        return list;
    }
}
