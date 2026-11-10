using System.Globalization;
using System.IO;
using Lingpane.Core.Localization;

namespace Lingpane.Core.FileSystem;

/// <summary>可撤销的操作种类（FR-6.8）。</summary>
public enum UndoKind
{
    /// <summary>新建文件/文件夹。撤销 = 把新建出来的东西删掉（送回收站）。</summary>
    Create,

    /// <summary>重命名。撤销 = 改回原名。</summary>
    Rename,

    /// <summary>移动（剪切粘贴）。撤销 = 搬回原处。</summary>
    Move,

    /// <summary>复制（复制粘贴）。撤销 = 把副本删掉（送回收站）。</summary>
    Copy,

    /// <summary>删除到回收站。撤销 = 从回收站还原。</summary>
    Delete,
}

/// <summary>
/// 一步可逆操作。字段的含义随 <see cref="UndoKind"/> 变：
/// <list type="bullet">
/// <item>Rename / Move：From = 操作前的位置，To = 操作后的位置。</item>
/// <item>Create / Copy：只用 To（造出来的那一项），From 留空。</item>
/// <item>Delete：只用 From（被删掉的原路径，也是还原目标）。</item>
/// </list>
///
/// <see cref="Size"/> 与 <see cref="ModifiedTicks"/> 是操作完成后当场拍的指纹，
/// 撤销时用它判断"这一项后来被改过没有"。改过就不动——这是不发生二次破坏的关键。
/// </summary>
public sealed record UndoStep(string From, string To, long Size = 0, long ModifiedTicks = 0);

/// <summary>一次操作的撤销记录。批量操作是一份记录里的多步，撤销时算一个单元。</summary>
public sealed record UndoRecord(UndoKind Kind, IReadOnlyList<UndoStep> Steps);

/// <summary>撤销结果。<see cref="Ok"/> 只在"全部撤成功"时为真。</summary>
public sealed record UndoOutcome(bool Ok, int Undone, int Skipped, string? Reason);

/// <summary>
/// 回收站还原。Windows Shell 专有，藏在接口后面，Core 的判定逻辑才能被单测覆盖。
/// </summary>
public interface IRecycleBin
{
    /// <summary>
    /// 尝试把这些原始路径从回收站还原，返回**确实回到原位**的那些。
    /// 做不到就返回空集合，不要抛——撤销失败是可以预期的事。
    /// </summary>
    IReadOnlyList<string> Restore(IReadOnlyList<string> originalPaths);

    /// <summary>
    /// 把这些路径送进回收站。撤销"新建/粘贴"用它，而不是直接删：
    /// 万一把不该删的东西删了，用户还能自己从回收站捞回来。
    /// </summary>
    Task<bool> DeleteToRecycleBinAsync(IReadOnlyList<string> paths);
}

/// <summary>
/// 单层撤销栈（FR-6.8：撤销**最近一次**操作）。
///
/// 三条硬规矩，都是为了"宁可撤不动，也不能再破坏一次"：
///
/// 1. **撤销本身只用可回退的动作**。搬回去、改回原名、把东西送进回收站、
///    从回收站还原——没有任何一条是"永久删除"。哪怕判定错了，用户还能自己捞回来。
/// 2. **动过的东西不碰**。新建/粘贴的撤销会先比对内容指纹，文件被编辑过、
///    目录里多出或少掉东西，一律跳过并说明原因，绝不"按路径删了再说"。
/// 3. **只给一次机会**。第一次撤销就清栈，失败也不允许再按一次——
///    半途失败的状态下重试，等于在不确定的现场上再赌一把。
/// </summary>
public sealed class UndoStack
{
    private readonly IRecycleBin? _recycleBin;

    private UndoRecord? _last;

    public UndoStack(IRecycleBin? recycleBin = null) => _recycleBin = recycleBin;

    /// <summary>栈内容变了（<c>Ctrl+Z</c> 的可执行性要跟着重算）。</summary>
    public event Action? Changed;

    public bool CanUndo => _last is not null;

    public UndoKind? Kind => _last?.Kind;

    /// <summary>这一步涉及多少项。批量操作是同一份记录里的多步。</summary>
    public int Count => _last?.Steps.Count ?? 0;

    /// <summary>记一次操作。步数为 0 时等于清空——没有可逆内容就不该留一个空壳。</summary>
    public void Push(UndoKind kind, IReadOnlyList<UndoStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        _last = steps.Count == 0 ? null : new UndoRecord(kind, steps);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_last is null) return;

        _last = null;
        Changed?.Invoke();
    }

    public async Task<UndoOutcome> UndoAsync()
    {
        if (_last is not { } record) return new UndoOutcome(false, 0, 0, Strings.Undo_Nothing);

        // 先清栈再干活：撤销过程中出错也不会留下"再按一次"的机会。
        _last = null;
        Changed?.Invoke();

        return record.Kind == UndoKind.Delete
            ? RestoreFromRecycleBin(record)
            : await RevertAsync(record).ConfigureAwait(false);
    }

    /// <summary>拍指纹。文件用大小 + 最后写入时刻；目录用总字节 + 内部最新写入时刻。</summary>
    public static (long Size, long ModifiedTicks) Snapshot(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return (file.Length, file.LastWriteTimeUtc.Ticks);
            }

            if (Directory.Exists(path)) return MeasureDirectory(path);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // 拍不到就返回"未知"，撤销时按"不敢动"处理
        }

        return (UnknownSize, 0);
    }

    // ── 内部实现 ────────────────────────────────────────────────────────────

    /// <summary>指纹里的"没拍到"。任何一侧是它，都判为不可删。</summary>
    private const long UnknownSize = -1;

    private async Task<UndoOutcome> RevertAsync(UndoRecord record)
    {
        int done = 0, skipped = 0;
        string? reason = null;

        foreach (UndoStep step in record.Steps)
        {
            string? why;
            try
            {
                why = record.Kind switch
                {
                    UndoKind.Rename or UndoKind.Move => MoveBack(step),
                    UndoKind.Create or UndoKind.Copy => await RemoveAsync(step).ConfigureAwait(false),
                    _ => Strings.Undo_Nothing,
                };
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                why = Describe(ex);
            }

            if (why is null)
            {
                done++;
            }
            else
            {
                skipped++;
                reason ??= why;
            }
        }

        return new UndoOutcome(done > 0 && skipped == 0, done, skipped, reason);
    }

    /// <summary>搬回去 / 改回原名。返回 null 表示成功，否则是不能做的原因。</summary>
    private static string? MoveBack(UndoStep step)
    {
        // 已经不在原位：要么用户自己搬走了，要么上一次撤销已经做过。不算失败
        if (!Exists(step.To)) return null;

        // 原位被别的东西占了。绝不覆盖——那正是二次破坏
        if (Exists(step.From)) return Strings.Undo_NameTaken;

        string? parent = Path.GetDirectoryName(step.From);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) return Strings.Undo_PlaceGone;

        MoveItem(step.To, step.From);
        return null;
    }

    /// <summary>删掉新建/复制出来的那一项。改过就跳过。</summary>
    private async Task<string?> RemoveAsync(UndoStep step)
    {
        if (!Exists(step.To)) return null;

        if (!Matches(step)) return Strings.Undo_Modified;

        // 网络位置没有回收站，FOF_ALLOWUNDO 会退化成永久删除。那种地方一律不做
        if (!CanRecycle(step.To)) return Strings.Undo_NoRecycle;

        if (_recycleBin is null) return Strings.Undo_NoRecycle;

        return await _recycleBin
            .DeleteToRecycleBinAsync([step.To])
            .ConfigureAwait(false)
            ? null
            : Strings.Undo_DeleteFailed;
    }

    private UndoOutcome RestoreFromRecycleBin(UndoRecord record)
    {
        if (_recycleBin is null)
        {
            return new UndoOutcome(false, 0, record.Steps.Count, Strings.Undo_NoRecycle);
        }

        string[] paths = record.Steps
            .Select(s => s.From)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        if (paths.Length == 0) return new UndoOutcome(false, 0, 0, Strings.Undo_Nothing);

        IReadOnlyList<string> restored;
        try
        {
            restored = _recycleBin.Restore(paths);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new UndoOutcome(false, 0, paths.Length, Describe(ex));
        }

        var back = new HashSet<string>(restored, StringComparer.OrdinalIgnoreCase);
        int done = paths.Count(back.Contains);
        int skipped = paths.Length - done;

        return new UndoOutcome(
            done > 0 && skipped == 0,
            done,
            skipped,
            skipped > 0 ? Strings.Undo_RestoreFailed : null);
    }

    /// <summary>内容有没有被改过。任一侧指纹不可知，一律按"改过"处理。</summary>
    private static bool Matches(UndoStep step)
    {
        if (step.Size == UnknownSize) return false;

        (long size, long ticks) = Snapshot(step.To);
        if (size == UnknownSize) return false;

        return size == step.Size && ticks == step.ModifiedTicks;
    }

    private static (long Size, long ModifiedTicks) MeasureDirectory(string path)
    {
        long total = 0;
        long newest = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
        };

        foreach (string file in Directory.EnumerateFiles(path, "*", options))
        {
            var info = new FileInfo(file);
            total += info.Length;

            long ticks = info.LastWriteTimeUtc.Ticks;
            if (ticks > newest) newest = ticks;
        }

        // 空目录也得有个可比的指纹：用目录自身的时间
        if (newest == 0) newest = new DirectoryInfo(path).LastWriteTimeUtc.Ticks;

        return (total, newest);
    }

    private static void MoveItem(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Move(from, to);
            return;
        }

        if (!Directory.Exists(from)) throw new FileNotFoundException(from);

        // 同卷用 Move（快且原子）；跨卷得自己复制再删，Directory.Move 不支持
        string? fromRoot = Path.GetPathRoot(from);
        string? toRoot = Path.GetPathRoot(to);

        if (string.Equals(fromRoot, toRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }

        CopyTree(from, to);
        Directory.Delete(from, recursive: true);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Join(to, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.EnumerateDirectories(from))
        {
            CopyTree(dir, Path.Join(to, Path.GetFileName(dir)));
        }
    }

    /// <summary>这个位置有没有回收站可用。</summary>
    private static bool CanRecycle(string path)
    {
        string? root = Path.GetPathRoot(path);

        // UNC（\\server\share）没有回收站
        return !string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static bool Exists(string path)
        => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

    private static bool IsExpected(Exception ex) => ex is
        IOException or UnauthorizedAccessException or ArgumentException
        or NotSupportedException or System.Security.SecurityException
        or InvalidOperationException or System.Runtime.InteropServices.COMException;

    private static string Describe(Exception ex)
        => string.Format(CultureInfo.CurrentCulture, Strings.Undo_FailedWithReason, ex.Message);
}
