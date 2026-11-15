using System.Windows.Threading;

namespace Jianlan.App.Shell;

/// <summary>
/// Shell 命名空间调用的专属单线程（STA）。
///
/// 存在的理由是 AC-03 压测里的一次真实崩溃：进程在 <c>DUI70.dll</c> 的元素布局函数上
/// 报 <c>0xC0000005</c>。DUI70 是 Windows 的 DirectUI，本产品（WPF）自己不产生 DirectUI
/// 元素——它来自 Shell 命名空间：<c>SHParseDisplayName</c>、<c>IShellFolder</c> 以及文件夹
/// 的图标/预览实现（<c>Windows.FileExplorer.Common.dll</c>）都会把 DirectUI 拉进本进程。
///
/// DirectUI 的两个硬约束：
/// <list type="number">
/// <item><b>线程相关</b>——只能在创建它的线程上布局/销毁；</item>
/// <item><b>需要一个消息泵</b>——不少组件靠投递窗口消息推进状态。</item>
/// </list>
///
/// 改动前，图标补充走 <c>Task.Run</c>（线程池，MTA），与 UI 线程（STA）、缩略图的
/// 每条独立 STA 线程并发摸同一份 Shell 状态，套间模型还不一致。改为本类之后：
/// 所有 Shell 命名空间调用排在**同一条常驻 STA 线程**上，串行执行，且该线程带
/// Dispatcher 消息泵。缩略图那条"每次请求一条 STA 线程"的路径暂未并入（它有超时护栏，
/// 合并要先解决超时与线程泄漏的冲突），因此 <see cref="RunAsync{T}"/> 目前只服务图标解析。
/// </summary>
internal static class ShellWorker
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    private static Dispatcher Dispatcher
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher is { } ready) return ready;

                var started = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);

                var thread = new Thread(() =>
                {
                    // 这条线程就是 Shell 的"家"：先拿到 Dispatcher，再跑消息泵。
                    // STA 由运行时在 SetApartmentState 后自动 CoInitializeEx，无需手写。
                    started.SetResult(Dispatcher.CurrentDispatcher);
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "Jianlan.Shell",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                _dispatcher = started.Task.GetAwaiter().GetResult();
                return _dispatcher;
            }
        }
    }

    /// <summary>把一段 Shell 调用排到 Shell 线程上执行，异常按原样进返回的 Task。</summary>
    public static Task<T> RunAsync<T>(Func<T> work) => Dispatcher.InvokeAsync(work).Task;

    /// <summary>同上，无返回值。</summary>
    public static Task RunAsync(Action work) => Dispatcher.InvokeAsync(work).Task;
}
