using System.Globalization;

namespace Lingpane.Core.FileSystem;

/// <summary>
/// 列表行的数据模型。展示用字符串在构造时一次算好，
/// 避免绑定阶段反复调用格式化（虚拟化滚动时 getter 会被高频访问）。
/// </summary>
public sealed class FileEntry
{
    public FileEntry(string name, string fullPath, bool isDirectory, long size, DateTime lastWriteTime)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Size = size;
        LastWriteTime = lastWriteTime;

        SizeText = isDirectory ? string.Empty : FormatSize(size);
        DateText = FormatDate(lastWriteTime);
        IconKey = ResolveIconKey(name, isDirectory);
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public long Size { get; }

    public DateTime LastWriteTime { get; }

    public string SizeText { get; }

    public string DateText { get; }

    /// <summary>图标缓存键：目录统一，文件按扩展名（FONT-06 图标不随字号变化）。</summary>
    public string IconKey { get; }

    private static string ResolveIconKey(string name, bool isDirectory)
    {
        if (isDirectory) return ".folder";

        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1
            ? ".file"
            : name[dot..].ToLowerInvariant();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 0 => string.Empty,
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024L * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024d * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
    };

    /// <summary>
    /// 当年条目省略年份（界面规则 4.2）：<c>10-28 14:32</c>；跨年显示 <c>2025-09-12</c>。
    /// </summary>
    private static string FormatDate(DateTime value) =>
        value.Year == DateTime.Now.Year
            ? value.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
