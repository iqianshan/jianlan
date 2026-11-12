using System.Globalization;
using Lingpane.Core.Localization;
using Lingpane.Core.Settings;
using Lingpane.Core.Tagging;

namespace Lingpane.App.ViewModels;

/// <summary>
/// 标签的菜单项与显示名（FR-4.1.1 / FR-4.1.7）。
///
/// 名单不再是写死的八色，而是**设置里的标签定义**（<see cref="AppearanceSettings.Tags"/>）：
/// 用户新建一个标签就多一项，删除就少一项。顺序即定义顺序，也就是侧栏与右键菜单里的顺序。
///
/// 与 <see cref="BookmarkPresets"/> 同一个理由：给预设而不是取色器——任意颜色在深浅两套主题下
/// 很容易选到读不出来的值，而色板上的三十来个槽位用户扫一眼就能挑。
///
/// 每次访问都重新拼：名字是本地化文案、还可能被用户改过，缓存成静态字段后就再也拿不到新名字。
/// </summary>
public static class TagPresets
{
    public sealed record ColorPreset(TagColors Value, string Name, string Hex, int Slot);

    /// <summary>当前启用的标签，按设置里的顺序。</summary>
    public static IReadOnlyList<ColorPreset> Colors => Build();

    /// <summary>
    /// 某一色当前显示的名字：用户改过就用改过的，否则用界面语言里的默认名；已删除的标签
    /// 也仍然给一个名字——结果页与状态栏还得把"这是哪一色"说清楚。
    /// </summary>
    public static string NameOf(TagColors color)
    {
        int slot = TagPalette.SlotOf(color);

        foreach (TagLabelSetting item in Definitions())
        {
            if (item.Slot != slot) continue;
            if (!string.IsNullOrWhiteSpace(item.Name)) return item.Name!.Trim();
            break;
        }

        return DefaultNameOf(color);
    }

    /// <summary>
    /// 某一色的**默认**名字（不含自定义名）。设置页要用它判断"用户写的这个名字是不是就是默认值"：
    /// 是的话不写进设置，这样以后切语言，这一色仍然跟着语言走。
    ///
    /// 前八个槽位有语言资源里的"红/橙/…"；第九位起没有对应的日常说法，用"标签 9"这样的占位，
    /// 用户新建时改成自己要的名字。
    /// </summary>
    public static string DefaultNameOf(TagColors color) => TagPalette.SlotOf(color) switch
    {
        0 => Strings.Tag_Color_Red,
        1 => Strings.Tag_Color_Orange,
        2 => Strings.Tag_Color_Yellow,
        3 => Strings.Tag_Color_Green,
        4 => Strings.Tag_Color_Blue,
        5 => Strings.Tag_Color_Purple,
        6 => Strings.Tag_Color_Gray,
        7 => Strings.Tag_Color_Black,
        int slot => string.Format(CultureInfo.CurrentCulture, Strings.Tag_Custom_DefaultName, slot + 1),
    };

    /// <summary>
    /// 设置里存的原始名字（null = 没改过，跟随语言）。设置页编辑时用它回填输入框：
    /// 回填默认名会让用户以为"这个名字已经存下来了"，而切语言时又会莫名其妙变回去。
    /// </summary>
    public static string? CustomNameOf(TagColors color)
    {
        int slot = TagPalette.SlotOf(color);

        foreach (TagLabelSetting item in Definitions())
        {
            if (item.Slot == slot) return string.IsNullOrWhiteSpace(item.Name) ? null : item.Name!.Trim();
        }

        return null;
    }

    /// <summary>某个槽位是否已经有标签占着（新增标签时跳过这些）。</summary>
    public static bool IsSlotTaken(int slot)
    {
        foreach (TagLabelSetting item in Definitions())
        {
            if (item.Slot == slot) return true;
        }

        return false;
    }

    /// <summary>第一个还空着的槽位；0 表示 32 个都用完了。</summary>
    public static int NextFreeSlot()
    {
        for (int slot = 0; slot < TagPalette.SlotCount; slot++)
        {
            if (!IsSlotTaken(slot)) return slot;
        }

        return -1;
    }

    private static IReadOnlyList<TagLabelSetting> Definitions()
    {
        IReadOnlyList<TagLabelSetting> defined = App.Settings.Appearance.Tags;
        if (defined.Count > 0) return defined;

        // 兜底：设置还没归一化（首个窗口构造得很早，或单测直接 new 出来）时给默认八色。
        // 返回空列表会让右键菜单弹出一个空子菜单，那比多显示几项难受得多。
        return
        [
            .. Enumerable.Range(0, AppearanceSettings.DefaultTagCount)
                .Select(slot => new TagLabelSetting { Slot = slot }),
        ];
    }

    private static List<ColorPreset> Build()
    {
        IReadOnlyList<TagLabelSetting> definitions = Definitions();
        var list = new List<ColorPreset>(definitions.Count);

        foreach (TagLabelSetting item in definitions)
        {
            TagColors value = TagPalette.FromSlot(item.Slot);
            string name = string.IsNullOrWhiteSpace(item.Name) ? DefaultNameOf(value) : item.Name!.Trim();

            list.Add(new ColorPreset(value, name, TagPalette.Hex(value), item.Slot));
        }

        return list;
    }
}
