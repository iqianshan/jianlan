using System.Collections.ObjectModel;
using System.ComponentModel;
using Lingpane.Core.Settings;

namespace Lingpane.App.ViewModels;

/// <summary>
/// 标签集合（FR-2）。持有一到多个 <see cref="TabViewModel"/>（每个标签内含 1–2 个窗格）
/// 与"当前是哪个"。
///
/// 为什么视图模型保活、视图壳只有一份：每个标签的路径、历史、选中项、滚动位置
/// 都活在视图模型里，切换标签只是把同一个 <c>PaneView</c> 重新绑到另一份视图模型
/// （见 PaneView.Attach）。这样 12 个标签不会堆 12 套工具栏/列表控件树——
/// 需求给的单标签视图壳预算是 3MB，一套控件树就吃掉大半。
///
/// 本类只管"有哪些标签、谁是当前的"，不碰界面：切换动作通过
/// <see cref="ActiveChanged"/> 通知宿主，由宿主去重新绑定视图。
/// </summary>
public sealed class TabHostViewModel : INotifyPropertyChanged
{
    /// <summary>最近关闭的标签保留几个用于 Ctrl+Shift+T（FR-2.3 / shortcuts §1）。</summary>
    private const int ClosedHistoryLimit = 10;

    private readonly AppSettings _settings;
    private readonly Func<string> _defaultPath;
    private readonly Stack<string> _closed = [];

    private TabViewModel? _active;

    /// <param name="settings">应用设置，新建标签时交给视图模型。</param>
    /// <param name="defaultPath">
    /// 新标签/回到主页时打开的目录（FR-2.1）。由宿主提供而不是在这里算——
    /// "默认目录"要读设置、命令行参数与已知文件夹，那是宿主的知识。
    /// </param>
    public TabHostViewModel(AppSettings settings, Func<string> defaultPath)
    {
        _settings = settings;
        _defaultPath = defaultPath;

        // 标签数直接决定"还能不能新建"，两个按钮/快捷键都要看它有没有变
        Tabs.CollectionChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFull)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAdd)));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>活动标签变化：(旧, 新)。宿主据此重新绑定视图与窗口标题。</summary>
    public event Action<TabViewModel?, TabViewModel?>? ActiveChanged;

    /// <summary>关掉了最后一个标签且设置为"关闭窗口"（FR-2.8）。</summary>
    public event Action? CloseWindowRequested;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public TabViewModel? Active
    {
        get => _active;
        private set
        {
            if (ReferenceEquals(_active, value)) return;
            _active = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
        }
    }

    /// <summary>
    /// 新建标签并激活（FR-2.1）。<paramref name="path"/> 为空时用默认目录。
    /// 已到上限（FR-2.5，12 个）时返回 <c>null</c>——由调用方决定怎么提示，
    /// 视图模型不弹 UI 文案。
    /// </summary>
    public TabViewModel? Add(string? path = null, bool activate = true)
    {
        if (IsFull) return null;

        string target = string.IsNullOrWhiteSpace(path) ? _defaultPath() : path;

        // 不在这里导航：调用方（`+` 按钮、Ctrl+T、会话恢复）时机各不相同，
        // 统一由宿主在激活后决定"现在读盘还是等激活再读"。
        var tab = new TabViewModel(_settings, target);

        Tabs.Add(tab);
        if (activate) Activate(tab);

        return tab;
    }

    /// <summary>标签数是否已达上限（FR-2.5）。</summary>
    public bool IsFull => Tabs.Count >= SessionSettings.MaxTabs;

    /// <summary>
    /// 还能不能再新建一个标签。给界面的 `+` 按钮用：到上限时按钮变灰，
    /// 比"点了没反应"清楚。Ctrl+T 与外部的"在新标签打开"仍要各自判一次。
    /// </summary>
    public bool CanAdd => Tabs.Count < SessionSettings.MaxTabs;

    /// <summary>恢复会话用：只建标签、只记路径，不读盘也不激活。</summary>
    public TabViewModel AddPending(string path)
    {
        var tab = new TabViewModel(_settings, path);
        Tabs.Add(tab);
        return tab;
    }

    public void Activate(TabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab)) return;

        TabViewModel? previous = _active;
        if (ReferenceEquals(previous, tab)) return;

        if (previous is not null) previous.IsTabActive = false;
        tab.IsTabActive = true;
        Active = tab;

        ActiveChanged?.Invoke(previous, tab);
    }

    /// <summary>Ctrl+1…8 跳转（0 起始的索引，越界时忽略而不是夹取到末端）。</summary>
    public void ActivateAt(int index)
    {
        if (index < 0 || index >= Tabs.Count) return;
        Activate(Tabs[index]);
    }

    public void ActivateNext() => StepActivate(+1);

    public void ActivatePrevious() => StepActivate(-1);

    /// <summary>Ctrl+9：跳到最后一个标签，而不是第 9 个（与浏览器一致）。</summary>
    public void ActivateLast()
    {
        if (Tabs.Count > 0) Activate(Tabs[^1]);
    }

    /// <summary>循环切换：到末尾回到第一个，反之同理。</summary>
    private void StepActivate(int delta)
    {
        if (Tabs.Count == 0) return;

        int index = _active is null ? -1 : Tabs.IndexOf(_active);
        if (index < 0) index = 0;
        else index = (index + delta + Tabs.Count) % Tabs.Count;

        Activate(Tabs[index]);
    }

    /// <summary>
    /// 关闭一个标签（FR-2.3 / FR-2.8）。关的是最后一个时两种去处：
    /// 回到默认目录（默认），或关窗口（设置项）。
    /// </summary>
    public void Close(TabViewModel? tab)
    {
        if (tab is null) return;

        int index = Tabs.IndexOf(tab);
        if (index < 0) return;

        if (Tabs.Count == 1)
        {
            if (_settings.Behavior.CloseLastTabToHome)
            {
                // 不销毁这个标签，只把它送回默认目录：用户刚关掉的是"这个地方"，
                // 不是"这个窗口"。历史栈保留，Alt+← 还能回退到刚才关掉的目录。
                RememberClosed(tab.Primary.CurrentPath);
                _ = tab.Primary.NavigateAsync(_defaultPath());
                return;
            }

            CloseWindowRequested?.Invoke();
            return;
        }

        RememberClosed(tab.Primary.CurrentPath);

        bool wasActive = ReferenceEquals(_active, tab);
        Tabs.RemoveAt(index);

        if (wasActive)
        {
            // 关掉当前标签后落到右邻居；关的是最后一个则落到新的最后一个
            int next = Math.Min(index, Tabs.Count - 1);
            _active = null;
            Activate(Tabs[next]);
        }
    }

    /// <summary>
    /// 关闭其他（FR-2.12）。**固定标签不参与**（FR-2.9）：固定的意义就是"这个我不要被顺手关掉"，
    /// 让它被"关闭其他"带走等于功能失效。
    /// </summary>
    public void CloseOthers(TabViewModel? keep)
    {
        if (keep is null || !Tabs.Contains(keep)) return;

        foreach (TabViewModel tab in Tabs
                     .Where(t => !ReferenceEquals(t, keep) && !t.IsPinned)
                     .ToList())
        {
            RememberClosed(tab.Primary.CurrentPath);
            Tabs.Remove(tab);
        }

        _active = null;
        Activate(keep);
    }

    /// <summary>关闭右侧（FR-2.12）。固定标签同样跳过；关的是当前最后一个时什么都不做。</summary>
    public void CloseToRight(TabViewModel? tab)
    {
        if (tab is null) return;

        int index = Tabs.IndexOf(tab);
        if (index < 0 || index == Tabs.Count - 1) return;

        foreach (TabViewModel right in Tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList())
        {
            RememberClosed(right.Primary.CurrentPath);
            Tabs.Remove(right);
        }

        // 现在的位置可能已经失效（关掉的是活动标签），统一回到被点的那个
        _active = null;
        Activate(tab);
    }

    /// <summary>拖拽排序（FR-2.6）。索引均为 0 起始，<paramref name="to"/> 是"插入到这个位置之前"。</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Tabs.Count) return;

        // 拖到自己身上，或拖到自己的右侧相邻位（等于没动）
        if (to < 0) to = 0;
        if (to > Tabs.Count - 1) to = Tabs.Count - 1;
        if (from == to) return;

        Tabs.Move(from, to);
    }

    /// <summary>Ctrl+Shift+T：重新打开最近关闭的标签（只保留路径）。</summary>
    public void ReopenClosed()
    {
        if (_closed.Count == 0 || IsFull) return;

        string path = _closed.Pop();
        TabViewModel? tab = Add(path, activate: true);
        if (tab is not null) _ = tab.Primary.NavigateAsync(path);
    }

    /// <summary>退出时写进会话的路径列表（空的标签不写，否则下次启动多出一个没内容的标签）。</summary>
    public List<string> SnapshotPaths()
        => Tabs.Select(t => t.Primary.CurrentPath)
               .Where(p => !string.IsNullOrWhiteSpace(p))
               .Take(SessionSettings.MaxTabs)
               .ToList();

    /// <summary>
    /// 第二窗格路径，与 <see cref="SnapshotPaths"/> 等长（没有第二窗格的位置写空串）。
    /// 两个列表必须一起取：分两次遍历可能因为期间有人关了标签而对不齐。
    /// </summary>
    public List<string> SnapshotSecondPaths()
        => Tabs.Select(t => t.SnapshotPaths())
               .Where(p => !string.IsNullOrWhiteSpace(p.Primary))
               .Select(p => p.Secondary)
               .Take(SessionSettings.MaxTabs)
               .ToList();

    /// <summary>固定标记，与 <see cref="SnapshotPaths"/> 等长（FR-2.9）。</summary>
    public List<bool> SnapshotPinned()
        => Tabs.Where(t => !string.IsNullOrWhiteSpace(t.Primary.CurrentPath))
               .Select(t => t.IsPinned)
               .Take(SessionSettings.MaxTabs)
               .ToList();

    public int ActiveIndex
    {
        get
        {
            int index = _active is null ? 0 : Tabs.IndexOf(_active);
            return index < 0 ? 0 : index;
        }
    }

    private void RememberClosed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Stack 没有容量上限的控制，超了就重建一份只留最近若干条
        if (_closed.Count >= ClosedHistoryLimit)
        {
            string[] keep = _closed.Take(ClosedHistoryLimit - 1).Reverse().ToArray();
            _closed.Clear();
            foreach (string item in keep) _closed.Push(item);
        }

        _closed.Push(path);
    }
}
