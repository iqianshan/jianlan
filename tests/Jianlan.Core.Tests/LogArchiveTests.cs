using System.IO.Compression;
using Jianlan.Core.Diagnostics;

namespace Jianlan.Core.Tests;

/// <summary>
/// 日志打包（FR-10.5"一键导出为 zip"）。
///
/// 这些用例守的是两件事：包里到底该有什么，以及**失败时不能留下半成品**。
/// 后者比前者重要——一个"看起来成功、解压只有半个"的包，用户会以为是程序把日志吞了。
/// </summary>
public sealed class LogArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "jianlan-tests", Guid.NewGuid().ToString("N"));

    private string LogsDir => Path.Combine(_root, "logs");

    private string ZipPath => Path.Combine(_root, "out", "logs.zip");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }
    }

    private void WriteLog(string name, string content)
    {
        Directory.CreateDirectory(LogsDir);
        File.WriteAllText(Path.Combine(LogsDir, name), content);
    }

    [Fact]
    public void Collects_only_log_files()
    {
        WriteLog("app.log", "one");
        WriteLog("crash.log", "two");
        Directory.CreateDirectory(LogsDir);
        File.WriteAllText(Path.Combine(LogsDir, "notes.txt"), "不算日志");
        File.WriteAllText(Path.Combine(LogsDir, "dump.dmp"), "也不算");

        IReadOnlyList<string> files = LogArchive.CollectFiles(LogsDir);

        Assert.Equal(2, files.Count);
        Assert.All(files, path => Assert.EndsWith(".log", path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_directory_yields_nothing()
    {
        // 首次运行还没写过日志；这里不能抛，否则关于页一点就崩
        Assert.Empty(LogArchive.CollectFiles(Path.Combine(_root, "nope")));
        Assert.Empty(LogArchive.CollectFiles(string.Empty));
    }

    [Fact]
    public void Export_writes_logs_and_diagnostics()
    {
        WriteLog("app.log", "line-1");
        WriteLog("crash.log", "line-2");

        int entries = LogArchive.Export(ZipPath, LogsDir, "diagnostics body");

        Assert.Equal(3, entries);
        Assert.True(File.Exists(ZipPath));

        using ZipArchive zip = ZipFile.OpenRead(ZipPath);
        Assert.Equal(3, zip.Entries.Count);
        Assert.Contains(zip.Entries, e => e.FullName == "app.log");
        Assert.Contains(zip.Entries, e => e.FullName == "crash.log");
        Assert.Contains(zip.Entries, e => e.FullName == LogArchive.DiagnosticEntryName);
    }

    [Fact]
    public void Export_keeps_diagnostics_text_intact()
    {
        WriteLog("app.log", "x");

        LogArchive.Export(ZipPath, LogsDir, "版本: 0.2.0\n目录: D:\\data");

        using ZipArchive zip = ZipFile.OpenRead(ZipPath);
        ZipArchiveEntry entry = zip.Entries.Single(e => e.FullName == LogArchive.DiagnosticEntryName);

        using var reader = new StreamReader(entry.Open());
        string text = reader.ReadToEnd();

        Assert.Contains("0.2.0", text);
        Assert.Contains(@"D:\data", text);
    }

    [Fact]
    public void Export_without_logs_still_packs_diagnostics()
    {
        // 界面那边会先拦一道"还没有日志"，但这里也不能崩——两个判断各自成立
        int entries = LogArchive.Export(ZipPath, LogsDir, "only diagnostics");

        Assert.Equal(1, entries);
        Assert.True(File.Exists(ZipPath));
    }

    [Fact]
    public void Export_creates_the_target_directory()
    {
        WriteLog("app.log", "x");

        // 用户手动在保存框里输入了一个还不存在的子目录
        LogArchive.Export(Path.Combine(_root, "a", "b", "logs.zip"), LogsDir, "d");

        Assert.True(File.Exists(Path.Combine(_root, "a", "b", "logs.zip")));
    }

    [Fact]
    public void Export_leaves_no_temp_file_behind()
    {
        WriteLog("app.log", "x");

        LogArchive.Export(ZipPath, LogsDir, "d");

        Assert.False(File.Exists(ZipPath + ".tmp"));
    }

    [Fact]
    public void Failed_export_does_not_replace_an_existing_archive()
    {
        // 目标是一个已存在的目录：File.Move 必然失败。
        // 此时旧包必须原样还在——用户上一次导出的内容不能被这次失败冲掉。
        WriteLog("app.log", "x");
        LogArchive.Export(ZipPath, LogsDir, "first");
        byte[] before = File.ReadAllBytes(ZipPath);

        string blocker = Path.Combine(_root, "blocked.zip");
        Directory.CreateDirectory(blocker);

        Assert.ThrowsAny<Exception>(() => LogArchive.Export(blocker, LogsDir, "second"));

        Assert.Equal(before, File.ReadAllBytes(ZipPath));
        Assert.False(File.Exists(blocker + ".tmp"));
    }
}
