using System.IO;
using System.IO.Compression;
using System.Text;

namespace Lingpane.Core.Diagnostics;

/// <summary>
/// 日志打包（FR-10.5 的"一键导出为 zip"）。
///
/// 为什么放在 Core 而不是关于页里：这段逻辑要能单独测——"哪些文件该进包"、
/// "打包失败会不会留下半个文件"都不是界面问题。关于页只负责选路径与给反馈。
/// </summary>
public static class LogArchive
{
    /// <summary>诊断快照在包里的名字。与日志平铺在一层，用户解压后一眼能看见。</summary>
    public const string DiagnosticEntryName = "diagnostics.txt";

    /// <summary>
    /// 日志目录里该进包的文件。只认 <c>.log</c>：
    /// 目录里还可能躺着崩溃转储或用户自己放的东西，把整个目录打进去会让包体积失控。
    /// </summary>
    public static IReadOnlyList<string> CollectFiles(string logsDir)
    {
        if (string.IsNullOrWhiteSpace(logsDir) || !Directory.Exists(logsDir)) return [];

        try
        {
            return [.. Directory.EnumerateFiles(logsDir, "*.log", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 把日志与一份诊断快照打成 zip。
    ///
    /// 先写 <c>.tmp</c> 再改名：打包中途失败（磁盘满、目标被占用）时不会留下一个
    /// 看起来正常、实际只含前两个文件的 zip——那种"导出成功但内容不全"最难被发现。
    ///
    /// 单个文件读不出来（正被别的进程独占）就跳过，不让一个日志把整次导出废掉。
    /// </summary>
    public static int Export(string destinationZip, string logsDir, string diagnosticText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZip);

        string temp = destinationZip + ".tmp";
        int entries = 0;

        try
        {
            // GetDirectoryName 对"只有文件名的相对路径"返回空串而不是 null，
            // 直接传给 CreateDirectory 会抛 ArgumentException
            string? directory = Path.GetDirectoryName(destinationZip);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (string file in CollectFiles(logsDir))
                {
                    try
                    {
                        zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
                        entries++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 独占中的日志跳过。宁可少一个文件，也不要这这次导出直接失败。
                    }
                }

                if (!string.IsNullOrEmpty(diagnosticText))
                {
                    ZipArchiveEntry entry = zip.CreateEntry(DiagnosticEntryName, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(
                        entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    writer.Write(diagnosticText);
                    entries++;
                }
            }

            File.Move(temp, destinationZip, overwrite: true);
            return entries;
        }
        catch
        {
            // 失败时清掉半成品；清不掉也无所谓，但不能让它顶替目标文件
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留在那儿，反正名字带 .tmp
        }
    }
}
