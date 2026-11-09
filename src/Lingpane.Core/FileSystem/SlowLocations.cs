namespace Lingpane.Core.FileSystem;

/// <summary>
/// "慢位置"判定（S-04、FR-1.2.3 验收）。网络位置与可移动设备，
/// 一次目录枚举可能卡住几十秒——SMB 重试是内核在等，不是我们的代码在等，
/// 加 <see cref="System.Threading.CancellationToken"/> 也取消不掉。
///
/// 所以只能从外面兜：这些位置给 3 秒的等待上限，超了就报"无法访问"，
/// 让后台那次枚举自己跑完（结果丢掉，见 <c>PaneViewModel.NavigateAsync</c> 的票号判断）。
///
/// 为什么不对所有路径一律 3 秒：本地大盘上的十万项目录在冷缓存下也可能超过 3 秒，
/// 那时报错是错的——用户盘是好的，只是我们没等够。
/// </summary>
public static class SlowLocations
{
    /// <summary>需求里写死的等待上限。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// UNC 路径（<c>\\server\share</c>）。<c>\\?\</c> 开头的是扩展长度前缀，
    /// 指向的仍是本地路径，不属于慢位置。
    ///
    /// 正斜杠也认：地址栏里 <c>//server/share</c> 是常见输入，<see cref="Path.GetFullPath"/>
    /// 虽然会把它归一化成反斜杠，但本方法在归一化之前就被调用了。
    /// </summary>
    public static bool IsUnc(string path)
    {
        if (path.Length < 2) return false;

        char first = path[0];
        if ((first != '\\' && first != '/') || path[1] != first) return false;

        // \\?\C:\... 与 \\.\PhysicalDrive0：前者是本地长路径，后者是设备命名空间。
        // 两者都不该按"网络"处理——它们的超时语义完全不同。
        return path.Length < 3 || path[2] is not ('?' or '.');
    }

    /// <summary>
    /// 盘符类型里会"卡在等设备"的那两种。
    ///
    /// 不包含 <see cref="DriveType.NoRootDirectory"/>：不存在的盘符（<c>Q://</c> 而 Q 没插）
    /// 也报这个类型，但枚举会立刻失败、根本不会等设备。把它算成慢位置，
    /// 会把"设备未就绪"说成"没响应"，还多等 3 秒。
    /// </summary>
    public static bool IsSlowDriveType(DriveType type)
        => type is DriveType.Network or DriveType.Removable;

    /// <summary>
    /// 这次导航要不要套 3 秒上限。判不出来（路径非法、盘符不存在）一律当作不要——
    /// 那种情况枚举会立刻失败并给出真实原因，套个超时只会把"路径不存在"说成"没响应"。
    /// </summary>
    public static bool NeedsTimeout(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (IsUnc(path)) return true;

            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;

            return IsSlowDriveType(new DriveInfo(root).DriveType);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// 等一次目录读取，最多等 <paramref name="timeout"/>。返回 true 表示读完了，false 表示超时。
    ///
    /// 超时**不取消**那次读：它卡在内核的 SMB 重试里，取消标记送不进去。调用方要做的是
    /// 丢掉它的结果（票号对不上），让它自己在后台结束——而不是假装取消了。
    ///
    /// 单独抽出来是为了可测：这里只有"等或者不等"的判断，没有 IO，
    /// 用两条用例就能证明"卡住的任务会在超时后返回 false"。
    /// </summary>
    public static async Task<bool> CompletedWithinAsync(Task read, TimeSpan timeout)
    {
        Task finished = await Task.WhenAny(read, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(finished, read);
    }
}
