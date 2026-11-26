using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using Jianlan.App.Diagnostics;
using Jianlan.App.ViewModels;
using Jianlan.Core.Commands;
using Jianlan.Core.Input;
using Jianlan.Core.Localization;
using Jianlan.Core.Settings;
using Jianlan.Core.Tagging;

namespace Jianlan.App.Input;

/// <summary>
/// 全局快捷键表（docs/shortcuts.md 的唯一实现处）。0.2.1 起这张表可以在设置页改：
/// 默认键位写在这里，用户改过的部分存在 <c>settings.json</c> 的 <c>shortcuts.overrides</c>，
/// <see cref="Rebuild"/> 把两者合成"此刻真正生效的绑定"。
///
/// 为什么不用 WPF 的 <c>Window.InputBindings</c>：
///   · <c>KeyBinding</c> 依赖 <c>KeyGesture</c> 精确匹配，标点类键（<c>OemComma</c> 等）
///     在不同键盘布局/输入法下上报的 <c>Key</c> 并不总是同一个值，实测 <c>Ctrl+,</c> 匹配不上；
///   · <c>KeyGesture</c> 不认 <c>Key.System</c>，Alt 组合需要额外归一化；
///   · 匹配规则藏在框架里，出问题时无法断言。
///
/// 为什么不用 <c>PreviewKeyDown</c> 作为唯一入口：
///   · 输入法（TSF/IME）会在 WPF 之前改写甚至消费按键，<c>Ctrl+,</c> 这类标点组合
///     在中文输入法下命中率不稳定；同一台机器上 <c>Ctrl+C/V</c> 正常、<c>Ctrl+,</c>
///     无反应，就是这一层的差异。
///
/// 因此这里维护两张索引：
///   · <see cref="ByVirtualKey"/>：窗口消息（<c>WM_KEYDOWN</c>）层用物理键匹配，最可靠；
///   · <see cref="ByKey"/>：<c>PreviewKeyDown</c> 兜底，覆盖拿不到物理键的场合（如程序内合成事件）。
/// 两者由同一份绑定重建，不会出现"菜单写着 Ctrl+, 但按了没反应"。
///
/// **键位以虚拟键码为准，不以字符为准**：设置里存 <c>Ctrl+,</c>，匹配时比的是 VK_OEM_COMMA。
/// 换键盘布局后物理位置不变、打出来的字符可能变，这是刻意的——用户记的是位置。
/// </summary>
internal static class KeyBindings
{
    /// <summary>修饰键只比较这三类；Win 键不参与匹配（避免吞掉系统级组合）。</summary>
    private const ModifierKeys Relevant = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    /// <summary>
    /// 输入框内必须让给文本语义的键：焦点在 <c>TextBoxBase</c> 时这些组合不派发，
    /// 否则地址栏里按 Ctrl+C 会变成"复制文件"。
    ///
    /// 这一条按**物理键**判断而不是按命令判断：它描述的是"输入框自己要用这个手势"，
    /// 与用户把哪条命令绑到了这里无关。绑到 Ctrl+C 的命令在输入框里天然不生效，
    /// 设置页会就这一点给出提示。
    /// </summary>
    private static readonly HashSet<int> TextEditingKeys =
    [
        Vk.C, Vk.X, Vk.V, Vk.A, Vk.Z, Vk.Y,
    ];

    /// <summary>设置页分组。顺序即显示顺序。</summary>
    public enum Group
    {
        Global = 0,
        Files = 1,
        Navigation = 2,
        View = 3,
        Tabs = 4,
        Window = 5,
        Tags = 6,
    }

    /// <summary>
    /// 一条可配置的命令。一个命令一个主键位（可改），可选一个固定备用键位（不可改）。
    ///
    /// 备用键位不开放修改：一条命令管 0–2 组键，加上"解绑"就是三种状态，
    /// 冲突检测与界面都要翻倍，而收益只在"主键位被输入法吃掉"的几个场景
    /// （Alt+P / F3 / F5）——那几组本来就该保持稳定。
    /// </summary>
    public sealed record Row(
        string CommandId,
        Group Group,
        Func<string> Title,
        string Default,
        bool Editable = true,
        string? Backup = null);

    private static readonly List<Row> Catalog = BuildCatalog();
    private static readonly Dictionary<string, Row> RowsById =
        Catalog.ToDictionary(r => r.CommandId, StringComparer.Ordinal);

    private static Dictionary<(int VirtualKey, ModifierKeys Modifiers), string> _byVirtualKey = [];
    private static Dictionary<(Key Key, ModifierKeys Modifiers), string> _byKey = [];
    private static Dictionary<string, string> _gestures = new(StringComparer.Ordinal);
    private static Dictionary<string, ShortcutGesture> _effective = new(StringComparer.Ordinal);
    private static Dictionary<string, ShortcutGesture> _backups = new(StringComparer.Ordinal);
    private static ShortcutGesture _showWindow = ShortcutGesture.None;

    static KeyBindings()
    {
        // 静态构造先建一份"全默认"的表：单测、探针、以及设置还没读出来的那一小段时间
        // 也要能匹配。真正的设置在 App 启动后由 Rebuild 覆盖。
        Rebuild(null);
    }

    // ── 设置页要用的只读视图 ────────────────────────────────────────────────

    /// <summary>全部可显示的命令（含不可改的参数化键位），顺序即设置页顺序。</summary>
    public static IReadOnlyList<Row> Rows => Catalog;

    public static string TitleOf(string commandId)
        => RowsById.TryGetValue(commandId, out Row? row) ? row.Title() : commandId;

    public static string DefaultTextOf(string commandId)
        => RowsById.TryGetValue(commandId, out Row? row)
            ? ParseOrDefault(row).Format()
            : string.Empty;

    /// <summary>此刻展示在菜单/设置页上的键位文本；未绑定为空串。主键位解绑后显示备用键位。</summary>
    public static string TextOf(string commandId)
        => _effective.TryGetValue(commandId, out ShortcutGesture gesture)
            ? gesture.Format()
            : _backups.TryGetValue(commandId, out ShortcutGesture backup)
                ? backup.Format()
                : string.Empty;

    public static ShortcutGesture? GestureOf(string commandId)
        => _effective.TryGetValue(commandId, out ShortcutGesture gesture) ? gesture : null;

    /// <summary>主键位被用户解绑或改过时为真。设置页据此显示「恢复默认」。</summary>
    public static bool IsOverridden(ShortcutSettings settings, string commandId)
    {
        if (!settings.Overrides.TryGetValue(commandId, out string? text)) return false;
        if (string.IsNullOrEmpty(text)) return true;

        return ShortcutGesture.TryParse(text, out ShortcutGesture gesture)
               && RowsById.TryGetValue(commandId, out Row? row)
               && gesture != ParseOrDefault(row);
    }

    /// <summary>备用键位此刻是否真的生效（被别的命令的主键位顶掉时为假）。</summary>
    public static string TextOfBackup(string commandId)
        => _backups.TryGetValue(commandId, out ShortcutGesture gesture) ? gesture.Format() : string.Empty;

    /// <summary>全局唤起键。设置页要把它与命令键位一起做冲突判定。</summary>
    public static ShortcutGesture ShowWindowGesture => _showWindow;

    // ── 重建 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 按设置重建全部索引。启动读设置之后调用一次；用户改完键位再调用一次。
    ///
    /// 解析顺序：
    ///   1. 每条命令的主键位（默认值，或被 overrides 覆盖）；
    ///   2. 再放各命令的固定备用键位。
    /// 之所以分两轮：用户把某条命令的主键位改到别人的备用键位上时，
    /// **用户显式改的那个应当赢**，否则界面上显示"已改"，实际却被安静地忽略。
    /// </summary>
    public static void Rebuild(ShortcutSettings? settings)
    {
        Dictionary<string, string> overrides = settings?.Overrides ?? [];
        _showWindow = ShortcutGesture.TryParse(settings?.ShowWindow, out ShortcutGesture showWindow)
            ? showWindow
            : ShortcutGesture.None;

        var effective = new Dictionary<string, ShortcutGesture>(StringComparer.Ordinal);
        var used = new HashSet<(int VirtualKey, ShortcutModifiers Modifiers)>();

        foreach (Row row in Catalog)
        {
            ShortcutGesture gesture = Resolve(row, overrides);

            // 解绑（None）是合法意图：所有按键都松开，这条命令只能用菜单/按钮触发
            if (gesture.IsEmpty) continue;

            if (!used.Add((gesture.VirtualKey, gesture.Modifiers)))
            {
                AppLog.Warn("shortcuts", "E7004",
                    $"duplicate primary {gesture.Format()} for {row.CommandId}, ignored");  // i18n-ignore：只进 app.log
                continue;
            }

            effective[row.CommandId] = gesture;
        }

        var backups = new Dictionary<string, ShortcutGesture>(StringComparer.Ordinal);
        foreach (Row row in Catalog)
        {
            if (row.Backup is null) continue;
            if (!ShortcutGesture.TryParse(row.Backup, out ShortcutGesture gesture) || gesture.IsEmpty) continue;

            if (!used.Add((gesture.VirtualKey, gesture.Modifiers)))
            {
                // 只可能是被某条命令的主键位占用了。用户改的优先，备用键位安静退场。
                AppLog.Warn("shortcuts", "E7005",
                    $"backup {gesture.Format()} of {row.CommandId} shadowed, ignored");  // i18n-ignore：只进 app.log
                continue;
            }

            backups[row.CommandId] = gesture;
        }

        var byVirtualKey = new Dictionary<(int, ModifierKeys), string>();
        var byKey = new Dictionary<(Key, ModifierKeys), string>();
        var gestures = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string commandId, ShortcutGesture gesture) in effective)
        {
            ModifierKeys modifiers = ToWpf(gesture.Modifiers);
            byVirtualKey[(gesture.VirtualKey, modifiers)] = commandId;

            Key key = KeyInterop.KeyFromVirtualKey(gesture.VirtualKey);
            if (key != Key.None) byKey[(key, modifiers)] = commandId;

            gestures[commandId] = gesture.Format();
        }

        // 备用键位也要能匹配，但不进 Gestures：菜单只显示主键位
        foreach ((string commandId, ShortcutGesture gesture) in backups)
        {
            ModifierKeys modifiers = ToWpf(gesture.Modifiers);
            byVirtualKey.TryAdd((gesture.VirtualKey, modifiers), commandId);

            Key key = KeyInterop.KeyFromVirtualKey(gesture.VirtualKey);
            if (key != Key.None) byKey.TryAdd((key, modifiers), commandId);
        }

        _effective = effective;
        _backups = backups;
        _byVirtualKey = byVirtualKey;
        _byKey = byKey;
        _gestures = gestures;
    }

    private static ShortcutGesture Resolve(Row row, Dictionary<string, string> overrides)
    {
        if (!overrides.TryGetValue(row.CommandId, out string? text))
        {
            return ParseOrDefault(row);
        }

        // 空串 = 解绑
        if (string.IsNullOrEmpty(text)) return ShortcutGesture.None;

        if (!ShortcutGesture.TryParse(text, out ShortcutGesture gesture))
        {
            AppLog.Warn("shortcuts", "E7003",
                $"invalid gesture \"{text}\" for {row.CommandId}, using default");  // i18n-ignore：只进 app.log
            return ParseOrDefault(row);
        }

        ShortcutProblem problem = ShortcutPolicy.Check(gesture);
        if (problem != ShortcutProblem.None)
        {
            AppLog.Warn("shortcuts", "E7003",
                $"rejected gesture {gesture.Format()} for {row.CommandId}: {problem}");  // i18n-ignore：只进 app.log
            return ParseOrDefault(row);
        }

        return gesture;
    }

    private static ShortcutGesture ParseOrDefault(Row row)
        => ShortcutGesture.TryParse(row.Default, out ShortcutGesture gesture) ? gesture : ShortcutGesture.None;

    // ── 校验 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 这组键能不能给 <paramref name="commandId"/> 用。返回 false 时给出占用者或原因。
    /// 判定顺序：先规则（修饰键/保留组合），再冲突（含备用键位与全局唤起键）。
    /// </summary>
    public static bool CanAssign(
        ShortcutGesture candidate,
        string commandId,
        out ShortcutProblem problem,
        out string? ownerCommandId)
    {
        ownerCommandId = null;

        // 唤起键是系统级热键，Win 组合由 RegisterHotKey 直接接管；程序内的命令不行。
        problem = ShortcutPolicy.Check(candidate, allowWin: commandId == GlobalShowWindowId);
        if (problem != ShortcutProblem.None) return false;

        foreach ((string id, ShortcutGesture gesture) in _effective)
        {
            if (id == commandId) continue;
            if (gesture == candidate) { ownerCommandId = id; return false; }
        }

        foreach ((string id, ShortcutGesture gesture) in _backups)
        {
            if (id == commandId) continue;
            if (gesture == candidate) { ownerCommandId = id; return false; }
        }

        // 全局键由系统先处理，窗口内的同名绑永远收不到——必须当冲突。
        // 反过来给自己设全局键时，当然不能与自己冲突。
        if (commandId != GlobalShowWindowId && !_showWindow.IsEmpty && _showWindow == candidate)
        {
            ownerCommandId = GlobalShowWindowId;
            return false;
        }

        return true;
    }

    /// <summary>全局唤起键在冲突提示里的显示名。它不是命令，借一个稳定的伪 Id 走同一套提示。</summary>
    public const string GlobalShowWindowId = "app.showWindow";

    public static string TitleOfIncludingGlobal(string commandId)
        => commandId == GlobalShowWindowId ? Strings.Settings_Shortcuts_ShowWindow : TitleOf(commandId);

    // ── 匹配 ────────────────────────────────────────────────────────────────

    /// <summary>窗口消息层匹配：用物理键（VK）判定，不受输入法改写 <c>Key</c> 的影响。</summary>
    public static bool TryMatchVirtualKey(int virtualKey, out string commandId)
    {
        commandId = string.Empty;

        ModifierKeys modifiers = Keyboard.Modifiers & Relevant;
        if (!_byVirtualKey.TryGetValue((virtualKey, modifiers), out string? id) || id is null) return false;

        commandId = id;
        return true;
    }

    /// <summary>
    /// <c>PreviewKeyDown</c> 兜底。<c>key</c> 由调用方归一化（Alt 组合下 WPF 上报 <c>Key.System</c>，
    /// 输入法合成时上报 <c>Key.ImeProcessed</c>）。
    /// </summary>
    public static bool TryMatchKey(Key key, out string commandId)
    {
        commandId = string.Empty;

        ModifierKeys modifiers = Keyboard.Modifiers & Relevant;
        if (!_byKey.TryGetValue((key, modifiers), out string? id) || id is null) return false;

        commandId = id;
        return true;
    }

    /// <summary>焦点在输入框时，该物理键是否必须让给文本编辑语义。</summary>
    public static bool IsTextEditingKey(int virtualKey) => TextEditingKeys.Contains(virtualKey);

    /// <summary>
    /// 这个无修饰的功能键是否绑了命令。列表没焦点时，只有这类键仍然要接管
    /// （F1 帮助、F3 分屏、F6 换窗格、F5 刷新）——用户绑到哪一颗，就按哪一颗放行。
    /// </summary>
    public static bool IsUnmodifiedFunctionKey(int virtualKey)
        => virtualKey is >= 0x70 and <= 0x87
           && _byVirtualKey.ContainsKey((virtualKey, ModifierKeys.None));

    /// <summary>上下文菜单里的键位提示文本，与表同源，避免菜单与文档各写一份。每次重建后更新。</summary>
    public static IReadOnlyDictionary<string, string> Gestures => _gestures;

    /// <summary>Core 的修饰键 → WPF 的修饰键。两套枚举的位值**不兼容**（Alt/Control 相反），必须显式映射。</summary>
    private static ModifierKeys ToWpf(ShortcutModifiers modifiers)
    {
        ModifierKeys result = ModifierKeys.None;
        if ((modifiers & ShortcutModifiers.Control) != 0) result |= ModifierKeys.Control;
        if ((modifiers & ShortcutModifiers.Alt) != 0) result |= ModifierKeys.Alt;
        if ((modifiers & ShortcutModifiers.Shift) != 0) result |= ModifierKeys.Shift;
        return result;
    }

    /// <summary>
    /// 注册系统热键用的修饰键（Win32 的 MOD_*，Alt=1 / Control=2，与 Core 枚举相反）。
    /// </summary>
    public static uint ToWin32Modifiers(ShortcutGesture gesture)
    {
        uint result = 0;
        if ((gesture.Modifiers & ShortcutModifiers.Alt) != 0) result |= 0x0001;
        if ((gesture.Modifiers & ShortcutModifiers.Control) != 0) result |= 0x0002;
        if ((gesture.Modifiers & ShortcutModifiers.Shift) != 0) result |= 0x0004;
        if ((gesture.Modifiers & ShortcutModifiers.Win) != 0) result |= 0x0008;
        return result;
    }

    // ── 目录 ────────────────────────────────────────────────────────────────

    private static List<Row> BuildCatalog()
    {
        var rows = new List<Row>
        {
            // 文件（§4）
            new(CommandIds.OpenItem, Group.Files, () => Strings.Command_Open, "Enter"),
            new(CommandIds.Rename, Group.Files, () => Strings.Command_Rename, "F2"),
            new(CommandIds.Delete, Group.Files, () => Strings.Command_Delete, "Delete"),
            new(CommandIds.DeletePermanent, Group.Files, () => Strings.Command_DeletePermanent, "Shift+Delete"),
            new(CommandIds.CopyItems, Group.Files, () => Strings.Command_Copy, "Ctrl+C"),
            new(CommandIds.CutItems, Group.Files, () => Strings.Command_Cut, "Ctrl+X"),
            new(CommandIds.Paste, Group.Files, () => Strings.Command_Paste, "Ctrl+V"),
            new(CommandIds.CopyPath, Group.Files, () => Strings.Command_CopyPath, "Ctrl+Shift+C"),
            new(CommandIds.SelectAll, Group.Files, () => Strings.Command_SelectAll, "Ctrl+A"),
            new(CommandIds.Undo, Group.Files, () => Strings.Command_Undo, "Ctrl+Z"),
            new(CommandIds.NewFolder, Group.Files, () => Strings.Command_NewFolder, "Ctrl+Shift+N"),
            new(CommandIds.NewFile, Group.Files, () => Strings.Command_NewFile, "Ctrl+Shift+M"),
            new(CommandIds.Properties, Group.Files, () => Strings.Command_Properties, "Alt+Enter"),
            new(CommandIds.ShowContextMenu, Group.Files, () => Strings.Shortcut_Action_ContextMenu, "Apps"),

            // 导航（§2）
            // Backspace 是资源管理器的老习惯（也是 docs 里最早写下的那条）。
            // 无修饰键只在文件列表有焦点时才生效，输入框里仍是退格——判定在 MainWindow 的
            // 按键入口，不在这一张表里。
            new(CommandIds.GoUp, Group.Navigation, () => Strings.Command_GoUp, "Alt+Up", Backup: "Backspace"),
            new(CommandIds.GoBack, Group.Navigation, () => Strings.Command_GoBack, "Alt+Left"),
            new(CommandIds.GoForward, Group.Navigation, () => Strings.Command_GoForward, "Alt+Right"),
            new(CommandIds.FocusAddress, Group.Navigation, () => Strings.Command_FocusAddress, "Ctrl+L"),
            new(CommandIds.FocusSearch, Group.Navigation, () => Strings.Command_Search, "Ctrl+F"),
            new(CommandIds.Refresh, Group.Navigation, () => Strings.Command_Refresh, "F5", Backup: "Ctrl+R"),
            new(CommandIds.Bookmark, Group.Navigation, () => Strings.Command_Bookmark, "Ctrl+D"),

            // 视图（§3 / §7 / §8）
            new(CommandIds.ViewDetails, Group.View, () => Strings.Command_ViewDetails, "Ctrl+Shift+1"),
            new(CommandIds.ViewList, Group.View, () => Strings.Command_ViewList, "Ctrl+Shift+2"),
            new(CommandIds.CycleTheme, Group.View, () => Strings.Command_CycleTheme, "Ctrl+Shift+D"),
            new(CommandIds.TogglePreview, Group.View, () => Strings.Command_TogglePreview, "Alt+P", Backup: "Ctrl+Shift+P"),
            new(CommandIds.ToggleQuickLook, Group.View, () => Strings.Command_QuickLook, "Space"),
            new(CommandIds.ToggleNav, Group.View, () => Strings.Shortcut_Action_ToggleNav, "Ctrl+B"),
            new(CommandIds.ToggleHidden, Group.View, () => Strings.Shortcut_Action_ToggleHidden, "Ctrl+Shift+H"),
            new(CommandIds.ToggleHistory, Group.View, () => Strings.Shortcut_Action_ToggleHistory, "Ctrl+H"),

            // 标签页（§1）
            new(CommandIds.NewTab, Group.Tabs, () => Strings.Shortcut_Action_TabNew, "Ctrl+T"),
            new(CommandIds.CloseTab, Group.Tabs, () => Strings.Shortcut_Action_TabClose, "Ctrl+W"),
            new(CommandIds.ReopenTab, Group.Tabs, () => Strings.Shortcut_Action_TabReopen, "Ctrl+Shift+T"),
            new(CommandIds.NextTab, Group.Tabs, () => Strings.Shortcut_Action_TabNext, "Ctrl+Tab"),
            new(CommandIds.PreviousTab, Group.Tabs, () => Strings.Shortcut_Action_TabPrevious, "Ctrl+Shift+Tab"),
            new(CommandIds.LastTab, Group.Tabs, () => Strings.Shortcut_Action_TabLast, "Ctrl+9"),

            // 窗口（§8 / §5）
            new(CommandIds.OpenSettings, Group.Window, () => Strings.Command_OpenSettings, "Ctrl+,"),
            new(CommandIds.ShortcutsHelp, Group.Window, () => Strings.Shortcut_Action_ShortcutsHelp, "F1"),
            new(CommandIds.CloseWindow, Group.Window, () => Strings.Shortcut_Action_CloseWindow, "Ctrl+Shift+W"),
            new(CommandIds.ToggleSplit, Group.Window, () => Strings.Command_ToggleSplit, "F3", Backup: "Ctrl+Shift+E"),
            new(CommandIds.FocusOtherPane, Group.Window, () => Strings.Command_FocusOtherPane, "F6"),

            // 标签（§6）。参数化的 8 色与 9 个标签跳转固定不可改：
            // 它们是"一批同类键位"，逐个改既列不出清楚的界面，也没有实际需求。
            new(CommandIds.RemoveTag, Group.Tags, () => Strings.Menu_Tag_Remove, "Alt+0"),
        };

        // 跳转到第 N 个标签页：只到 8。Ctrl+9 按惯例是"最后一个标签页"（LastTab），
        // 循环写到 9 会与它撞车，启动日志里每次都会多两条 E7004。
        for (int n = 1; n <= 8; n++)
        {
            int slot = n;
            rows.Add(new Row(
                CommandIds.GotoTab(n),
                Group.Tabs,
                () => string.Format(CultureInfo.CurrentCulture, Strings.Shortcut_Action_TabGoto, slot),
                $"Ctrl+{n}",
                Editable: false));
        }

        for (int slot = 0; slot < AppearanceSettings.DefaultTagCount; slot++)
        {
            int index = slot;
            rows.Add(new Row(
                CommandIds.SetTagSlot(slot),
                Group.Tags,
                () => string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Shortcut_Action_SetTag,
                    TagPresets.NameOf(TagPalette.FromSlot(index))),
                $"Alt+{slot + 1}",
                Editable: false));
        }

        return rows;
    }

    /// <summary>虚拟键码。只列本表用到的，避免引入 <c>System.Windows.Forms.Keys</c>。</summary>
    internal static class Vk
    {
        public const int Back = 0x08;
        public const int Tab = 0x09;
        public const int Enter = 0x0D;
        public const int Space = 0x20;
        public const int Up = 0x26;
        public const int Left = 0x25;
        public const int Right = 0x27;
        public const int Delete = 0x2E;
        public const int D1 = 0x31;
        public const int D2 = 0x32;
        public const int D3 = 0x33;
        public const int D4 = 0x34;
        public const int D5 = 0x35;
        public const int D6 = 0x36;
        public const int D7 = 0x37;
        public const int D8 = 0x38;
        public const int D9 = 0x39;
        public const int A = 0x41;
        public const int C = 0x43;
        public const int D = 0x44;
        public const int L = 0x4C;
        public const int M = 0x4D;
        public const int N = 0x4E;
        public const int P = 0x50;
        public const int R = 0x52;
        public const int T = 0x54;
        public const int V = 0x56;
        public const int W = 0x57;
        public const int X = 0x58;
        public const int Y = 0x59;
        public const int Z = 0x5A;
        public const int Apps = 0x5D;
        public const int E = 0x45;
        public const int F = 0x46;
        public const int F1 = 0x70;
        public const int F2 = 0x71;
        public const int F3 = 0x72;
        public const int F6 = 0x75;
        public const int F5 = 0x74;
        public const int F11 = 0x7A;
        public const int OemComma = 0xBC;
        public const int H = 0x48;
        public const int B = 0x42;
    }
}
