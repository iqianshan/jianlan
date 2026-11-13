using System.Reflection;
using Jianlan.Core.Settings;

namespace Jianlan.Core.Tests;

/// <summary>
/// 设置页按页恢复默认（FR-5.3.8 的分页粒度）。
///
/// <see cref="BehaviorSettings"/> 被拆到四页里管辖，四份字段清单必须恰好铺满
/// 这个类型、且互不重叠。这里用反射把三件事钉死：
///   ① 清单里的名字都真实存在（写错名字 = 那一项永远不重置，且没有任何报错）；
///   ② 四份合起来覆盖全部公开属性（新增字段忘了归类，测试变红）；
///   ③ 任意两份没有交集（同一字段归两页，说明归属没想清楚）。
///
/// 后半段的几个用例验证"点这一页不会动到那一页"，那是分页重置存在的全部理由。
/// </summary>
public sealed class SettingsPageFieldTests
{
    private static readonly IReadOnlyList<string>[] Pages =
    [
        AppSettings.ListPageFields,
        AppSettings.ActionPageFields,
        AppSettings.GeneralPageFields,
        AppSettings.AppearancePageFields,
    ];

    /// <summary>不是设置项、不归任何一页：新版写进来的未知字段原样保留。</summary>
    private const string ExtensionDataName = "Extra";

    [Fact]
    public void 清单里的字段名都存在()
    {
        foreach (string name in Pages.SelectMany(p => p))
        {
            Assert.NotNull(typeof(BehaviorSettings).GetProperty(name));
        }
    }

    [Fact]
    public void 四份清单覆盖BehaviorSettings的全部属性()
    {
        string[] declared = typeof(BehaviorSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => name != ExtensionDataName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] covered = Pages
            .SelectMany(p => p)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, covered);
    }

    [Fact]
    public void 任意两份清单没有交集()
    {
        for (int i = 0; i < Pages.Length; i++)
        {
            for (int j = i + 1; j < Pages.Length; j++)
            {
                Assert.Empty(Pages[i].Intersect(Pages[j]));
            }
        }
    }

    // ── 行为：这一页只动自己那几项 ──────────────────────────────────────

    [Fact]
    public void 恢复文件列表页不动操作页的字段()
    {
        var settings = new AppSettings();
        settings.Behavior.ConfirmDelete = false;      // 操作页
        settings.Behavior.OpenFolderInNewTab = true;   // 操作页
        settings.Behavior.DefaultView = DefaultViewKind.List;  // 列表页
        settings.Behavior.ShowHidden = true;           // 列表页

        settings.ResetListPage();

        Assert.Equal(DefaultViewKind.Details, settings.Behavior.DefaultView);
        Assert.False(settings.Behavior.ShowHidden);

        Assert.False(settings.Behavior.ConfirmDelete);
        Assert.True(settings.Behavior.OpenFolderInNewTab);
    }

    [Fact]
    public void 恢复操作页不动列表页的字段()
    {
        var settings = new AppSettings();
        settings.Behavior.ShowHidden = true;
        settings.Behavior.SortBy = SortFieldKind.Size;
        settings.Behavior.ConfirmDelete = false;

        settings.ResetActionPage();

        Assert.True(settings.Behavior.ConfirmDelete);

        Assert.True(settings.Behavior.ShowHidden);
        Assert.Equal(SortFieldKind.Size, settings.Behavior.SortBy);
    }

    [Fact]
    public void 恢复外观页保留标签定义与标签栏位置()
    {
        var settings = new AppSettings();
        settings.Appearance.Tags = [new TagLabelSetting { Slot = 4, Name = "海蓝" }];
        settings.Appearance.TabStripPosition = AppearanceSettings.TabBottom;
        settings.Appearance.Theme = ThemeKind.Dark;
        settings.Behavior.PreviewAutoOpen = false;

        settings.ResetAppearancePage();

        // 外观归位
        Assert.Equal(ThemeKind.System, settings.Appearance.Theme);
        Assert.True(settings.Behavior.PreviewAutoOpen);

        // 标签定义归「标签」页、标签栏位置归「文件列表」页，这一页都不该碰
        Assert.Equal("海蓝", settings.Appearance.Tags.Single(t => t.Slot == 4).Name);
        Assert.Equal(AppearanceSettings.TabBottom, settings.Appearance.TabStripPosition);
    }

    /// <summary>
    /// 隐藏系统标题栏的控件在「外观」页，恢复本页时必须一起回默认值（true）。
    /// 归错页的症状与上面那条相反：控件明明在这一页，点恢复却没有变化。
    /// </summary>
    [Fact]
    public void 恢复外观页重置隐藏系统标题栏()
    {
        var settings = new AppSettings();
        settings.Appearance.ImmersiveTitleBar = false;

        settings.ResetAppearancePage();

        Assert.True(settings.Appearance.ImmersiveTitleBar);
    }

    /// <summary>
    /// 标签栏位置的控件在「文件列表」页，所以"恢复本页默认"必须能重置它。
    /// 归属写错时的症状是：点了恢复，页面上那一项纹丝不动，而另一页悄悄变了。
    /// </summary>
    [Fact]
    public void 恢复文件列表页重置标签栏位置()
    {
        var settings = new AppSettings();
        settings.Appearance.TabStripPosition = AppearanceSettings.TabBottom;
        settings.Appearance.Theme = ThemeKind.Dark;

        settings.ResetListPage();

        Assert.Equal(new AppearanceSettings().TabStripPosition, settings.Appearance.TabStripPosition);

        // 只动这一项，外观页的其它设置不受牵连
        Assert.Equal(ThemeKind.Dark, settings.Appearance.Theme);
    }

    [Fact]
    public void 恢复常规页保留界面语言()
    {
        var settings = new AppSettings();
        settings.General.Language = GeneralSettings.LanguageEnglish;
        settings.General.NewTabPath = @"C:\Temp";
        settings.Behavior.RestoreLastSession = false;

        settings.ResetGeneralPage();

        Assert.Null(settings.General.NewTabPath);
        Assert.True(settings.Behavior.RestoreLastSession);

        Assert.Equal(GeneralSettings.LanguageEnglish, settings.General.Language);
    }

    [Fact]
    public void 恢复隐私页不动标签定义()
    {
        var settings = new AppSettings();
        settings.Appearance.Tags = [new TagLabelSetting { Slot = 4, Name = "海蓝" }];
        settings.Privacy.HistoryLimit = 500;

        settings.ResetPrivacyPage();

        // 标签管理已经搬到独立的「标签」页，这一页不该顺手把用户建的标签清掉
        Assert.Equal("海蓝", settings.Appearance.Tags.Single(t => t.Slot == 4).Name);
        Assert.Equal(new PrivacySettings().HistoryLimit, settings.Privacy.HistoryLimit);
    }

    [Fact]
    public void 恢复标签页回到默认八色且不动标记()
    {
        var settings = new AppSettings();
        settings.Appearance.Tags = [new TagLabelSetting { Slot = 9, Name = "重要" }];

        settings.ResetTagsPage();

        Assert.Equal(AppearanceSettings.DefaultTagCount, settings.Appearance.Tags.Count);
        Assert.All(settings.Appearance.Tags, tag => Assert.Null(tag.Name));
    }

    [Fact]
    public void 恢复文件列表页同时清掉窗格的视图记忆()
    {
        var settings = new AppSettings();
        settings.Layout.PrimaryPane.View = DefaultViewKind.List;
        settings.Layout.SecondaryPane.SortBy = SortFieldKind.Size;

        settings.ResetListPage();

        Assert.Null(settings.Layout.PrimaryPane.View);
        Assert.Null(settings.Layout.SecondaryPane.SortBy);
    }
}
