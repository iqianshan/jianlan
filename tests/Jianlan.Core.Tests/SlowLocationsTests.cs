using Jianlan.Core.FileSystem;

namespace Jianlan.Core.Tests;

/// <summary>
/// 慢位置判定的用例（FR-1.2.3 验收 / S-04）：3 秒超时只该套在"会卡在等设备"的路上，
/// 套错方向的两个后果都要防——本地大盘被误判超时会冤枉用户盘，
/// UNC 被漏判则会让窗口卡几十秒。
/// </summary>
public sealed class SlowLocationsTests
{
    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server")]
    [InlineData("//server/share")]
    [InlineData(@"\\10.0.0.5\share\folder")]
    public void UncPaths_need_timeout(string path)
    {
        Assert.True(SlowLocations.IsUnc(path));
        Assert.True(SlowLocations.NeedsTimeout(path));
    }

    [Theory]
    [InlineData(@"\\?\C:\very\long\path")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void DeviceAndLongPathPrefixes_are_not_unc(string path)
    {
        // \\?\ 是本地长路径前缀，\\.\ 是设备命名空间：都不是网络，3 秒超时套上去没有意义
        Assert.False(SlowLocations.IsUnc(path));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\work")]
    [InlineData("")]
    [InlineData("   ")]
    public void LocalPaths_are_not_unc(string path)
    {
        Assert.False(SlowLocations.IsUnc(path));
    }

    [Theory]
    [InlineData(DriveType.Network, true)]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.NoRootDirectory, false)]
    [InlineData(DriveType.Fixed, false)]
    [InlineData(DriveType.CDRom, false)]
    [InlineData(DriveType.Ram, false)]
    [InlineData(DriveType.Unknown, false)]
    public void Slow_drive_types(DriveType type, bool expected)
    {
        Assert.Equal(expected, SlowLocations.IsSlowDriveType(type));
    }

    [Fact]
    public void Local_temp_directory_does_not_need_timeout()
    {
        // 本机临时目录一定在固定盘上；这条用例真正防的是"给所有路径都套 3 秒"
        Assert.False(SlowLocations.NeedsTimeout(Path.GetTempPath()));
    }

    [Fact]
    public void Missing_drive_does_not_need_timeout()
    {
        // 不存在的盘符要立刻报"设备未就绪"，套超时会把原因说成"没响应"
        Assert.False(SlowLocations.NeedsTimeout(@"Q:\nonexistent"));
    }

    [Fact]
    public void Invalid_path_is_not_a_slow_location()
    {
        // 非法字符在 GetFullPath 就会抛，判定必须自己吞掉——它跑在导航路径上
        Assert.False(SlowLocations.NeedsTimeout("C:\\bad|name"));
    }

    [Fact]
    public void Timeout_is_three_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), SlowLocations.Timeout);
    }
}

/// <summary>
/// 超时等待本身（<see cref="SlowLocations.CompletedWithinAsync"/>）。这条链在真实环境里
/// 很难复现——本机上给一个不存在的 UNC 地址，Windows 会立刻返回"找不到网络路径"，
/// 真"卡住"要有台 SMB 不应答的机器。所以把"等或者不等"抽出来单独测。
/// </summary>
public sealed class SlowWaitTests
{
    [Fact]
    public async Task Finished_read_returns_true()
    {
        Task read = Task.Run(() => Thread.Sleep(10));

        Assert.True(await SlowLocations.CompletedWithinAsync(read, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Hanging_read_returns_false_after_the_timeout()
    {
        // 永远不完成的任务：模拟卡在 SMB 重试里的枚举
        var never = new TaskCompletionSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        bool finished = await SlowLocations.CompletedWithinAsync(never.Task, TimeSpan.FromMilliseconds(80));

        Assert.False(finished);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(70), $"提前返回：{clock.ElapsedMilliseconds}ms");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"等太久：{clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Faulted_read_still_counts_as_finished()
    {
        // 读失败（无权限、路径不存在）不是超时：调用方要看到真实原因，不能报"没响应"
        Task read = Task.FromException(new IOException("boom"));

        Assert.True(await SlowLocations.CompletedWithinAsync(read, TimeSpan.FromSeconds(3)));
    }
}
