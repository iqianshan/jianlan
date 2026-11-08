using Lingpane.Core.Navigation;
using Lingpane.Core.Settings;

namespace Lingpane.Core.Tests;

/// <summary>收藏的读写（FR-7，主页收藏区 FR-1.1.5 同源）。</summary>
public sealed class BookmarkStoreTests
{
    private static BookmarkStore Create() => new(new BookmarkSettings());

    [Fact]
    public void 新增后可按顺序读回()
    {
        BookmarkStore store = Create();

        Assert.True(store.Add(@"C:\a", "甲"));
        Assert.True(store.Add(@"C:\b", "乙"));

        Assert.Equal(["甲", "乙"], store.Items.Select(BookmarkStore.DisplayName));
    }

    [Fact]
    public void 同一路径不重复添加()
    {
        BookmarkStore store = Create();
        store.Add(@"C:\a");

        Assert.False(store.Add(@"C:\a"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void 路径比较忽略大小写()
    {
        BookmarkStore store = Create();
        store.Add(@"C:\Users\Me");

        Assert.True(store.Contains(@"c:\users\me"));
        Assert.False(store.Add(@"c:\users\me"));
    }

    [Fact]
    public void 空白路径不添加()
    {
        BookmarkStore store = Create();

        Assert.False(store.Add("   "));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void 未起名时显示路径末段()
    {
        BookmarkStore store = Create();
        store.Add(@"C:\Users\Me\Documents\");

        Assert.Equal("Documents", BookmarkStore.DisplayName(store.Items[0]));
    }

    [Fact]
    public void 盘根未起名时显示整个路径()
    {
        BookmarkStore store = Create();
        store.Add(@"C:\");

        Assert.Equal(@"C:\", BookmarkStore.DisplayName(store.Items[0]));
    }

    [Fact]
    public void 上移下移改变顺序()
    {
        BookmarkStore store = Create();
        store.Add(@"C:\a");
        store.Add(@"C:\b");
        store.Add(@"C:\c");

        store.Move(@"C:\c", -1);
        Assert.Equal([@"C:\a", @"C:\c", @"C:\b"], store.Items.Select(b => b.Path));

        store.Move(@"C:\a", +1);
        Assert.Equal([@"C:\c", @"C:\a", @"C:\b"], store.Items.Select(b => b.Path));
    }

    [Fact]
    public void 越界移动保持原样()
    {
        BookmarkStore store = Create();
        store.Add(@"C:\a");
        store.Add(@"C:\b");

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
        for (int i = 0; i < 12; i++) store.Add($@"C:\dir{i:00}");

        Assert.Equal(BookmarkSettings.HomePageLimit, store.HomePageItems.Count);
        Assert.Equal(12, store.Count);
    }

    [Fact]
    public void 达到上限后拒绝新增()
    {
        var settings = new BookmarkSettings();
        var store = new BookmarkStore(settings);

        for (int i = 0; i < BookmarkSettings.MaxItems; i++) Assert.True(store.Add($@"C:\dir{i}"));

        Assert.True(store.IsFull);
        Assert.False(store.Add(@"C:\overflow"));
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
}
