using System.IO;
using System.Runtime.InteropServices;

using System.Globalization;
using Jianlan.Core.Diagnostics;
using Jianlan.Core.Localization;

namespace Jianlan.Core.FileSystem;

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
/// 目标处有同名项时怎么办（FR-5.2.8 的"覆盖"分项）。
///
/// <see cref="KeepBoth"/> 是 0.2.0 及以前唯一的行为（自动改名"名称 (2)"），
/// 保留为默认值：不传这个参数的老调用点语义不变，覆盖必须由调用方显式要求。
/// </summary>
public enum ConflictPolicy
{
    /// <summary>自动改名，两个都留下。</summary>
    KeepBoth = 0,

    /// <summary>用源替换目标。目录是**合并**而不是删除重建——见 CopyDirectory 的说明。</summary>
    Overwrite = 1,
}

/// <summary>
/// 文件操作（新建 / 重命名 / 复制 / 移动 / 删除）。
///
/// 约定：
///   · 全部方法不抛异常，失败返回结果或 false，由界面层转成状态栏文本；
///   · 目标重名默认自动改名（"名称 (2)"），与资源管理器一致，避免覆盖用户数据；
///     需要覆盖时由调用方显式传 <see cref="ConflictPolicy.Overwrite"/>（且要先问过用户）；
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
            error = Strings.Error_NameEmpty;
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = string.Format(CultureInfo.CurrentCulture, Strings.Error_NameTooLong, MaxNameLength);
            return false;
        }

        if (name.IndexOfAny(InvalidChars) >= 0)
        {
            error = Strings.Error_NameInvalidChars;
            return false;
        }

        if (name[^1] is '.' or ' ')
        {
            error = Strings.Error_NameTrailingChar;
            return false;
        }

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
        {
            error = Strings.Error_NameReserved;
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

        string desired = string.IsNullOrWhiteSpace(name) ? Strings.Menu_NewFolder : name.Trim();

        if (!TryValidateName(desired, out error)) return false;

        try
        {
            createdPath = Path.Join(directory, UniqueName(directory, desired, isDirectory: true));
            Directory.CreateDirectory(createdPath);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            error = DescribeFailure(ex, "fs.createFolder", directory);
            createdPath = string.Empty;
            return false;
        }
    }

    /// <summary>新建文件的默认名。带扩展名，否则 Windows 下双击问"用什么打开"。</summary>
    public static string DefaultFileName => Strings.File_NewTextFileName;

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
            error = DescribeFailure(ex, "fs.createFile", directory);
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
            error = Strings.Error_PathUnresolved;
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
            error = Strings.Error_ItemMissing;
            return false;
        }

        bool caseOnlyChange = string.Equals(path, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnlyChange && Exists(parent, trimmed))
        {
            error = Strings.Error_ItemExists;
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
            error = DescribeFailure(ex, "fs.rename", path);
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
        if (existing.Length == 0) return Task.FromResult(FileOpResult.Failure(Strings.Error_NothingToDelete));

        var tcs = new TaskCompletionSource<FileOpResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(DeleteCore(existing, permanent));
            }
            catch (Exception ex)
            {
                tcs.SetResult(FileOpResult.Failure(
                    DescribeFailure(ex, "fs.delete", string.Join(", ", existing))));
            }
        })
        {
            IsBackground = true,
            Name = "Jianlan.Delete",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    /// <summary>
    /// 复制到目标目录。<paramref name="sources"/> 为文件或目录的完整路径。
    ///
    /// <paramref name="destinations"/> 非空时，把每一项**实际落到的路径**收进去（FR-6.8 的撤销要用）。
    /// 不用返回值带：<see cref="FileOpResult"/> 是"成功几项"的汇总，把逐项路径塞进去
    /// 会让所有既有调用点都被迫处理一个它们不关心的字段。
    /// </summary>
    public static Task<FileOpResult> CopyAsync(
        IReadOnlyList<string> sources,
        string targetDirectory,
        ConflictPolicy conflict = ConflictPolicy.KeepBoth,
        ICollection<string>? destinations = null)
        => Task.Run(() => CopyCore(sources, targetDirectory, conflict, destinations));

    /// <summary>移动到目标目录。同盘用 Move（快且原子），跨盘失败后退化为复制 + 删除。</summary>
    public static Task<FileOpResult> MoveAsync(
        IReadOnlyList<string> sources,
        string targetDirectory,
        ConflictPolicy conflict = ConflictPolicy.KeepBoth,
        ICollection<string>? destinations = null)
        => Task.Run(() => MoveCore(sources, targetDirectory, conflict, destinations));

    /// <summary>
    /// 这批源在目标目录里会撞上同名项的**名字**（FR-5.2.8）。界面层据此决定要不要问用户。
    ///
    /// 不在 <see cref="CopyCore"/> 里临时问：那时已经动过磁盘了，问到一半取消会留下半个结果。
    /// 也不在这里判"能不能覆盖"（权限、占用）——那是真写的时候才知道的事。
    /// </summary>
    public static IReadOnlyList<string> FindConflicts(IReadOnlyList<string> sources, string targetDirectory)
    {
        var hits = new List<string>();

        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory)) return hits;

        foreach (string source in sources)
        {
            string name = Path.GetFileName(source);
            if (string.IsNullOrEmpty(name)) continue;

            if (Directory.Exists(Path.Join(targetDirectory, name)) || File.Exists(Path.Join(targetDirectory, name)))
            {
                hits.Add(name);
            }
        }

        return hits;
    }

    // ── 内部实现 ────────────────────────────────────────────────────────────

    private static FileOpResult CopyCore(
        IReadOnlyList<string> sources,
        string targetDirectory,
        ConflictPolicy conflict,
        ICollection<string>? destinations)
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
                    firstError ??= Strings.Error_CannotCopyIntoSelf;
                    continue;
                }

                if (Directory.Exists(source))
                {
                    string dest = Path.Join(targetDirectory, ResolveName(targetDirectory, source, true, conflict));
                    CopyDirectory(source, dest, overwrite: conflict == ConflictPolicy.Overwrite);
                    destinations?.Add(dest);
                    ok++;
                }
                else if (File.Exists(source))
                {
                    string dest = Path.Join(targetDirectory, ResolveName(targetDirectory, source, false, conflict));
                    File.Copy(source, dest, overwrite: conflict == ConflictPolicy.Overwrite);
                    destinations?.Add(dest);
                    ok++;
                }
                else
                {
                    failed++;
                    firstError ??= string.Format(CultureInfo.CurrentCulture, Strings.Error_ItemGone, source);
                }
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                failed++;
                firstError ??= DescribeFailure(ex, "fs.copy", source);
            }
        }

        return new FileOpResult(failed == 0, ok, failed, firstError);
    }

    private static FileOpResult MoveCore(
        IReadOnlyList<string> sources,
        string targetDirectory,
        ConflictPolicy conflict,
        ICollection<string>? destinations)
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
                    firstError ??= Strings.Error_CannotMoveIntoSelf;
                    continue;
                }

                bool isDirectory = Directory.Exists(source);
                if (!isDirectory && !File.Exists(source))
                {
                    failed++;
                    firstError ??= string.Format(CultureInfo.CurrentCulture, Strings.Error_ItemGone, source);
                    continue;
                }

                // 剪切到自己所在的目录等于什么都没做，不能走"自动改名"分支
                if (IsSameDirectory(Path.GetDirectoryName(source), targetDirectory))
                {
                    ok++;
                    continue;
                }

                string dest = Path.Join(targetDirectory, ResolveName(targetDirectory, source, isDirectory, conflict));
                bool overwrite = conflict == ConflictPolicy.Overwrite;

                if (overwrite && Directory.Exists(dest))
                {
                    // 目录移动到自己同名目录上：**合并**（把源里的内容并进去再删源），
                    // 先删目标再搬会让"目标里源没有的文件"凭空消失——那不是用户理解的覆盖。
                    CopyDirectory(source, dest, overwrite: true);
                    Directory.Delete(source, recursive: true);
                    destinations?.Add(dest);
                    ok++;
                    continue;
                }

                if (overwrite && File.Exists(dest))
                {
                    // 同卷 File.Move 不允许覆盖已存在文件（.NET 的 API 就是没有这个参数），
                    // 先删再搬。目标已经在回收站外被删除，这是覆盖语义的必然代价。
                    File.Delete(dest);
                }

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
                        CopyDirectory(source, dest, overwrite);
                        Directory.Delete(source, recursive: true);
                    }
                    else
                    {
                        File.Copy(source, dest, overwrite);
                        File.Delete(source);
                    }
                }

                destinations?.Add(dest);
                ok++;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                failed++;
                firstError ??= DescribeFailure(ex, "fs.move", source);
            }
        }

        return new FileOpResult(failed == 0, ok, failed, firstError);
    }

    /// <summary>目标文件名：覆盖时原样，否则撞名自动加序号。</summary>
    private static string ResolveName(
        string targetDirectory, string source, bool isDirectory, ConflictPolicy conflict)
    {
        string name = Path.GetFileName(source);
        return conflict == ConflictPolicy.Overwrite ? name : UniqueName(targetDirectory, name, isDirectory);
    }

    /// <summary>
    /// 删除失败后推断原因，返回错误码；判不出来返回 null。
    ///
    /// 做法是回到文件本身试开一次（只读句柄不改内容），用异常区分"被占用"与"没权限"。
    /// 目录不探测：判删除权限需要真写/真删，代价远大于收益，直接走通用文案。
    /// </summary>
    private static string? DiagnoseDeleteFailure(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (Directory.Exists(path)) continue;

            try
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException io) when ((io.HResult & 0xFFFF) is 32 or 33)   // 共享冲突 / 锁冲突
            {
                return FsErrors.InUse;
            }
            catch (UnauthorizedAccessException)
            {
                return FsErrors.AccessDenied;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // 其它情况（文件已不在、路径过长等）无法归因，交给通用文案
            }
        }

        return null;
    }

    /// <summary>
    /// 递归复制。
    /// <paramref name="overwrite"/> 为 true 时**合并**：目标里已有的同名文件被源覆盖，
    /// 目标里独有的文件保留。
    ///
    /// 为什么不是"删掉目标目录再整份复制"：那样在复制中途失败（磁盘满、文件被占用）
    /// 会让目标目录里原本存在的内容一起消失——用户点的是"覆盖同名"，不是"清空这个目录"。
    /// </summary>
    private static void CopyDirectory(string source, string destination, bool overwrite = false)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Join(destination, Path.GetFileName(file)), overwrite);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Join(destination, Path.GetFileName(directory)), overwrite);
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

    /// <summary>
    /// 异常 → 用户可读原因。原文只进 app.log：运行时异常消息跟随系统语言，
    /// 直接显示会出现"中文界面里夹一句英文"（见 <see cref="FsErrors"/>）。
    /// </summary>
    private static string DescribeFailure(Exception ex, string eventName, string context)
    {
        string code = FsErrors.CodeOf(ex);
        LogSink.Warn(eventName, code, $"{context} · {ex.GetType().Name}: {ex.Message}");
        return FsErrors.DescribeForOperation(code);
    }

    // ── 回收站（Shell）──────────────────────────────────────────────────────

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_SILENT = 0x0004;

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
                // FOF_SILENT：不要 Shell 自己的进度窗口。
                //
                // 不只为了好看。不加它时，删除一批文件（全选删除可达上百个）会让 Shell 在
                // **本进程里**起一个 DirectUI 进度界面，且跑在它自己的线程上；TSF 会给这个
                // 新窗口挂输入上下文，两者在窗口建毁时互相踩。压测留下的五份全内存转储
                // 全部崩在同一帧：`dui70+0x65642`（`mov [rdx], rax`，`rdx = 0`），
                // 栈上自内向外 `dui70 → duser → msctf → shell32 → textinputframework`，
                // 崩溃线程不是 UI 线程——正是 Shell 进度界面那条线。
                // 同一时刻 GDI 从 196 跳到 641，也来自这扇窗口。
                //
                // 代价：批量删除时没有进度条。删除是瞬时操作，且界面本来就有"正在删除"状态，
                // 不需要 Shell 那扇窗；换来的是不再往进程里塞第二个 UI 线程。
                fFlags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI
                                  | (permanent ? 0 : FOF_ALLOWUNDO)),
            };

            int code = SHFileOperationW(ref op);

            if (op.fAnyOperationsAborted)
            {
                return new FileOpResult(false, 0, paths.Length, Strings.Error_Cancelled);
            }

            if (code != 0)
            {
                // SHFileOperation 的返回码混了 DE_* 与 Win32 两段，部分值未文档化，
                // 直接怼给用户（"删除失败（Shell 错误 0x78）"）既看不懂也不该看
                // （docs/logging.md §6：界面不得出现错误码）。十六进制码进日志，
                // 界面按能判定的原因给一句人话。
                string? reason = DiagnoseDeleteFailure(paths);
                LogSink.Warn("fs.delete", reason ?? FsErrors.DeleteRefused,
                    $"shell=0x{code.ToString("X", CultureInfo.InvariantCulture)} · {string.Join(", ", paths)}");

                return new FileOpResult(false, 0, paths.Length,
                    reason is null ? Strings.Error_DeleteRefused : FsErrors.DescribeForOperation(reason));
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
