using Lingpane.App.Diagnostics;
using Lingpane.App.ViewModels;
using Lingpane.Core.Localization;
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

        // 打一次标、删一次标都要让侧栏「标签」那一段的数量跟上。
        // 不用整条重建：那会把当前选中的系统节点清掉，表现为"加了个标签，侧栏跳回顶部"。
        App.Tags.Changed += OnTagsChanged;

        _ = LoadTagsAsync();
    }

    /// <summary>
    /// 颜色名改了（FR-4.1.7）：侧栏那八个节点的文案要按新名字重建。
    ///
    /// 复用 <see cref="RefreshTagViews"/> 而不是只刷侧栏：正在看的标签结果页标题里
    /// 也写着颜色名，只刷一半会出现"侧栏写重要、页面标题写红色"。
    /// </summary>
    public void RefreshTagNames() => RefreshTagViews();

    private void OnTagsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshTagViews);
            return;
        }

        RefreshTagViews();
    }

    /// <summary>
    /// 标签变了（打上 / 去掉）：侧栏那一段的数量、以及正在看的结果页都要跟上。
    ///
    /// 侧栏重建会把当前选中项清掉——重建后要把它放回去，
    /// 否则"在看红标，顺手给一个文件打了红标，侧栏高亮就没了"。
    /// </summary>
    private void RefreshTagViews()
    {
        NavTagRow? selected = QuickAccessList.SelectedItem as NavTagRow;

        _nav.RefreshTagRows();

        if (selected is not null)
        {
            _syncingNavSelection = true;
            try
            {
                QuickAccessList.SelectedItem = _nav.FindTagRow(selected.Color);
            }
            finally
            {
                _syncingNavSelection = false;
            }
        }

        // 正在看某一色的结果页：这一色刚被改动，必须强制重取，
        // 否则页面停在旧列表上，用户以为"刚打的标签没生效"
        if (Tab is { IsTagResults: true } tab)
        {
            _tagResults.Load(App.Tags, tab.TagFilter, tab.TagFilterName, force: true);
        }
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

        // 侧栏那一段在首帧时是"还没有标签"的空态（那时库还没读），现在换成真实颜色。
        // 只换标签分区，不动用户已经选中的那一行。
        _nav.RefreshTagRows();

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

    // ── 标签结果页（FR-4.1.5） ─────────────────────────────────────────────

    /// <summary>
    /// 打开某一色的全部标记项。
    ///
    /// <paramref name="newTab"/> 为 false 时在当前标签里换内容：点完红再点蓝，
    /// 是在"换个角度看"，不该越点标签越多。Ctrl+单击才新开一个，原目录留着不动。
    /// </summary>
    private void OpenTagResults(TagColors color, string colorName, bool newTab)
    {
        if (!newTab)
        {
            Tab.ShowTagResults(color, colorName);
            return;
        }

        if (_tabs.OpenTagResults(color, colorName) is null)
        {
            // 到标签上限：退化成在当前标签里看，比"点了没反应"清楚
            Tab.ShowTagResults(color, colorName);
            Vm.SetStatus(Strings.Status_TabLimit);
        }
    }

    /// <summary>
    /// 双击结果里的某一条 → 跳到它所在目录并选中。
    ///
    /// 先 <see cref="TabViewModel.ShowFiles"/> 再导航：反过来的话，导航完成时视图
    /// 还停在结果页上，选中的那一行在屏外，用户看到的是"跳了但没选中"。
    /// </summary>
    private void OnTagResultActivated(TagResultItem item)
    {
        string? dir = System.IO.Path.GetDirectoryName(item.Path);

        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
        {
            Vm.SetStatus(Strings.Status_TagResultMissing);
            return;
        }

        Tab.ShowFiles();

        // 留住视图本身而不是事后取 ActiveView：等待导航的这段时间里用户可能切了标签，
        // 那时 ActiveView 已经是另一个窗格，选中会落到错的列表上
        PaneView view = ActiveView;
        PaneViewModel pane = Vm;

        void OnArrived(string path)
        {
            pane.NavigationCompleted -= OnArrived;
            view.SelectPaths([item.Path]);

            // 已经离开结果页了，侧栏不该还亮着那个颜色节点
            SyncNavSelection(path);
        }

        pane.NavigationCompleted += OnArrived;
        ActiveView.NavigateTo(dir);
    }
}
