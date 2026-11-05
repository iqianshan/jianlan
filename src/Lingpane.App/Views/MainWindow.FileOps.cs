using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Lingpane.App.Services;
using Lingpane.Core.FileSystem;

namespace Lingpane.App.Views;

/// <summary>
/// 文件操作：新建 / 重命名 / 复制 / 剪切 / 粘贴 / 删除 / 复制路径 / 属性。
///
/// 分工：Core 的 <see cref="FileOperations"/> 只管磁盘与结果，本文件负责对话框、
/// 确认、状态栏文案与刷新后的选中恢复。界面层不碰磁盘细节，Core 不碰 UI。
/// </summary>
public partial class MainWindow
{
    private void OpenSelection()
    {
        if (_viewModel.SelectedEntries.Count == 0) return;

        OpenEntry(_viewModel.SelectedEntries[0]);
    }

    private void OpenEntry(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            _ = _viewModel.NavigateAsync(entry.FullPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            _viewModel.SetStatus($"无法打开「{entry.Name}」：{ex.Message}");
        }
    }

    private async Task NewFolderAsync()
    {
        string directory = _viewModel.CurrentPath;
        if (string.IsNullOrEmpty(directory)) return;

        if (!FileOperations.TryCreateFolder(directory, null, out string created, out string? error))
        {
            _viewModel.SetStatus("新建文件夹失败：" + error);
            return;
        }

        await RefreshAndSelectAsync([created]);

        // 建完立刻进重命名：默认名几乎总要改，省一次 F2
        await RenameEntryAsync(_viewModel.FindByPath(created));
    }

    private async Task NewFileAsync()
    {
        string directory = _viewModel.CurrentPath;
        if (string.IsNullOrEmpty(directory)) return;

        if (!FileOperations.TryCreateFile(directory, null, out string created, out string? error))
        {
            _viewModel.SetStatus("新建文件失败：" + error);
            return;
        }

        await RefreshAndSelectAsync([created]);

        // 默认名带了扩展名，重命名时选中词干部分，改完按回车即可
        await RenameEntryAsync(_viewModel.FindByPath(created));
    }

    private async Task RenameSelectionAsync()
    {
        if (_viewModel.SelectedEntries.Count != 1) return;
        await RenameEntryAsync(_viewModel.SelectedEntries[0]);
    }

    private async Task RenameEntryAsync(FileEntry? entry)
    {
        if (entry is null) return;

        string? name = TextPromptWindow.Ask(
            this,
            "重命名",
            $"为「{entry.Name}」输入新名称",
            entry.Name,
            value => FileOperations.TryValidateName(value, out string? error) ? null : error,
            selectStem: !entry.IsDirectory);

        if (name is null) return;

        if (!FileOperations.TryRename(entry.FullPath, name, out string renamedPath, out string? renameError))
        {
            _viewModel.SetStatus("重命名失败：" + renameError);
            return;
        }

        await RefreshAndSelectAsync([renamedPath]);
        _viewModel.SetStatus($"已重命名为「{Path.GetFileName(renamedPath)}」");
    }

    private async Task DeleteSelectionAsync(bool permanent)
    {
        var items = _viewModel.SelectedEntries;
        if (items.Count == 0) return;

        string what = items.Count == 1 ? $"「{items[0].Name}」" : $"这 {items.Count} 项";

        // 永久删除无论如何都要确认；普通删除看设置（默认开）
        if (permanent || App.Settings.Behavior.ConfirmDelete)
        {
            string question = permanent
                ? $"永久删除{what}？\n\n永久删除不进回收站，无法撤销。"
                : $"将{what}移到回收站？";

            if (MessageBox.Show(this, question, permanent ? "永久删除" : "删除",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        string[] paths = items.Select(i => i.FullPath).ToArray();

        await RunOperationAsync(
            () => FileOperations.DeleteAsync(paths, permanent),
            permanent ? "正在永久删除…" : "正在删除…",
            result => result.Ok ? $"已删除 {result.Succeeded} 项" : $"删除失败：{result.Error}");

        await RefreshAndSelectAsync([]);
    }

    private void CopySelection(bool cut)
    {
        var items = _viewModel.SelectedEntries;
        if (items.Count == 0) return;

        FileClipboard.Set(items.Select(i => i.FullPath).ToArray(), cut);
        _viewModel.SetStatus(cut ? $"已剪切 {items.Count} 项" : $"已复制 {items.Count} 项");
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task PasteAsync()
    {
        if (!FileClipboard.HasData) return;

        string directory = _viewModel.CurrentPath;
        if (string.IsNullOrEmpty(directory)) return;

        string[] paths = FileClipboard.Paths.ToArray();
        bool cut = FileClipboard.IsCut;

        var result = await RunOperationAsync(
            () => cut ? FileOperations.MoveAsync(paths, directory) : FileOperations.CopyAsync(paths, directory),
            cut ? "正在移动…" : "正在复制…",
            r => !r.Ok
                ? $"{(cut ? "移动" : "复制")}失败：{r.Error}"
                : r.Failed > 0
                    ? $"完成 {r.Succeeded} 项，{r.Failed} 项失败：{r.Error}"
                    : $"已{(cut ? "移动" : "复制")} {r.Succeeded} 项");

        if (cut && result.Ok) FileClipboard.Clear();

        // 自动改名的情况下匹配不到，选中为空，不影响结果
        await RefreshAndSelectAsync(paths
            .Select(p => Path.Join(directory, Path.GetFileName(p)))
            .ToArray());
    }

    private void CopyPathSelection()
    {
        var items = _viewModel.SelectedEntries;
        if (items.Count == 0) return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.FullPath)));
            _viewModel.SetStatus($"已复制 {items.Count} 个完整路径");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // 剪贴板被其他程序占用时 SetText 会抛 COMException，不能让它冒泡
            _viewModel.SetStatus("复制路径失败：" + ex.Message);
        }
    }

    private void ShowProperties()
    {
        var items = _viewModel.SelectedEntries;
        if (items.Count == 0) return;

        if (items.Count > 1)
        {
            long total = items.Sum(i => i.Size);
            MessageBox.Show(this,
                $"共 {items.Count} 项\n总计大小：{FileEntry.FormatSizeText(total)}",
                "属性", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var entry = items[0];
        string size = entry.IsDirectory ? "文件夹（大小不计）" : FileEntry.FormatSizeText(entry.Size);
        string parent = Path.GetDirectoryName(entry.FullPath) ?? _viewModel.CurrentPath;

        MessageBox.Show(this,
            $"名称：{entry.Name}\n位置：{parent}\n类型：{entry.TypeText}\n大小：{size}\n" +
            $"修改时间：{entry.LastWriteTime:yyyy-MM-dd HH:mm:ss}",
            "属性", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 执行一次会改动磁盘的操作：期间暂停目录监听（否则自己的写操作会反复触发刷新），
    /// 结束后统一把结果写进状态栏。
    /// </summary>
    private async Task<FileOpResult> RunOperationAsync(Func<Task<FileOpResult>> operation,
        string runningText, Func<FileOpResult, string> resultText)
    {
        _suppressWatcher = true;
        _viewModel.SetStatus(runningText);

        try
        {
            var result = await operation();
            _viewModel.SetStatus(resultText(result));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.SetStatus("操作失败：" + ex.Message);
            return FileOpResult.Failure(ex.Message);
        }
        finally
        {
            _suppressWatcher = false;
        }
    }

    /// <summary>刷新当前目录并按路径恢复选中。刷新会整表替换，选中必须重设。</summary>
    private async Task RefreshAndSelectAsync(IReadOnlyList<string> selectPaths)
    {
        await _viewModel.RefreshAsync();
        SelectPaths(selectPaths);
    }

    private void SelectPaths(IReadOnlyList<string> selectPaths)
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
            if (_viewModel.FindByPath(path) is not { } entry) continue;

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
