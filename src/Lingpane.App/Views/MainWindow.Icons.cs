using System.Windows.Threading;
using Lingpane.App.Services;
using Lingpane.Core.FileSystem;

namespace Lingpane.App.Views;

/// <summary>
/// 真实图标补充：目录专属图标（ADR-0006）与快捷方式图标（FR-6.5），
/// 导航完成后在后台补齐。
///
/// 时序上刻意排在首帧之后（<see cref="DispatcherPriority.ApplicationIdle"/>）：
/// 图标只是"看起来更像资源管理器"，绝不能占用启动预算。旧结果用代次号丢弃，
/// 连续快速导航不会出现图标串到别的目录。
/// </summary>
public partial class MainWindow
{
    private int _iconGeneration;

    private void ScheduleFolderIcons(string directory)
    {
        int generation = ++_iconGeneration;
        if (string.IsNullOrEmpty(directory)) return;

        Dispatcher.InvokeAsync(() => ResolveFolderIconsAsync(generation), DispatcherPriority.ApplicationIdle);
    }

    private async void ResolveFolderIconsAsync(int generation)
    {
        if (generation != _iconGeneration) return;

        // 快照必须在 UI 线程取：Entries 会被过滤/刷新整体替换
        FileEntry[] snapshot = _viewModel.Entries.ToArray();
        if (snapshot.Length == 0) return;

        List<(FileEntry Entry, string Token)> updates;
        try
        {
            updates = await Task.Run(() => FolderIcons.Resolve(snapshot)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 拿不到图标只是少个视觉细节，绝不能让浏览流程因此报错
            Diagnostics.AppLog.Warn("icons.resolve", "E6001", ex.Message);
            return;
        }

        if (updates.Count == 0 || generation != _iconGeneration) return;

        foreach ((FileEntry entry, string token) in updates)
        {
            entry.SetIconToken(token);
        }
    }
}
