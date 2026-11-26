using System;

namespace Jianlan.Core.Input;

/// <summary>一条候选键位为什么不能用。</summary>
public enum ShortcutProblem
{
    None = 0,

    /// <summary>没有按住任何键（录制框收到的是纯修饰键）。</summary>
    Empty,

    /// <summary>
    /// 带了 Win 键。系统组合优先，窗口内的按键多半到不了本程序，而且用户按下去常常
    /// 先触发系统动作。只有全局唤起键例外（<c>RegisterHotKey</c> 能接管 MOD_WIN）。
    /// </summary>
    WinNotAllowed,

    /// <summary>既没有 Ctrl/Alt，又不是 F1–F24，也不是 Shift + 不产生文字的键。</summary>
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

    /// <summary>
    /// <paramref name="allowWin"/> 只给全局唤起键开：它走 <c>RegisterHotKey</c>，
    /// MOD_WIN 是合法修饰键，注册不上时还能当场告诉用户"已被占用"。
    /// 窗口内的命令不开——那些组合多数在系统层就被吃掉了。
    /// </summary>
    public static ShortcutProblem Check(ShortcutGesture gesture, bool allowWin = false)
    {
        if (gesture.IsEmpty) return ShortcutProblem.Empty;
        if (gesture.HasWin && !allowWin) return ShortcutProblem.WinNotAllowed;

        // 全局热键场景下 Win 本身就是命令修饰键：Win+J 与 Ctrl+J 一样是完整组合。
        bool hasCommandModifier = gesture.HasCommandModifier || (allowWin && gesture.HasWin);

        if (!hasCommandModifier && !gesture.IsFunctionKey && !IsShiftWithNonTypingKey(gesture))
        {
            return ShortcutProblem.NeedsModifier;
        }

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
    /// 只有 Shift、且按键本身不产生文字的组合：<c>Shift+Delete</c>、<c>Shift+←</c>。
    ///
    /// 这类组合不与输入框打字打架，而且资源管理器自己就用 <c>Shift+Delete</c> 表示
    /// 永久删除——它本来就是本程序的默认键位。如果把它们和 <c>Shift+字母</c> 一起拒收，
    /// 规则就与内置默认值自相矛盾：默认值能用，用户解绑后再也录不回来。
    ///
    /// <c>Tab</c> 故意不在其中：<c>Shift+Tab</c> 是反向焦点导航，绑走之后键盘就走不出列表了。
    /// </summary>
    public static bool IsShiftWithNonTypingKey(ShortcutGesture gesture)
    {
        if ((gesture.Modifiers & ShortcutModifiers.Shift) == 0) return false;

        // 还有别的修饰键就不属于这一类，交给 HasCommandModifier 判
        if ((gesture.Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Win)) != 0)
        {
            return false;
        }

        return gesture.VirtualKey is 0x08 or 0x1B or 0x21 or 0x22 or 0x23 or 0x24  // Backspace Esc PgUp PgDn Home End
            or 0x25 or 0x26 or 0x27 or 0x28                                          // ← ↑ → ↓
            or 0x2D or 0x2E;                                                         // Insert Delete
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
