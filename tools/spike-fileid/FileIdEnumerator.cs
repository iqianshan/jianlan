using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileIdSpike;

internal sealed class FileIdEntry
{
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required long LastWriteFileTime { get; init; }
    public required FileAttributes Attributes { get; init; }
    public required ulong FileId { get; init; }

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public DateTime LastWriteLocal => DateTime.FromFileTimeUtc(LastWriteFileTime).ToLocalTime();
}

internal sealed class EnumStats
{
    public int Calls;
    public int BufferResizes;
    public int Entries;
    public int BufferBytes;

    /// <summary>被丢掉的 "." 与 ".." 条数，正常恒为 2。</summary>
    public int DotEntriesSkipped;

    public override string ToString()
        => $"calls={Calls} resizes={BufferResizes} entries={Entries} " +
           $"dots={DotEntriesSkipped} buf={BufferBytes / 1024}KB";
}

internal static class FileIdEnumerator
{
    private const int InitialBufferSize = 64 * 1024;
    private const int MaxBufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// 用 <c>GetFileInformationByHandleEx(FileIdBothDirectoryInfo)</c> 枚举目录，
    /// 一条记录同时给出 名称 / 大小 / 修改时间 / 属性 / FileId ——
    /// 不需要"先枚举再对每个文件查 FileId"，那会变成 N 次系统调用。
    /// </summary>
    internal static List<FileIdEntry> Read(string path, out EnumStats stats)
    {
        stats = new EnumStats();
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        using SafeFileHandle handle = NativeMethods.CreateFileW(
            EnsureExtendedPrefix(directory),
            NativeMethods.FILE_LIST_DIRECTORY,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"打开目录失败：{directory}");
        }

        var list = new List<FileIdEntry>(1024);
        byte[] buffer = new byte[InitialBufferSize];
        stats.BufferBytes = buffer.Length;

        while (true)
        {
            if (!NativeMethods.GetFileInformationByHandleEx(
                    handle, NativeMethods.FileIdBothDirectoryInfo, buffer, (uint)buffer.Length))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ERROR_NO_MORE_FILES) break;

                // 一个条目都放不下时内核不推进游标，放大重试即可；放大到上限仍失败才算真错
                if ((error == NativeMethods.ERROR_MORE_DATA || error == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                    && buffer.Length < MaxBufferSize)
                {
                    buffer = new byte[buffer.Length * 4];
                    stats.BufferResizes++;
                    stats.BufferBytes = buffer.Length;
                    continue;
                }

                throw new Win32Exception(error, $"GetFileInformationByHandleEx 失败：{directory}");
            }

            stats.Calls++;
            int parsed = ParseBuffer(buffer, list, stats);

            // 成功但一条也没解析出来：某些实现（含部分 SMB 服务器）会这样收尾。
            // 不在这里退出就是死循环，而它不会报错，只会让后台线程永远转下去。
            if (parsed == 0) break;
        }

        stats.Entries = list.Count;
        return list;
    }

    private static int ParseBuffer(byte[] buffer, List<FileIdEntry> list, EnumStats stats)
    {
        int offset = 0;
        int count = 0;

        while (offset + NativeMethods.FileIdBothDirInfoHeaderSize <= buffer.Length)
        {
            var info = MemoryMarshal.Read<NativeMethods.FILE_ID_BOTH_DIR_INFO>(buffer.AsSpan(offset));

            int nameBytes = (int)info.FileNameLength;
            int nameOffset = offset + NativeMethods.FileIdBothDirInfoHeaderSize;

            // 越界说明布局假设错了，宁可少一项也不要读出乱码当文件名
            if (nameBytes <= 0 || nameOffset + nameBytes > buffer.Length) break;

            string name = Encoding.Unicode.GetString(buffer, nameOffset, nameBytes);

            // FileIdBothDirectoryInfo **会**返回 "." 和 ".."，而 FileSystemEnumerable 不会。
            // 不过滤的话列表会多出两项，条目数与现有实现差 2 —— 这个差异不报错，
            // 只会让"项数对不上"在后面某个地方变成难查的 bug。
            if (name is "." or "..")
            {
                count++;
                stats.DotEntriesSkipped++;
                goto next;
            }

            list.Add(new FileIdEntry
            {
                Name = name,
                Size = (info.FileAttributes & (uint)FileAttributes.Directory) != 0 ? 0 : info.EndOfFile,
                LastWriteFileTime = info.LastWriteTime,
                Attributes = (FileAttributes)info.FileAttributes,
                FileId = unchecked((ulong)info.FileId),
            });

            count++;

        next:
            if (info.NextEntryOffset == 0) break;
            offset += (int)info.NextEntryOffset;
        }

        return count;
    }

    /// <summary>取单个路径的 (卷序列号, FileId)。目录要用 FILE_FLAG_BACKUP_SEMANTICS 才打得开。</summary>
    internal static (ulong VolumeSerial, ulong FileId) Identify(string path)
    {
        using SafeFileHandle handle = Open(path);

        if (!NativeMethods.GetFileIdInfo(handle, NativeMethods.FileIdInfo, out NativeMethods.FILE_ID_INFO info,
                (uint)Marshal.SizeOf<NativeMethods.FILE_ID_INFO>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetFileInformationByHandleEx(FileIdInfo) 失败：{path}");
        }

        // NTFS 上高 64 位恒为 0；ReFS 等文件系统可能用到，这里只取低 64 位作键（与目录枚举同口径）
        return (info.VolumeSerialNumber, info.FileIdLow);
    }

    /// <summary>
    /// 旧口径：<c>BY_HANDLE_FILE_INFORMATION.nFileIndexHigh/Low</c>。
    /// 只有 MFT 记录号、没有序列号，**不能**当标签主键用。留在这里是为了让对照数据可见。
    /// </summary>
    internal static (uint VolumeSerial, ulong Index) IdentifyByHandleIndex(string path)
    {
        using SafeFileHandle handle = Open(path);

        if (!NativeMethods.GetFileInformationByHandle(handle, out NativeMethods.BY_HANDLE_FILE_INFORMATION info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetFileInformationByHandle 失败：{path}");
        }

        ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return (info.VolumeSerialNumber, index);
    }

    /// <summary>
    /// 加 <c>\\?\</c> 前缀。<c>CreateFileW</c> 受 MAX_PATH(260) 限制，
    /// **即使系统开了 <c>LongPathsEnabled</c> 也一样** —— 那条注册表只对 manifest 里声明了
    /// <c>longPathAware</c> 的进程生效，控制台宿主/自建 apphost 未必带这个声明。
    /// 实测：352 字符的目录直接打开报 ERROR_PATH_NOT_FOUND。
    ///
    /// <c>FileSystemEnumerable</c> 内部就是这么做的，所以现有枚举能吃长路径；
    /// 自己写 P/Invoke 时这一步必须补，否则"深目录下标签不显示"会成为一个永远查不出的 bug ——
    /// 它只在不常见的深目录出现，且异常被降级分支吞掉。
    ///
    /// 注意：<c>\\?\</c> 会关闭路径规范化，所以传进来的必须是已用
    /// <c>Path.GetFullPath</c> 规范化过的绝对路径（不含 <c>.</c> / <c>..</c> / 正斜杠）。
    /// </summary>
    private static string EnsureExtendedPrefix(string path)
    {
        // 必须先规范化：`\\?\` 会关闭路径里的「.」「..」与分隔符折叠，
        // 传 `D:/a//b` 或 `D://a//..//b` 进去会直接 ERROR_PATH_NOT_FOUND。
        // 这一步顺序反了，症状是"同一目录用反斜杠能开、用正斜杠打不开"，极难定位。
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

    private static SafeFileHandle Open(string path)
    {
        SafeFileHandle handle = NativeMethods.CreateFileW(
            EnsureExtendedPrefix(path),
            0,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"打开失败：{path}");
        }

        return handle;
    }
}
