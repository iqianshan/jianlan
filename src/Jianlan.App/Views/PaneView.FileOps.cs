using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Jianlan.App.Services;
using Jianlan.Core.FileSystem;

using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// 文件操作：新建 / 重命名 / 复制 / 剪切 / 粘贴 / 删除 / 复制路径 / 属性。
///
/// 分工：Core 的 <see cref="FileOperations"/> 只管磁盘与结果，本文件负责对话框、
/// 确认、状态栏文案与刷新后的选中恢复。界面层不碰磁盘细节，Core 不碰 UI。
/// </summary>
public partial class PaneView
{
    internal void OpenSelection()
    {
        if (Vm.SelectedEntries.Count == 0) return;

        OpenEntry(Vm.SelectedEntries[0]);
    }

    internal void OpenEntry(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            // 设置项：双击/回车进目录时另开标签（默认关，见 BehaviorSettings.OpenFolderInNewTab）。
            // 这里恒为"切过去"：双击是明确的"我要看这个"，开在后台等于什么都没发生。
            //
            // 分屏时这一项**不生效**（FR-3.2）：标签是一对窗格，开新标签会把两侧一起换掉，
            // 等于顺手冲掉另一侧正在看的目录——那正好抵消了分屏的意义。
            if (!IsSplitActive && App.Settings.Behavior.OpenFolderInNewTab)
            {
                OpenInNewTabRequested?.Invoke(entry.FullPath, false);
                return;
            }

            _ = Vm.NavigateAsync(entry.FullPath);
            return;
        }

        // FR-5.2.11：可执行文件先问一句。判定与问句在 ExecutableGuard，
        // 拖到标签栏、从收藏点开也走同一份——四处口径一致，不会漏一条路。
        if (!ExecutableGuard.Confirm(Window.GetWindow(this), entry.FullPath)) return;

        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Vm.SetStatus(string.Format(Strings.Status_OpenItemFailed, entry.Name));
        }
    }

    internal async Task NewFolderAsync()
    {
        string directory = Vm.CurrentPath;
        if (string.IsNullOrEmpty(directory)) return;

        if (!FileOperations.TryCreateFolder(directory, null, out string created, out string? error))
        {
            Vm.SetStatus(string.Format(Strings.Status_NewFolderFailed, error));
            return;
        }

        await RefreshAndSelectAsync([created]);

        // 建完立刻进就地改名：默认名几乎总要改，省一次 F2。
        //
        // 这一步**不单独记撤销**：撤销要撤的是"新建"整个动作，不是"改名"。
        // 两件事记成两条的话，用户按一次 Ctrl+Z 只会把名字改回去，
        // 还得再按一次才删掉——而他心里这就是一件事。收尾在 CompletePendingCreate。
        BeginRename(Vm.FindByPath(created), pendingCreate: created);
    }

    internal async Task NewFileAsync()
    {
        string directory = Vm.CurrentPath;
        if (string.IsNullOrEmpty(directory)) return;

        if (!FileOperations.TryCreateFile(directory, null, out string created, out string? error))
        {
            Vm.SetStatus(string.Format(Strings.Status_NewFileFailed, error));
            return;
        }

        await RefreshAndSelectAsync([created]);

        // 默认名带了扩展名，进入改名时只选中词干，改完按回车即可。
        // 与上面同理：整件事记成一条"新建"。
        BeginRename(Vm.FindByPath(created), pendingCreate: created);
    }

    /// <summary>F2 / 右键「重命名」：与"再点一次名称"走同一条就地编辑。</summary>
    internal void RenameSelection()
    {
        if (Vm.SelectedEntries.Count != 1) return;
        BeginRename(Vm.SelectedEntries[0]);
    }

    internal async Task DeleteSelectionAsync(bool permanent)
    {
        var items = Vm.SelectedEntries;
        if (items.Count == 0) return;

        await DeleteEntriesAsync(items.ToArray(), permanent);
    }

    /// <summary>
    /// 删除指定的几项。与"删除选中项"分开，是给预览窗格那三颗按钮用的（FR-9.1.5）：
    /// 预览的作用对象是它自己显示的那一份，不一定是列表的选中集。
    /// </summary>
    internal async Task DeleteEntryAsync(FileEntry entry) => await DeleteEntriesAsync([entry], permanent: false);

    private async Task DeleteEntriesAsync(IReadOnlyList<FileEntry> items, bool permanent)
    {
        if (items.Count == 0) return;

        string what = items.Count == 1
            ? string.Format(Strings.Dialog_DeleteItem, items[0].Name)
            : string.Format(Strings.Dialog_DeleteItems, items.Count);

        // 永久删除无论如何都要确认；普通删除看设置（默认开）。
        // 批量与单删分开判（FR-5.2.8）：嫌单删啰嗦的人会关掉那一项，
        // 但一次删掉一整批时仍要拦一道——两个意图不该被同一个开关绑在一起。
        bool needsConfirm = permanent
            || (items.Count >= 2 ? App.Settings.Behavior.ConfirmBatch : App.Settings.Behavior.ConfirmDelete);

        if (needsConfirm)
        {
            string question = permanent
                ? string.Format(Strings.Dialog_DeleteConfirmPermanent, what)
                : string.Format(Strings.Dialog_DeleteConfirmRecycle, what);

            if (!ConfirmWindow.Ask(Window.GetWindow(this),
                    permanent ? Strings.Dialog_DeleteTitlePermanent : Strings.Dialog_DeleteTitle,
                    question))
            {
                return;
            }
        }

        string[] paths = items.Select(i => i.FullPath).ToArray();

        var (result, text) = await RunOperationAsync(
            () => FileOperations.DeleteAsync(paths, permanent),
            permanent ? Strings.Status_DeletingPermanent : Strings.Status_Deleting,
            r => r.Ok
                ? string.Format(Strings.Status_DeletedCount, r.Succeeded)
                : string.Format(Strings.Status_DeleteFailed, r.Error));

        await RefreshAndSelectAsync([]);

        // 撤销记录按"确实消失了的那些"记，不按提交的那些：删除可能部分失败，
        // 记错了会让撤销去回收站找一个根本没删的文件，白报一次失败。
        if (permanent)
        {
            // 永久删除无法撤销。这里清栈，是为了不让用户留着一次"看起来还能撤"的机会——
            // 上一次操作的记录还挂在栈上，按 Ctrl+Z 会把更早的那件事撤掉，那才是真正的意外。
            Undo?.Clear();

            if (result.Ok)
            {
                Vm.SetStatusHold(
                    string.Format(Strings.Status_PermanentDeletedNoUndo, result.Succeeded), OperationStatusHold);
            }
        }
        else
        {
            var gone = items
                .Where(i => !File.Exists(i.FullPath) && !Directory.Exists(i.FullPath))
                .Select(i => i.FullPath)
                .ToArray();

            if (gone.Length > 0)
            {
                Undo?.Push(UndoKind.Delete, gone.Select(p => new UndoStep(p, string.Empty)).ToArray());
            }
        }

        // 刷新会用新目录的统计重算状态栏，把本次结论覆盖掉——用户只看到"12 项"，
        // 既不知道成功了几项，也不知道失败为什么失败。结论整条重写回去并保持一会儿。
        // 永久删除成功那条已经写过了，而且信息更全（带"不可撤销"），不能在这里盖成普通的"N 项"
        if (!permanent || !result.Ok) Vm.SetStatusHold(text, OperationStatusHold);
    }

    internal void CopySelection(bool cut)
    {
        var items = Vm.SelectedEntries;
        if (items.Count == 0) return;

        // 返回值是"有没有写进系统剪贴板"。写不进去时本程序内还能粘，但换个程序去粘
        // 就什么也没有——这时候只说"已复制"就是在骗人。
        bool onSystem = FileClipboard.Set(items.Select(i => i.FullPath).ToArray(), cut);

        Vm.SetStatus(onSystem
            ? string.Format(cut ? Strings.Status_CutCount : Strings.Status_CopiedCount, items.Count)
            : string.Format(Strings.Status_CopiedInternalOnly, items.Count));

        CommandManager.InvalidateRequerySuggested();
    }

    internal async Task PasteAsync()
    {
        // 来源以系统剪贴板为准：这样在资源管理器里复制一批文件，切回简览也能粘；
        // 而中途在别的程序里复制了别的东西，这里也会自然失效，不会粘出过期内容。
        if (!FileClipboard.TryGet(out string[] paths, out bool cut)) return;

        string directory = Vm.CurrentPath;
        if (string.IsNullOrEmpty(directory)) return;

        IReadOnlyList<string> conflicts = FileOperations.FindConflicts(paths, directory);
        ConflictPolicy conflict = ResolveConflictPolicy(conflicts);

        // 收"实际落到哪儿"：撞名会自动改名，自己拼路径猜不准（FR-6.8 的撤销要靠它）
        var destinations = new List<string>();

        var (result, text) = await RunOperationAsync(
            () => cut
                ? FileOperations.MoveAsync(paths, directory, conflict, destinations)
                : FileOperations.CopyAsync(paths, directory, conflict, destinations),
            cut ? Strings.Status_Moving : Strings.Status_Copying,
            r => !r.Ok
                ? string.Format(cut ? Strings.Status_MoveFailed : Strings.Status_CopyFailed, r.Error)
                : r.Failed > 0
                    ? string.Format(Strings.Status_PartialFailed, r.Succeeded, r.Failed, r.Error)
                    : string.Format(cut ? Strings.Status_MovedCount : Strings.Status_CopiedCount, r.Succeeded));

        if (cut && result.Ok) FileClipboard.Clear();

        // 撤销记录只在"整批成功、且没有把别人覆盖掉"时留。
        //
        // 覆盖式粘贴把目标原有内容抹掉了，而回收站里没有它——撤销只能删掉新文件，
        // 旧文件再也回不来。那不是撤销，是第二次删除，所以这种情况一律清栈。
        // 部分失败同理：说不清落了哪几项，就不该让 Ctrl+Z 去撤别的事。
        if (result.Ok && result.Failed == 0 && destinations.Count == paths.Length
            && !(conflict == ConflictPolicy.Overwrite && conflicts.Count > 0))
        {
            RecordTransfer(cut, paths, destinations);
        }
        else
        {
            Undo?.Clear();
        }

        // 自动改名的情况下匹配不到，选中为空，不影响结果
        await RefreshAndSelectAsync(paths
            .Select(p => Path.Join(directory, Path.GetFileName(p)))
            .ToArray());

        // 同删除：结论不能被刷新覆盖，成功也要让用户看见落了几项
        Vm.SetStatusHold(text, OperationStatusHold);
    }

    /// <summary>
    /// 粘贴前决定冲突策略（FR-5.2.8 的"覆盖"分项）。
    ///
    /// 三种结局都必须是用户明确选择的：覆盖、两个都留、什么都不做。
    /// 所以问句里写清"选否会保留两份"——只写"是否覆盖"，用户点否之后
    /// 看到目标里多出一个"xxx (2)"，会以为点错了。
    ///
    /// <c>ConfirmOverwrite</c> 关掉时直接覆盖，不弹窗：这一项与"删除确认"同一个口径，
    /// 关掉的含义是"别再问我"，不是"永远别覆盖"。
    /// </summary>
    private ConflictPolicy ResolveConflictPolicy(IReadOnlyList<string> conflicts)
    {
        if (conflicts.Count == 0) return ConflictPolicy.KeepBoth;

        if (!App.Settings.Behavior.ConfirmOverwrite) return ConflictPolicy.Overwrite;

        string question = conflicts.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, Strings.Dialog_OverwriteOne, conflicts[0])
            : string.Format(CultureInfo.CurrentCulture, Strings.Dialog_OverwriteMany, conflicts.Count);

        return ConfirmWindow.Ask(Window.GetWindow(this), Strings.Dialog_OverwriteTitle, question)
            ? ConflictPolicy.Overwrite
            : ConflictPolicy.KeepBoth;
    }

    /// <summary>
    /// 复制完整路径。选中了条目就复制那些条目，没选中就复制**当前目录本身**——
    /// 在列表空白处右键时，用户要的往往正是后者（见 <see cref="CanCopyPath"/>）。
    /// </summary>
    internal void CopyPathSelection()
    {
        var items = Vm.SelectedEntries;
        if (items.Count > 0)
        {
            CopyPaths(items);
            return;
        }

        if (!string.IsNullOrEmpty(Vm.CurrentPath))
        {
            CopyPathText(Vm.CurrentPath, Strings.Status_FolderPathCopied);
        }
    }

    /// <summary>把某一项的路径放进剪贴板（FR-9.1.5 的预览按钮走这里）。</summary>
    internal void CopyPathOf(FileEntry entry) => CopyPaths([entry]);

    private void CopyPaths(IReadOnlyList<FileEntry> items)
        => CopyPathText(
            string.Join(Environment.NewLine, items.Select(i => i.FullPath)),
            string.Format(Strings.Status_PathsCopied, items.Count));

    private void CopyPathText(string text, string status)
    {
        try
        {
            Clipboard.SetText(text);
            Vm.SetStatus(status);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // 剪贴板被其他程序占用时 SetText 会抛 COMException，不能让它冒泡
            Vm.SetStatus(Strings.Status_CopyPathFailed);
        }
    }

    internal void ShowProperties()
    {
        var items = Vm.SelectedEntries;
        if (items.Count == 0) return;

        if (items.Count > 1)
        {
            long total = items.Sum(i => i.Size);
            ConfirmWindow.Notify(Window.GetWindow(this),
                Strings.Properties_Title,
                string.Format(Strings.Properties_MultiSummary, items.Count, FileEntry.FormatSizeText(total)));
            return;
        }

        var entry = items[0];
        string size = entry.IsDirectory ? Strings.Properties_FolderSizeUnknown : FileEntry.FormatSizeText(entry.Size);
        string parent = Path.GetDirectoryName(entry.FullPath) ?? Vm.CurrentPath;

        ConfirmWindow.Notify(Window.GetWindow(this),
            Strings.Properties_Title,
            string.Format(Strings.Properties_EntryDetail,
                entry.Name, parent, entry.TypeText, size,
                entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)));
    }

    /// <summary>
    /// 操作结论在状态栏停留的时长。要长过目录监视的防抖（400 ms），
    /// 否则自己刷新完、监视器再刷一次，结论必被重算的摘要覆盖。
    /// </summary>
    private static readonly TimeSpan OperationStatusHold = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 执行一次会改动磁盘的操作：期间暂停目录监听（否则自己的写操作会反复触发刷新），
    /// 结束后统一把结果写进状态栏。返回结果与结果文案——调用方刷新目录后要按需重写，
    /// 因为刷新会重算状态栏并把这里的文案覆盖掉。
    /// </summary>
    private async Task<(FileOpResult Result, string Text)> RunOperationAsync(
        Func<Task<FileOpResult>> operation, string runningText, Func<FileOpResult, string> resultText)
    {
        _suppressWatcher = true;
        Vm.SetStatus(runningText);

        try
        {
            var result = await operation();
            string text = resultText(result);
            Vm.SetStatus(text);
            return (result, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 异常原文跟随系统语言，只进日志；界面给译文（FsErrors 有说明）
            string code = FsErrors.CodeOf(ex);
            Diagnostics.AppLog.Warn("ops.run", code, $"{ex.GetType().Name}: {ex.Message}");

            string reason = FsErrors.DescribeForOperation(code);
            string text = string.Format(Strings.Status_OperationFailed, reason);
            Vm.SetStatus(text);
            return (FileOpResult.Failure(reason), text);
        }
        finally
        {
            _suppressWatcher = false;
        }
    }

    /// <summary>刷新当前目录并按路径恢复选中。刷新会整表替换，选中必须重设。</summary>
    private async Task RefreshAndSelectAsync(IReadOnlyList<string> selectPaths)
    {
        await Vm.RefreshAsync();
        SelectPaths(selectPaths);
    }

    internal void SelectPaths(IReadOnlyList<string> selectPaths)
    {
        if (selectPaths.Count == 0)
        {
            FileList.SelectedItems.Clear();
            return;
        }

        FileList.SelectedItems.Clear();

        FileEntry? first = null;
        foreach (string path in selectPaths)
        {
            if (Vm.FindByPath(path) is not { } entry) continue;

            FileList.SelectedItems.Add(entry);
            first ??= entry;
        }

        if (first is not null)
        {
            FileList.ScrollIntoView(first);
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
