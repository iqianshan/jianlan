using System.IO;
using Lingpane.App.Diagnostics;

namespace Lingpane.App.Services;

/// <summary>
/// 一个驱动器的快照（FR-1.2.2）。容量三个字段为 0 表示没读到——
/// 未就绪的读卡器、空仓光驱、断线的映射盘都会走到这里，它们仍然是"存在的盘符"。
/// </summary>
public sealed record DriveEntry(string Root, string Label, DriveType Type, long TotalBytes, long FreeBytes)
{
    public bool HasCapacity => TotalBytes > 0;

    /// <summary>盘符，去掉结尾的反斜杠：<c>C:</c>。</summary>
    public string Letter => Root.TrimEnd(Path.DirectorySeparatorChar);
}

/// <summary>
/// 驱动器枚举与缓存（FR-1.2.2）。
///
/// 为什么单独一层而不是在导航栏里直接调 <see cref="DriveInfo.GetDrives()"/>：
///   · 导航栏重建很频繁（加收藏、换语言、切标签都会走），每次都枚举一遍是白花的时间；
///   · 枚举本身在线程池上跑，结果回到 UI 线程填充，首帧不碰设备。
///
/// 为什么读容量而只读盘符不够：需求要的是"展开显示全部驱动器与剩余空间"，
/// 剩余空间是这一项的核心信息——只有盘符的列表和地址栏补全没有区别。
/// </summary>
internal static class DriveService
{
    /// <summary>
    /// 单轮枚举的上限。断线的网络盘读 <c>TotalSize</c> 可以挂十几秒，
    /// 而它旁边那几个正常的盘早就读完了——不能为了一台盘让整条侧栏一直空着。
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private static readonly object Gate = new();
    private static IReadOnlyList<DriveEntry> _cached = [];

    /// <summary>上一次枚举的结果。首次访问时是空表，调用方应先出占位再补。</summary>
    public static IReadOnlyList<DriveEntry> Cached
    {
        get
        {
            lock (Gate) return _cached;
        }
    }

    /// <summary>
    /// 重新枚举并缓存。超时保留上一次结果——宁可显示旧容量，也不要让列表整块消失。
    /// 返回的列表已经写进 <see cref="Cached"/>，调用方直接拿去重建界面即可。
    /// </summary>
    public static async Task<IReadOnlyList<DriveEntry>> RefreshAsync()
    {
        Task<IReadOnlyList<DriveEntry>> probe = Task.Run<IReadOnlyList<DriveEntry>>(Enumerate);
        Task finished = await Task.WhenAny(probe, Task.Delay(ProbeTimeout)).ConfigureAwait(true);

        if (!ReferenceEquals(finished, probe))
        {
            AppLog.Warn("nav.drives", "E9002", $"驱动器枚举超时（>{ProbeTimeout.TotalSeconds:0}s）");  // i18n-ignore：只进 app.log
            return Cached;
        }

        IReadOnlyList<DriveEntry> list = await probe.ConfigureAwait(true);
        lock (Gate) _cached = list;
        return list;
    }

    /// <summary>
    /// 枚举本机全部驱动器。对每个盘单独 try：
    /// 一个 BitLocker 锁着、或者介质门打开着的光驱，不该让其余六个盘一并消失。
    /// </summary>
    private static IReadOnlyList<DriveEntry> Enumerate()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("nav.drives", "E9002", $"GetDrives: {ex.GetType().Name}: {ex.Message}");  // i18n-ignore：只进 app.log
            return [];
        }

        var list = new List<DriveEntry>(drives.Length);

        foreach (DriveInfo drive in drives)
        {
            string label = string.Empty;
            long total = 0;
            long free = 0;

            try
            {
                if (drive.IsReady)
                {
                    label = drive.VolumeLabel ?? string.Empty;
                    total = drive.TotalSize;

                    // 用 TotalFreeSpace 而不是 AvailableFreeSpace：前者是"卷剩余"，
                    // 与资源管理器显示的是同一个数。有磁盘配额时后者会更小，
                    // 两个界面对同一个盘报不同的剩余空间，用户只会认为其中之一算错了。
                    free = drive.TotalFreeSpace;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                label = string.Empty;
                total = 0;
                free = 0;
            }

            list.Add(new DriveEntry(drive.Name, label, drive.DriveType, total, free));
        }

        return list;
    }
}
