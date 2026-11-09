using Lingpane.Core.Diagnostics;

namespace Lingpane.Core.Tests;

/// <summary>
/// 会话标记（FR-10.6）的用例。它只有"在/不在"两种状态，但读错方向的代价不对称：
/// 漏报只是少一条提示，误报会让用户每次开机都看到"上次异常退出"。
/// </summary>
public sealed class SessionMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "lingpane-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }
    }

    [Fact]
    public void Missing_marker_means_clean_exit()
    {
        Directory.CreateDirectory(_dir);
        Assert.Null(SessionMarker.Read(_dir));
    }

    [Fact]
    public void Written_marker_reads_back()
    {
        var stamp = new SessionStamp(4242, new DateTimeOffset(2026, 11, 9, 19, 58, 0, TimeSpan.FromHours(8)), "0.2.0");

        SessionMarker.Write(_dir, stamp);
        SessionStamp? read = SessionMarker.Read(_dir);

        Assert.NotNull(read);
        Assert.Equal(4242, read.ProcessId);
        Assert.Equal(stamp.StartedAt, read.StartedAt);
        Assert.Equal("0.2.0", read.Version);
    }

    [Fact]
    public void Clear_removes_the_marker()
    {
        SessionMarker.Write(_dir, new SessionStamp(1, DateTimeOffset.Now, "0.2.0"));

        SessionMarker.Clear(_dir);

        Assert.Null(SessionMarker.Read(_dir));
        Assert.False(File.Exists(SessionMarker.PathIn(_dir)));
    }

    [Fact]
    public void Clear_on_missing_directory_does_not_throw()
    {
        // 正常退出时目录可能已经被用户删掉/便携盘拔了
        SessionMarker.Clear(Path.Combine(_dir, "nope"));
    }

    [Fact]
    public void Corrupted_marker_still_counts_as_unclean()
    {
        // 半截 JSON 只可能来自"写到一半断电"，那正是要提示的情况
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SessionMarker.PathIn(_dir), "{ \"ProcessId\": 12");

        SessionStamp? read = SessionMarker.Read(_dir);

        Assert.Same(SessionMarker.Unknown, read);
        Assert.Equal(DateTimeOffset.MinValue, read!.StartedAt);
    }

    [Fact]
    public void Empty_file_counts_as_unclean()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SessionMarker.PathIn(_dir), string.Empty);

        Assert.NotNull(SessionMarker.Read(_dir));
    }

    [Fact]
    public void Write_leaves_no_temp_file()
    {
        SessionMarker.Write(_dir, new SessionStamp(7, DateTimeOffset.Now, "0.2.0"));

        // 临时文件残留会被下一次 Read 忽略，但留在用户目录里很难解释
        Assert.False(File.Exists(SessionMarker.PathIn(_dir) + ".tmp"));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Write_creates_the_directory()
    {
        string nested = Path.Combine(_dir, "a", "b");

        SessionMarker.Write(nested, new SessionStamp(9, DateTimeOffset.Now, "0.2.0"));

        Assert.True(File.Exists(SessionMarker.PathIn(nested)));
    }

    [Fact]
    public void Marker_file_name_is_fixed()
    {
        // 0.1.0 的包不认识这个文件，但它只会在数据目录里多一个几十字节的 json
        Assert.Equal("session.json", Path.GetFileName(SessionMarker.PathIn(@"C:\data")));
    }
}
