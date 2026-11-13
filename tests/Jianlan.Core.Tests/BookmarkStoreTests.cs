using Jianlan.Core.Navigation;
using Jianlan.Core.Settings;

namespace Jianlan.Core.Tests;

/// <summary>收藏的读写（FR-7，主页收藏区 FR-1.1.5 同源）。</summary>
public sealed class BookmarkStoreTests
{
    private static BookmarkStore Create() => new(new BookmarkSettings());

    [Fact]
    public void 新增后可按顺序读回()
    {
        BookmarkStore store = Create();

        Assert.Equal(BookmarkAddResult.Added, store.TryAdd(@"C:\a", "甲"));
        Assert.Equal(BookmarkAddResult.Added, store.TryAdd(@"C:\b", "乙"));

        Assert.Equal(["甲", "乙"], store.Items.Select(BookmarkStore.DisplayName));
    }

    [Fact]
    public void 同一路径不重复添加()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\a");

        // 重复收藏要能区分出来，界面才能提示"已在收藏里"并高亮那一行（FR-7.1）
        Assert.Equal(BookmarkAddResult.Duplicate, store.TryAdd(@"C:\a"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void 路径比较忽略大小写()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\Users\Me");

        Assert.True(store.Contains(@"c:\users\me"));
        Assert.Equal(BookmarkAddResult.Duplicate, store.TryAdd(@"c:\users\me"));
    }

    [Fact]
    public void 空白路径不添加()
    {
        BookmarkStore store = Create();

        Assert.Equal(BookmarkAddResult.Invalid, store.TryAdd("   "));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void 未起名时显示路径末段()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\Users\Me\Documents\");

        Assert.Equal("Documents", BookmarkStore.DisplayName(store.Items[0]));
    }

    [Fact]
    public void 盘根未起名时显示整个路径()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\");

        Assert.Equal(@"C:\", BookmarkStore.DisplayName(store.Items[0]));
    }

    [Fact]
    public void 上移下移改变顺序()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\a");
        store.TryAdd(@"C:\b");
        store.TryAdd(@"C:\c");

        store.Move(@"C:\c", -1);
        Assert.Equal([@"C:\a", @"C:\c", @"C:\b"], store.Items.Select(b => b.Path));

        store.Move(@"C:\a", +1);
        Assert.Equal([@"C:\c", @"C:\a", @"C:\b"], store.Items.Select(b => b.Path));
    }

    [Fact]
    public void 越界移动保持原样()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\a");
        store.TryAdd(@"C:\b");

        store.Move(@"C:\a", -1);
        store.Move(@"C:\b", +1);

        Assert.Equal([@"C:\a", @"C:\b"], store.Items.Select(b => b.Path));
    }

    [Fact]
    public void 删除不存在的路径返回false()
    {
        BookmarkStore store = Create();

        Assert.False(store.Remove(@"C:\nope"));
    }

    [Fact]
    public void 主页只取前八项()
    {
        BookmarkStore store = Create();
        for (int i = 0; i < 12; i++) store.TryAdd($@"C:\dir{i:00}");

        Assert.Equal(BookmarkSettings.HomePageLimit, store.HomePageItems.Count);
        Assert.Equal(12, store.Count);
    }

    [Fact]
    public void 达到上限后拒绝新增()
    {
        var settings = new BookmarkSettings();
        var store = new BookmarkStore(settings);

        for (int i = 0; i < BookmarkSettings.MaxItems; i++)
        {
            Assert.Equal(BookmarkAddResult.Added, store.TryAdd($@"C:\dir{i}"));
        }

        Assert.True(store.IsFull);
        // 上限是拒绝，不是静默丢弃（FR-7.9）：界面靠这个返回值提示用户
        Assert.Equal(BookmarkAddResult.Full, store.TryAdd(@"C:\overflow"));
        Assert.Equal(BookmarkSettings.MaxItems, store.Count);
    }

    [Fact]
    public void 归一化去掉空项与重复项()
    {
        var settings = new BookmarkSettings
        {
            Items =
            [
                new BookmarkEntry { Path = @"C:\a" },
                new BookmarkEntry { Path = "   " },
                new BookmarkEntry { Path = @"c:\A" },
            ],
        };

        settings.Normalize();

        Assert.Single(settings.Items);
    }

    // ── 分组（FR-7.3） ──────────────────────────────────────────────────────

    [Fact]
    public void 新建分组后可归入()
    {
        BookmarkStore store = Create();

        Assert.True(store.AddGroup("项目"));
        store.TryAdd(@"C:\a", group: "项目");

        Assert.Single(store.Groups);
        Assert.Equal(["C:\\a"], store.ItemsOf("项目").Select(b => b.Path));
        Assert.Empty(store.ItemsOf(null));
    }

    [Fact]
    public void 分组重名不重复创建且不区分大小写()
    {
        BookmarkStore store = Create();
        store.AddGroup("Project");

        Assert.False(store.AddGroup("project"));
        Assert.Single(store.Groups);
    }

    [Fact]
    public void 空白分组名等同于未分组()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\a", group: "   ");

        Assert.Null(store.Items[0].Group);
        Assert.Empty(store.Groups);
    }

    [Fact]
    public void 重命名分组时组内条目跟着走()
    {
        BookmarkStore store = Create();
        store.AddGroup("旧名");
        store.TryAdd(@"C:\a", group: "旧名");

        Assert.True(store.RenameGroup("旧名", "新名"));

        Assert.Equal("新名", store.Groups[0].Name);
        Assert.Equal("新名", store.Items[0].Group);
        Assert.Single(store.ItemsOf("新名"));
    }

    [Fact]
    public void 重命名成分组已有名字时拒绝()
    {
        BookmarkStore store = Create();
        store.AddGroup("甲");
        store.AddGroup("乙");

        Assert.False(store.RenameGroup("甲", "乙"));
        Assert.Equal(["甲", "乙"], store.Groups.Select(g => g.Name));
    }

    [Fact]
    public void 删除分组时条目落到未分组而不是被删掉()
    {
        BookmarkStore store = Create();
        store.AddGroup("项目");
        store.TryAdd(@"C:\a", group: "项目");

        Assert.True(store.RemoveGroup("项目"));

        Assert.Empty(store.Groups);
        Assert.Equal(1, store.Count);
        Assert.Null(store.Items[0].Group);
    }

    [Fact]
    public void 折叠状态可持久化()
    {
        var settings = new BookmarkSettings();
        var store = new BookmarkStore(settings);
        store.AddGroup("项目");

        store.SetGroupExpanded("项目", false);

        Assert.False(store.IsGroupExpanded("项目"));
        Assert.False(settings.Groups[0].IsExpanded);

        // 重新读一遍设置文件（同一份对象走一次归一化）后折叠状态仍在
        settings.Normalize();
        Assert.False(store.IsGroupExpanded("项目"));
    }

    [Fact]
    public void 默认展开()
    {
        BookmarkStore store = Create();
        store.AddGroup("项目");

        Assert.True(store.IsGroupExpanded("项目"));
    }

    [Fact]
    public void 组内上下移不会跨组()
    {
        BookmarkStore store = Create();
        store.AddGroup("甲");
        store.AddGroup("乙");
        store.TryAdd(@"C:\a1", group: "甲");
        store.TryAdd(@"C:\b1", group: "乙");
        store.TryAdd(@"C:\a2", group: "甲");

        // a2 在"甲"组里已经是最后一个，再往下移没有同组邻居，保持原样
        store.Move(@"C:\a2", +1);
        Assert.Equal([@"C:\a1", @"C:\b1", @"C:\a2"], store.Items.Select(b => b.Path));

        // 上移时与同组的 a1 交换，跨过中间的 b1（视觉上就是"组内往上挪了一行"）
        store.Move(@"C:\a2", -1);
        Assert.Equal([@"C:\a2", @"C:\b1", @"C:\a1"], store.Items.Select(b => b.Path));
        Assert.Equal(["C:\\a2", "C:\\a1"], store.ItemsOf("甲").Select(b => b.Path));
    }

    [Fact]
    public void 拖拽到另一组并落到指定位置()
    {
        BookmarkStore store = Create();
        store.AddGroup("甲");
        store.AddGroup("乙");
        store.TryAdd(@"C:\a1", group: "甲");
        store.TryAdd(@"C:\a2", group: "甲");
        store.TryAdd(@"C:\b1", group: "乙");

        store.MoveTo(@"C:\b1", "甲", 1);

        Assert.Equal("甲", store.Items.First(b => b.Path == @"C:\b1").Group);
        Assert.Equal(["C:\\a1", "C:\\b1", "C:\\a2"], store.ItemsOf("甲").Select(b => b.Path));
        Assert.Empty(store.ItemsOf("乙"));
    }

    [Fact]
    public void 拖进不存在的分组时自动建组()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\a", group: "临时");

        // 条目不能指向一个看不见的分组，否则导航栏里这条收藏凭空消失
        Assert.True(store.ContainsGroup("临时"));
        Assert.Single(store.ItemsOf("临时"));
    }

    [Fact]
    public void 批量清理只删点名的那些()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\keep");
        store.TryAdd(@"C:\gone1");
        store.TryAdd(@"C:\gone2");

        int removed = store.RemoveMany([@"C:\gone1", @"c:\GONE2", @"C:\never"]);

        Assert.Equal(2, removed);
        Assert.Equal([@"C:\keep"], store.Items.Select(b => b.Path));
    }

    [Fact]
    public void 重命名与图标颜色可设置也可清空()
    {
        BookmarkStore store = Create();
        store.TryAdd(@"C:\a");

        Assert.True(store.Rename(@"C:\a", "  自定义  "));
        Assert.True(store.SetIcon(@"C:\a", "\uE8B7"));
        Assert.True(store.SetColor(@"C:\a", "#FF8800"));

        Assert.Equal("自定义", store.Items[0].Name);
        Assert.Equal("\uE8B7", store.Items[0].Icon);
        Assert.Equal("#FF8800", store.Items[0].Color);

        // 清空即回到默认（名字回落路径末段、图标回落系统图标、颜色跟随主题）
        store.Rename(@"C:\a", null);
        store.SetIcon(@"C:\a", null);
        store.SetColor(@"C:\a", "  ");

        Assert.Null(store.Items[0].Name);
        Assert.Null(store.Items[0].Icon);
        Assert.Null(store.Items[0].Color);
        Assert.Equal("a", BookmarkStore.DisplayName(store.Items[0]));
    }

    [Fact]
    public void 分组顺序可调()
    {
        BookmarkStore store = Create();
        store.AddGroup("甲");
        store.AddGroup("乙");
        store.AddGroup("丙");

        store.MoveGroup("丙", -1);

        Assert.Equal(["甲", "丙", "乙"], store.Groups.Select(g => g.Name));
    }

    [Fact]
    public void 归一化补出条目引用但不存在的分组()
    {
        var settings = new BookmarkSettings
        {
            Items = [new BookmarkEntry { Path = @"C:\a", Group = " 手改的组 " }],
        };

        settings.Normalize();

        Assert.Single(settings.Groups);
        Assert.Equal("手改的组", settings.Groups[0].Name);
        Assert.Equal("手改的组", settings.Items[0].Group);
    }

    [Fact]
    public void 分组被截断到上限()
    {
        var settings = new BookmarkSettings();
        for (int i = 0; i < BookmarkSettings.MaxGroups + 5; i++)
        {
            settings.Groups.Add(new BookmarkGroup { Name = $"g{i}" });
        }

        settings.Normalize();

        Assert.Equal(BookmarkSettings.MaxGroups, settings.Groups.Count);
    }
}
