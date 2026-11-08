using Lingpane.Core.Settings;

namespace Lingpane.Core.Navigation;

/// <summary>
/// 最近访问目录的读写（FR-1.1.2 / FR-5.3.2）。数据落在 settings.json 的 <c>history</c> 段，
/// 本类只负责"记一条 / 取前 N 条 / 清空"，不碰界面也不读盘。
///
/// 为什么只记目录：主页那一栏的用途是"回到刚才去过的地方"。文件级历史既列不下、
/// 也用不上（点开文件还是得先到它所在目录），却会把大量本地路径写进设置文件。
///
/// 为什么不在 <see cref="Record"/> 里判目录是否存在：目录可能暂时不在（U 盘没插、网络盘断线），
/// 判了就把用户的历史悄悄吃掉。存在性由展示方按需异步检查（见主页的失效标记）。
/// </summary>
public sealed class VisitHistory
{
    private readonly HistorySettings _store;
    private readonly Func<int> _limit;

    /// <param name="store">settings.json 里的 <c>history</c> 段，读写都直接作用于它。</param>
    /// <param name="limit">
    /// 条数上限，取"设置 → 隐私 → 历史条数"。用委托而不是值：
    /// 用户在设置页改完上限后应立即生效，而不是等重启。
    /// </param>
    public VisitHistory(HistorySettings store, Func<int>? limit = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _limit = limit ?? (() => HistorySettings.MaxEntries);

        // 读进来的顺序不可信（手改、旧版本写入、时钟回拨），先按时间排一次
        _store.Normalize(_limit());
    }

    public int Count => _store.Entries.Count;

    /// <summary>按访问时间倒序（最新在前）。</summary>
    public IReadOnlyList<HistoryEntry> Entries => _store.Entries;

    /// <summary>
    /// 记录一次访问。同一目录重复访问只保留最新一条并移到最前——
    /// 历史里出现五个同名目录是最典型的"这功能坏了"。
    /// </summary>
    public void Record(string path, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string normalized = path.Trim();
        if (normalized.Length == 0) return;

        _store.Entries.RemoveAll(e => string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase));

        // 插到最前而不是"加完再排"：相同时刻的两条（同一秒内连点）也只有最新的一条在最前
        _store.Entries.Insert(0, new HistoryEntry
        {
            Path = normalized,
            VisitedAt = at ?? DateTimeOffset.UtcNow,
        });

        int limit = Math.Max(0, _limit());
        if (_store.Entries.Count > limit) _store.Entries.RemoveRange(limit, _store.Entries.Count - limit);
    }

    /// <summary>主页要展示的最近若干条（默认 12，见 FR-1.1.2）。</summary>
    public IReadOnlyList<HistoryEntry> Recent(int count = HistorySettings.HomePageLimit)
        => count <= 0 ? [] : _store.Entries.Take(count).ToList();

    /// <summary>一键清空（FR-1.1.2 / FR-5.3.2）。只动历史，不碰会话与收藏。</summary>
    public void Clear() => _store.Entries.Clear();
}
