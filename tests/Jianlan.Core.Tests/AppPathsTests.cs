using Jianlan.Core.Diagnostics;

namespace Jianlan.Core.Tests;

/// <summary>
/// 便携模式判定（FR-10.3）。这条一旦判错，配置文件会写到用户没预期的地方，
/// 而 AC-09「便携包不在系统目录留文件」也一起失效——所以三种标记各锁一条用例。
/// </summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "jianlan-portable-" + Guid.NewGuid().ToString("N"));

    public AppPathsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void 空目录不算便携()
        => Assert.False(AppPaths.IsPortableDirectory(_dir));

    [Fact]
    public void portable_flag_生效()
    {
        File.WriteAllText(Path.Combine(_dir, AppPaths.PortableFlag), string.Empty);

        Assert.True(AppPaths.IsPortableDirectory(_dir));
    }

    [Fact]
    public void 老口径_portable_marker_仍然生效()
    {
        // 需求改名前的包已经带过这个文件，认它不算兼容包袱，算不给已发布版本挖坑
        File.WriteAllText(Path.Combine(_dir, AppPaths.LegacyPortableMarker), string.Empty);

        Assert.True(AppPaths.IsPortableDirectory(_dir));
    }

    [Fact]
    public void 存在_data_目录即算便携()
    {
        Directory.CreateDirectory(Path.Combine(_dir, AppPaths.PortableDataDir));

        Assert.True(AppPaths.IsPortableDirectory(_dir));
    }

    [Fact]
    public void 同名文件不算_data_目录()
    {
        // 需求写的是"data 目录"。有人建了个叫 data 的文件时不该切到便携模式——
        // 那种情况下 Root 会指向一个文件路径，后续所有路径拼接都会失败。
        File.WriteAllText(Path.Combine(_dir, AppPaths.PortableDataDir), string.Empty);

        Assert.False(AppPaths.IsPortableDirectory(_dir));
    }

    [Fact]
    public void 空路径不算便携()
        => Assert.False(AppPaths.IsPortableDirectory(string.Empty));

    [Fact]
    public void 不存在的目录不算便携()
        => Assert.False(AppPaths.IsPortableDirectory(Path.Combine(_dir, "no-such-dir")));

    // ── 改名前数据目录的迁移 ────────────────────────────────────────────────
    // 产品从 Lingpane 改名为 Jianlan（0.2.2）。老用户机器上数据还在旧目录里，
    // 不搬过来就会表现成"收藏、标签、会话全没了"。

    [Fact]
    public void 旧数据目录会被搬到新名下()
    {
        string local = Path.Combine(_dir, "local");
        string legacy = Path.Combine(local, AppPaths.LegacyAppDataFolder);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        string root = AppPaths.ResolveUserRoot(local);

        Assert.Equal(Path.Combine(local, AppPaths.AppDataFolder), root);
        Assert.True(File.Exists(Path.Combine(root, "settings.json")));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void 新目录已存在时不动旧目录()
    {
        string local = Path.Combine(_dir, "local");
        string legacy = Path.Combine(local, AppPaths.LegacyAppDataFolder);
        string current = Path.Combine(local, AppPaths.AppDataFolder);
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "settings.json"), "new");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "old");

        string root = AppPaths.ResolveUserRoot(local);

        Assert.Equal(current, root);
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, "settings.json")));

        // 旧目录必须原样留着：用户可能还要从里面捞东西，静默删掉是不可接受的
        Assert.Equal("old", File.ReadAllText(Path.Combine(legacy, "settings.json")));
    }

    [Fact]
    public void 没有旧目录时直接返回新路径()
    {
        string local = Path.Combine(_dir, "local");
        Directory.CreateDirectory(local);

        Assert.Equal(Path.Combine(local, AppPaths.AppDataFolder), AppPaths.ResolveUserRoot(local));
    }

    // ── 数据目录的拼接规则 ──────────────────────────────────────────────────
    // 便携模式 Root 就是 exe\data，再拼一层会变成 data\data。0.2.x 就是这么错的：
    // 便携包里的标签库实际落在 data\data\tags.jsonl，与文档写的 data\tags.jsonl 不符。

    [Fact]
    public void 便携模式的数据目录就是_Root_本身()
        => Assert.Equal(_dir, AppPaths.ResolveDataDir(_dir, portable: true));

    [Fact]
    public void 非便携模式的数据目录在_Root_下再分一层()
        => Assert.Equal(
            Path.Combine(_dir, AppPaths.PortableDataDir),
            AppPaths.ResolveDataDir(_dir, portable: false));

    [Fact]
    public void 双层_data_里的文件会被搬回上一层()
    {
        string nested = Path.Combine(_dir, AppPaths.PortableDataDir);
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "tags.jsonl"), "legacy");

        int moved = AppPaths.MigrateNestedPortableData(_dir, portable: true);

        Assert.Equal(1, moved);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(_dir, "tags.jsonl")));
        Assert.False(Directory.Exists(nested));
    }

    [Fact]
    public void 目标已有同名文件时不覆盖也不删旧文件()
    {
        string nested = Path.Combine(_dir, AppPaths.PortableDataDir);
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "tags.jsonl"), "old");
        File.WriteAllText(Path.Combine(_dir, "tags.jsonl"), "new");

        int moved = AppPaths.MigrateNestedPortableData(_dir, portable: true);

        Assert.Equal(0, moved);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dir, "tags.jsonl")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(nested, "tags.jsonl")));
    }

    [Fact]
    public void 非便携模式不做双层迁移()
    {
        // 非便携模式下 <Root>\data 就是正常的数据目录，搬走等于删用户数据
        Directory.CreateDirectory(Path.Combine(_dir, AppPaths.PortableDataDir));

        Assert.Equal(0, AppPaths.MigrateNestedPortableData(_dir, portable: false));
    }
}
