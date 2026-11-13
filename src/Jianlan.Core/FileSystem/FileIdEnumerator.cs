// i18n-ignore-file：本文件的中文字符串只作为枚举失败原因交给日志与诊断，不进界面。
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace Jianlan.Core.FileSystem;

/// <summary>
/// 一条带 FileId 的目录项。字段与 <see cref="FileEntry"/> 一一对应，
/// 区别只有多一个 <see cref="FileId"/>——枚举路径不同，落到界面上的东西必须是同一套。
/// </summary>
public readonly record struct FileIdEntry(
    string Name,
    ulong FileId,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTime,
    bool IsHidden,
    bool IsSystem);

/// <summary>
/// 用 <c>GetFileInformationByHandleEx(FileIdBothDirectoryInfo)</c> 枚举目录（FR-4.2.2）。
///
/// 为什么要单写一套枚举：<c>FileSystemEnumerable</c>（<c>FindFirstFileEx</c>）拿不到 FileId——
/// <c>WIN32_FIND_DATA</c> 里没有这个字段。要拿只能换这条一次返回整批记录的内核调用，
/// 或者给每个文件单独开一次句柄（万项目录就是一万次系统调用，直接击穿 NF-03）。
///
/// 实测（`docs/fileid-enumeration.md` §2.1，10000 项）：纯枚举 6.2 ms，比
/// <c>FileSystemEnumerable</c> 的 11.7 ms 还快一倍——所以"只在需要时才走这条路径"
/// 的理由是**避免第二遍 IO**，不是因为它慢。
///
/// 本类只在后台线程调用（与 <see cref="DirectoryEnumerator"/> 同一条约束）。
/// </summary>
public static class FileIdEnumerator
{
    private const int InitialBufferSize = 64 * 1024;
    private const int MaxBufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// 枚举目录。<paramref name="volumeSerial"/> 由**目录句柄**单独取一次，
    /// 该目录下所有条目共用——<c>FILE_ID_BOTH_DIR_INFO</c> 本身不带卷序列号，
    /// 而标签键是 <c>(卷, FileId)</c> 的组合。
    /// </summary>
    /// <returns>
    /// 失败返回 <c>false</c> 并给出可展示的原因，不抛异常。失败时调用方应回落
    /// <see cref="DirectoryEnumerator"/>：无权限目录、不支持的卷、不可用的网络路径
    /// 都会走到这里，把异常冒到界面比"标签不显示"更糟。
    /// </returns>
    public static bool TryRead(string path, out List<FileIdEntry> entries, out ulong volumeSerial, out string? error)
    {
        entries = [];
        volumeSerial = 0;
        error = null;

        SafeFileHandle handle;
        string directory;
        try
        {
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            handle = OpenDirectory(directory);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = ex.Message;
            return false;
        }

        using (handle)
        {
            if (handle.IsInvalid)
            {
                error = $"打开目录失败（{Marshal.GetLastWin32Error()}）";
                return false;
            }

            if (!TryReadVolumeSerial(handle, out volumeSerial))
            {
                error = $"读取卷序列号失败（{Marshal.GetLastWin32Error()}）";
                return false;
            }

            var list = new List<FileIdEntry>(1024);
            byte[] buffer = new byte[InitialBufferSize];

            while (true)
            {
                if (!FileIdNative.GetFileInformationByHandleEx(
                        handle, FileIdNative.FileIdBothDirectoryInfo, buffer, (uint)buffer.Length))
                {
                    int code = Marshal.GetLastWin32Error();
                    if (code == FileIdNative.ERROR_NO_MORE_FILES) break;

                    // 一条记录都放不下时内核不推进游标，放大缓冲重试即可
                    if ((code == FileIdNative.ERROR_MORE_DATA || code == FileIdNative.ERROR_INSUFFICIENT_BUFFER)
                        && buffer.Length < MaxBufferSize)
                    {
                        buffer = new byte[buffer.Length * 4];
                        continue;
                    }

                    entries = [];
                    error = $"枚举目录失败（{code}）";
                    return false;
                }

                int parsed = ParseBuffer(buffer, list);

                // 成功但一条也没解析出来：某些实现（含部分 SMB 服务器）会这样收尾。
                // 不在这里退出就是死循环，而它不报错，只会让后台线程一直转。
                if (parsed == 0) break;
            }

            entries = list;
            return true;
        }
    }

    /// <summary>
    /// 取单个路径的 <c>(卷序列号, FileId)</c>。目录也能取（判断子项时不必打开句柄）。
    ///
    /// 只有"给这一项第一次打标签"才会用到：条目本身已经带着枚举时的 FileId，
    /// 不必为了一个文件再开一次句柄。
    /// </summary>
    public static bool TryIdentify(string path, out ulong volumeSerial, out ulong fileId)
    {
        volumeSerial = 0;
        fileId = 0;

        try
        {
            using SafeFileHandle handle = Open(path);
            if (handle.IsInvalid) return false;

            if (!FileIdNative.GetFileIdInfo(handle, FileIdNative.FileIdInfo, out FileIdNative.FILE_ID_INFO info,
                    (uint)Marshal.SizeOf<FileIdNative.FILE_ID_INFO>()))
            {
                return false;
            }

            volumeSerial = info.VolumeSerialNumber;
            fileId = info.FileIdLow;
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    /// <summary>取某个路径所在卷的序列号（打开目录句柄读一次）。</summary>
    public static bool TryReadVolumeSerial(string path, out ulong volumeSerial)
    {
        volumeSerial = 0;

        try
        {
            using SafeFileHandle handle = OpenDirectory(Path.GetFullPath(path));
            return !handle.IsInvalid && TryReadVolumeSerial(handle, out volumeSerial);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// 文件名里的 <c>.</c> 与 <c>..</c> 会被内核返回，而 <c>FileSystemEnumerable</c> 不返回。
    /// 不过滤的话列表恒多两项——不报错，只在某个地方变成"项数对不上"。
    /// </summary>
    private static bool IsDotEntry(string name) => name is "." or "..";

    private static bool TryReadVolumeSerial(SafeFileHandle handle, out ulong volumeSerial)
    {
        volumeSerial = 0;
        if (!FileIdNative.GetFileIdInfo(handle, FileIdNative.FileIdInfo, out FileIdNative.FILE_ID_INFO info,
                (uint)Marshal.SizeOf<FileIdNative.FILE_ID_INFO>()))
        {
            return false;
        }

        volumeSerial = info.VolumeSerialNumber;
        return true;
    }

    private static int ParseBuffer(byte[] buffer, List<FileIdEntry> list)
    {
        int offset = 0;
        int count = 0;

        while (offset + FileIdNative.BothDirInfoHeaderSize <= buffer.Length)
        {
            var info = MemoryMarshal.Read<FileIdNative.FILE_ID_BOTH_DIR_INFO>(buffer.AsSpan(offset));

            int nameBytes = (int)info.FileNameLength;
            int nameOffset = offset + FileIdNative.BothDirInfoHeaderSize;

            // 越界说明布局假设错了，宁可少一项也不要把乱码当文件名
            if (nameBytes <= 0 || nameOffset + nameBytes > buffer.Length) break;

            string name = Encoding.Unicode.GetString(buffer, nameOffset, nameBytes);
            count++;

            if (!IsDotEntry(name))
            {
                bool isDirectory = (info.FileAttributes & (uint)FileAttributes.Directory) != 0;
                list.Add(new FileIdEntry(
                    name,
                    unchecked((ulong)info.FileId),
                    isDirectory,
                    isDirectory ? 0L : info.EndOfFile,
                    DateTime.FromFileTimeUtc(info.LastWriteTime).ToLocalTime(),
                    (info.FileAttributes & (uint)FileAttributes.Hidden) != 0,
                    (info.FileAttributes & (uint)FileAttributes.System) != 0));
            }

            if (info.NextEntryOffset == 0) break;
            offset += (int)info.NextEntryOffset;
        }

        return count;
    }

    private static SafeFileHandle OpenDirectory(string fullPath)
        => FileIdNative.CreateFileW(
            EnsureExtendedPrefix(fullPath),
            FileIdNative.FILE_LIST_DIRECTORY,
            FileIdNative.FILE_SHARE_READ | FileIdNative.FILE_SHARE_WRITE | FileIdNative.FILE_SHARE_DELETE,
            IntPtr.Zero,
            FileIdNative.OPEN_EXISTING,
            FileIdNative.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

    private static SafeFileHandle Open(string path)
        => FileIdNative.CreateFileW(
            EnsureExtendedPrefix(path),
            0,
            FileIdNative.FILE_SHARE_READ | FileIdNative.FILE_SHARE_WRITE | FileIdNative.FILE_SHARE_DELETE,
            IntPtr.Zero,
            FileIdNative.OPEN_EXISTING,
            FileIdNative.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

    /// <summary>
    /// 补 <c>\\?\</c> 前缀。<c>CreateFileW</c> 受 MAX_PATH(260) 限制，
    /// **系统开了 <c>LongPathsEnabled</c> 也不够**——那条注册表只对 manifest 里声明了
    /// <c>longPathAware</c> 的进程生效。实测 352 字符的目录不加前缀直接 ERROR_PATH_NOT_FOUND。
    ///
    /// <c>FileSystemEnumerable</c> 内部就是这么做的，所以现有枚举能吃长路径；
    /// 自己写 P/Invoke 时漏了这一步，"深目录下标签不显示"会成为一个查不出的 bug
    /// （只在不常见的深目录出现，且异常被降级分支吞掉）。
    ///
    /// 顺序不能反：<c>\\?\</c> 会关闭路径规范化，传含 <c>/</c>、<c>..</c>、重复分隔符的
    /// 路径进去同样失败。必须先 <c>Path.GetFullPath</c>——症状是"反斜杠能开、正斜杠打不开"。
    /// </summary>
    private static string EnsureExtendedPrefix(string path)
    {
        string full = Path.GetFullPath(path);

        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    /// <summary>
    /// 可预期的外部失败：路径不合法、无权限、卷不支持、文件被独占。
    /// 它们都该变成"回落 + 一句状态栏说明"，而不是让界面看到异常对话框。
    /// </summary>
    private static bool IsExpected(Exception ex)
        => ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException;
}
