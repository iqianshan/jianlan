using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Lingpane.Core.FileSystem;

/// <summary>
/// FileId 枚举所需的 Win32 声明（FR-4 前置，来自 <c>tools/spike-fileid</c> 的验证成果）。
///
/// 整块代码的坑都在结构体布局上，且每一条都**不报错**——只给出一个"看起来对"的错值。
/// 具体见两个结构体各自的注释，以及 <c>docs/fileid-enumeration.md</c>。
/// </summary>
internal static class FileIdNative
{
    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint FILE_SHARE_DELETE = 0x00000004;
    internal const uint OPEN_EXISTING = 3;

    /// <summary>
    /// 打开目录必须带这个标志：<c>CreateFileW</c> 默认只能开文件，
    /// 少了它打开任何目录都直接 ERROR_ACCESS_DENIED。
    /// </summary>
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    /// <summary><c>FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryInfo</c>。</summary>
    internal const int FileIdBothDirectoryInfo = 10;

    /// <summary><c>FILE_INFO_BY_HANDLE_CLASS.FileIdInfo</c>。</summary>
    internal const int FileIdInfo = 18;

    internal const int ERROR_NO_MORE_FILES = 18;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary><c>FILE_ID_BOTH_DIR_INFO</c> 定长部分的字节数；变长文件名从这里开始。</summary>
    internal const int BothDirInfoHeaderSize = 104;

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

    /// <summary>
    /// 同一入口的强类型重载（<c>FileIdInfo</c> 只有一条定长记录）。
    /// 与上面的 <c>byte[]</c> 版本是两个不同的 P/Invoke 签名，不能合并——
    /// 一个按缓冲区解析变长记录，一个直接拷结构体。
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileIdInfo(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FILE_ID_INFO lpFileInformation,
        uint dwBufferSize);

    /// <summary>
    /// <c>FILE_ID_BOTH_DIR_INFO</c>。**必须用 Explicit 布局**：
    /// <c>ShortNameLength</c>（CCHAR，1 字节）后面紧跟 <c>WCHAR ShortName[12]</c>，
    /// 而 WCHAR 要求 2 字节对齐——编译器会在 68 处补一个字节、在 ShortName 结束后
    /// 再补两个字节让 <c>FileId</c> 落到 8 的倍数上（实测 96）。
    ///
    /// 用 Sequential + <c>fixed byte[24]</c> 写，ShortName 会从 68 开始、后面全错位，
    /// 读出来的"文件名"是一片乱码，而且不抛异常。
    ///
    /// 偏移量与 SDK 头文件逐字节对齐；正确性由 <c>FileIdEnumeratorTests</c> 兜底
    /// （布局错了条目数或文件名会明显不对）。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = BothDirInfoHeaderSize)]
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
    /// <c>FILE_ID_INFO</c>（<c>FileIdInfo</c> = 18）。这才是与
    /// <c>FILE_ID_BOTH_DIR_INFO.FileId</c> 同口径的单文件取法：8 字节卷序列号 + 16 字节 FileId。
    ///
    /// **不要**用 <c>BY_HANDLE_FILE_INFORMATION.nFileIndexHigh/Low</c> 代替：那是纯 MFT 记录号，
    /// 缺了序列号之后，一个文件被删除、新文件复用同一条 MFT 记录时两者会算出同一个键，
    /// 标签就串到不相干的文件上——不报错、不自愈，只表现为"标签莫名其妙跑到别的文件"。
    ///
    /// 另外别把两种口径的卷序列号混用：这里 8 字节，旧结构体是 4 字节，
    /// 同一个文件会算出两个不同的键，表现为"标签设了不显示"。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct FILE_ID_INFO
    {
        [FieldOffset(0)] public ulong VolumeSerialNumber;
        [FieldOffset(8)] public ulong FileIdLow;
        [FieldOffset(16)] public ulong FileIdHigh;
    }
}
