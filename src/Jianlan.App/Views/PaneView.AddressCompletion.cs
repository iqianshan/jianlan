// i18n-ignore-file：本文件的中文字符串只出现在注释里，界面文字走 Strings 资源。
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Localization;
using Jianlan.Core.Navigation;

namespace Jianlan.App.Views;

/// <summary>
/// 地址栏自动补全的视图侧（FR-6.1）。判定逻辑在 <see cref="PathCompletion"/>（Core，可单测），
/// 这里只做三件事：防抖、超时、把候选摆到 Popup 里并接键盘。
///
/// 单独成一个文件是因为它有一套自己的状态（防抖 token、序号、候选列表），
/// 混进 <c>PaneView.xaml.cs</c> 里会和导航、文件操作那些逻辑互相干扰——
/// 最典型的是"上一次输入的回包打到了新一次输入上"，那种 bug 很难在原文件里看出来。
/// </summary>
public partial class PaneView
{
    /// <summary>
    /// 防抖窗口。比搜索的 250ms 短：地址栏的输入节奏是"敲几个字就回车"，
    /// 等 250ms 用户已经敲完了，补全等于没出现过。
    /// </summary>
    private static readonly TimeSpan CompletionDebounce = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// 探测超时。网络盘上 <c>EnumerateDirectories</c> 能挂几十秒，
    /// 超时这一轮就退回"只用当前目录缓存"，宁可少给几条，也不让地址栏失去响应。
    /// </summary>
    private static readonly TimeSpan CompletionProbeTimeout = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _completionCts;
    private int _completionSeq;
    private IReadOnlyList<CompletionItem> _completionItems = [];

    /// <summary>
    /// 进入编辑态时的路径。文本与它相同说明用户还没改过任何东西，
    /// 此时不该弹候选——Ctrl+L 一按就盖住面包屑，等于挡住了用户刚要看的东西。
    /// </summary>
    private string _addressSnapshot = string.Empty;

    /// <summary>代码自己改写 <see cref="AddressBox"/> 文本时抑制一次补全，避免"补全触发补全"。</summary>
    private bool _suppressAutoComplete;

    private async void OnAddressTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressAutoComplete)
        {
            _suppressAutoComplete = false;
            return;
        }

        if (_viewModel is null) return;

        string text = AddressBox.Text;

        // 还没进编辑态，或者内容仍是进入时的路径：不补全
        if (!_viewModel.IsAddressEditing ||
            string.Equals(text, _addressSnapshot, StringComparison.Ordinal))
        {
            CloseCompletion();
            return;
        }

        CancellationTokenSource cts = new();
        CancellationTokenSource? previous = _completionCts;
        _completionCts = cts;
        previous?.Cancel();
        previous?.Dispose();

        int seq = ++_completionSeq;

        try
        {
            await Task.Delay(CompletionDebounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        string currentPath = _viewModel.CurrentPath;

        // 快照之后不能再碰 _viewModel.Entries：枚举在后台线程跑，而 Entries 会被导航替换，
        // 读到一半换掉会拿到半新半旧的列表
        IReadOnlyList<FileEntry> entries = _viewModel.Entries;

        IReadOnlyList<PathCandidate> candidates;
        try
        {
            candidates = await Task.Run(
                    () => PathCompletion.Complete(text, currentPath, entries, ProbeDirectories),
                    cts.Token)
                .WaitAsync(CompletionProbeTimeout, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TimeoutException)
        {
            // 探测这一轮放弃，但当前目录的缓存候选还要给：它零 IO，不受网络盘影响。
            // 后台那次枚举仍在跑完为止，它的结果不会被采用，也不会碰 UI。
            candidates = PathCompletion.Complete(text, currentPath, entries, probe: null);
        }

        // 三重复核，缺一不可：
        //   1. 这轮没被更新的输入取消（用户在防抖窗口后又敲了一个字）
        //   2. 文本仍是发起时的文本（防止旧结果覆盖新输入）
        //   3. 控件还活着（窗格可能已被切走）
        if (seq != _completionSeq || cts.IsCancellationRequested) return;
        if (!IsLoaded || !string.Equals(AddressBox.Text, text, StringComparison.Ordinal)) return;

        ShowCompletion(candidates);
    }

    /// <summary>
    /// 目录探测。默认选项会跳过重解析点：<c>C:\Users\All Users</c> 这类联接点指回自己，
    /// 列进候选只会让用户选到一个看起来一样、实际是别处的路径。
    /// </summary>
    private static IReadOnlyList<string> ProbeDirectories(string directory, string pattern)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = false,
        };

        var found = new List<string>(16);
        foreach (string path in Directory.EnumerateDirectories(directory, pattern, options))
        {
            found.Add(path);
            if (found.Count >= PathCompletion.MaxProbeEntries) break;
        }

        return found;
    }

    private void ShowCompletion(IReadOnlyList<PathCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            CloseCompletion();
            return;
        }

        _completionItems = candidates.Select(c => new CompletionItem(c)).ToList();
        CompletionList.ItemsSource = _completionItems;
        CompletionList.SelectedIndex = -1;
        CompletionPopup.IsOpen = true;
    }

    private void CloseCompletion()
    {
        CompletionPopup.IsOpen = false;
        CompletionList.ItemsSource = null;
        _completionItems = [];
    }

    private bool IsCompletionOpen => CompletionPopup.IsOpen && _completionItems.Count > 0;

    /// <summary>
    /// 候选区的键盘处理。返回 true 表示这个键已经被补全用掉，原逻辑不要再管。
    ///
    /// Enter 是唯一的例外：它在这里只负责"把选中项写回地址栏"，然后交回原逻辑去导航。
    /// 在这里自己导航等于把"路径不存在"的检查复制一份，两份迟早不一致。
    /// </summary>
    private bool TryHandleCompletionKey(KeyEventArgs e)
    {
        if (!IsCompletionOpen) return false;

        switch (e.Key)
        {
            case Key.Down:
                CompletionList.SelectedIndex =
                    Math.Min(CompletionList.SelectedIndex + 1, _completionItems.Count - 1);
                e.Handled = true;
                return true;

            case Key.Up:
                CompletionList.SelectedIndex = Math.Max(CompletionList.SelectedIndex - 1, -1);
                e.Handled = true;
                return true;

            case Key.Tab:
                // 没选中时补第一条：Tab 是"接受建议"的通用键，要求用户先按 ↓ 再 Tab 太啰嗦
                CompletionItem item = SelectedCompletionItem ?? _completionItems[0];
                ApplyCompletionText(item);
                e.Handled = true;
                return true;

            case Key.Escape:
                CloseCompletion();
                e.Handled = true;
                return true;

            case Key.Enter when SelectedCompletionItem is { } chosen:
                ApplyCompletionText(chosen);
                return false;   // 交给原有 Enter 分支去导航

            default:
                return false;
        }
    }

    private CompletionItem? SelectedCompletionItem =>
        CompletionList.SelectedIndex >= 0 && CompletionList.SelectedIndex < _completionItems.Count
            ? _completionItems[CompletionList.SelectedIndex]
            : null;

    /// <summary>
    /// 把候选写回地址栏，但不导航、不退出编辑态。
    /// 目录会补一个尾分隔符——用户接下来要写的是下一层，少了这个 <c>\</c>，
    /// 下一次补全会把 <c>D:\work</c> 当成待展开的词干继续匹配。
    /// </summary>
    private void ApplyCompletionText(CompletionItem item)
    {
        _suppressAutoComplete = true;
        AddressBox.Text = PathCompletion.ToInputText(item.Candidate);
        AddressBox.CaretIndex = AddressBox.Text.Length;
        CloseCompletion();
        AddressBox.Focus();
    }

    private void OnCompletionItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        var container = FindAncestor<ListBoxItem>(source);
        if (container?.DataContext is not CompletionItem item) return;

        e.Handled = true;

        if (!item.Candidate.IsDirectory)
        {
            // 文件候选只填文本：把文件路径喂给 NavigateAsync 只会得到"路径不存在"，
            // 而"打开文件"不在 FR-6.1 的范围内
            ApplyCompletionText(item);
            return;
        }

        _suppressAutoComplete = true;
        AddressBox.Text = PathCompletion.ToInputText(item.Candidate);
        CloseCompletion();
        EndAddressEdit();

        if (_viewModel is not null) _ = _viewModel.NavigateAsync(item.Candidate.FullPath);
    }

    /// <summary>
    /// 候选行。补全只需要"名字 + 是目录还是文件"，但类型要显示成文字而不是只靠图标——
    /// 同名目录与文件并排时，图标是最容易被看漏的信息。
    ///
    /// 图标复用列表行的同一套键（<see cref="FileEntry.ResolveIconKey"/>）与同一个转换器，
    /// 这样补全里的文件图标与列表里的一致；自己挑字形（例如 MDL2 的 E8B7）
    /// 会在某些字体下渲染成完全不同的形状，用户反而更难认。
    /// </summary>
    private sealed class CompletionItem(PathCandidate candidate)
    {
        public PathCandidate Candidate { get; } = candidate;

        public string Name => Candidate.Name;

        public string IconToken { get; } = FileEntry.ResolveIconKey(candidate.Name, candidate.IsDirectory);

        public string KindText => Candidate.IsDirectory ? Strings.Type_Folder : Strings.Type_File;
    }
}
