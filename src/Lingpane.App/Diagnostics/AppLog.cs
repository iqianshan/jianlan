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

    /// <summary>
    /// 不带 BOM 的 UTF-8。<see cref="Encoding.UTF8"/> 会在首次创建文件时写入 EF BB BF，
    /// 于是 app.log 第一行以一个看不见的字符开头——用 grep / Python 解析时首行永远匹配不上，
    /// 排查时最容易误判成"日志没写"。
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void Error(string evt, string code, string message)
        => Write("ERR", evt, code, message);

    public static void Warn(string evt, string code, string message)
        => Write("WRN", evt, code, message);

    /// <summary>
    /// 一次启动只写一条，用于对齐"用户报的问题发生在哪个版本、哪种数据目录"。
    /// 不做通用 INFO 通道：这个类刻意只有三个出口，免得变成第二个日志框架（见文件头注释）。
    /// </summary>
    public static void Info(string evt, string message)
        => Write("INF", evt, "-", message);

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
                File.AppendAllText(Path.Combine(AppPaths.LogsDir, "app.log"), line, Utf8NoBom);
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
