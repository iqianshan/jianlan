using System.Windows.Threading;
using Jianlan.App.Services;
using Jianlan.App.Shell;
using Jianlan.Core.FileSystem;

namespace Jianlan.App.Views;

/// <summary>
/// 真实图标补充：目录专属图标（ADR-0006）与快捷方式图标（FR-6.5），
/// 导航完成后在后台补齐。
///
/// 时序上刻意排在首帧之后（<see cref="DispatcherPriority.ApplicationIdle"/>）：
/// 图标只是"看起来更像资源管理器"，绝不能占用启动预算。旧结果用代次号丢弃，
/// 连续快速导航不会出现图标串到别的目录。
/// </summary>
public partial class PaneView
{
    private int _iconGeneration;

    internal void ScheduleFolderIcons(string directory)
    {
        int generation = ++_iconGeneration;
        if (string.IsNullOrEmpty(directory)) return;

        Dispatcher.InvokeAsync(() => ResolveFolderIconsAsync(generation), DispatcherPriority.ApplicationIdle);
    }

    private async void ResolveFolderIconsAsync(int generation)
    {
        if (generation != _iconGeneration) return;

        // 快照必须在 UI 线程取：Entries 会被过滤/刷新整体替换。
        //
        // 这里不能用非空访问器 Vm：本方法是 async void，异常没有调用方可接，
        // 只会落到 DispatcherUnhandledException 上（AC-03 压测里就是这样把一次
        // "关分屏 + 图标补充"的时序撞成了进程级未处理异常）。`Release()` 会把
        // 视图模型置空，而本方法的排队回调可能晚于它——图标是视觉细节，宿主换绑
        // 或收走窗格时应当静默退出，不能把进程带走。
        if (ViewModel is not { } vm) return;

        FileEntry[] snapshot;
        try
        {
            snapshot = vm.Entries.ToArray();
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Warn("icons.snapshot", "E2002", ex.Message);
            return;
        }

        if (snapshot.Length == 0) return;

        List<(FileEntry Entry, string Token)> updates;
        try
        {
            // Shell 命名空间（SHParseDisplayName / IShellFolder）必须单线程 STA 访问，
            // 见 ShellWorker：并发 + 跨套间会让 Shell 里的 DirectUI 组件崩在布局上。
            updates = await ShellWorker.RunAsync(() => FolderIcons.Resolve(snapshot)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 拿不到图标只是少个视觉细节，绝不能让浏览流程因此报错
            Diagnostics.AppLog.Warn("icons.resolve", "E2001", ex.Message);
            return;
        }

        if (updates.Count == 0 || generation != _iconGeneration) return;

        foreach ((FileEntry entry, string token) in updates)
        {
            entry.SetIconToken(token);
        }
    }
}
