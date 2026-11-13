using Jianlan.Core.FileSystem;
using Jianlan.Core.Tagging;

namespace Jianlan.Core.Tests;

public class FileEntryTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024, "1 MB")]
    [InlineData(1024L * 1024 * 1024 * 3, "3 GB")]
    public void SizeText_按量级格式化(long bytes, string expected)
    {
        var entry = new FileEntry("a.txt", @"C:\a.txt", false, bytes, DateTime.Now);
        Assert.Equal(expected, entry.SizeText);
    }

    [Fact]
    public void SizeText_目录为空字符串()
    {
        var entry = new FileEntry("docs", @"C:\docs", true, 0, DateTime.Now);
        Assert.Equal(string.Empty, entry.SizeText);
    }

    [Fact]
    public void DateText_当年省略年份()
    {
        var now = DateTime.Now;
        var value = new DateTime(now.Year, 10, 28, 14, 32, 0);
        var entry = new FileEntry("a.txt", @"C:\a.txt", false, 1, value);

        Assert.Equal("10-28 14:32", entry.DateText);
    }

    [Fact]
    public void DateText_跨年显示完整日期()
    {
        var entry = new FileEntry("a.txt", @"C:\a.txt", false, 1, new DateTime(2025, 9, 12, 8, 0, 0));
        Assert.Equal("2025-09-12", entry.DateText);
    }

    [Theory]
    [InlineData("report.PDF", ".pdf")]
    [InlineData("noext", ".file")]
    [InlineData("trailingdot.", ".file")]
    [InlineData(".gitignore", ".file")]
    public void IconKey_按扩展名归一(string name, string expected)
    {
        var entry = new FileEntry(name, @"C:\" + name, false, 0, DateTime.Now);
        Assert.Equal(expected, entry.IconKey);
    }

    [Fact]
    public void IconKey_目录统一为folder()
    {
        var entry = new FileEntry("Photos", @"C:\Photos", true, 0, DateTime.Now);
        Assert.Equal(".folder", entry.IconKey);
    }

    [Fact]
    public void IconToken_默认取IconKey_解析后改用真实图标键并通知一次()
    {
        var entry = new FileEntry("Photos", @"C:\Photos", true, 0, DateTime.Now);
        Assert.Equal(".folder", entry.IconToken);

        int notified = 0;
        entry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileEntry.IconToken)) notified++;
        };

        entry.SetIconToken("#42");
        Assert.Equal("#42", entry.IconToken);
        Assert.Equal(1, notified);
        Assert.Equal(".folder", entry.IconKey);   // 原始扩展名键不能被覆盖，否则回退无路

        // 同值重复设置不应再触发重绘
        entry.SetIconToken("#42");
        Assert.Equal(1, notified);
    }

    // ── 多色标签（FR-4.1.2）──────────────────────────────────────────────

    [Fact]
    public void 多色时色点按位序给出_最多三个()
    {
        var entry = new FileEntry("a.txt", @"C://tmp", false, 0, DateTime.Now);

        entry.SetTags(TagColors.Blue | TagColors.Red);

        // 位序即展示顺序：红在蓝之前，与右键菜单里的排列一致
        Assert.Equal([TagColors.Red, TagColors.Blue], entry.ColorDots);

        entry.SetTags(TagColors.Red | TagColors.Orange | TagColors.Yellow | TagColors.Green | TagColors.Blue);

        // 行内只塞得下三个：按位序保留前三个，且顺序稳定
        Assert.Equal(TagPalette.MaxDots, entry.ColorDots.Count);
        Assert.Equal([TagColors.Red, TagColors.Orange, TagColors.Yellow], entry.ColorDots);
    }

    [Fact]
    public void 去掉标签后色点为空的并触发通知()
    {
        var entry = new FileEntry("a.txt", @"C://tmp", false, 0, DateTime.Now);
        entry.SetTags(TagColors.Red);

        var changed = new List<string?>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        entry.SetTags(TagColors.None);

        Assert.Empty(entry.ColorDots);
        Assert.False(entry.HasTags);
        Assert.Contains(nameof(FileEntry.ColorDots), changed);
    }
}
