using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jianlan.App.ViewModels;
using Jianlan.Core.Localization;
using Jianlan.Core.Settings;
using Jianlan.Core.Workspaces;

namespace Jianlan.App.Views;

/// <summary>
/// 工作区的交互（FR-8）：保存当前标签、打开、覆盖、重命名、删除、设为启动恢复。
///
/// 与收藏（FR-7）的分工：收藏是"一个位置"，工作区是"一整套标签"。
/// 数据落在独立的 <c>workspaces.json</c>，**首帧不读**——需求明确要求它不参与
/// 冷启动默认路径，所以导航栏与主页先按"没有工作区"渲染，由
/// <see cref="InitializeWorkspaces"/> 在首帧之后补上（唯一的例外见 <see cref="RestoreStartupWorkspace"/>）。
///
/// 没有单独的"管理工作区"窗口：列表在导航栏与主页各有一份，数量（行标题）
/// 与最后保存时间（悬停提示）都在行上，重命名/覆盖/删除走右键菜单——
/// 再开一个窗口只会让同一份数据多出一处要同步的地方（FR-8.3 的验收点已被覆盖）。
/// </summary>
public partial class MainWindow
{
    private WorkspaceStore? _workspaces;

    private WorkspaceStore Workspaces => _workspaces ??= App.Workspaces;

    /// <summary>
    /// 接上工作区数据。首帧之后调用：读一次 workspaces.json，让导航栏与主页各补一段。
    /// 读到问题时只记日志并在状态栏提示，不拦任何操作——工作区坏了不该让程序不可用。
    /// </summary>
    private void InitializeWorkspaces()
    {
        WorkspaceStore store = Workspaces;

        if (store.LoadWarning is not null)
        {
            Diagnostics.AppLog.Warn("workspace.load", "E9003", store.LoadWarning);  // i18n-ignore：只进 app.log
            Vm.SetStatus(store.LoadWarning);
        }

        string? startup = App.Settings.Behavior.StartupWorkspace;

        // 指向的工作区已经不在了：清掉设置，否则每次启动都会白读一次文件、什么都不发生
        if (startup is not null && !store.Contains(startup))
        {
            App.Settings.Behavior.StartupWorkspace = null;
            App.SaveSettings();
        }

        _nav.SetWorkspaces(store, App.Settings.Behavior.StartupWorkspace);
        _home.SetWorkspaces(store, App.Settings.Behavior.StartupWorkspace);
    }

    /// <summary>
    /// 启动时自动恢复工作区（FR-8.4）。返回 true 表示已经接管了标签恢复，
    /// 调用方（<c>RestoreTabs</c>）就不该再按会话恢复。
    ///
    /// 这是**唯一在首帧读 workspaces.json 的路径**，而且是用户显式开启的：
    /// 需求允许"启用自动恢复时读取"，其余时候文件不进冷启动。
    /// </summary>
    private bool RestoreStartupWorkspace()
    {
        string? name = App.Settings.Behavior.StartupWorkspace;
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (Workspaces.Find(name) is not { } workspace)
        {
            // 文件被删/改名了：当没设置过，继续走会话恢复，别让用户卡在主页
            App.Settings.Behavior.StartupWorkspace = null;
            App.SaveSettings();
            return false;
        }

        TabViewModel[] created = BuildWorkspaceTabs(workspace, out _);
        _tabs.ReplaceWith(created.Length > 0 ? created : [_tabs.AddPendingHome()],
            Math.Clamp(workspace.ActiveTabIndex, 0, Math.Max(0, created.Length - 1)));
        ApplyWorkspaceSplit(workspace);
        return true;
    }

    /// <summary>
    /// 还原双窗格形态（FR-8.2）。工作区里只要有一个标签记着第二窗格，就说明存的时候是分屏的。
    ///
    /// 不去 workspaces.json 里单独存一个 splitEnabled 开关：分屏与"某个标签有第二窗格"
    /// 是同一件事，存两份必然会出现"开关说分屏、标签都没有第二窗格"这种自相矛盾的状态。
    /// </summary>
    private void ApplyWorkspaceSplit(Workspace workspace)
    {
        bool wanted = workspace.Tabs.Exists(t => !string.IsNullOrEmpty(t.Second));
        if (wanted == _splitEnabled) return;

        _splitEnabled = wanted;
        App.Settings.Layout.SplitEnabled = wanted;

        if (wanted && _tabs.Active is { IsHome: false } active)
        {
            // 与 ToggleSplit 走同一条路：只为当前标签建第二窗格，其余等被激活时再建
            BindTab(active);
            ApplySplitLayout();
            if (_started) _ = LoadPanesAsync(active);
        }
        else if (!wanted)
        {
            foreach (TabViewModel tab in _tabs.Tabs) tab.DropSecondary();
            Pane2.Release();
            Pane2.Visibility = Visibility.Collapsed;
            ApplySplitLayout();
        }

        RefreshPaneFocus();
        SyncSplitButton();
        App.SaveSettings();
    }

    /// <summary>把当前全部标签收成工作区的一份快照（FR-8.1）。主页标签不进快照：它没有路径。</summary>
    private List<WorkspaceTab> CollectWorkspaceTabs()
    {
        var list = new List<WorkspaceTab>();

        foreach (TabViewModel tab in _tabs.Tabs)
        {
            if (tab.Kind != TabKind.Files) continue;

            (string primary, string second) = tab.SnapshotPaths();
            if (string.IsNullOrEmpty(primary)) continue;

            list.Add(new WorkspaceTab
            {
                Path = primary,
                Second = string.IsNullOrEmpty(second) ? null : second,
                Pinned = tab.IsPinned,
            });
        }

        return list;
    }

    /// <summary>
    /// 按工作区的记录建标签。只建"壳"（<see cref="TabHostViewModel.AddPending"/>），
    /// 窗格内容等标签被激活时才加载——打开一个 12 标签的工作区不该在首帧建 24 个窗格
    /// （FR-8.2 的"标签逐个就绪"）。
    /// </summary>
    private TabViewModel[] BuildWorkspaceTabs(Workspace workspace, out int skipped)
    {
        var created = new List<TabViewModel>();
        skipped = 0;

        foreach (WorkspaceTab entry in workspace.Tabs)
        {
            // 标签数上限（<see cref="SessionSettings.MaxTabs"/>）是程序级约束，
            // 手改过的工作区可能有 50 个：超出的部分打不开，由调用方提示
            if (_tabs.IsFull)
            {
                skipped++;
                continue;
            }

            if (!Directory.Exists(entry.Path))
            {
                skipped++;
                continue;
            }

            TabViewModel tab = _tabs.AddPending(entry.Path);
            tab.IsPinned = entry.Pinned;

            if (!string.IsNullOrEmpty(entry.Second) && Directory.Exists(entry.Second))
            {
                tab.PrepareSecondaryPending(entry.Second);
            }

            created.Add(tab);
        }

        return [.. created];
    }

    /// <summary>
    /// 打开工作区列表（Ctrl+Alt+O）。列表在主页的工作区卡片区与侧栏「工作区」分区各有一份，
    /// 这里把用户送到主页那份；当前标签已经是主页就直接复用，不再叠一个空标签。
    ///
    /// 不用 <c>Tab.ShowHome()</c> 就地替换：那会把正在浏览的目录换成主页，
    /// 按一个"打开列表"的键不该丢掉当前位置。
    /// </summary>
    private void OpenWorkspaceList()
    {
        if (!Tab.IsHome) _tabs.Activate(_tabs.AddPendingHome());

        Vm.SetStatusHold(Strings.Status_Workspace_ListHint, TimeSpan.FromSeconds(1.6));
    }

    /// <summary>保存当前标签为一份新工作区（FR-8.1 / 8.3）。</summary>
    private void SaveCurrentWorkspace()
    {
        List<WorkspaceTab> tabs = CollectWorkspaceTabs();
        if (tabs.Count == 0)
        {
            Vm.SetStatus(Strings.Status_Workspace_NoTabs);
            return;
        }

        string suggested = Tab.IsHome || string.IsNullOrEmpty(Vm.CurrentPath)
            ? string.Empty
            : Path.GetFileName(Vm.CurrentPath.TrimEnd(Path.DirectorySeparatorChar));

        string? name = TextPromptWindow.Ask(this,
            Strings.Workspace_Prompt_SaveTitle,
            Strings.Workspace_Prompt_Name,
            suggested,
            value => Workspaces.Contains(value) ? Strings.Workspace_Prompt_NameExists : null);

        if (name is null) return;

        switch (Workspaces.SaveAs(name, tabs, _tabs.ActiveIndex))
        {
            case WorkspaceSaveResult.Saved:
                Vm.SetStatus(string.Format(Strings.Status_Workspace_Saved, name));
                ApplyWorkspaceChange();
                break;

            case WorkspaceSaveResult.Duplicate:
                if (ConfirmWindow.Ask(this, Strings.Dialog_Workspace_OverwriteTitle,
                        string.Format(Strings.Dialog_Workspace_OverwriteMessage, name)))
                {
                    OverwriteWorkspace(name);
                }

                break;

            case WorkspaceSaveResult.Full:
                Vm.SetStatus(string.Format(Strings.Status_Workspace_Full, WorkspacesDocument.MaxWorkspaces));
                break;

            default:
                Vm.SetStatus(Strings.Status_Workspace_NoTabs);
                break;
        }
    }

    /// <summary>用当前标签覆盖已有工作区（FR-8.3）。</summary>
    private void OverwriteWorkspace(string name)
    {
        List<WorkspaceTab> tabs = CollectWorkspaceTabs();
        if (tabs.Count == 0)
        {
            Vm.SetStatus(Strings.Status_Workspace_NoTabs);
            return;
        }

        if (Workspaces.Overwrite(name, tabs, _tabs.ActiveIndex))
        {
            Vm.SetStatus(string.Format(Strings.Status_Workspace_Saved, name));
            ApplyWorkspaceChange();
        }
    }

    /// <summary>
    /// 打开一份工作区（FR-8.2）：先建新标签，再整体换掉旧的。
    ///
    /// 顺序不能反：关掉全部标签会触发"关最后一个标签"的策略（回主页或关窗口），
    /// 中途出现零标签会让整个窗口退出。
    /// </summary>
    private void OpenWorkspace(Workspace workspace)
    {
        // 只有主页、或压根没有标签时没什么可丢的，不必打断；其余情况必须先问一句
        bool needsConfirm = _tabs.Tabs.Count > 1 || (_tabs.Active is { IsHome: false });

        if (needsConfirm && !ConfirmWindow.Ask(this, Strings.Dialog_Workspace_ReplaceTitle,
                string.Format(Strings.Dialog_Workspace_ReplaceMessage, workspace.Name, _tabs.Tabs.Count)))
        {
            return;
        }

        TabViewModel[] created = BuildWorkspaceTabs(workspace, out int skipped);

        if (created.Length == 0)
        {
            // 全部失效：不动当前标签，只说明情况。清空用户正在看的东西是最差的选择
            Vm.SetStatus(Strings.Status_Workspace_Missing);
            return;
        }

        _tabs.ReplaceWith(created, Math.Clamp(workspace.ActiveTabIndex, 0, created.Length - 1));
        ApplyWorkspaceSplit(workspace);

        string status = string.Format(Strings.Status_Workspace_Restored, workspace.Name, created.Length);
        if (skipped > 0) status += " · " + string.Format(Strings.Status_Workspace_Skipped, skipped);
        Vm.SetStatus(status);
    }

    private void RenameWorkspace(Workspace workspace)
    {
        string? name = TextPromptWindow.Ask(this,
            Strings.Workspace_Prompt_RenameTitle,
            Strings.Workspace_Prompt_Name,
            workspace.Name,
            value => Workspaces.Contains(value) ? Strings.Workspace_Prompt_NameExists : null,
            selectStem: true);

        if (name is null || name == workspace.Name) return;

        if (!Workspaces.Rename(workspace.Name, name))
        {
            Vm.SetStatus(Strings.Status_Workspace_Duplicate);
            return;
        }

        // 改的正好是"启动时恢复"的那一份：引用跟着一起改，否则下次启动找不到、设置被清空
        if (string.Equals(App.Settings.Behavior.StartupWorkspace, workspace.Name, StringComparison.OrdinalIgnoreCase))
        {
            App.Settings.Behavior.StartupWorkspace = name;
            App.SaveSettings();
        }

        ApplyWorkspaceChange();
    }

    private void RemoveWorkspace(Workspace workspace)
    {
        if (!ConfirmWindow.Ask(this, Strings.Dialog_Workspace_DeleteTitle,
                string.Format(Strings.Dialog_Workspace_DeleteMessage, workspace.Name)))
        {
            return;
        }

        if (!Workspaces.Remove(workspace.Name)) return;

        if (string.Equals(App.Settings.Behavior.StartupWorkspace, workspace.Name, StringComparison.OrdinalIgnoreCase))
        {
            App.Settings.Behavior.StartupWorkspace = null;
            App.SaveSettings();
        }

        Vm.SetStatus(string.Format(Strings.Status_Workspace_Deleted, workspace.Name));
        ApplyWorkspaceChange();
    }

    /// <summary>
    /// 设为/取消"启动时自动恢复"（FR-8.4）。与 FR-2.11 会话恢复互斥：
    /// 设置归一化（<see cref="AppSettings.Normalize"/>）会让工作区胜出，
    /// 因为它是用户在这里显式选的，会话恢复只是默认值。
    /// </summary>
    private void ToggleStartupWorkspace(Workspace workspace)
    {
        bool current = string.Equals(App.Settings.Behavior.StartupWorkspace, workspace.Name,
            StringComparison.OrdinalIgnoreCase);

        App.Settings.Behavior.StartupWorkspace = current ? null : workspace.Name;
        App.SaveSettings();

        Vm.SetStatus(current
            ? Strings.Status_Workspace_StartupCleared
            : string.Format(Strings.Status_Workspace_StartupSet, workspace.Name));

        ApplyWorkspaceChange();
    }

    /// <summary>
    /// 改完工作区：导航栏只重建「工作区」那一段、主页重排卡片。落盘由 <see cref="WorkspaceStore"/> 自己做。
    ///
    /// 不走整条 <c>Rebuild()</c>：那会把 500 条收藏一并重建、再触发一次失效检测，
    /// 还会丢掉侧栏的选中项与滚动位置——改个名字，侧栏却闪一下跳回顶部，看起来像出了故障。
    /// </summary>
    private void ApplyWorkspaceChange()
    {
        _nav.SetWorkspaces(Workspaces, App.Settings.Behavior.StartupWorkspace);
        _home.SetWorkspaces(Workspaces, App.Settings.Behavior.StartupWorkspace);
    }

    // ── 导航栏与主页的入口（FR-8.6） ─────────────────────────────────────────

    private void OnWorkspaceSaveCurrent(object sender, RoutedEventArgs e) => SaveCurrentWorkspace();

    private void OnWorkspaceOpen(object sender, RoutedEventArgs e)
    {
        if (RowWorkspace(sender) is { } workspace) OpenWorkspace(workspace);
    }

    private void OnWorkspaceOverwrite(object sender, RoutedEventArgs e)
    {
        if (RowWorkspace(sender) is { } workspace) OverwriteWorkspace(workspace.Name);
    }

    private void OnWorkspaceRename(object sender, RoutedEventArgs e)
    {
        if (RowWorkspace(sender) is { } workspace) RenameWorkspace(workspace);
    }

    private void OnWorkspaceDelete(object sender, RoutedEventArgs e)
    {
        if (RowWorkspace(sender) is { } workspace) RemoveWorkspace(workspace);
    }

    private void OnWorkspaceToggleStartup(object sender, RoutedEventArgs e)
    {
        if (RowWorkspace(sender) is { } workspace) ToggleStartupWorkspace(workspace);
    }

    /// <summary>
    /// 主页卡片菜单的动作分派（FR-8.3）。落到与侧栏菜单完全相同的那批方法上——
    /// 两个入口共用一套实现，才不会有"侧栏能删、主页删不掉"这种分歧。
    /// </summary>
    private void OnHomeWorkspaceCommand(Workspace workspace, WorkspaceCommand command)
    {
        switch (command)
        {
            case WorkspaceCommand.Open:
                OpenWorkspace(workspace);
                break;
            case WorkspaceCommand.Overwrite:
                OverwriteWorkspace(workspace.Name);
                break;
            case WorkspaceCommand.Rename:
                RenameWorkspace(workspace);
                break;
            case WorkspaceCommand.ToggleStartup:
                ToggleStartupWorkspace(workspace);
                break;
            case WorkspaceCommand.Delete:
                RemoveWorkspace(workspace);
                break;
        }
    }

    /// <summary>
    /// 从菜单项反查它属于哪一份工作区。侧栏行与主页卡片是两种不同的行对象，
    /// 但都只带一个 <see cref="Workspace"/>——统一在这里认，五个菜单处理器就不用各写两版。
    /// </summary>
    private static Workspace? RowWorkspace(object sender) => sender is FrameworkElement element
        ? element.DataContext switch
        {
            NavWorkspaceRow row => row.Workspace,
            HomeWorkspaceTile tile => tile.Workspace,
            _ => null,
        }
        : null;
}
