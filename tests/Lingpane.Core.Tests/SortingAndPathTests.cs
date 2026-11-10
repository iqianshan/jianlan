using Lingpane.Core.FileSystem;
using Lingpane.Core.Navigation;
using Lingpane.Core.Settings;

namespace Lingpane.Core.Tests;

/// <summary>
/// 排序与面包屑的边界用例。这两处都是"看起来简单、实际容易出错"的地方：
/// 自然排序的数字段、文件夹优先与降序的交互、UNC 路径的层级拆分。
/// </summary>
public sealed class SortingAndPathTests
{
    private static FileEntry File(string name, long size = 0, DateTime? date = null, bool hidden = false)
        => new(name, @"C:\tmp", isDirectory: false, size, date ?? new DateTime(2026, 1, 1), hidden);

    private static FileEntry Dir(string name)
        => new(name, @"C:\tmp", isDirectory: true, 0, new DateTime(2026, 1, 1));

    private static List<FileEntry> Sort(IEnumerable<FileEntry> items, EntrySort sort)
    {
        var list = items.ToList();
        list.Sort(sort.ToComparison());
        return list;
    }

    [Fact]
    public void 自然排序_数字按数值比较()
    {
        var list = Sort(
            [File("file10.txt"), File("file2.txt"), File("file1.txt")],
            EntrySort.Default);

        Assert.Equal(["file1.txt", "file2.txt", "file10.txt"], list.Select(x => x.Name));
    }

    [Fact]
    public void 自然排序_前导零与纯数字名()
    {
        var list = Sort([File("a100"), File("a020"), File("a9")], EntrySort.Default);
        Assert.Equal(["a9", "a020", "a100"], list.Select(x => x.Name));
    }

    [Fact]
    public void 文件夹优先_降序时目录仍在最前()
    {
        var list = Sort(
            [File("z.txt"), Dir("a_dir"), File("a.txt")],
            new EntrySort(SortFieldKind.Name, Descending: true, FoldersFirst: true));

        Assert.Equal("a_dir", list[0].Name);
        Assert.True(list[0].IsDirectory);
        // 文件之间才是降序
        Assert.Equal(["z.txt", "a.txt"], list.Skip(1).Select(x => x.Name));
    }

    [Fact]
    public void 文件夹优先关闭时_按名称混排()
    {
        var list = Sort(
            [Dir("b_dir"), File("a.txt")],
            new EntrySort(SortFieldKind.Name, Descending: false, FoldersFirst: false));

        Assert.Equal(["a.txt", "b_dir"], list.Select(x => x.Name));
    }

    [Fact]
    public void 按大小降序()
    {
        var list = Sort(
            [File("small", 10), File("big", 5000), File("mid", 900)],
            new EntrySort(SortFieldKind.Size, Descending: true, FoldersFirst: false));

        Assert.Equal(["big", "mid", "small"], list.Select(x => x.Name));
    }

    [Fact]
    public void 按日期升序()
    {
        var list = Sort(
            [
                File("new", date: new DateTime(2026, 3, 1)),
                File("old", date: new DateTime(2024, 1, 1)),
                File("mid", date: new DateTime(2025, 6, 1)),
            ],
            new EntrySort(SortFieldKind.Date, Descending: false, FoldersFirst: false));

        Assert.Equal(["old", "mid", "new"], list.Select(x => x.Name));
    }

    [Fact]
    public void 点击同一列切换升降序_点击其他列改为升序()
    {
        var sort = EntrySort.Default with { Descending = true };

        var same = sort.Toggle(SortFieldKind.Name);
        Assert.True(same.Descending == false);

        var other = sort.Toggle(SortFieldKind.Size);
        Assert.Equal(SortFieldKind.Size, other.Field);
        Assert.False(other.Descending);
    }

    [Fact]
    public void 显示扩展名开关只影响文件()
    {
        var file = File("report.txt");
        var dir = new FileEntry("folder.name", @"C:\tmp", isDirectory: true, 0, DateTime.Now);

        FileEntry.SetShowExtensions([file, dir], show: false);
        try
        {
            Assert.Equal("report", file.DisplayName);
            // 目录名里的点不是扩展名，不能截断
            Assert.Equal("folder.name", dir.DisplayName);
        }
        finally
        {
            FileEntry.SetShowExtensions([file, dir], show: true);
            Assert.Equal("report.txt", file.DisplayName);
        }
    }

    [Fact]
    public void 面包屑_盘符路径逐级拆分()
    {
        var segments = Breadcrumb.Build(@"C:\Users\何彦征\Documents");

        Assert.Equal(["C:", "Users", "何彦征", "Documents"], segments.Select(s => s.Label));
        Assert.Equal(@"C:\", segments[0].FullPath);
        Assert.Equal(@"C:\Users\何彦征\Documents", segments[^1].FullPath);
    }

    [Fact]
    public void 面包屑_末尾分隔符不产生空段()
    {
        var segments = Breadcrumb.Build(@"C:\Windows\");
        Assert.Equal(["C:", "Windows"], segments.Select(s => s.Label));
    }

    [Fact]
    public void 面包屑_盘符根目录只有一段且可点击()
    {
        var segments = Breadcrumb.Build(@"D:\");
        Assert.Single(segments);
        Assert.Equal(@"D:\", segments[0].FullPath);
    }

    [Fact]
    public void 面包屑_UNC共享作为一整段()
    {
        var segments = Breadcrumb.Build(@"\\nas\share\docs\2026");

        Assert.Equal([@"\\nas\share", "docs", "2026"], segments.Select(s => s.Label));
        // 第一段必须是共享根，不能是 \\nas（点进去会无权限）
        Assert.Equal(@"\\nas\share", segments[0].FullPath);
        Assert.Equal(@"\\nas\share\docs", segments[1].FullPath);
    }

    // ── 名称比较口径（FR-5.2.3）────────────────────────────────────────────

    [Fact]
    public void 关掉自然排序时数字按字符比较()
    {
        EntrySort sort = new(SortFieldKind.Name, Descending: false, FoldersFirst: true, Natural: false);

        var list = Sort([File("file10.txt"), File("file2.txt")], sort);

        // 字典序：'1' < '2'，所以 file10 在前——这正是关掉自然排序后要的结果
        Assert.Equal(["file10.txt", "file2.txt"], list.Select(e => e.Name));
    }

    [Fact]
    public void 打开自然排序时数字按数值比较()
    {
        EntrySort sort = new(SortFieldKind.Name, Descending: false, FoldersFirst: true, Natural: true);

        var list = Sort([File("file10.txt"), File("file2.txt")], sort);

        Assert.Equal(["file2.txt", "file10.txt"], list.Select(e => e.Name));
    }

    [Fact]
    public void 区分大小写会改变大小写混合名字的顺序()
    {
        EntrySort insensitive = new(SortFieldKind.Name, Descending: false, FoldersFirst: false, CaseSensitive: false);
        EntrySort sensitive = new(SortFieldKind.Name, Descending: false, FoldersFirst: false, CaseSensitive: true);

        FileEntry[] items = [File("Zebra.txt"), File("apple.txt")];

        // 不区分大小写：Z 与 a 先折成同一种大小再比，苹果在前
        Assert.Equal(["apple.txt", "Zebra.txt"], Sort(items, insensitive).Select(e => e.Name));

        // 区分大小写：按码位，'Z'(90) < 'a'(97)，斑马在前
        Assert.Equal(["Zebra.txt", "apple.txt"], Sort(items, sensitive).Select(e => e.Name));

        // 同名的两份副本在两种口径下都不为"相等"，结果仍由码位定序决定
        Assert.NotEqual(0, NameComparer.Compare("Readme.txt", "readme.txt", natural: true, caseSensitive: true));
        Assert.Equal(0, NameComparer.Compare("A.txt", "A.txt", natural: true, caseSensitive: true));
    }

    [Fact]
    public void 自然排序与大小写选项都不影响文件夹优先()
    {
        EntrySort sort = new(SortFieldKind.Name, Descending: true, FoldersFirst: true,
            Natural: false, CaseSensitive: true);

        var list = Sort([File("a.txt"), Dir("ZFolder"), File("b.txt")], sort);

        Assert.Equal("ZFolder", list[0].Name);
    }
}
