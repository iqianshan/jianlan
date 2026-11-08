using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileIdSpike;

/// <summary>
/// FileId 枚举所需的 Win32 声明。全部集中在这里，产品化时整体搬走即可。
/// </summary>
internal static class NativeMethods
{
    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint FILE_SHARE_DELETE = 0x00000004;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    internal const int FileIdBothDirectoryInfo = 10;
    internal const int FileIdInfo = 18;

    internal const int ERROR_NO_MORE_FILES = 18;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        [Out] byte[] lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileIdInfo(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FILE_ID_INFO lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    /// <summary>
    /// <c>BY_HANDLE_FILE_INFORMATION</c>。
    ///
    /// **必须用 Explicit 布局**：结构体里的三个时间字段是 <c>FILETIME</c>，它由两个 DWORD 组成、
    /// 对齐要求是 **4**，不是 <c>LARGE_INTEGER</c> 的 8。用 <c>long</c> 写这三个字段，
    /// C# 会按 8 对齐并在 <c>dwFileAttributes</c> 后补 4 字节，于是从第一处时间字段起整块错位 4 字节 ——
    /// <c>dwVolumeSerialNumber</c> 会读到"最后写入时间"的低 32 位，<c>nFileIndexHigh/Low</c> 同理。
    ///
    /// 后果特别隐蔽：不抛异常、不报错，只给出一个**看起来像 FileId 的随机数**。
    /// 把它的结果当标签主键，结果是"同一个文件每次算出来的标签键都不一样"，
    /// 表现为标签设了不显示、或者显示在别的文件上。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 52)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        [FieldOffset(0)] public uint FileAttributes;
        [FieldOffset(4)] public uint CreationTimeLow;
        [FieldOffset(8)] public uint CreationTimeHigh;
        [FieldOffset(12)] public uint LastAccessTimeLow;
        [FieldOffset(16)] public uint LastAccessTimeHigh;
        [FieldOffset(20)] public uint LastWriteTimeLow;
        [FieldOffset(24)] public uint LastWriteTimeHigh;
        [FieldOffset(28)] public uint VolumeSerialNumber;
        [FieldOffset(32)] public uint FileSizeHigh;
        [FieldOffset(36)] public uint FileSizeLow;
        [FieldOffset(40)] public uint NumberOfLinks;
        [FieldOffset(44)] public uint FileIndexHigh;
        [FieldOffset(48)] public uint FileIndexLow;
    }

    /// <summary>
    /// <c>FILE_ID_BOTH_DIR_INFO</c>。
    ///
    /// **必须用 Explicit 布局**：结构体里 <c>ShortNameLength</c>（CCHAR，1 字节）后面紧跟
    /// <c>WCHAR ShortName[12]</c>，而 WCHAR 要求 2 字节对齐 —— 编译器会在 69 处补一个字节、
    /// 在 ShortName 结束后再补两个字节让 <c>FileId</c> 落到 8 的倍数上。
    /// 用 Sequential + <c>fixed byte[24]</c> 写，ShortName 会从 69 开始，后面全错位，
    /// 读出来的"文件名"是一片乱码，而且不报错。
    ///
    /// 这里把所有字段偏移写死，等于把 SDK 头文件里的布局抄了一遍；
    /// 正确性由 <c>dump</c> 子命令自检（对齐错了名字会明显不对）。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    internal struct FILE_ID_BOTH_DIR_INFO
    {
        [FieldOffset(0)] public uint NextEntryOffset;
        [FieldOffset(4)] public uint FileIndex;
        [FieldOffset(8)] public long CreationTime;
        [FieldOffset(16)] public long LastAccessTime;
        [FieldOffset(24)] public long LastWriteTime;
        [FieldOffset(32)] public long ChangeTime;
        [FieldOffset(40)] public long EndOfFile;
        [FieldOffset(48)] public long AllocationSize;
        [FieldOffset(56)] public uint FileAttributes;
        [FieldOffset(60)] public uint FileNameLength;
        [FieldOffset(64)] public uint EaSize;
        [FieldOffset(68)] public byte ShortNameLength;
        [FieldOffset(96)] public long FileId;
        // 104 起是变长的 WCHAR FileName[]，长度由 FileNameLength 给出
    }

    /// <summary>
    /// <c>FILE_ID_INFO</c>（<c>FileIdInfo</c> = 18）。
    ///
    /// 这才是与 <c>FILE_ID_BOTH_DIR_INFO.FileId</c> 同口径的单文件取法：
    /// 8 字节卷序列号 + 16 字节 FileId。
    ///
    /// 不要用 <c>BY_HANDLE_FILE_INFORMATION.nFileIndexHigh/Low</c> 代替它 —— 那是**纯 MFT 记录号**
    /// （NTFS 上 <c>FILE_ID_BOTH_DIR_INFO.FileId</c> 是「序列号 &lt;&lt; 48 | 记录号」）。
    /// 缺了序列号，一个文件被删除、新文件复用同一条 MFT 记录时两者会算出同一个键，
    /// 标签就会串到不相干的文件上；而这个错误不报错、不自愈，只表现为"标签莫名其妙跑到了别的文件"。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct FILE_ID_INFO
    {
        [FieldOffset(0)] public ulong VolumeSerialNumber;
        [FieldOffset(8)] public ulong FileIdLow;
        [FieldOffset(16)] public ulong FileIdHigh;
    }

    internal const int FileIdBothDirInfoHeaderSize = 104;
}
