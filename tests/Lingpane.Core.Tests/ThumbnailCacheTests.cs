using Lingpane.Core.Preview;

namespace Lingpane.Core.Tests;

/// <summary>
/// 缩略图缓存的淘汰决策（FR-5.3.5）。
///
/// 只测 <see cref="ThumbnailCache.PlanTrim"/>：真去 Trim 得在磁盘上造几百 MB 缩略图，
/// 而边界（刚好等于上限、单个文件超上限、时间戳相同）恰恰是手测最容易漏的部分。
/// 目录扫描与实际删除由 Trim 负责，走查里验。
/// </summary>
public class ThumbnailCacheTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>sizeMb 是这一个文件的大小，minute 决定新旧（越大越新）。</summary>
    private static CacheEntry File(string name, long sizeMb, int minute) =>
        new(name, sizeMb << 20, Origin.AddMinutes(minute));

    private static long Mb(int value) => (long)value << 20;

    [Fact]
    public void Empty_List_Needs_No_Trim()
    {
        Assert.Empty(ThumbnailCache.PlanTrim([], Mb(500)));
    }

    [Fact]
    public void Under_The_Limit_Needs_No_Trim()
    {
        CacheEntry[] files = [File("a", 10, 0), File("b", 20, 1), File("c", 30, 2)];

        Assert.Empty(ThumbnailCache.PlanTrim(files, Mb(500)));
    }

    [Fact]
    public void Exactly_At_The_Limit_Needs_No_Trim()
    {
        // 边界：等于上限不删。写成 > 还是 >= 决定了"刚好 500MB 会不会白删一批"。
        CacheEntry[] files = [File("a", 300, 0), File("b", 200, 1)];

        Assert.Empty(ThumbnailCache.PlanTrim(files, Mb(500)));
    }

    [Fact]
    public void Over_The_Limit_Trims_Down_To_Ninety_Percent()
    {
        // 六个 30MB 共 180MB，上限 100MB。目标 90MB：删到剩 90 就停，不是删到 0。
        CacheEntry[] files =
        [
            File("a", 30, 0), File("b", 30, 1), File("c", 30, 2),
            File("d", 30, 3), File("e", 30, 4), File("f", 30, 5),
        ];

        IReadOnlyList<CacheEntry> doomed = ThumbnailCache.PlanTrim(files, Mb(100));

        // 删 3 个后剩 90MB ≤ 目标 90MB，再删一个就白删 30MB。
        Assert.Equal(3, doomed.Count);
    }

    [Fact]
    public void Oldest_Files_Go_First()
    {
        CacheEntry[] files =
        [
            File("newest", 50, 9), File("oldest", 50, 0), File("middle", 50, 5),
        ];

        // 上限 60MB、共 150MB，目标 54MB：必须删到剩一个，只能是最新的留下。
        IReadOnlyList<CacheEntry> doomed = ThumbnailCache.PlanTrim(files, Mb(60));

        Assert.Equal(["oldest", "middle"], doomed.Select(f => f.Path));
    }

    [Fact]
    public void Same_Timestamp_Falls_Back_To_Input_Order()
    {
        // 一批缩略图常常在同一秒写完，时间戳全一样。稳定排序保证结果可复现，
        // 否则这条断言会时过时不过。
        CacheEntry[] files = [File("first", 40, 0), File("second", 40, 0), File("third", 40, 0)];

        IReadOnlyList<CacheEntry> doomed = ThumbnailCache.PlanTrim(files, Mb(50));

        Assert.Equal(["first", "second"], doomed.Select(f => f.Path));
    }

    [Fact]
    public void Single_File_Larger_Than_The_Limit_Is_Removed()
    {
        CacheEntry[] files = [File("huge", 200, 0)];

        IReadOnlyList<CacheEntry> doomed = ThumbnailCache.PlanTrim(files, Mb(100));

        Assert.Single(doomed);
    }

    [Fact]
    public void Zero_Cap_Falls_Back_To_The_Effective_Cap()
    {
        long original = ThumbnailCache.CapBytes;

        try
        {
            ThumbnailCache.CapBytes = Mb(10);

            CacheEntry[] files = [File("a", 20, 0)];

            // capBytes 传 0 表示"用当前生效上限"，与 Trim() 无参调用是同一语义。
            Assert.Single(ThumbnailCache.PlanTrim(files, 0));
        }
        finally
        {
            ThumbnailCache.CapBytes = original;
        }
    }

    [Fact]
    public void Cap_Property_Rejects_Non_Positive_Values()
    {
        long original = ThumbnailCache.CapBytes;

        try
        {
            ThumbnailCache.CapBytes = 0;

            // 0 会让 PlanTrim 立刻把缓存删空，必须落回默认值。
            Assert.Equal(ThumbnailCache.DefaultCapBytes, ThumbnailCache.CapBytes);
        }
        finally
        {
            ThumbnailCache.CapBytes = original;
        }
    }

    [Fact]
    public void Default_Cap_Matches_The_Settings_Default()
    {
        // 两个默认值必须一致：设置页显示 500MB、实际按另一个数淘汰，没人查得出来。
        Assert.Equal(
            ThumbnailCache.DefaultCapBytes,
            new Lingpane.Core.Settings.PrivacySettings().CacheLimitMb * 1024L * 1024);
    }
}
