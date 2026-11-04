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
/// 这里改成一张显式表 + 一个 <c>PreviewKeyDown</c> 派发点：
/// 匹配规则是自己的代码，可读、可断言、不依赖键盘布局。
/// </summary>
internal static class KeyBindings
{
    /// <summary>修饰键只比较这三类；Win 键不参与匹配（避免吞掉系统级组合）。</summary>
    private const ModifierKeys Relevant = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    private static readonly Dictionary<(Key Key, ModifierKeys Modifiers), string> Map = new()
    {
        // 应用（§7）
        [(Key.OemComma, ModifierKeys.Control)] = CommandIds.OpenSettings,

        // 导航（§2）
        [(Key.Up, ModifierKeys.Alt)] = CommandIds.GoUp,
        [(Key.Left, ModifierKeys.Alt)] = CommandIds.GoBack,
        [(Key.Right, ModifierKeys.Alt)] = CommandIds.GoForward,
        [(Key.Back, ModifierKeys.None)] = CommandIds.GoUp,
        [(Key.L, ModifierKeys.Control)] = CommandIds.FocusAddress,
        [(Key.F5, ModifierKeys.None)] = CommandIds.Refresh,
        [(Key.R, ModifierKeys.Control)] = CommandIds.Refresh,

        // 视图（§3）：大图标 `Ctrl+Shift+3` 不做，见 docs/adr/0005-large-icon-view.md
        [(Key.D1, ModifierKeys.Control | ModifierKeys.Shift)] = CommandIds.ViewDetails,
        [(Key.D2, ModifierKeys.Control | ModifierKeys.Shift)] = CommandIds.ViewList,
        [(Key.D, ModifierKeys.Control | ModifierKeys.Shift)] = CommandIds.CycleTheme,

        // 文件操作（§4）
        [(Key.Enter, ModifierKeys.None)] = CommandIds.OpenItem,
        [(Key.Enter, ModifierKeys.Alt)] = CommandIds.Properties,
        [(Key.F2, ModifierKeys.None)] = CommandIds.Rename,
        [(Key.Delete, ModifierKeys.None)] = CommandIds.Delete,
        [(Key.Delete, ModifierKeys.Shift)] = CommandIds.DeletePermanent,
        [(Key.C, ModifierKeys.Control)] = CommandIds.CopyItems,
        [(Key.X, ModifierKeys.Control)] = CommandIds.CutItems,
        [(Key.V, ModifierKeys.Control)] = CommandIds.Paste,
        [(Key.N, ModifierKeys.Control | ModifierKeys.Shift)] = CommandIds.NewFolder,
        [(Key.C, ModifierKeys.Control | ModifierKeys.Shift)] = CommandIds.CopyPath,
    };

    /// <summary>
    /// key 由 <paramref name="e"/> 归一化得到（Alt 组合下 WPF 上报的是 <c>Key.System</c>），
    /// 因此按键名直接查表即可。
    /// </summary>
    public static bool TryMatch(KeyEventArgs e, out string commandId)
    {
        commandId = string.Empty;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 单独按下修饰键不参与匹配
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return false;
        }

        ModifierKeys modifiers = Keyboard.Modifiers & Relevant;
        if (!Map.TryGetValue((key, modifiers), out string? id) || id is null) return false;

        commandId = id;
        return true;
    }

    /// <summary>上下文菜单里的键位提示文本，与表同源，避免菜单与文档各写一份。</summary>
    public static IReadOnlyDictionary<string, string> Gestures { get; } = BuildGestures();

    private static Dictionary<string, string> BuildGestures()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var ((key, modifiers), id) in Map)
        {
            // 一个命令可能有多组键位（如刷新有 F5 与 Ctrl+R），菜单只显示第一组
            result.TryAdd(id, Describe(key, modifiers));
        }

        return result;
    }

    private static string Describe(Key key, ModifierKeys modifiers)
    {
        string text = key switch
        {
            Key.OemComma => ",",
            Key.Up => "↑",
            Key.Left => "←",
            Key.Right => "→",
            Key.Back => "Backspace",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.Enter => "Enter",
            Key.Delete => "Delete",
            Key.F5 => "F5",
            Key.F2 => "F2",
            _ => key.ToString(),
        };

        if ((modifiers & ModifierKeys.Control) != 0) text = "Ctrl+" + text;
        if ((modifiers & ModifierKeys.Alt) != 0) text = "Alt+" + text;
        if ((modifiers & ModifierKeys.Shift) != 0) text = "Shift+" + text;

        return text;
    }
}
