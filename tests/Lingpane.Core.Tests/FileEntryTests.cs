using Lingpane.Core.FileSystem;

namespace Lingpane.Core.Tests;

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
}
