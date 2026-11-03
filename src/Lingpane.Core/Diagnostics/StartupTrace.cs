using System.Diagnostics;
using System.Text;

namespace Lingpane.Core.Diagnostics;

/// <summary>
/// 启动耗时埋点（NF-08 门禁的数据来源）。
///
/// 用法约定：
///   Main 第一行 → <see cref="Begin"/>
///   每个阶段结束 → <see cref="Mark"/>
///   首帧渲染完成 → <see cref="Mark"/>("firstframe") 后调 <see cref="Flush"/>
///
/// 本类只做时间记录，不做格式化输出，不在 Mark 路径上分配。
/// </summary>
public static class StartupTrace
{
    private const int Capacity = 16;

    private static readonly Stopwatch Clock = new();
    private static readonly (string Name, long Ms)[] Marks = new (string, long)[Capacity];

    private static int _count;
    private static bool _begun;

    public static bool IsEnabled => _begun;

    public static long ElapsedMs => _begun ? Clock.ElapsedMilliseconds : 0;

    /// <summary>在进程内最早的可控位置调用一次。</summary>
    public static void Begin()
    {
        if (_begun) return;
        _begun = true;
        Clock.Start();
        Marks[0] = ("begin", 0);
        _count = 1;
    }

    public static void Mark(string name)
    {
        if (!_begun) Begin();
        if (_count >= Capacity) return;
        Marks[_count++] = (name, Clock.ElapsedMilliseconds);
    }

    /// <summary>单行摘要，形如 <c>begin=0 paths=2 window=196 firstframe=312</c>。</summary>
    public static string Summary()
    {
        if (!_begun) return "startup trace not begun";

        var sb = new StringBuilder(160);
        for (int i = 0; i < _count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Marks[i].Name).Append('=').Append(Marks[i].Ms).Append("ms");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 写一行启动日志。必须在首帧之后调用，避免磁盘 IO 进入启动预算。
    /// 任何异常都吞掉——埋点失败不得影响程序可用性。
    /// </summary>
    public static void Flush()
    {
        if (!_begun) return;

        try
        {
            string dir = AppPaths.LogsDir;
            Directory.CreateDirectory(dir);

            long wsMb = 0;
            try
            {
                using var p = Process.GetCurrentProcess();
                wsMb = p.WorkingSet64 / (1024 * 1024);
            }
            catch
            {
                // 进程信息读取失败不影响主流程
            }

            string line =
                $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} | total={ElapsedMs} | {Summary()} | ws={wsMb}MB";

            File.AppendAllText(Path.Combine(dir, "startup.log"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }
    }
}
