using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Lingpane.App.Services;
using Lingpane.Core.FileSystem;

using Lingpane.Core.Localization;

namespace Lingpane.App.Views;

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

        // 建完立刻进重命名：默认名几乎总要改，省一次 F2
        await RenameEntryAsync(Vm.FindByPath(created));
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

        // 默认名带了扩展名，重命名时选中词干部分，改完按回车即可
        await RenameEntryAsync(Vm.FindByPath(created));
    }

    internal async Task RenameSelectionAsync()
    {
        if (Vm.SelectedEntries.Count != 1) return;
        await RenameEntryAsync(Vm.SelectedEntries[0]);
    }

    internal async Task RenameEntryAsync(FileEntry? entry)
    {
        if (entry is null) return;

        string? name = TextPromptWindow.Ask(
            Window.GetWindow(this),
            Strings.Dialog_RenameTitle,
            string.Format(Strings.Dialog_RenamePrompt, entry.Name),
            entry.Name,
            value => FileOperations.TryValidateName(value, out string? error) ? null : error,
            selectStem: !entry.IsDirectory);

        if (name is null) return;

        if (!FileOperations.TryRename(entry.FullPath, name, out string renamedPath, out string? renameError))
        {
            Vm.SetStatus(string.Format(Strings.Status_RenameFailed, renameError));
            return;
        }

        await RefreshAndSelectAsync([renamedPath]);
        Vm.SetStatus(string.Format(Strings.Status_Renamed, Path.GetFileName(renamedPath)));
    }

    internal async Task DeleteSelectionAsync(bool permanent)
    {
        var items = Vm.SelectedEntries;
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

        // 刷新会用新目录的统计重算状态栏，把失败原因覆盖掉——用户只看到"1 项"，
        // 根本不知道删除失败。失败结果必须重新写回去。
        if (!result.Ok) Vm.SetStatus(text);
    }

    internal void CopySelection(bool cut)
    {
        var items = Vm.SelectedEntries;
        if (items.Count == 0) return;

        FileClipboard.Set(items.Select(i => i.FullPath).ToArray(), cut);
        Vm.SetStatus(string.Format(cut ? Strings.Status_CutCount : Strings.Status_CopiedCount, items.Count));
        CommandManager.InvalidateRequerySuggested();
    }

    internal async Task PasteAsync()
    {
        if (!FileClipboard.HasData) return;

        string directory = Vm.CurrentPath;
        if (string.IsNullOrEmpty(directory)) return;

        string[] paths = FileClipboard.Paths.ToArray();
        bool cut = FileClipboard.IsCut;

        ConflictPolicy conflict = ResolveConflictPolicy(paths, directory);

        var (result, text) = await RunOperationAsync(
            () => cut
                ? FileOperations.MoveAsync(paths, directory, conflict)
                : FileOperations.CopyAsync(paths, directory, conflict),
            cut ? Strings.Status_Moving : Strings.Status_Copying,
            r => !r.Ok
                ? string.Format(cut ? Strings.Status_MoveFailed : Strings.Status_CopyFailed, r.Error)
                : r.Failed > 0
                    ? string.Format(Strings.Status_PartialFailed, r.Succeeded, r.Failed, r.Error)
                    : string.Format(cut ? Strings.Status_MovedCount : Strings.Status_CopiedCount, r.Succeeded));

        if (cut && result.Ok) FileClipboard.Clear();

        // 自动改名的情况下匹配不到，选中为空，不影响结果
        await RefreshAndSelectAsync(paths
            .Select(p => Path.Join(directory, Path.GetFileName(p)))
            .ToArray());

        // 同删除：全量失败或部分失败的原因不能被刷新覆盖
        if (!result.Ok || result.Failed > 0) Vm.SetStatus(text);
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
    private ConflictPolicy ResolveConflictPolicy(string[] paths, string directory)
    {
        IReadOnlyList<string> conflicts = FileOperations.FindConflicts(paths, directory);
        if (conflicts.Count == 0) return ConflictPolicy.KeepBoth;

        if (!App.Settings.Behavior.ConfirmOverwrite) return ConflictPolicy.Overwrite;

        string question = conflicts.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, Strings.Dialog_OverwriteOne, conflicts[0])
            : string.Format(CultureInfo.CurrentCulture, Strings.Dialog_OverwriteMany, conflicts.Count);

        return ConfirmWindow.Ask(Window.GetWindow(this), Strings.Dialog_OverwriteTitle, question)
            ? ConflictPolicy.Overwrite
            : ConflictPolicy.KeepBoth;
    }

    internal void CopyPathSelection()
    {
        var items = Vm.SelectedEntries;
        if (items.Count == 0) return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.FullPath)));
            Vm.SetStatus(string.Format(Strings.Status_PathsCopied, items.Count));
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
