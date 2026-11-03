using System.Collections.Concurrent;
using System.Globalization;

namespace Lingpane.Core.FileSystem;

/// <summary>
/// 列表行的数据模型。
///
/// 内存口径（NF-05）：万项目录下每个条目都会常驻，因此字段按"必须常驻"和"按需计算"分开：
///   · 常驻：名称、所在目录（**所有条目共享同一个字符串实例**）、是否为目录、大小、修改时间
///   · 按需：完整路径、大小文本、日期文本、图标键
///
/// 这样做的理由：列表只显示名称/日期/大小，完整路径在打开、复制路径之前根本用不到；
/// 而一万个条目各存一份完整路径，绝大部分内容是重复的父目录前缀。
///
/// 唯一例外是 <see cref="IconKey"/>：它参与绑定且每行首个可见项就会取用，
/// 但相同扩展名共用一个驻留实例，避免一万个 ".txt" 各占一份。
/// </summary>
public sealed class FileEntry
{
    private static readonly ConcurrentDictionary<string, string> IconKeyCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _parentPath;

    private string? _fullPath;
    private string? _sizeText;
    private string? _dateText;

    public FileEntry(string name, string parentPath, bool isDirectory, long size, DateTime lastWriteTime)
    {
        Name = name;
        _parentPath = parentPath;
        IsDirectory = isDirectory;
        Size = size;
        LastWriteTime = lastWriteTime;
        IconKey = ResolveIconKey(name, isDirectory);
    }

    public string Name { get; }

    public bool IsDirectory { get; }

    public long Size { get; }

    public DateTime LastWriteTime { get; }

    /// <summary>完整路径。首次访问时拼接并缓存，之后不再重复分配。</summary>
    public string FullPath => _fullPath ??= Path.Join(_parentPath, Name);

    public string SizeText
    {
        get
        {
            if (IsDirectory) return string.Empty;
            return _sizeText ??= FormatSize(Size);
        }
    }

    public string DateText => _dateText ??= FormatDate(LastWriteTime, DateTime.Now.Year);

    /// <summary>图标缓存键：目录统一，文件按扩展名（FONT-06 图标不随字号变化）。</summary>
    public string IconKey { get; }

    private static string ResolveIconKey(string name, bool isDirectory)
    {
        if (isDirectory) return ".folder";

        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return ".file";

        // 相同扩展名共用驻留实例：一万个文件通常只有十几种扩展名
        return IconKeyCache.GetOrAdd(name[dot..], static key => key.ToLowerInvariant());
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
    /// <paramref name="currentYear"/> 由调用方传入，避免每行都读一次系统时间。
    /// </summary>
    private static string FormatDate(DateTime value, int currentYear) =>
        value.Year == currentYear
            ? value.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
