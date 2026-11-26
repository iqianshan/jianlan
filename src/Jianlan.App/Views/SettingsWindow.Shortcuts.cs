using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Jianlan.App.Input;
using Jianlan.Core.Input;
using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// 设置页第 7 页「快捷键」（FR-5 / FR-10.12）。
///
/// 页面结构：分组标题 + 每行「动作名 / 当前组合 / 修改 / 恢复默认」。
/// 行由代码生成而不是写在 XAML 里：命令有四十多条、还要按过滤词重画与进入录制态，
/// 四十份几乎一样的 XAML 既没法审，也没法保证"菜单上的键位"和"这里显示的键位"同源。
///
/// 所有键位文本都取自 <see cref="KeyBindings"/>，这一页不自己拼字符串——
/// 那正是 docs/shortcuts.md 当初写出六条"文档有、代码无"的键位的原因。
/// </summary>
public partial class SettingsWindow
{
    /// <summary>正在录制的命令；null = 没在录制。录制期间窗口的切页快捷键也要让路。</summary>
    private string? _recordingId;

    /// <summary>F1 / 外部调用：把窗口翻到「快捷键」页。</summary>
    public void ShowShortcutsPage()
    {
        NavList.SelectedIndex = (int)SettingsPage.Shortcuts;
        Activate();
    }

    private void OnShortcutFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        // 用户在筛选框里开始打字，说明录制已经结束了。不清掉的话，
        // 下一个字符会被当成"新键位"绑上去，而用户此刻只是想筛选。
        if (_recordingId is not null)
        {
            _recordingId = null;
            Owner_?.SetShortcutCaptureMode(false);
        }

        RebuildShortcutRows();
    }

    // ── 重建列表 ────────────────────────────────────────────────────────────

    private sealed record Entry(
        KeyBindings.Group Group,
        string CommandId,
        string Title,
        string Gesture,
        string Backup,
        bool Editable,
        bool Overridden,
        string Footnote);

    private List<Entry> BuildShortcutEntries()
    {
        var entries = new List<Entry>
        {
            // 全局唤起键排在第一位：它管的是"窗口在不在眼前"，与下面所有命令不是一类
            new(
                KeyBindings.Group.Global,
                KeyBindings.GlobalShowWindowId,
                Strings.Settings_Shortcuts_ShowWindow,
                KeyBindings.ShowWindowGesture.IsEmpty ? string.Empty : KeyBindings.ShowWindowGesture.Format(),
                string.Empty,
                Editable: true,
                Overridden: Settings.Shortcuts.ShowWindow is not null,
                Footnote: Strings.Settings_Shortcuts_ShowWindow_Hint),
        };

        foreach (KeyBindings.Row row in KeyBindings.Rows)
        {
            string gesture = KeyBindings.GestureOf(row.CommandId)?.Format() ?? string.Empty;
            string backup = KeyBindings.TextOfBackup(row.CommandId);

            var notes = new List<string>();
            if (backup.Length > 0) notes.Add($"{Strings.Settings_Shortcuts_Backup}：{backup}");
            if (!row.Editable) notes.Add(Strings.Settings_Shortcuts_Fixed);
            else if (ShortcutGesture.TryParse(gesture, out ShortcutGesture parsed)
                     && ShortcutPolicy.IsTextEditingGesture(parsed))
            {
                notes.Add(Strings.Settings_Shortcuts_TextEditing);
            }

            entries.Add(new Entry(
                row.Group,
                row.CommandId,
                row.Title(),
                gesture,
                backup,
                row.Editable,
                Settings.Shortcuts.Overrides.ContainsKey(row.CommandId),
                string.Join("　", notes)));
        }

        return entries;
    }

    private void RebuildShortcutRows()
    {
        if (ShortcutListPanel is null) return;

        ShortcutListPanel.Children.Clear();

        string filter = ShortcutFilterBox?.Text.Trim() ?? string.Empty;
        KeyBindings.Group? lastGroup = null;

        foreach (Entry entry in BuildShortcutEntries())
        {
            if (filter.Length > 0 && !Matches(entry, filter)) continue;

            if (lastGroup != entry.Group)
            {
                lastGroup = entry.Group;
                ShortcutListPanel.Children.Add(BuildGroupHeader(entry.Group));
            }

            ShortcutListPanel.Children.Add(BuildShortcutRow(entry));
        }
    }

    private static bool Matches(Entry entry, string filter)
        => entry.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
           || entry.Gesture.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || entry.Backup.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || entry.CommandId.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string GroupTitle(KeyBindings.Group group) => group switch
    {
        KeyBindings.Group.Global => Strings.Settings_Shortcuts_Group_Global,
        KeyBindings.Group.Files => Strings.Settings_Shortcuts_Group_Files,
        KeyBindings.Group.Navigation => Strings.Settings_Shortcuts_Group_Navigation,
        KeyBindings.Group.View => Strings.Settings_Shortcuts_Group_View,
        KeyBindings.Group.Tabs => Strings.Settings_Shortcuts_Group_Tabs,
        KeyBindings.Group.Window => Strings.Settings_Shortcuts_Group_Window,
        _ => Strings.Settings_Shortcuts_Group_Tags,
    };

    private Border BuildGroupHeader(KeyBindings.Group group)
    {
        var border = new Border { Style = (Style)FindResource("Heading.Group") };
        border.Child = new TextBlock
        {
            Style = (Style)FindResource("Text.GroupTitle"),
            Text = GroupTitle(group),
        };
        return border;
    }

    // ── 行布局的固定度量 ────────────────────────────────────────────────────
    //
    // 三列必须定宽，不能用 Auto：每一行是各自的 Grid，Auto 只按"这一行"的内容量宽，
    // 于是带「恢复默认」的行整块右移、不带的那行左移，几十行看下来键位列像锯口一样参差
    // （走查第一版就是这个样子）。定宽之后键位右缘、按钮左缘在整页里对齐。

    /// <summary>键位列的宽度，取最长默认组合（<c>Ctrl+Shift+Delete</c>）再加余量。</summary>
    private const double GestureColumnWidth = 168;

    /// <summary>「修改」列。</summary>
    private const double ChangeColumnWidth = 72;

    /// <summary>「恢复默认」列。没有覆盖项的行留空，但列宽要占住。</summary>
    private const double ResetColumnWidth = 96;

    private Grid BuildShortcutRow(Entry entry)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GestureColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ChangeColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ResetColumnWidth) });

        bool recording = string.Equals(_recordingId, entry.CommandId, StringComparison.Ordinal);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            // 这里不能用 Text.FieldLabel：那是给"标签 + 右侧控件"的两栏表单用的定宽样式，
            // 定宽 + 默认 Stretch 会让文字在星列里居中，看着像被缩进了一大截。
            Style = (Style)FindResource("Text.Row"),
            Text = entry.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        string footnote = recording ? Strings.Settings_Shortcuts_Recording_Hint : entry.Footnote;
        if (footnote.Length > 0)
        {
            left.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Text.FieldHint"),
                Text = footnote,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        grid.Children.Add(left);

        Border chip = BuildChip(recording
            ? Strings.Settings_Shortcuts_Recording
            : entry.Gesture.Length == 0 ? Strings.Settings_Shortcuts_None : entry.Gesture,
            recording);
        chip.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);

        if (entry.Editable)
        {
            var change = new Button
            {
                Style = (Style)FindResource("Button.Link"),
                Content = Strings.Settings_Shortcuts_Change,
                Margin = new Thickness(12, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(change, $"{Strings.Settings_Shortcuts_Change}：{entry.Title}");
            change.Click += (_, _) => BeginRecording(entry.CommandId);
            Grid.SetColumn(change, 2);
            grid.Children.Add(change);
        }

        if (entry.Overridden)
        {
            var reset = new Button
            {
                Style = (Style)FindResource("Button.Link"),
                Content = Strings.Settings_Shortcuts_Reset,
                Margin = new Thickness(12, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(reset, $"{Strings.Settings_Shortcuts_Reset}：{entry.Title}");
            reset.Click += (_, _) => ResetShortcut(entry.CommandId);
            Grid.SetColumn(reset, 3);
            grid.Children.Add(reset);
        }

        return grid;
    }

    /// <summary>键位用等宽字体 + 边框，避免和动作名混成一句话，也让用户看得出"这是一颗键"。</summary>
    private Border BuildChip(string text, bool recording)
    {
        var chip = new Border
        {
            MinWidth = 108,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Style = (Style)FindResource("Text.Mono"),
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        chip.SetResourceReference(Border.BackgroundProperty, "Brush.Surface.Sidebar");
        chip.SetResourceReference(
            Border.BorderBrushProperty,
            recording ? "Brush.Border.Focus" : "Brush.Border.Hairline");

        return chip;
    }

    // ── 录制 ────────────────────────────────────────────────────────────────

    private void BeginRecording(string commandId)
    {
        _recordingId = commandId;

        // 系统热键先摘下来：否则用户按到"唤起键"或 Ctrl+, 时，按键被系统直接
        // 投递给主窗口，设置窗口根本收不到，看起来像录制框坏了。
        Owner_?.SetShortcutCaptureMode(true);

        RebuildShortcutRows();
        StatusText.Text = Strings.Settings_Shortcuts_Recording_Hint;
    }

    private void StopRecording()
    {
        _recordingId = null;
        Owner_?.SetShortcutCaptureMode(false);
        RebuildShortcutRows();
        StatusText.Text = string.Empty;
    }

    /// <summary>
    /// 录制态下接管按键。返回 true 表示已经消费掉，窗口不要再处理（翻页、按钮默认动作）。
    /// </summary>
    private bool HandleShortcutCaptureKey(KeyEventArgs e)
    {
        if (_recordingId is null) return false;

        e.Handled = true;

        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (key == Key.Escape)
        {
            StopRecording();
            return true;
        }

        ShortcutModifiers modifiers = ReadModifiers();

        // Delete/Backspace 单独按 = 解绑（与系统"清除快捷键"的习惯一致）。
        // 带修饰键时它就是一个普通候选键，交给下面的校验。
        if (key is Key.Delete or Key.Back && modifiers == ShortcutModifiers.None)
        {
            CommitRecording(null);
            return true;
        }

        // 纯修饰键不构成组合，等用户按下一个实键
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return true;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return true;

        var candidate = new ShortcutGesture(modifiers, virtualKey);

        if (!KeyBindings.CanAssign(candidate, _recordingId, out ShortcutProblem problem, out string? owner))
        {
            // 录不进去时留一条：用户报"我按了组合没反应"，
            // 日志里能看到抓到的到底是哪个组合、被哪条规则挡下。
            Diagnostics.AppLog.Info("shortcuts",
                $"record rejected {candidate.Format()} problem={problem} owner={owner ?? "-"}");

            StatusText.Text = problem switch
            {
                ShortcutProblem.NeedsModifier => Strings.Settings_Shortcuts_NeedsModifier,
                ShortcutProblem.WinNotAllowed => Strings.Settings_Shortcuts_Win,
                ShortcutProblem.Reserved => Strings.Settings_Shortcuts_Reserved,
                ShortcutProblem.Empty => Strings.Settings_Shortcuts_Recording_Hint,
                _ when owner is not null => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Strings.Settings_Shortcuts_Conflict,
                    KeyBindings.TitleOfIncludingGlobal(owner)),
                _ => Strings.Settings_Shortcuts_NeedsModifier,
            };
            return true;
        }

        CommitRecording(candidate);
        return true;
    }

    private void CommitRecording(ShortcutGesture? gesture)
    {
        string commandId = _recordingId!;
        _recordingId = null;

        if (commandId == KeyBindings.GlobalShowWindowId)
        {
            Settings.Shortcuts.ShowWindow = gesture?.Format();
        }
        else if (gesture is null)
        {
            // 空串 = 明确解绑。不能 remove：remove 表示"没改过"，会回到默认键位
            Settings.Shortcuts.Overrides[commandId] = string.Empty;
        }
        else
        {
            string text = gesture.Value.Format();

            // 改回默认值时把覆盖项删掉，设置文件保持干净，
            // 也让以后调整默认键位能自然生效
            if (string.Equals(text, KeyBindings.DefaultTextOf(commandId), StringComparison.Ordinal))
            {
                Settings.Shortcuts.Overrides.Remove(commandId);
            }
            else
            {
                Settings.Shortcuts.Overrides[commandId] = text;
            }
        }

        App.SaveSettings();

        Owner_?.SetShortcutCaptureMode(false);
        bool registered = Owner_?.ApplyShortcutSettings() ?? true;

        RebuildShortcutRows();
        StatusText.Text = registered ? Strings.Settings_Shortcuts_Saved : Strings.Settings_Shortcuts_Busy;
    }

    private void ResetShortcut(string commandId)
    {
        if (commandId == KeyBindings.GlobalShowWindowId)
        {
            Settings.Shortcuts.ShowWindow = null;
        }
        else
        {
            Settings.Shortcuts.Overrides.Remove(commandId);
        }

        App.SaveSettings();
        Owner_?.ApplyShortcutSettings();
        RebuildShortcutRows();
        StatusText.Text = Strings.Settings_Shortcuts_Saved;
    }

    private void ResetShortcutsPage()
    {
        Settings.Shortcuts.Overrides.Clear();
        Settings.Shortcuts.ShowWindow = null;
        _recordingId = null;

        App.SaveSettings();
        Owner_?.SetShortcutCaptureMode(false);
        Owner_?.ApplyShortcutSettings();

        RebuildShortcutRows();
        StatusText.Text = Strings.Settings_Shortcuts_ResetDone;
    }

    /// <summary>
    /// 录制时的修饰键状态。
    ///
    /// 主来源是 <c>Keyboard.Modifiers</c>：它跟着输入消息走，反映的是"按下这个键的那一刻"。
    /// 只有 Win 单独问系统——它由外壳在 WPF 之前处理，WPF 这一位不可靠；不补的话
    /// <c>Win+Shift+F9</c> 会被录成 <c>Shift+F9</c>，键位块显示的组合与实际注册的
    /// 组合不是一回事，用户还以为自己绑了三个键。
    /// </summary>
    private static ShortcutModifiers ReadModifiers()
    {
        ShortcutModifiers result = ToCore(Keyboard.Modifiers);

        if ((GetAsyncKeyState(VkLWin) & 0x8000) != 0 || (GetAsyncKeyState(VkRWin) & 0x8000) != 0)
        {
            result |= ShortcutModifiers.Win;
        }

        return result;
    }

    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static ShortcutModifiers ToCore(ModifierKeys modifiers)
    {
        ShortcutModifiers result = ShortcutModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0) result |= ShortcutModifiers.Control;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= ShortcutModifiers.Alt;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= ShortcutModifiers.Shift;
        if ((modifiers & ModifierKeys.Windows) != 0) result |= ShortcutModifiers.Win;
        return result;
    }
}
