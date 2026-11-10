using System.Windows.Input;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Localization;

namespace Lingpane.App.Views;

/// <summary>
/// 撤销（FR-6.8）在窗格侧的接线：一条命令、一个状态栏反馈，以及"什么时候记一步"。
///
/// 栈是窗口级的，两个窗格共用一份——撤销面向的是"最近一次操作"，
/// 不是"这个窗格的操作"。否则在右窗格删了文件、焦点回到左边按 Ctrl+Z，
/// 撤的会是左边更早的那一次，用户完全无法预期。
/// </summary>
public partial class PaneView
{
    private UndoStack? _undo;

    public UndoStack? Undo
    {
        get => _undo;
        set
        {
            if (ReferenceEquals(_undo, value)) return;

            if (_undo is not null) _undo.Changed -= OnUndoChanged;

            _undo = value;

            if (_undo is not null) _undo.Changed += OnUndoChanged;

            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OnUndoChanged() => CommandManager.InvalidateRequerySuggested();

    /// <summary>
    /// Ctrl+Z。撤销完刷新当前目录：磁盘已经变了，列表还停在原样会让人以为没成功。
    /// </summary>
    internal async Task UndoLastAsync()
    {
        if (_undo is not { CanUndo: true } undo)
        {
            Vm.SetStatusHold(Strings.Status_UndoNothing, OperationStatusHold);
            return;
        }

        // 撤销自己会改盘，目录监视因此还会再刷一次。屏蔽掉，省一次整表枚举，
        // 也免得两次刷新互相赛跑
        bool wasSuppressed = _suppressWatcher;
        _suppressWatcher = true;

        UndoOutcome outcome;
        try
        {
            outcome = await undo.UndoAsync();
            await Vm.RefreshAsync();
        }
        finally
        {
            _suppressWatcher = wasSuppressed;
        }

        string text = outcome switch
        {
            { Ok: true } => string.Format(Strings.Status_UndoDone, outcome.Undone),
            { Undone: > 0 } => string.Format(
                Strings.Status_UndoPartial, outcome.Undone, outcome.Skipped, outcome.Reason ?? string.Empty),
            _ => string.Format(Strings.Status_UndoFailed, outcome.Reason ?? string.Empty),
        };

        // 用"保持"而不是直接写：监视器那次刷新晚到一步（防抖 400 ms），
        // 直接写会被它盖成"12 项 · 枚举 3 ms"，等于什么都没说
        Vm.SetStatusHold(text, OperationStatusHold);
    }

    /// <summary>记一步"新建"。指纹在这里拍：拍晚了用户可能已经改过它。</summary>
    internal void RecordCreate(string path)
    {
        if (_undo is null || string.IsNullOrEmpty(path)) return;

        (long size, long ticks) = UndoStack.Snapshot(path);
        _undo.Push(UndoKind.Create, [new UndoStep(string.Empty, path, size, ticks)]);
    }

    /// <summary>记一步"粘贴"。只记确实落到盘上的那些项。</summary>
    internal void RecordTransfer(bool cut, IReadOnlyList<string> sources, IReadOnlyList<string> destinations)
    {
        if (_undo is null || destinations.Count == 0) return;

        var steps = new List<UndoStep>(destinations.Count);

        for (int i = 0; i < destinations.Count; i++)
        {
            string from = i < sources.Count ? sources[i] : string.Empty;

            if (cut)
            {
                // 移动的撤销是"搬回去"，不需要指纹：原样搬回即可
                steps.Add(new UndoStep(from, destinations[i]));
                continue;
            }

            (long size, long ticks) = UndoStack.Snapshot(destinations[i]);
            steps.Add(new UndoStep(from, destinations[i], size, ticks));
        }

        _undo.Push(cut ? UndoKind.Move : UndoKind.Copy, steps);
    }
}
