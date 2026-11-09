namespace Lingpane.Core.FileSystem;

/// <summary>
/// 列表过滤：隐藏/系统文件的可见性开关（FR-5.2.4）与当前目录内的名称搜索（FR-6.13）。
///
/// 放在 Core 而不是视图模型里，因为这是整个应用里唯一一处"什么条目该出现在列表中"的判定。
/// 视图模型负责何时调用、界面负责把结果画出来；判定本身必须是可单独测试的纯函数，
/// 否则"搜索到 3 项但状态栏说 5 项"这类不一致只能靠肉眼发现。
///
/// 搜索只作用于**已经枚举好的这一批条目**：不递归子目录、不读盘。
/// 全盘搜索在需求黑名单里（见 docs/m2-plan.md §七），这里也不给它留后门。
/// </summary>
public static class EntryFilter
{
    /// <summary>
    /// 搜索结果上限。超过这个数，列表里排后面的条目用户根本不会翻到，
    /// 但每一条都会参与行容器创建与绑定——1 万项的搜索结果会让滚动变成幻灯片。
    /// 命中总数仍然照实统计，状态栏据此提示"仅显示前 N 项"。
    /// </summary>
    public const int MaxSearchResults = 1000;

    /// <summary>条目是否通过可见性开关。两个开关各判一次：带 H+S 的条目（desktop.ini）要两个都开才显示。</summary>
    public static bool IsVisible(FileEntry entry, bool showHidden, bool showSystem)
    {
        if (!showHidden && entry.IsHidden) return false;
        if (!showSystem && entry.IsSystem) return false;
        return true;
    }

    /// <summary>
    /// 把查询串拆成若干关键词：空白分隔、去空项。空查询返回空数组（= 不做名称过滤）。
    /// 用 <see cref="string.Split(char[], StringSplitOptions)"/> 的空白重载，
    /// 全角空格（U+3000）也一并拆开——中文用户从文档里粘过来的关键词常带全角空格。
    /// </summary>
    public static string[] Tokenize(string? query)
        => string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 名称是否命中查询：多个关键词之间是 **AND**（每一词都要出现），词内是无次序的子串匹配。
    ///
    /// 大小写口径用 <see cref="StringComparison.OrdinalIgnoreCase"/>，与
    /// <see cref="NaturalStringComparer"/>（列表名称排序）同一套：排序认作相同的名字，
    /// 搜索也应当认得出来。文化敏感比较在这里没有增益——子串匹配不做排序，
    /// 而它比序号比较慢数倍，万项目录的过滤时间是直接感知得到的。
    /// </summary>
    public static bool Matches(string name, string? query)
    {
        string[] tokens = Tokenize(query);
        return MatchesTokens(name, tokens);
    }

    private static bool MatchesTokens(string name, string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }

        return true;
    }

    /// <summary>
    /// 过滤并计数。<paramref name="sink"/> 只收前 <paramref name="limit"/> 项，
    /// 返回值是**命中总数**（不受上限影响）——两个数字分别用于列表与状态栏。
    ///
    /// 顺序保持 <paramref name="source"/> 的顺序：排序已经在枚举时做完了，
    /// 这里再排一次既浪费又可能引入第二套口径。文件夹优先也随之保留。
    /// </summary>
    /// <returns>命中总数。</returns>
    public static int Fill(
        IReadOnlyList<FileEntry> source,
        bool showHidden,
        bool showSystem,
        string? query,
        List<FileEntry> sink,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        string[] tokens = Tokenize(query);
        int matched = 0;

        foreach (FileEntry entry in source)
        {
            if (!IsVisible(entry, showHidden, showSystem)) continue;
            if (tokens.Length > 0 && !MatchesTokens(entry.Name, tokens)) continue;

            matched++;
            if (sink.Count < limit) sink.Add(entry);
        }

        return matched;
    }
}
