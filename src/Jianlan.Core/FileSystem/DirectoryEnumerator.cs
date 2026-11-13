using System.IO.Enumeration;

namespace Jianlan.Core.FileSystem;

/// <summary>
/// 目录枚举。走 <see cref="FileSystemEnumerable{TResult}"/>：
/// 内建缓冲 + 直接回调转换，比 Directory.GetFiles 少一次 string[] 全量分配。
///
/// 约束（界面规则 R-10）：本类只能在后台线程调用，禁止从 Loaded/SizeChanged 回调直接进入。
/// </summary>
public static class DirectoryEnumerator
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,          // 隐藏/系统文件也要显示（文件管理器的基本预期）
        ReturnSpecialDirectories = false,
        BufferSize = 0,                // 0 = 使用平台默认缓冲
    };

    private static readonly Comparison<FileEntry> DefaultOrder = EntrySort.Default.ToComparison();

    /// <summary>惰性枚举，调用方负责在后台线程消费。</summary>
    public static IEnumerable<FileEntry> Enumerate(string path)
    {
        // 归一化一次：条目只保存所在目录，完整路径按需拼接。
        // 这个字符串被本目录下所有条目共享，因此一万个条目只留一份前缀。
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        var enumerable = new FileSystemEnumerable<FileEntry>(
            directory,
            (ref FileSystemEntry entry) => new FileEntry(
                entry.FileName.ToString(),
                directory,
                entry.IsDirectory,
                entry.IsDirectory ? 0L : entry.Length,
                entry.LastWriteTimeUtc.LocalDateTime,
                (entry.Attributes & FileAttributes.Hidden) != 0,
                (entry.Attributes & FileAttributes.System) != 0),
            Options);

        return enumerable;
    }

    /// <summary>枚举并按给定规则排序，返回完成的列表。</summary>
    public static List<FileEntry> Read(string path, EntrySort sort, int capacityHint = 256)
    {
        var list = new List<FileEntry>(capacityHint);
        foreach (var entry in Enumerate(path))
        {
            list.Add(entry);
        }

        list.Sort(sort.ToComparison());
        return list;
    }

    /// <summary>枚举并排序（目录优先 + 名称升序）。</summary>
    public static List<FileEntry> ReadSorted(string path, int capacityHint = 256)
    {
        var list = new List<FileEntry>(capacityHint);
        foreach (var entry in Enumerate(path))
        {
            list.Add(entry);
        }

        list.Sort(DefaultOrder);
        return list;
    }

    /// <summary>
    /// 枚举并排序，每条目附带 FileId（FR-4.2.2）。标签库非空时走这一条。
    ///
    /// 为什么不"先普通枚举、再对拿不到标签的项补一次 FileId 查询"：
    /// 那样要么对每个文件各开一次句柄（万项目录一万次系统调用），
    /// 要么把整个目录枚举两遍。<c>FileIdBothDirectoryInfo</c> 一次调用就把
    /// 名称/大小/时间/属性/FileId 全给出来，实测比 <see cref="FileSystemEnumerable{TResult}"/> 还快
    /// （10000 项 6.2 ms vs 11.7 ms，见 <c>docs/fileid-enumeration.md</c>），所以只枚举一遍。
    ///
    /// 失败（无权限、卷不支持、网络盘不可用）返回 <c>false</c>，调用方回落到普通枚举——
    /// 标签不显示远好过打不开目录。
    /// </summary>
    public static bool TryReadWithFileIds(string path, EntrySort sort, out List<FileEntry> entries,
        out ulong volumeSerial, out string? error)
    {
        entries = [];
        if (!FileIdEnumerator.TryRead(path, out List<FileIdEntry> raw, out volumeSerial, out error)) return false;

        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var list = new List<FileEntry>(raw.Count);
        foreach (FileIdEntry entry in raw)
        {
            list.Add(new FileEntry(
                entry.Name,
                directory,
                entry.IsDirectory,
                entry.Size,
                entry.LastWriteTime,
                entry.IsHidden,
                entry.IsSystem,
                entry.FileId));
        }

        list.Sort(sort.ToComparison());
        entries = list;
        return true;
    }

    /// <summary>读取失败返回 false 并给出可展示的原因，不抛异常。</summary>
    public static bool TryRead(string path, EntrySort sort, out List<FileEntry> entries, out string? error)
    {
        try
        {
            entries = Read(path, sort);
            if (entries.Count == 0) ThrowIfRootUnreadable(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            entries = [];
            error = ex.Message;
            return false;
        }
    }

    /// <summary>读取失败返回 false 并给出可展示的原因，不抛异常。</summary>
    public static bool TryReadSorted(string path, out List<FileEntry> entries, out string? error)
    {
        try
        {
            entries = ReadSorted(path);
            if (entries.Count == 0) ThrowIfRootUnreadable(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            entries = [];
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// <see cref="Options"/> 里的 IgnoreInaccessible 会连"根目录本身打不开"一起吞掉，
    /// 返回 0 项——界面于是显示成一个空目录，用户无从判断是真空还是被拒（AC-05 S3 实测）。
    /// 空结果时用默认选项再探一次根目录：默认 EnumerationOptions.Compatible 不忽略不可访问，
    /// 会如实抛出 UnauthorizedAccessException。
    ///
    /// 只在 0 项时多这一次系统调用，万项目录的枚举耗时不受影响；
    /// 保留 IgnoreInaccessible=true 是为了枚举途中子项被删除时不至于整表失败。
    /// </summary>
    private static void ThrowIfRootUnreadable(string path)
    {
        using var probe = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        probe.MoveNext();
    }
}
