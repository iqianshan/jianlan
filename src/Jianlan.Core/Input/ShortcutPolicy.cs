using System;

namespace Jianlan.Core.Input;

/// <summary>一条候选键位为什么不能用。</summary>
public enum ShortcutProblem
{
    None = 0,

    /// <summary>没有按住任何键（录制框收到的是纯修饰键）。</summary>
    Empty,

    /// <summary>带了 Win 键。系统组合优先，注册不上的居多，而且用户按下去常常先触发系统动作。</summary>
    WinNotAllowed,

    /// <summary>既没有 Ctrl/Alt，也不是 F1–F24。</summary>
    NeedsModifier,

    /// <summary>被系统或本程序保留（Alt+Tab、Ctrl+Esc 之类）。</summary>
    Reserved,
}

/// <summary>
/// 一条快捷键能不能绑给命令。
///
/// 规则刻意从严：可用性比"什么都能绑"重要。允许单字母会让列表的首字母定位与输入框的
/// 打字全部失效；允许 Win 组合会在注册时静默失败，用户只会看到"按了没反应"。
/// 冲突检测不在这里——那要知道整张表，归界面层做。
/// </summary>
public static class ShortcutPolicy
{
    /// <summary>系统占用、程序内保留的组合。按"修饰键 + 虚拟键码"精确匹配。</summary>
    private static readonly (ShortcutModifiers Modifiers, int VirtualKey)[] ReservedCombos =
    [
        (ShortcutModifiers.Alt, 0x73),                                   // Alt+F4 关闭窗口
        (ShortcutModifiers.Alt, 0x09),                                   // Alt+Tab 切换窗口
        (ShortcutModifiers.Alt, 0x20),                                   // Alt+Space 窗口菜单
        (ShortcutModifiers.Control, 0x1B),                               // Ctrl+Esc 开始菜单
        (ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x1B),     // Ctrl+Shift+Esc 任务管理器
        (ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x2E),       // Ctrl+Alt+Del
        (ShortcutModifiers.Alt, 0x2C),                                   // Alt+PrintScreen
    ];

    public static ShortcutProblem Check(ShortcutGesture gesture)
    {
        if (gesture.IsEmpty) return ShortcutProblem.Empty;
        if (gesture.HasWin) return ShortcutProblem.WinNotAllowed;
        if (!gesture.HasCommandModifier && !gesture.IsFunctionKey) return ShortcutProblem.NeedsModifier;

        foreach ((ShortcutModifiers modifiers, int virtualKey) in ReservedCombos)
        {
            if (gesture.VirtualKey == virtualKey && gesture.Modifiers == modifiers)
            {
                return ShortcutProblem.Reserved;
            }
        }

        return ShortcutProblem.None;
    }

    /// <summary>
    /// 这组键在文本输入框里会被输入框本身用掉。允许绑，但要在界面上说清楚：
    /// 焦点在地址栏、搜索框、就地改名输入框里时，这条命令不会触发。
    /// </summary>
    public static bool IsTextEditingGesture(ShortcutGesture gesture)
        => (gesture.Modifiers & ShortcutModifiers.Control) != 0
           && (gesture.Modifiers & (ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Win)) == 0
           && gesture.VirtualKey is 0x43 or 0x58 or 0x56 or 0x41 or 0x5A or 0x59;
}
