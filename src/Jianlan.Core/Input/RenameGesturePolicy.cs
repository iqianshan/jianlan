using System;

namespace Jianlan.Core.Input;

/// <summary>
/// “再点一次改名”的判定规则（资源管理器的慢速双击重命名）。
///
/// 单独放在 Core 而不是写进 PaneView：这条规则有一张容易记错的真值表，
/// 散在鼠标事件里只能靠手点验，而它在 Core 里可以用单测把十几种组合逐条钉住。
/// 视图侧只负责把“点击次数、修饰键、命中位置”这些事实读出来传进来。
/// </summary>
public static class RenameGesturePolicy
{
    /// <summary>
    /// 按下鼠标的这一刻，是否要把这一行记为“改名的候选”。
    ///
    /// 判定发生在**按下**、决定发生在**松开**：中间只要手指移动超过拖拽阈值，
    /// 调用方就把候选清掉——那是拖文件，不是改名。
    /// </summary>
    /// <param name="doubleClickOpens">双击才打开（即“单击打开”关着）。单击打开时这个手势必须让位，否则两次点击都变成打开。</param>
    /// <param name="clickCount">本次按下的连击数。1 才是独立的一次点击，2 及以上是双击/三击序列。</param>
    /// <param name="hasModifiers">是否按着 Ctrl/Shift/Alt。带修饰键的点击另有语义（加选、新标签）。</param>
    /// <param name="hasFocus">列表此刻是否持有键盘焦点。分屏里点另一个窗格的已选中项，第一下只切活动窗格，资源管理器也不改名。</param>
    /// <param name="isSelected">按下的这一行此刻是否已被选中。</param>
    /// <param name="selectionCount">当前选中项总数。多选时先收敛成单选，那一次点击不改名。</param>
    /// <param name="onNameCell">是否点在文件名文字上。图标、色点、行尾空白都不算。</param>
    /// <param name="alreadyEditing">这一行是否已经在编辑中（避免重复进入）。</param>
    public static bool ShouldArmRename(
        bool doubleClickOpens,
        int clickCount,
        bool hasModifiers,
        bool hasFocus,
        bool isSelected,
        int selectionCount,
        bool onNameCell,
        bool alreadyEditing)
        => doubleClickOpens
           && clickCount == 1
           && !hasModifiers
           && hasFocus
           && isSelected
           && selectionCount == 1
           && onNameCell
           && !alreadyEditing;

    /// <summary>
    /// 进入改名时要预选的那一段文字。
    ///
    /// 文件只选词干（<c>报告.docx</c> → 选中 <c>报告</c>），因为绝大多数改名只动前半截；
    /// 文件夹没有扩展名概念，整名选中。以点开头且只有一个点的名字（<c>.gitignore</c>）
    /// 按 Windows 的老规矩视为“无扩展名”，也是整名选中——否则会选中一个空串，
    /// 用户一按键就把整名替换掉了。
    /// </summary>
    public static (int Start, int Length) StemSelection(string? name, bool isDirectory)
    {
        if (string.IsNullOrEmpty(name)) return (0, 0);
        if (isDirectory) return (0, name.Length);

        int dot = name.LastIndexOf('.');
        return dot > 0 ? (0, dot) : (0, name.Length);
    }
}
