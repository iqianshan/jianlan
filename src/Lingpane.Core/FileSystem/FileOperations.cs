using System.IO;
using System.Runtime.InteropServices;

namespace Lingpane.Core.FileSystem;

/// <summary>
/// 批量文件操作的结果。失败只统计数量并把首条原因带给状态栏，
/// 不在这里弹窗——弹窗属于界面层职责。
/// </summary>
public readonly record struct FileOpResult(bool Ok, int Succeeded, int Failed, string? Error)
{
    public static FileOpResult Success(int succeeded) => new(true, succeeded, 0, null);

    public static FileOpResult Failure(string error) => new(false, 0, 0, error);
}

/// <summary>
/// 文件操作（新建 / 重命名 / 复制 / 移动 / 删除）。
///
/// 约定：
///   · 全部方法不抛异常，失败返回结果或 false，由界面层转成状态栏文本；
///   · 目标重名一律自动改名（"名称 (2)"），与资源管理器一致，避免覆盖用户数据；
///   · 复制/移动在后台线程执行，UI 线程不会被大目录卡住；
///   · 删除走 Shell（<c>SHFileOperationW</c>），才能进回收站——.NET 没有回收站 API。
/// </summary>
public static class FileOperations
{
    public const int MaxNameLength = 255;

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>Windows 保留设备名。这些名字建不出来，提前拦住比让系统报错更清楚。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>校验用户输入的名称。合法返回 true，否则给出可直接展示的原因。</summary>
    public static bool TryValidateName(string? name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "名称不能为空";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"名称不能超过 {MaxNameLength} 个字符";
            return false;
        }

        if (name.IndexOfAny(InvalidChars) >= 0)
        {
            error = "名称不能包含 \\ / : * ? \" < > |";
            return false;
        }

        if (name[^1] is '.' or ' ')
        {
            error = "名称不能以空格或句点结尾";
            return false;
        }

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
        {
            error = "该名称是系统保留名";
            return false;
        }

        return true;
    }

    /// <summary>在目录内取一个不冲突的名称：已存在则追加 " (2)"、" (3)"……</summary>
    public static string UniqueName(string directory, string desired, bool isDirectory)
    {
        if (!Exists(directory, desired)) return desired;

        string stem = desired;
        string extension = string.Empty;

        if (!isDirectory)
        {
            int dot = desired.LastIndexOf('.');
            if (dot > 0)
            {
                stem = desired[..dot];
                extension = desired[dot..];
            }
        }

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{stem} ({i}){extension}";
            if (!Exists(directory, candidate)) return candidate;
        }

        // 极端情况（同一目录上千个同名）退化为唯一后缀，绝不覆盖
        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }

    /// <summary>新建文件夹。<paramref name="name"/> 为空时用"新建文件夹"。</summary>
    public static bool TryCreateFolder(string directory, string? name, out string createdPath, out string? error)
    {
        createdPath = string.Empty;

        string desired = string.IsNullOrWhiteSpace(name) ? "新建文件夹" : name.Trim();

        if (!TryValidateName(desired, out error)) return false;

        try
        {
            createdPath = Path.Join(directory, UniqueName(directory, desired, isDirectory: true));
            Directory.CreateDirectory(createdPath);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = ex.Message;
            createdPath = string.Empty;
            return false;
        }
    }

    /// <summary>新建文件的默认名。带扩展名，否则 Windows 下双击问"用什么打开"。</summary>
    public const string DefaultFileName = "新建文本文档.txt";

    /// <summary>
    /// 新建空文件。<paramref name="name"/> 为空时用 <see cref="DefaultFileName"/>。
    ///
    /// 用 <see cref="FileMode.CreateNew"/> 而不是 <c>File.Create</c>：前者在文件已存在时抛异常，
    /// 后者会把同名文件截成 0 字节。虽然前面已经做过重名改名，但"改名 → 创建"之间仍有窗口期，
    /// 并发时宁可失败也不能毁掉别人的数据。
    /// </summary>
    public static bool TryCreateFile(string directory, string? name, out string createdPath, out string? error)
    {
        createdPath = string.Empty;

        string desired = string.IsNullOrWhiteSpace(name) ? DefaultFileName : name.Trim();

        if (!TryValidateName(desired, out error)) return false;

        try
        {
            createdPath = Path.Join(directory, UniqueName(directory, desired, isDirectory: false));

            using (new FileStream(createdPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // 只建空文件，不写内容
            }

            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = ex.Message;
            createdPath = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// 重命名。新名与原名仅大小写不同时允许（Windows 上常见需求，如 readme → README），
    /// 其余重名一律拒绝，不自动改名——用户刚输入的名字被悄悄改掉更糟。
    /// </summary>
    public static bool TryRename(string path, string newName, out string renamedPath, out string? error)
    {
        renamedPath = path;

        if (!TryValidateName(newName, out error)) return false;

        string trimmed = newName.Trim();
        string? parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            error = "无法解析所在目录";
            return false;
        }

        string target = Path.Join(parent, trimmed);

        if (string.Equals(path, target, StringComparison.Ordinal))
        {
            // 什么都没改，视为成功，避免界面报一个没意义的错
            renamedPath = target;
            return true;
        }

        bool isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            error = "该项已不存在";
            return false;
        }

        bool caseOnlyChange = string.Equals(path, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnlyChange && Exists(parent, trimmed))
        {
            error = "该目录下已存在同名项";
            return false;
        }

        try
        {
            if (isDirectory)
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            renamedPath = target;
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 删除。默认送回收站，<paramref name="permanent"/> 为 true 时永久删除（调用方必须先二次确认）。
    /// Shell 操作要求 STA 线程，这里单独起一个，避免 UI 线程在大目录上卡住。
    /// </summary>
    public static Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool permanent = false)
    {
        var existing = paths.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (existing.Length == 0) return Task.FromResult(FileOpResult.Failure("没有可删除的项"));

        var tcs = new TaskCompletionSource<FileOpResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(DeleteCore(existing, permanent));
            }
            catch (Exception ex)
            {
                tcs.SetResult(FileOpResult.Failure(ex.Message));
            }
        })
        {
            IsBackground = true,
            Name = "Lingpane.Delete",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    /// <summary>复制到目标目录。<paramref name="sources"/> 为文件或目录的完整路径。</summary>
    public static Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string targetDirectory)
        => Task.Run(() => CopyCore(sources, targetDirectory));

    /// <summary>移动到目标目录。同盘用 Move（快且原子），跨盘失败后退化为复制 + 删除。</summary>
    public static Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string targetDirectory)
        => Task.Run(() => MoveCore(sources, targetDirectory));

    // ── 内部实现 ────────────────────────────────────────────────────────────

    private static FileOpResult CopyCore(IReadOnlyList<string> sources, string targetDirectory)
    {
        int ok = 0, failed = 0;
        string? firstError = null;

        foreach (string source in sources)
        {
            try
            {
                if (IsSelfOrAncestor(source, targetDirectory))
                {
                    failed++;
                    firstError ??= "不能把文件夹复制到它自己里面";
                    continue;
                }

                if (Directory.Exists(source))
                {
                    string dest = Path.Join(targetDirectory, UniqueName(targetDirectory, Path.GetFileName(source), true));
                    CopyDirectory(source, dest);
                    ok++;
                }
                else if (File.Exists(source))
                {
                    string dest = Path.Join(targetDirectory, UniqueName(targetDirectory, Path.GetFileName(source), false));
                    File.Copy(source, dest);
                    ok++;
                }
                else
                {
                    failed++;
                    firstError ??= $"项已不存在：{source}";
                }
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                failed++;
                firstError ??= ex.Message;
            }
        }

        return new FileOpResult(failed == 0, ok, failed, firstError);
    }

    private static FileOpResult MoveCore(IReadOnlyList<string> sources, string targetDirectory)
    {
        int ok = 0, failed = 0;
        string? firstError = null;

        foreach (string source in sources)
        {
            try
            {
                if (IsSelfOrAncestor(source, targetDirectory))
                {
                    failed++;
                    firstError ??= "不能把文件夹移动到它自己里面";
                    continue;
                }

                bool isDirectory = Directory.Exists(source);
                if (!isDirectory && !File.Exists(source))
                {
                    failed++;
                    firstError ??= $"项已不存在：{source}";
                    continue;
                }

                // 剪切到自己所在的目录等于什么都没做，不能走"自动改名"分支
                if (IsSameDirectory(Path.GetDirectoryName(source), targetDirectory))
                {
                    ok++;
                    continue;
                }

                string dest = Path.Join(targetDirectory,
                    UniqueName(targetDirectory, Path.GetFileName(source), isDirectory));

                try
                {
                    if (isDirectory)
                    {
                        Directory.Move(source, dest);
                    }
                    else
                    {
                        File.Move(source, dest);
                    }
                }
                catch (IOException)
                {
                    // 跨卷移动在 NTFS 上会抛 IOException：退化为"复制 + 删除"
                    if (isDirectory)
                    {
                        CopyDirectory(source, dest);
                        Directory.Delete(source, recursive: true);
                    }
                    else
                    {
                        File.Copy(source, dest);
                        File.Delete(source);
                    }
                }

                ok++;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                failed++;
                firstError ??= ex.Message;
            }
        }

        return new FileOpResult(failed == 0, ok, failed, firstError);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Join(destination, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Join(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>目标目录是否等于源目录或位于源目录之下（防止无限递归复制）。</summary>
    private static bool IsSelfOrAncestor(string source, string targetDirectory)
    {
        if (!Directory.Exists(source)) return false;

        string normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        string normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));

        return string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase)
               || normalizedTarget.StartsWith(normalizedSource + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameDirectory(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;

        string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Exists(string directory, string name)        => File.Exists(Path.Join(directory, name)) || Directory.Exists(Path.Join(directory, name));

    private static bool IsExpected(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException;

    // ── 回收站（Shell）──────────────────────────────────────────────────────

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    private static FileOpResult DeleteCore(string[] paths, bool permanent)
    {
        IntPtr from = IntPtr.Zero;

        try
        {
            // pFrom 必须是"多个路径以 \0 分隔、整体再以 \0\0 结尾"的双空终止列表，
            // 托管 string 会在第一个 \0 处被截断，所以手工分配非托管内存。
            string joined = string.Join('\0', paths) + "\0\0";
            from = Marshal.StringToHGlobalUni(joined);

            var op = new ShFileOpStruct
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = from,
                pTo = IntPtr.Zero,
                fFlags = (ushort)(FOF_NOCONFIRMATION | FOF_NOERRORUI | (permanent ? 0 : FOF_ALLOWUNDO)),
            };

            int code = SHFileOperationW(ref op);

            if (op.fAnyOperationsAborted)
            {
                return new FileOpResult(false, 0, paths.Length, "操作已取消");
            }

            if (code != 0)
            {
                return new FileOpResult(false, 0, paths.Length, $"删除失败（Shell 错误 0x{code:X}）");
            }

            return FileOpResult.Success(paths.Length);
        }
        finally
        {
            if (from != IntPtr.Zero) Marshal.FreeHGlobal(from);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
}
