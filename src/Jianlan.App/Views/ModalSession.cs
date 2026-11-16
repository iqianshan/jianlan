// i18n-ignore-file：本文件不含界面文本。
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Jianlan.App.Shell;

namespace Jianlan.App.Views;

/// <summary>
/// 手动模态：把一个**常驻窗口**显示一轮并等待结果，而不是每次都 <c>new</c> + <c>ShowDialog()</c>。
///
/// 存在的理由（AC-03 的原生崩溃，四份转储逐帧一致）：
/// 崩溃固定在 <c>dui70.dll+0x65642</c>（<c>mov [rdx], rax</c>，<c>rdx = 0</c>），
/// 栈上自内向外是 <c>dui70 → duser → msctf → shell32 → textinputframework</c>，
/// 即 TSF 文本输入界面元素在 DirectUI 上被摘除时前驱为空。
///
/// 触发面是"打开-关闭"这个动作本身：<c>ShowDialog()</c> 返回时 WPF 会销毁 HWND，
/// 而 TSF 会为每个拿到键盘焦点的 HWND 建立一份默认文本存储与对应的 DirectUI 元素。
/// 压测里约 250 次模态开关就是 250 次上下文建/毁，输入法把界面元素挂在那份上下文上，
/// HWND 没了绑定还在，下一次按键就写到了已摘除的节点。
///
/// 这里改成"窗口建一次、之后只 Hide/Show"：HWND 不销毁，TSF 的默认文本存储也就
/// 只有一份、活到进程结束，链路上不再有反复的创建与销毁。
///
/// 代价：<c>ShowDialog()</c> 的模态语义要自己维护——禁父窗、压自己的消息泵、接 Esc/Enter、
/// 恢复原焦点。这几件事都在下面的 <see cref="Run"/> 里，调用方的同步语义不变。
/// </summary>
internal static class ModalSession
{
    private static bool _busy;

    /// <summary>
    /// 以手动模态显示 <paramref name="window"/>，直到 <c>finish</c> 回调被调用。
    /// </summary>
    /// <param name="prepare">
    /// 显示前的准备：设置标题/文案/按钮，并把 <c>finish</c> 存起来供窗口内部调用。
    /// 每次显示都要完整重置一遍——窗口是复用的，上一轮的文案与按钮可见性会残留。
    /// </param>
    /// <param name="onShown">显示后需要抢焦点、选中文本时用（在输入优先级上排队执行）。</param>
    /// <returns>窗口给的结果；被重入拦下时返回 <c>null</c>。</returns>
    public static bool? Run(Window window, Window? owner, Action<Action<bool?>> prepare, Action? onShown = null)
    {
        // 同一时刻只允许一层模态。正常路径进不来（父窗已禁用），
        // 但双击、键盘重复触发这类竞态下宁可少弹一个窗，也不能把消息泵压成两层。
        if (_busy) return null;

        bool? result = null;
        bool finished = false;
        bool allowClose = false;
        var frame = new DispatcherFrame();

        void Finish(bool? value)
        {
            if (finished) return;
            finished = true;
            result = value;
            frame.Continue = false;
        }

        IInputElement? previousFocus = Keyboard.FocusedElement;
        bool ownerWasEnabled = owner?.IsEnabled ?? false;

        // 窗口是常驻的：关闭请求只结束本轮模态，不销毁 HWND。
        // 唯一例外是主窗口已经不在了（应用正在退出），此时必须放行，否则进程退不出去。
        CancelEventHandler onClosing = (_, e) =>
        {
            Finish(false);
            allowClose = Application.Current?.MainWindow is not { IsVisible: true };
            e.Cancel = !allowClose;
        };

        _busy = true;
        window.Closing += onClosing;

        try
        {
            if (owner is not null)
            {
                window.Owner = owner;
                owner.IsEnabled = false;
            }

            prepare(Finish);
            window.Show();
            window.Activate();

            if (onShown is not null)
            {
                // 必须在 Show 之后、且等布局完成再抢焦点，否则 Select 会被随后的
                // 排版重置掉（原来靠 Loaded 事件，复用后 Loaded 只触发一次）。
                window.Dispatcher.BeginInvoke(DispatcherPriority.Input, onShown);
            }

            Dispatcher.PushFrame(frame);
        }
        finally
        {
            window.Closing -= onClosing;

            if (!allowClose)
            {
                // 隐藏前撤掉未提交的组字，但**不**解 IMM 关联：
                // 窗口还要再用，解关联会逼着下一次显示重新建上下文，等于绕回老路。
                ImeFocusRelease.CancelComposition(window);
                window.Hide();
            }

            if (owner is not null)
            {
                owner.IsEnabled = ownerWasEnabled;
                if (owner.IsVisible) owner.Activate();
            }

            // ShowDialog 关闭时 WPF 会把焦点还给父窗，这里得自己还，
            // 而且要还到"弹出之前那个元素"上，否则焦点落在窗口壳上，
            // 后续按键全部打空（压测里表现为某一段动作一次都没落地）。
            if (previousFocus is UIElement element && element.IsVisible)
            {
                Keyboard.Focus(element);
            }

            _busy = false;
        }

        return result;
    }
}
