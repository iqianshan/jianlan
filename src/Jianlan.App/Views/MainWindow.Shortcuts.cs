using System;
using System.Windows;
using Jianlan.Core.Commands;
using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// 与快捷键配套的窗口级命令：视图开关、帮助、关闭窗口。
///
/// 这些动作原先只有设置页或鼠标入口，文档里却写着键位（Ctrl+H / Ctrl+B / Ctrl+Shift+H /
/// Ctrl+Shift+W / F1）。补齐的做法不是"把文档删掉"，而是给它们真正的命令：
/// 只有进了命令表，才能被键位表派发、才能在 CanExecute 里表达"此刻能不能做"。
/// </summary>
public partial class MainWindow
{
    private void RegisterShortcutCommands()
    {
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ToggleNav,
            TitleFactory = () => Strings.Shortcut_Action_ToggleNav,
            Execute = ToggleNavPane,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ToggleHidden,
            TitleFactory = () => Strings.Shortcut_Action_ToggleHidden,
            Execute = ToggleHiddenFiles,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ToggleHistory,
            TitleFactory = () => Strings.Shortcut_Action_ToggleHistory,
            Execute = ToggleHistoryPane,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ShortcutsHelp,
            TitleFactory = () => Strings.Shortcut_Action_ShortcutsHelp,
            Execute = () => OpenSettings(startAtShortcuts: true),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CloseWindow,
            TitleFactory = () => Strings.Shortcut_Action_CloseWindow,
            Execute = Close,
        });
    }

    /// <summary>
    /// 设置页进入/退出"录制键位"时调用：录制期间把本窗口持有的系统热键全部摘掉。
    ///
    /// 不摘的后果很具体：用户按到唤起键时，按键被系统直接投递给主窗口，
    /// 设置窗口收不到这一次按键，看起来像录制框坏了；按到 Ctrl+, 还会把设置窗口重新激活一次。
    /// 退出录制（含关掉设置窗口）必须恢复，否则热键就永久失效了。
    /// </summary>
    public void SetShortcutCaptureMode(bool capturing)
    {
        if (capturing)
        {
            UnregisterSettingsHotkey();
            UnregisterSummonHotkey();
            return;
        }

        if (IsActive) RegisterSettingsHotkey();
        RegisterSummonHotkey();
    }

    /// <summary>
    /// 收起/展开导航栏（Ctrl+B）。写设置并立即重排：只在下次启动生效的话，
    /// 用户按完看不到任何变化，会以为键位没绑上。
    /// </summary>
    private void ToggleNavPane()
    {
        bool show = !App.Settings.Appearance.ShowNavPane;
        App.Settings.Appearance.ShowNavPane = show;

        ApplyNavLayout();
        App.SaveSettings();

        Vm.SetStatusHold(
            show ? Strings.Status_NavShown : Strings.Status_NavHidden,
            TimeSpan.FromSeconds(1.6));
    }

    /// <summary>
    /// 显示/隐藏隐藏文件（Ctrl+Shift+H）。走与设置页同一个入口 ApplySettingsToUi，
    /// 由窗格重新过滤——不重新枚举目录，万项列表也是即时的。
    /// </summary>
    private void ToggleHiddenFiles()
    {
        bool show = !App.Settings.Behavior.ShowHidden;
        App.Settings.Behavior.ShowHidden = show;

        ApplySettingsToUi();
        App.SaveSettings();

        Vm.SetStatusHold(
            show ? Strings.Status_HiddenOn : Strings.Status_HiddenOff,
            TimeSpan.FromSeconds(1.6));
    }

    /// <summary>
    /// 显示/隐藏侧栏的历史分区（Ctrl+H）。与「关掉历史记录」是两件事：
    /// 那个管记不记（privacy.historyEnabled），这个只管看不看。
    /// </summary>
    private void ToggleHistoryPane()
    {
        bool show = !App.Settings.Appearance.ShowHistory;
        App.Settings.Appearance.ShowHistory = show;

        // 分区是整条列表的一部分，只能重建；重建后把高亮还给当前目录，
        // 否则侧栏会停在一个"看起来选中、其实已经清空"的状态。
        _nav.Rebuild();
        SyncNavSelection(Vm.CurrentPath);
        App.SaveSettings();

        Vm.SetStatusHold(
            show ? Strings.Status_HistoryOn : Strings.Status_HistoryOff,
            TimeSpan.FromSeconds(1.6));
    }
}
