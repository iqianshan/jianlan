using Lingpane.App.Diagnostics;
using Lingpane.App.ViewModels;
using Lingpane.Core.Tagging;

namespace Lingpane.App.Views;

/// <summary>
/// 彩色标签在窗口层的接线（FR-4.2.3 / FR-4.2.5）。
///
/// 标签是**懒加载**的：构造函数不读文件，首帧之后由 <see cref="InitializeTags"/>
/// 在线程池里读一次 <c>data\tags.jsonl</c>。列表早就在屏幕上了，读完再回头补圆点——
/// 这是需求里"未就绪先渲染，就绪后补标记"的落点，而不是"等标签库读好再显示文件"。
///
/// 窗格那一侧不需要知道标签库什么时候就绪：它每次导航都会问一次
/// <c>IsLoaded</c>，已就绪就走带 FileId 的枚举，没就绪就走快枚举。
/// 所以这里唯一要补的是"首帧那次导航"留下的那一屏。
/// </summary>
public partial class MainWindow
{
    private bool _tagsInitialized;

    private void InitializeTags()
    {
        if (_tagsInitialized) return;

        _tagsInitialized = true;
        _ = LoadTagsAsync();
    }

    private async Task LoadTagsAsync()
    {
        TagStore tags = App.Tags;

        try
        {
            await Task.Run(tags.EnsureLoaded);
        }
        catch (Exception ex)
        {
            // EnsureLoaded 内部已吃掉可预期的 IO 失败，走到这里说明是别的意外：
            // 记日志即可，标签不可用不该影响浏览（用户甚至看不出来）
            AppLog.Warn("tags.init", "E3007", ex.Message);
            return;
        }

        if (tags.Count == 0) return;

        await RefreshTagsAsync();
    }

    /// <summary>
    /// 给已经在屏幕上的行补圆点（FR-4.2.5）。
    ///
    /// 只补有内容的窗格：半开的标签（<c>HasPendingContent</c>）激活时会重新导航，
    /// 那时标签库已就绪，自然一次到位——提前补等于替它们白读一遍目录。
    /// </summary>
    private async Task RefreshTagsAsync()
    {
        foreach (TabViewModel tab in _tabs.Tabs)
        {
            await tab.Primary.RefreshTagsAsync();
            if (tab.Secondary is { } second) await second.RefreshTagsAsync();
        }
    }
}
