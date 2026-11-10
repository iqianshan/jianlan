using System.Windows.Input;
using Lingpane.Core.Commands;

namespace Lingpane.App.Input;

/// <summary>
/// 全局快捷键表（docs/shortcuts.md §1–§7 的唯一实现处）。
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
/// 两者共用同一份表，不会出现"菜单写着 Ctrl+, 但按了没反应"。
/// </summary>
internal static class KeyBindings
{
    /// <summary>修饰键只比较这三类；Win 键不参与匹配（避免吞掉系统级组合）。</summary>
    private const ModifierKeys Relevant = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    /// <summary>
    /// 输入框内必须让给文本语义的键：焦点在 <c>TextBoxBase</c> 时这些组合不派发，
    /// 否则地址栏里按 Ctrl+C 会变成"复制文件"。
    /// </summary>
    private static readonly HashSet<int> TextEditingKeys =
    [
        Vk.C, Vk.X, Vk.V, Vk.A, Vk.Z, Vk.Y,
    ];

    private sealed record Entry(Key Key, int VirtualKey, ModifierKeys Modifiers, string CommandId, string Text);

    private static readonly Entry[] Table =
    [
        // 应用（§7）
        new(Key.OemComma, Vk.OemComma, ModifierKeys.Control, CommandIds.OpenSettings, ","),

        // 标签页（§1）。Ctrl+Tab 在 WPF 里同样是 WM_KEYDOWN(VK_TAB)+Ctrl，
        // 与浏览器一致；Ctrl+1…8 跳到第 N 个，Ctrl+9 跳最后一个（不是第 9 个）。
        new(Key.T, Vk.T, ModifierKeys.Control, CommandIds.NewTab, "T"),
        new(Key.W, Vk.W, ModifierKeys.Control, CommandIds.CloseTab, "W"),
        new(Key.T, Vk.T, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.ReopenTab, "T"),
        new(Key.Tab, Vk.Tab, ModifierKeys.Control, CommandIds.NextTab, "Tab"),
        new(Key.Tab, Vk.Tab, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.PreviousTab, "Tab"),
        new(Key.D1, Vk.D1, ModifierKeys.Control, CommandIds.GotoTab(1), "1"),
        new(Key.D2, Vk.D2, ModifierKeys.Control, CommandIds.GotoTab(2), "2"),
        new(Key.D3, Vk.D3, ModifierKeys.Control, CommandIds.GotoTab(3), "3"),
        new(Key.D4, Vk.D4, ModifierKeys.Control, CommandIds.GotoTab(4), "4"),
        new(Key.D5, Vk.D5, ModifierKeys.Control, CommandIds.GotoTab(5), "5"),
        new(Key.D6, Vk.D6, ModifierKeys.Control, CommandIds.GotoTab(6), "6"),
        new(Key.D7, Vk.D7, ModifierKeys.Control, CommandIds.GotoTab(7), "7"),
        new(Key.D8, Vk.D8, ModifierKeys.Control, CommandIds.GotoTab(8), "8"),
        new(Key.D9, Vk.D9, ModifierKeys.Control, CommandIds.LastTab, "9"),

        // 双窗格（§8）。F3 与资源管理器的分屏习惯一致，Ctrl+Shift+E 是给键盘党的另一条路。
        // F6 换焦点窗格：资源管理器里 F6 就是在导航栏/内容/预览之间轮换焦点，用户不用重新学。
        new(Key.F3, Vk.F3, ModifierKeys.None, CommandIds.ToggleSplit, "F3"),
        new(Key.F6, Vk.F6, ModifierKeys.None, CommandIds.FocusOtherPane, "F6"),
        new(Key.E, Vk.E, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.ToggleSplit, "E"),

        // 预览（§9 / FR-9.1.1）。Alt+P 与资源管理器一致；面板类开关一律用 Alt+字母，
        // 这样它们和 Ctrl+字母的文件操作不会抢键。Ctrl+Shift+P 是给外接键盘/远程桌面的
        // 备用入口——某些环境里 Alt 组合会被输入法截走。
        new(Key.P, Vk.P, ModifierKeys.Alt, CommandIds.TogglePreview, "P"),
        new(Key.P, Vk.P, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.TogglePreview, "P"),

        // 导航（§2）
        new(Key.Up, Vk.Up, ModifierKeys.Alt, CommandIds.GoUp, "↑"),
        new(Key.Left, Vk.Left, ModifierKeys.Alt, CommandIds.GoBack, "←"),
        new(Key.Right, Vk.Right, ModifierKeys.Alt, CommandIds.GoForward, "→"),
        new(Key.Back, Vk.Back, ModifierKeys.None, CommandIds.GoUp, "Backspace"),
        new(Key.L, Vk.L, ModifierKeys.Control, CommandIds.FocusAddress, "L"),
        // 当前目录搜索（FR-6.13）。与浏览器/资源管理器一致，用户不必重新学。
        new(Key.F, Vk.F, ModifierKeys.Control, CommandIds.FocusSearch, "F"),
        new(Key.F5, Vk.F5, ModifierKeys.None, CommandIds.Refresh, "F5"),
        new(Key.R, Vk.R, ModifierKeys.Control, CommandIds.Refresh, "R"),

        // 视图（§3）：大图标 `Ctrl+Shift+3` 不做，见 docs/adr/0005-large-icon-view.md
        new(Key.D1, Vk.D1, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.ViewDetails, "1"),
        new(Key.D2, Vk.D2, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.ViewList, "2"),
        new(Key.D, Vk.D, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.CycleTheme, "D"),

        // 文件操作（§4）
        new(Key.Enter, Vk.Enter, ModifierKeys.None, CommandIds.OpenItem, "Enter"),
        new(Key.Enter, Vk.Enter, ModifierKeys.Alt, CommandIds.Properties, "Enter"),
        new(Key.F2, Vk.F2, ModifierKeys.None, CommandIds.Rename, "F2"),
        new(Key.Delete, Vk.Delete, ModifierKeys.None, CommandIds.Delete, "Delete"),
        new(Key.Delete, Vk.Delete, ModifierKeys.Shift, CommandIds.DeletePermanent, "Delete"),
        new(Key.C, Vk.C, ModifierKeys.Control, CommandIds.CopyItems, "C"),
        new(Key.X, Vk.X, ModifierKeys.Control, CommandIds.CutItems, "X"),
        new(Key.V, Vk.V, ModifierKeys.Control, CommandIds.Paste, "V"),
        new(Key.N, Vk.N, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.NewFolder, "N"),
        new(Key.M, Vk.M, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.NewFile, "M"),
        new(Key.C, Vk.C, ModifierKeys.Control | ModifierKeys.Shift, CommandIds.CopyPath, "C"),
        new(Key.A, Vk.A, ModifierKeys.Control, CommandIds.SelectAll, "A"),

        // 撤销（FR-6.8）。焦点在输入框里时 Ctrl+Z 归输入框自己——Vk.Z 在 TextEditingKeys 里，
        // 所以这里注册的是"列表有焦点时"的那一份。
        new(Key.Z, Vk.Z, ModifierKeys.Control, CommandIds.Undo, "Z"),

        // 上下文菜单（§4）：键盘上的菜单键
        new(Key.Apps, Vk.Apps, ModifierKeys.None, CommandIds.ShowContextMenu, "Apps"),

        // 导航栏（§2）：收藏当前目录（FR-7.1）。与浏览器的"加书签"同一个手势，
        // 不给 Ctrl+B——那是"打开侧栏"的习惯，容易与"收藏"混起来。
        new(Key.D, Vk.D, ModifierKeys.Control, CommandIds.Bookmark, "D"),
    ];

    private static readonly Dictionary<(Key, ModifierKeys), string> ByKey = BuildByKey();
    private static readonly Dictionary<(int, ModifierKeys), string> ByVirtualKey = BuildByVirtualKey();

    private static Dictionary<(Key, ModifierKeys), string> BuildByKey()
    {
        var map = new Dictionary<(Key, ModifierKeys), string>();
        foreach (Entry e in Table) map.TryAdd((e.Key, e.Modifiers), e.CommandId);
        return map;
    }

    private static Dictionary<(int, ModifierKeys), string> BuildByVirtualKey()
    {
        var map = new Dictionary<(int, ModifierKeys), string>();
        foreach (Entry e in Table) map.TryAdd((e.VirtualKey, e.Modifiers), e.CommandId);
        return map;
    }

    /// <summary>窗口消息层匹配：用物理键（VK）判定，不受输入法改写 <c>Key</c> 的影响。</summary>
    public static bool TryMatchVirtualKey(int virtualKey, out string commandId)
    {
        commandId = string.Empty;

        ModifierKeys modifiers = Keyboard.Modifiers & Relevant;
        if (!ByVirtualKey.TryGetValue((virtualKey, modifiers), out string? id) || id is null) return false;

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
        if (!ByKey.TryGetValue((key, modifiers), out string? id) || id is null) return false;

        commandId = id;
        return true;
    }

    /// <summary>焦点在输入框时，该物理键是否必须让给文本编辑语义。</summary>
    public static bool IsTextEditingKey(int virtualKey) => TextEditingKeys.Contains(virtualKey);

    /// <summary>上下文菜单里的键位提示文本，与表同源，避免菜单与文档各写一份。</summary>
    public static IReadOnlyDictionary<string, string> Gestures { get; } = BuildGestures();

    private static Dictionary<string, string> BuildGestures()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Entry e in Table)
        {
            // 一个命令可能有多组键位（如刷新有 F5 与 Ctrl+R），菜单只显示第一组
            result.TryAdd(e.CommandId, Describe(e.Text, e.Modifiers));
        }

        return result;
    }

    private static string Describe(string text, ModifierKeys modifiers)
    {
        // 前缀要从后往前加，最终才是 Windows 的书写顺序 Ctrl+Alt+Shift+Key。
        // 反过来写会得到 "Shift+Ctrl+N" 这种别扭形式。
        if ((modifiers & ModifierKeys.Shift) != 0) text = "Shift+" + text;
        if ((modifiers & ModifierKeys.Alt) != 0) text = "Alt+" + text;
        if ((modifiers & ModifierKeys.Control) != 0) text = "Ctrl+" + text;
        return text;
    }

    /// <summary>虚拟键码。只列本表用到的，避免引入 <c>System.Windows.Forms.Keys</c>。</summary>
    internal static class Vk
    {
        public const int Back = 0x08;
        public const int Tab = 0x09;
        public const int Enter = 0x0D;
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
        public const int F2 = 0x71;
        public const int F3 = 0x72;
        public const int F6 = 0x75;
        public const int F5 = 0x74;
        public const int OemComma = 0xBC;
    }
}
