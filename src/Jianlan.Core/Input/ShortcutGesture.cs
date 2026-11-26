using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jianlan.Core.Input;

/// <summary>快捷键的修饰键。与 Win32 的 MOD_* 不是同一套值，转换在界面层做。</summary>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// 一组快捷键：修饰键 + 物理键（虚拟键码）。
///
/// 为什么 Core 用自己的模型而不是 <c>System.Windows.Input.KeyGesture</c>：
///   · Core 不引 WPF，设置文件与校验规则要能被单测直接覆盖；
///   · <c>Key</c> 是"这一套键盘布局下的字符"，跨布局不稳定，真正匹配用的是物理键码。
///     两套输入（<c>WM_KEYDOWN</c> 与 <c>PreviewKeyDown</c>）都归一到 VK 之后再比，
///     才不会出现"这一台机器上 Ctrl+, 有效、换一台无效"。
///
/// 文本格式即设置文件里存的样子：<c>Ctrl+Alt+Shift+Key</c>，修饰键顺序固定，
/// 与用户在界面上看到的一致。解析与格式化必须成对，单测钉着往返。
/// </summary>
public readonly record struct ShortcutGesture(ShortcutModifiers Modifiers, int VirtualKey)
{
    /// <summary>未绑定。虚拟键码 0 永远不是有效按键。</summary>
    public static ShortcutGesture None => default;

    public bool IsEmpty => VirtualKey <= 0;

    /// <summary>是否带 Ctrl 或 Alt。校验"能不能绑"时用它，Shift 不算。</summary>
    public bool HasCommandModifier =>
        (Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt)) != 0;

    /// <summary>功能键（F1–F24）可以不带修饰键：它们不产生文字，不与输入打架。</summary>
    public bool IsFunctionKey => VirtualKey is >= 0x70 and <= 0x87;

    public bool HasWin => (Modifiers & ShortcutModifiers.Win) != 0;

    /// <summary>
    /// 解析用户输入/设置文件里的写法。失败返回 false，不抛异常——
    /// 手改坏了一行设置，该回落默认值，而不是让程序起不来。
    /// </summary>
    public static bool TryParse(string? text, out ShortcutGesture gesture)
    {
        gesture = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        ShortcutModifiers modifiers = ShortcutModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ShortcutModifiers.Control;
                    break;
                case "alt":
                    modifiers |= ShortcutModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= ShortcutModifiers.Shift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ShortcutModifiers.Win;
                    break;
                default:
                    return false;
            }
        }

        if (!TryParseKey(parts[^1], out int virtualKey)) return false;

        gesture = new ShortcutGesture(modifiers, virtualKey);
        return true;
    }

    /// <summary>
    /// 规范写法。未知键码退回 <c>VK_XX</c>：这种组合不能进设置表（<see cref="ShortcutPolicy"/>
    /// 会拦下），但日志里要能看出到底按了什么，不能变成空串。
    /// </summary>
    public string Format()
    {
        if (IsEmpty) return string.Empty;

        var text = new StringBuilder();
        // 顺序固定：Win 在最前面，其余与 Windows 的书写习惯一致（Win+Shift+S 而不是
        // Shift+Win+S）。用户按的是 "Win+Shift+F9"，界面上就得显示成这三个字。
        if ((Modifiers & ShortcutModifiers.Win) != 0) text.Append("Win+");
        if ((Modifiers & ShortcutModifiers.Control) != 0) text.Append("Ctrl+");
        if ((Modifiers & ShortcutModifiers.Alt) != 0) text.Append("Alt+");
        if ((Modifiers & ShortcutModifiers.Shift) != 0) text.Append("Shift+");
        text.Append(KeyName(VirtualKey));
        return text.ToString();
    }

    /// <summary>显示在界面上的单个键名，供录制框与表尾使用。</summary>
    public static string KeyName(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x30 and <= 0x39) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87) return "F" + (virtualKey - 0x70 + 1).ToString(CultureInfo.InvariantCulture);

        foreach ((string name, int vk) in NamedKeys)
        {
            if (vk == virtualKey) return name;
        }

        return "VK_" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>按键名字 → 虚拟键码。别名只用于"读得懂用户手写的设置"，写回去一律用规范名。</summary>
    private static readonly (string Name, int VirtualKey)[] NamedKeys =
    [
        ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Escape", 0x1B), ("Backspace", 0x08),
        ("Delete", 0x2E), ("Insert", 0x2D), ("Home", 0x24), ("End", 0x23),
        ("PageUp", 0x21), ("PageDown", 0x22),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
        ("Apps", 0x5D),
        // OEM 标点：物理位置固定，字符随键盘布局变，所以按 VK 记
        (",", 0xBC), (".", 0xBE), (";", 0xBA), ("/", 0xBF), ("\\", 0xDC),
        ("[", 0xDB), ("]", 0xDD), ("-", 0xBD), ("=", 0xBB), ("`", 0xC0), ("'", 0xDE),
    ];

    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = 0x1B,
        ["return"] = 0x0D,
        ["del"] = 0x2E,
        ["ins"] = 0x2D,
        ["pgup"] = 0x21,
        ["pgdn"] = 0x22,
        ["menu"] = 0x5D,
        ["period"] = 0xBE,
        ["comma"] = 0xBC,
        ["slash"] = 0xBF,
        ["backslash"] = 0xDC,
        ["semicolon"] = 0xBA,
        ["minus"] = 0xBD,
        ["equals"] = 0xBB,
        ["backquote"] = 0xC0,
        ["quote"] = 0xDE,
    };

    private static bool TryParseKey(string name, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;

        string key = name.Trim();

        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') { virtualKey = c; return true; }
            if (c is >= '0' and <= '9') { virtualKey = c; return true; }
        }

        foreach ((string known, int vk) in NamedKeys)
        {
            if (string.Equals(known, key, StringComparison.OrdinalIgnoreCase)) { virtualKey = vk; return true; }
        }

        if (Aliases.TryGetValue(key, out virtualKey)) return true;

        // F1–F24
        if (key.Length is >= 2 and <= 3 && (key[0] is 'F' or 'f')
            && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            && n is >= 1 and <= 24)
        {
            virtualKey = 0x70 + n - 1;
            return true;
        }

        return false;
    }
}
