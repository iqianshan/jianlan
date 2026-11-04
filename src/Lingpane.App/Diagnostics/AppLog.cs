using System.IO;
using System.Text;
using Lingpane.Core.Diagnostics;

namespace Lingpane.App.Diagnostics;

/// <summary>
/// 最小写入器，只记"用户感知不到但必须留痕"的事件（如设置写盘失败）。
/// 完整日志规范见 docs/logging.md §3，这里刻意不实现环形队列与异步落盘：
/// 调用点都在退出路径或低频失败分支，同步一次几百字节不会影响任何指标。
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();

    public static void Error(string evt, string code, string message)
        => Write("ERR", evt, code, message);

    public static void Warn(string evt, string code, string message)
        => Write("WRN", evt, code, message);

    private static void Write(string level, string evt, string code, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            string line = string.Format(
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2} code={3} msg=\"{4}\"{5}",
                DateTime.Now, level, evt, code, Escape(message), Environment.NewLine);

            lock (Gate)
            {
                File.AppendAllText(Path.Combine(AppPaths.LogsDir, "app.log"), line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写不进去只能放弃，绝不能再抛
        }
    }

    private static string Escape(string value)
    {
        string s = value.Replace('"', '\'').ReplaceLineEndings(" ");
        return s.Length <= 512 ? s : s[..512] + "…";
    }
}
