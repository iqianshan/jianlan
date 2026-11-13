using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jianlan.Core.FileSystem;

using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// 拖放：外部文件拖入（落到当前目录或某个文件夹行）、选中项拖出到其他程序。
///
/// 规则与资源管理器保持一致，用户不必重新学：
///   · 同卷移动、跨卷复制；
///   · 按住 Ctrl 强制复制，按住 Shift 强制移动；
///   · 拖到文件夹行上放进该文件夹，拖到空白处放进当前目录；
///   · 拖到自身或自身子目录一律拒绝（会造成无限递归）。
///
/// 磁盘操作全部交给 <see cref="FileOperations"/>，这里只做命中测试与效果提示。
/// </summary>
public partial class PaneView
{
    private Point _dragOrigin;
    private bool _dragArmed;

    private void OnFileDragStart(object sender, MouseButtonEventArgs e)
    {
        // 空白处按下 = 框选起手（FR-6.7），不是拖拽。
        // 若仍走下面的拖出逻辑，用户"重新圈一批"的意图会变成"把上一次选中的东西拖走"。
        if (IsBlankArea(e.OriginalSource as DependencyObject))
        {
            BeginRubberBand(e);
            return;
        }

        _dragArmed = true;
        _dragOrigin = e.GetPosition(FileList);

        // Ctrl+点文件夹是"新标签打开"（FR-2.10），不是多选。选中必须在**按下**就拦掉：
        // 让 ListBox 先处理的话，用户会看到文件夹被选中、同时又冒出一个标签。
        // 拖拽不受影响——真拖起来了，选中由 OnFileDragMove 自己补。
        //
        // 拦掉之后焦点也不会落到列表上，所以这里补一次 Focus：单窗格看不出来，
        // 分屏时"点哪边哪边成为活动窗格"的规矩不能因为改了手势就丢掉。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && HitFolder(e.OriginalSource) is not null)
        {
            e.Handled = true;
            FileList.Focus();
        }
    }

    private void OnFileDragMove(object sender, MouseEventArgs e)
    {
        // 框选优先：这一路与"拖出文件"互斥，见 BeginRubberBand 的说明
        if (_bandArmed)
        {
            UpdateRubberBand(e);
            return;
        }

        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;

        Point now = e.GetPosition(FileList);
        if (Math.Abs(now.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragArmed = false;

        // 起点行没被选中时先选中它，否则拖走的是上一批选中项
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item &&
            item.DataContext is FileEntry && !item.IsSelected)
        {
            FileList.SelectedItems.Clear();
            item.IsSelected = true;
        }

        var items = Vm.SelectedEntries;
        if (items.Count == 0) return;

        var data = new DataObject(DataFormats.FileDrop, items.Select(i => i.FullPath).ToArray());

        // Copy | Move 交给目标端决定；拖到资源管理器同名目录时由对方提示覆盖
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        DragDropEffects effect = ResolveExternalDropEffect(e, out string? target);
        e.Effects = effect;
        e.Handled = true;

        if (target is null || effect == DragDropEffects.None)
        {
            Vm.SetHint(string.Empty);
            return;
        }

        // 拖到文件夹行上时要让用户看清"放进哪个文件夹"，否则容易放错层
        string label = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar));
        if (label.Length == 0) label = target;

        Vm.SetHint(string.Format(
            effect == DragDropEffects.Move ? Strings.Hint_DragMoveTo : Strings.Hint_DragCopyTo, label));
    }

    private void OnFileDragLeave(object sender, DragEventArgs e) => Vm.SetHint(string.Empty);

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        Vm.SetHint(string.Empty);
        _dragArmed = false;

        DragDropEffects effect = ResolveExternalDropEffect(e, out string? target);
        if (target is null || effect == DragDropEffects.None) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] sources) return;

        bool move = effect == DragDropEffects.Move;

        var (result, text) = await RunOperationAsync(
            () => move
                ? FileOperations.MoveAsync(sources, target)
                : FileOperations.CopyAsync(sources, target),
            move ? Strings.Status_Moving : Strings.Status_Copying,
            r => !r.Ok
                ? string.Format(move ? Strings.Status_MoveFailed : Strings.Status_CopyFailed, r.Error)
                : r.Failed > 0
                    ? string.Format(Strings.Status_PartialFailed, r.Succeeded, r.Failed, r.Error)
                    : string.Format(move ? Strings.Status_MovedCount : Strings.Status_CopiedCount, r.Succeeded));

        if (result.Succeeded > 0) await RefreshAndSelectAsync([]);

        // 失败原因不能被随后的刷新覆盖
        if (!result.Ok || result.Failed > 0) Vm.SetStatus(text);
    }

    /// <summary>判定外部拖入的目标目录与效果。<paramref name="target"/> 为 null 表示不允许放下。</summary>
    private DragDropEffects ResolveExternalDropEffect(DragEventArgs e, out string? target)
    {
        target = null;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] sources || sources.Length == 0)
        {
            return DragDropEffects.None;
        }

        string? directory = ResolveDropDirectory(e);
        if (string.IsNullOrEmpty(directory)) return DragDropEffects.None;

        foreach (string source in sources)
        {
            if (Directory.Exists(source) && PathRules.IsSameOrAncestor(source, directory))
            {
                return DragDropEffects.None;
            }
        }

        // 源已经全在目标目录里：放下没有任何效果
        bool allInPlace = sources.All(s => string.Equals(
            Path.GetDirectoryName(s.TrimEnd(Path.DirectorySeparatorChar)),
            directory.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase));

        if (allInPlace) return DragDropEffects.None;

        target = directory;

        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;

        string targetRoot = Path.GetPathRoot(directory) ?? string.Empty;
        bool sameVolume = sources.All(s =>
            string.Equals(Path.GetPathRoot(s), targetRoot, StringComparison.OrdinalIgnoreCase));

        return sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    /// <summary>落在文件夹行上就放进该文件夹，否则放进当前目录。</summary>
    private string? ResolveDropDirectory(DragEventArgs e)
    {
        DependencyObject? hit = FileList.InputHitTest(e.GetPosition(FileList)) as DependencyObject;
        if (FindAncestor<ListBoxItem>(hit) is { DataContext: FileEntry { IsDirectory: true } folder })
        {
            return folder.FullPath;
        }

        return Vm.CurrentPath;
    }
}
