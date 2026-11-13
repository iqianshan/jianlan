using Jianlan.Core.Navigation;
using Jianlan.Core.Settings;

namespace Jianlan.Core.Tests;

/// <summary>最近访问的读写（FR-1.1.2 / FR-5.3.2）。</summary>
public sealed class VisitHistoryTests
{
    private static VisitHistory Create(int limit = 100)
    {
        var settings = new AppSettings { Privacy = { HistoryLimit = limit } };
        settings.Normalize();
        return new VisitHistory(settings.History, () => settings.Privacy.HistoryLimit);
    }

    [Fact]
    public void 新记录排在最前()
    {
        VisitHistory history = Create();

        history.Record(@"C:\a");
        history.Record(@"C:\b");

        Assert.Equal([@"C:\b", @"C:\a"], history.Entries.Select(e => e.Path));
    }

    [Fact]
    public void 重复访问只保留一条并移到最前()
    {
        VisitHistory history = Create();

        history.Record(@"C:\a");
        history.Record(@"C:\b");
        history.Record(@"C:\a");

        Assert.Equal(2, history.Count);
        Assert.Equal(@"C:\a", history.Entries[0].Path);
    }

    [Fact]
    public void 大小写不同视为同一目录()
    {
        VisitHistory history = Create();

        history.Record(@"C:\Users\Me");
        history.Record(@"c:\users\me");

        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void 空白路径被忽略()
    {
        VisitHistory history = Create();

        history.Record("   ");
        history.Record(string.Empty);

        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void 超过上限时丢弃最旧的()
    {
        VisitHistory history = Create(limit: 3);

        for (int i = 0; i < 5; i++) history.Record($@"C:\dir{i}");

        Assert.Equal(3, history.Count);
        Assert.Equal(@"C:\dir4", history.Entries[0].Path);
        Assert.Equal(@"C:\dir2", history.Entries[2].Path);
    }

    [Fact]
    public void 清空后一条不剩()
    {
        VisitHistory history = Create();
        history.Record(@"C:\a");

        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Empty(history.Recent());
    }

    [Fact]
    public void 主页只取前十二条()
    {
        VisitHistory history = Create(limit: 100);
        for (int i = 0; i < 20; i++) history.Record($@"C:\dir{i:00}");

        Assert.Equal(HistorySettings.HomePageLimit, history.Recent().Count);
        Assert.Equal(@"C:\dir19", history.Recent()[0].Path);
    }

    [Fact]
    public void 上限为零时不再记录()
    {
        VisitHistory history = Create(limit: 0);

        history.Record(@"C:\a");

        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void 手改乱的顺序会在读取时排好()
    {
        var settings = new AppSettings();
        settings.History.Entries =
        [
            new HistoryEntry { Path = @"C:\old", VisitedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            new HistoryEntry { Path = @"C:\new", VisitedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero) },
        ];

        settings.Normalize();

        Assert.Equal(@"C:\new", settings.History.Entries[0].Path);
    }

    [Fact]
    public void 归一化按上限截断历史()
    {
        var settings = new AppSettings { Privacy = { HistoryLimit = 2 } };
        settings.History.Entries =
        [
            new HistoryEntry { Path = @"C:\a", VisitedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            new HistoryEntry { Path = @"C:\b", VisitedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) },
            new HistoryEntry { Path = @"C:\c", VisitedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) },
        ];

        settings.Normalize();

        Assert.Equal(2, settings.History.Entries.Count);
        Assert.Equal(@"C:\c", settings.History.Entries[0].Path);
    }

    [Fact]
    public void 上限调小后立即裁剪()
    {
        // FR-5.3.4：设置页把上限从 100 改成 50 之后，文件里不该还留着 100 条
        // 等下次启动才归一化——那样用户会以为设置没生效。
        var settings = new AppSettings { Privacy = { HistoryLimit = 100 } };
        settings.History.Entries =
            [.. Enumerable.Range(0, 10).Select(i => new HistoryEntry
            {
                Path = $@"C:\p{i}",
                VisitedAt = new DateTimeOffset(2026, 1, 1, 0, 0, i, TimeSpan.Zero),
            })];

        var history = new VisitHistory(settings.History, () => settings.Privacy.HistoryLimit);

        settings.Privacy.HistoryLimit = 3;
        int removed = history.Trim();

        Assert.Equal(7, removed);
        Assert.Equal(3, settings.History.Entries.Count);

        // 保留的是最新的三条（构造函数已按时间倒序归一化）
        Assert.Equal(@"C:\p9", settings.History.Entries[0].Path);

        // 已经是 3 条，再裁一次不该有任何变化
        Assert.Equal(0, history.Trim());
    }
}
