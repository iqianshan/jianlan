using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Jianlan.App.Shell;

/// <summary>
/// 在窗口销毁之前，把这个 HWND 与输入法上下文的关联摘掉。
///
/// 存在的理由（AC-03 压测的原生崩溃）：
/// 崩溃固定在 <c>dui70.dll+0x65642</c>，指令是 <c>mov [rdx], rax</c> 且 <c>rdx = 0</c>，
/// 属于 DirectUI 元素链表摘除时前驱为空。调用链为
/// <c>msctf!CtfImeToAsciiEx → textinputframework!InputFocusChanged →
/// DirectUI::Element::OnInput</c>，元素属于输入法自己的界面（探针里能看到
/// <c>Sogou_TSF_UI</c> / <c>SoPY_UI</c> / <c>CiceroUIWndFrame</c>）。
///
/// 触发条件是"模态窗反复打开关闭 + 每轮 45 秒空闲"：我们的模态窗每次都是一次新的 HWND，
/// 关闭即销毁；输入法把界面元素挂在那个 HWND 的输入上下文上，HWND 没了但绑定还在，
/// 下一次按键就写到了已摘除的元素上。
///
/// 这里做两件事：
/// · <see cref="CancelComposition"/>  隐藏前撤掉未提交的组字，但**保留** IMM 关联；
/// · <see cref="Attach"/>             窗口真正销毁（进程退出）时才解除关联。
///
/// 2026-11-16 更正：只做"关窗前松手"没能压住崩溃，8 小时压测跑到 69 分钟仍崩在同一帧，
/// 说明病症不在"松手不够彻底"，而在"窗口反复新建/销毁"这个动作本身。模态窗改为常驻复用
/// （Views/ModalSession.cs）后，每次隐藏就只剩撤组字这一件事；解关联挪到真正的销毁路径上。
/// </summary>
internal static class ImeFocusRelease
{
    private const uint NI_COMPOSITIONSTR = 0x0015;
    private const uint CPS_CANCEL = 0x0004;

    /// <summary>把 <paramref name="window"/> 的关窗时机挂上"先松手再销毁"。</summary>
    public static void Attach(Window window) => window.Closing += (_, _) => Release(window);

    /// <summary>
    /// 只撤掉未提交的组字，不动 IMM 关联。给"常驻窗口 Hide 之前"用：
    /// 关联留着，下一次显示就不用重建输入上下文，不会退回反复建毁的老路。
    /// </summary>
    public static void CancelComposition(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        IntPtr himc = ImmGetContext(hwnd);
        if (himc == IntPtr.Zero) return;

        ImmNotifyIME(himc, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
        ImmReleaseContext(hwnd, himc);
    }

    private static void Release(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 先撤掉未完成的组字：窗口销毁时还挂着未提交的组字串，输入法会在销毁回调里
        // 再走一遍组字结束流程，正是这条路径上的危险动作。
        CancelComposition(window);

        // 解除关联：此后 IMM32 不再把这个 HWND 当作输入目标，输入法也就没有
        // "这个窗口还在"的依据，不需要在销毁后继续持有它的界面元素。
        ImmAssociateContext(hwnd, IntPtr.Zero);
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);

    [DllImport("imm32.dll")]
    private static extern bool ImmNotifyIME(IntPtr himc, uint action, uint index, uint value);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hwnd, IntPtr himc);
}
