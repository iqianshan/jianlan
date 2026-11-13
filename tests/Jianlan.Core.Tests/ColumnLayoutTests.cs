using Jianlan.Core.Navigation;
using Jianlan.Core.Settings;

namespace Jianlan.Core.Tests;

/// <summary>列显示配置（FR-6.4）：顺序归一、槽位分配、档位判定与设置迁移。</summary>
public sealed class ColumnLayoutTests
{
    // ── 顺序归一 ────────────────────────────────────────────────────────────

    [Fact]
    public void 空顺序表补全为默认顺序()
    {
        var profile = new ColumnProfileSettings { Order = [] };
        profile.Normalize();

        Assert.Equal(["date", "type", "size"], profile.Order);
    }

    [Fact]
    public void 顺序表里的未知项与重复项被剔除()
    {
        var profile = new ColumnProfileSettings { Order = ["size", "bogus", "size", "date"] };
        profile.Normalize();

        Assert.Equal(["size", "date", "type"], profile.Order);
    }

    [Fact]
    public void 名称列写进顺序表也会被剔除()
    {
        // 名称列恒在首位、不可隐藏；允许它出现在顺序表里只会让槽位算法多一个分支
        var profile = new ColumnProfileSettings { Order = ["name", "type", "date", "size"] };
        profile.Normalize();

        Assert.DoesNotContain("name", profile.Order);
        Assert.Equal(3, profile.Order.Count);
    }

    [Fact]
    public void 顺序表大小写不敏感()
    {
        var profile = new ColumnProfileSettings { Order = ["SIZE", "Date"] };
        profile.Normalize();

        Assert.Equal(["size", "date", "type"], profile.Order);
    }

    // ── 槽位 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 默认顺序下三个数据列依次占槽三位到五位()
    {
        var profile = new ColumnProfileSettings();
        profile.Normalize();

        Assert.Equal(ColumnLayout.NameSlot, profile.SlotOf(PaneColumn.Name));
        Assert.Equal(3, profile.SlotOf(PaneColumn.Date));
        Assert.Equal(4, profile.SlotOf(PaneColumn.Type));
        Assert.Equal(5, profile.SlotOf(PaneColumn.Size));
    }

    [Fact]
    public void 换序后槽位跟着变且不重复()
    {
        var profile = new ColumnProfileSettings { Order = ["size", "date", "type"] };
        profile.Normalize();

        Assert.Equal(3, profile.SlotOf(PaneColumn.Size));
        Assert.Equal(4, profile.SlotOf(PaneColumn.Date));
        Assert.Equal(5, profile.SlotOf(PaneColumn.Type));

        // 两列落进同一个槽位会在界面上重叠显示，这条断言就是防这个
        PaneColumn[] all = [PaneColumn.Name, PaneColumn.Date, PaneColumn.Type, PaneColumn.Size];
        Assert.Equal(all.Length, all.Select(profile.SlotOf).Distinct().Count());
    }

    [Fact]
    public void 隐藏的列仍占自己那个槽位()
    {
        // 槽位与显隐解耦：让隐藏列让出槽位，意味着每次显隐都要重算三个键，出错面大一倍
        var profile = new ColumnProfileSettings { Order = ["size", "date", "type"], ShowSize = false };
        profile.Normalize();

        Assert.Equal(3, profile.SlotOf(PaneColumn.Size));
        Assert.Equal(4, profile.SlotOf(PaneColumn.Date));
    }

    // ── 移动 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 移到边界之外时原地不动()
    {
        var profile = new ColumnProfileSettings();
        profile.Normalize();

        Assert.False(profile.Move(PaneColumn.Date, -1));   // 已在最左
        Assert.False(profile.Move(PaneColumn.Size, 1));    // 已在最右
        Assert.Equal(["date", "type", "size"], profile.Order);

        Assert.True(profile.Move(PaneColumn.Size, -1));
        Assert.Equal(["date", "size", "type"], profile.Order);
    }

    // ── 显隐 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 名称列不可隐藏()
    {
        var profile = new ColumnProfileSettings();
        profile.SetVisible(PaneColumn.Name, false);

        Assert.True(profile.IsVisible(PaneColumn.Name));
        Assert.True(profile.IsVisible(PaneColumn.Date));   // 关名称列不该顺手把别的列关掉
    }

    [Fact]
    public void 三个数据列可以全部关掉()
    {
        // 剩下的是文件名——那是"列表视图"，不是坏配置
        var profile = new ColumnProfileSettings();
        profile.SetVisible(PaneColumn.Date, false);
        profile.SetVisible(PaneColumn.Type, false);
        profile.SetVisible(PaneColumn.Size, false);

        Assert.False(profile.IsVisible(PaneColumn.Date));
        Assert.False(profile.IsVisible(PaneColumn.Type));
        Assert.False(profile.IsVisible(PaneColumn.Size));
        Assert.True(profile.IsVisible(PaneColumn.Name));
    }

    // ── 宽度 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-20)]
    [InlineData(10)]      // 低于 56 的下限
    [InlineData(9999)]    // 高于 400 的上限
    public void 非法列宽归一后落在合法区间(double raw)
    {
        var profile = new ColumnProfileSettings { DateWidth = raw };
        profile.Normalize();

        Assert.InRange(profile.DateWidth, AppSettings.MinColumnWidth, AppSettings.MaxColumnWidth);
    }

    // ── 档位判定 ────────────────────────────────────────────────────────────

    private static readonly KnownFolder[] Folders =
    [
        new(KnownFolderKind.Documents, @"C:\Users\me\Documents"),
        new(KnownFolderKind.Downloads, @"C:\Users\me\Downloads"),
        new(KnownFolderKind.Pictures, @"D:\Pictures"),
    ];

    [Fact]
    public void 已知文件夹本身与它的子目录归同一档()
    {
        Assert.Equal(ColumnProfileKind.Documents, ColumnProfiles.Resolve(@"C:\Users\me\Documents", Folders));
        Assert.Equal(ColumnProfileKind.Documents, ColumnProfiles.Resolve(@"C:\Users\me\Documents\工作", Folders));
    }

    [Fact]
    public void 末尾多余的分隔符不影响判定()
    {
        Assert.Equal(ColumnProfileKind.Downloads, ColumnProfiles.Resolve(@"C:\Users\me\Downloads\", Folders));
    }

    [Fact]
    public void 前缀相同但不同目录不算命中()
    {
        // D:\Pictures2 不是 D:\Pictures 的子目录，只比字符串会误判
        Assert.Equal(ColumnProfileKind.Default, ColumnProfiles.Resolve(@"D:\Pictures2", Folders));
    }

    [Fact]
    public void 路径大小写不敏感()
    {
        Assert.Equal(ColumnProfileKind.Pictures, ColumnProfiles.Resolve(@"d:\pictures\2026", Folders));
    }

    [Fact]
    public void 嵌套的已知文件夹取更具体的那一档()
    {
        KnownFolder[] nested =
        [
            new(KnownFolderKind.Documents, @"C:\Users\me\Documents"),
            new(KnownFolderKind.Downloads, @"C:\Users\me\Documents\Downloads"),
        ];

        Assert.Equal(ColumnProfileKind.Downloads,
            ColumnProfiles.Resolve(@"C:\Users\me\Documents\Downloads\setup", nested));
        Assert.Equal(ColumnProfileKind.Documents,
            ColumnProfiles.Resolve(@"C:\Users\me\Documents\notes", nested));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"E:\Elsewhere")]
    public void 空路径与无关目录回落到默认档(string? path)
    {
        Assert.Equal(ColumnProfileKind.Default, ColumnProfiles.Resolve(path, Folders));
    }

    [Fact]
    public void 没有已知文件夹时一律默认档()
    {
        Assert.Equal(ColumnProfileKind.Default, ColumnProfiles.Resolve(@"C:\Users\me\Documents", null));
    }

    // ── 设置层 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 读取没有条目时回落默认档且不写字典()
    {
        var layout = new LayoutSettings();

        ColumnProfileSettings profile = layout.Get(ColumnProfileKind.Pictures);

        Assert.Equal(AppSettings.DefaultColumnDateWidth, profile.DateWidth);
        Assert.DoesNotContain("pictures", layout.Columns.Keys);
    }

    [Fact]
    public void 新建档位继承默认档的宽度与顺序()
    {
        var layout = new LayoutSettings();
        layout.Standard.DateWidth = 200;
        layout.Standard.ShowSize = false;

        ColumnProfileSettings pictures = layout.GetOrAdd(ColumnProfileKind.Pictures);

        Assert.Equal(200, pictures.DateWidth);
        Assert.False(pictures.ShowSize);

        // 是副本，不是同一个实例：改图片档不该动到默认档
        pictures.DateWidth = 120;
        Assert.Equal(200, layout.Standard.DateWidth);
    }

    [Fact]
    public void 旧版三个宽度字段迁移进默认档()
    {
        var settings = new AppSettings();
        settings.Layout.Extra = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["columnDateWidth"] = System.Text.Json.JsonDocument.Parse("220").RootElement,
            ["columnTypeWidth"] = System.Text.Json.JsonDocument.Parse("130").RootElement,
        };

        settings.Normalize();

        Assert.Equal(220, settings.Layout.Standard.DateWidth);
        Assert.Equal(130, settings.Layout.Standard.TypeWidth);

        // 迁移完要从 Extra 里摘掉，否则写回时文件里会有两份真值
        Assert.False(settings.Layout.Extra.ContainsKey("columnDateWidth"));
    }

    [Fact]
    public void 档位名大小写混写也能读到()
    {
        var settings = new AppSettings();
        settings.Layout.Columns["Pictures"] = new ColumnProfileSettings { DateWidth = 180 };

        settings.Normalize();

        Assert.Equal(180, settings.Layout.Get(ColumnProfileKind.Pictures).DateWidth);
        Assert.Contains("pictures", settings.Layout.Columns.Keys);
        Assert.DoesNotContain("Pictures", settings.Layout.Columns.Keys);
    }

    [Fact]
    public void 重置列配置清空所有档位()
    {
        var layout = new LayoutSettings();
        layout.GetOrAdd(ColumnProfileKind.Pictures).DateWidth = 300;
        layout.Standard.ShowType = false;

        layout.ResetColumns();

        Assert.Single(layout.Columns);
        Assert.True(layout.Standard.ShowType);
        Assert.Equal(AppSettings.DefaultColumnDateWidth, layout.Standard.DateWidth);
    }
}
