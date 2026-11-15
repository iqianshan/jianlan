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
/// 这里做的只是"关窗前主动松手"：取消未完成的组字、解除 IMM 关联。
/// 不改窗口生命周期、不改模态语义，出问题可以直接摘掉这一个订阅。
/// </summary>
internal static class ImeFocusRelease
{
    private const uint NI_COMPOSITIONSTR = 0x0015;
    private const uint CPS_CANCEL = 0x0004;

    /// <summary>把 <paramref name="window"/> 的关窗时机挂上"先松手再销毁"。</summary>
    public static void Attach(Window window) => window.Closing += (_, _) => Release(window);

    private static void Release(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 先撤掉未完成的组字：窗口销毁时还挂着未提交的组字串，输入法会在销毁回调里
        // 再走一遍组字结束流程，正是这条路径上的危险动作。
        IntPtr himc = ImmGetContext(hwnd);
        if (himc != IntPtr.Zero)
        {
            ImmNotifyIME(himc, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
            ImmReleaseContext(hwnd, himc);
        }

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
