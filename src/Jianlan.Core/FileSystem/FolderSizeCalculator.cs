using System.Diagnostics;
using System.IO.Enumeration;

namespace Jianlan.Core.FileSystem;

/// <summary>
/// 一次扫描的中间读数。字段与 <see cref="FolderSizeResult"/> 同名，
/// 单独开一个类型是因为它按 200ms 一跳被频繁分配，而结果只在最后产生一次。
/// </summary>
public readonly record struct FolderSizeProgress(long Bytes, long FileCount, long FolderCount);

/// <summary>
/// 扫描结果。<paramref name="Completed"/> 为 false 表示中途被取消或到了时间上限，
/// 此时数值是"至少这么多"，不是全部——界面要据此标注，否则用户会拿半个数字当结论。
/// </summary>
public readonly record struct FolderSizeResult(
    long Bytes, long FileCount, long FolderCount, bool Completed);

/// <summary>
/// 一个目录行的大小列状态。<see cref="None"/> 是默认值——与"算出来是 0 字节"必须区分开，
/// 否则空目录和没算过的目录在界面上长得一模一样。
/// </summary>
public enum FolderSizeState
{
    /// <summary>从未计算过，大小列留空。</summary>
    None,

    /// <summary>正在计算，大小列显示"计算中…"。</summary>
    Computing,

    /// <summary>已算完，是精确值。</summary>
    Done,

    /// <summary>被取消或到了时间上限，显示成"至少这么多"。</summary>
    Partial,
}

/// <summary>
/// 文件夹大小递归求和（FR-6.16）。**只由用户显式触发**：没有任何路径会自动调用它，
/// 需求原文是"严禁自动递归"——一个含符号链接死循环或网络盘的目录足以把整个界面拖住。
///
/// 三条边界写死在这里，调用方不用重复判断：
///   · 不跟随 reparse point（符号链接 / 目录联接），既防循环也避免把链接目标算两遍；
///   · 无权限的子目录跳过而不是中断整个扫描（<c>IgnoreInaccessible</c>）；
///   · 到时间上限或收到取消就返回**已累计的值**并标记未完成，不丢已经扫过的部分。
/// </summary>
public static class FolderSizeCalculator
{
    /// <summary>默认时间上限。网络盘/机械盘上大目录的兜底，见 m1-fr6-plan §3 的风险表。</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(30);

    /// <summary>每多少项检查一次取消与时钟。512 是"够密"与"不拖慢"之间的折中。</summary>
    private const int CheckInterval = 512;

    /// <summary>进度回报间隔。太密会让 UI 线程忙于刷新状态栏，反而卡。</summary>
    private const int ProgressIntervalMs = 200;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,

        // 只跳 reparse point。**不能**沿用默认值：默认是 Hidden | System，
        // 那样隐藏文件与系统文件不计入大小，用户会得到一个"比我看到的小"的数字。
        AttributesToSkip = FileAttributes.ReparsePoint,

        ReturnSpecialDirectories = false,
        BufferSize = 0,
    };

    /// <summary>
    /// 同步扫描。必须在后台线程调用（同 <see cref="DirectoryEnumerator"/> 的约束 R-10）。
    /// 不抛异常：路径不可用、中途被拔盘都在内部收口成"未完成"的结果。
    /// </summary>
    public static FolderSizeResult Count(
        string path,
        CancellationToken cancellationToken = default,
        IProgress<FolderSizeProgress>? progress = null,
        TimeSpan? limit = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new FolderSizeResult(0, 0, 0, Completed: false);
        }

        TimeSpan cap = limit ?? DefaultLimit;
        var clock = Stopwatch.StartNew();
        long bytes = 0;
        long files = 0;
        long folders = 0;
        int sinceCheck = 0;
        long lastReport = 0;
        bool completed = true;

        try
        {
            var entries = new FileSystemEnumerable<(bool IsDirectory, long Length)>(
                path,
                (ref FileSystemEntry entry) =>
                    (entry.IsDirectory, entry.IsDirectory ? 0L : entry.Length),
                Options);

            foreach (var (isDirectory, length) in entries)
            {
                if (isDirectory)
                {
                    folders++;
                }
                else
                {
                    files++;

                    // 溢出保护：单个文件就接近 long 上限的路径不存在，
                    // 但一万个"稀疏文件"报上来的长度之和可以溢出成负数
                    bytes = length > long.MaxValue - bytes ? long.MaxValue : bytes + length;
                }

                if (++sinceCheck < CheckInterval) continue;
                sinceCheck = 0;

                if (cancellationToken.IsCancellationRequested || clock.Elapsed >= cap)
                {
                    completed = false;
                    break;
                }

                // 取消检查要够密，进度回报不必：后者每次都要切到 UI 线程
                if (progress is not null && clock.ElapsedMilliseconds - lastReport >= ProgressIntervalMs)
                {
                    lastReport = clock.ElapsedMilliseconds;
                    progress.Report(new FolderSizeProgress(bytes, files, folders));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 扫到一半目录被删、U 盘被拔：已累计的部分仍然有意义，标记未完成就返回。
            // 这里不写日志——调用方（PaneViewModel）比 Core 更清楚该记什么事件名。
            completed = false;
        }

        progress?.Report(new FolderSizeProgress(bytes, files, folders));
        return new FolderSizeResult(bytes, files, folders, completed);
    }
}
