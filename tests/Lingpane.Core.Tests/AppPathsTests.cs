using Lingpane.Core.Diagnostics;

namespace Lingpane.Core.Tests;

/// <summary>
/// 便携模式判定（FR-10.3）。这条一旦判错，配置文件会写到用户没预期的地方，
/// 而 AC-09「便携包不在系统目录留文件」也一起失效——所以三种标记各锁一条用例。
/// </summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "lingpane-portable-" + Guid.NewGuid().ToString("N"));

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
}
