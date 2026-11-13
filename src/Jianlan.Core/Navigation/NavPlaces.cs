namespace Jianlan.Core.Navigation;

/// <summary>
/// 侧栏「位置」分区里九项的**稳定键**，以及"哪些项被隐藏"的判定（FR-1.2.9）。
///
/// 为什么要单独一张键表：这九项来自两个不同的源头——桌面～视频 六项由
/// <see cref="KnownFolderService"/> 按 <see cref="KnownFolderKind"/> 解析，
/// 此电脑 / 回收站 / 网络 三项是界面层写死的系统节点。两类节点在
/// <c>NavItem</c> 上都表现为 <c>Kind = Folder</c>（后三项各有自己的 Kind），
/// **没有任何字段能区分"这是桌面还是下载"**。设置文件里要记"隐藏了哪几项"，
/// 就必须先给每一项一个不随语言、不随顺序、不随对象重建而变化的标识。
///
/// 用枚举名而不是序号：settings.json 是给人看、也是给人手改的。
/// 这一点与 <see cref="KnownFolders.CacheKey"/> 同一个口径，两者指向同一批目录，
/// 同一个概念用同一套写法，读文件的人不必猜"Desktop 和 desktop 是不是两回事"。
/// </summary>
public static class NavPlaces
{
    /// <summary>此电脑（可展开的容器节点，FR-1.2.2）。</summary>
    public const string ThisPc = "ThisPc";

    /// <summary>回收站（shell 命名空间，FR-1.2.3）。</summary>
    public const string RecycleBin = "RecycleBin";

    /// <summary>网络（shell 命名空间，FR-1.2.3）。</summary>
    public const string Network = "Network";

    /// <summary>
    /// 九项的全部键，按侧栏从上到下的显示顺序。菜单项顺序、以及
    /// <see cref="Normalize"/> 的归一顺序都取自这里——设置文件里存的是数组，
    /// 顺序乱了会让"我明明只隐藏了图片"变成一次看不出来的误读。
    ///
    /// 主页不在其中：它是应用内视图，不是某个位置，也不在「位置」标题下面。
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        .. KnownFolders.DisplayOrder.Select(KnownFolders.CacheKey),
        ThisPc,
        RecycleBin,
        Network,
    ];

    /// <summary>已知文件夹的键。</summary>
    public static string KeyOf(KnownFolderKind kind) => KnownFolders.CacheKey(kind);

    /// <summary>
    /// 这一项是否被隐藏。键为空（主页、驱动器等不该参与过滤的行）一律当作可见。
    ///
    /// <paramref name="hidden"/> 为空集合是**最常见的情形**（用户从没动过这项设置），
    /// 所以先按 Count 短路，不做集合构造、也不去 Contains。
    /// </summary>
    public static bool IsHidden(IReadOnlyCollection<string>? hidden, string? key)
        => !string.IsNullOrEmpty(key) && hidden is { Count: > 0 } && hidden.Contains(key);

    /// <summary>
    /// 把手改过的、旧版本留下的、或大小写不一致的键收成规范值。
    ///
    /// 行为有三条：
    ///   · 认不出的键**丢弃**——将来删掉某一项时，旧设置文件里的残留不该让程序出错，
    ///     也不该让下一次保存把它再写回去；
    ///   · 重复项**合并**（比较不分大小写，输出统一成枚举名）；
    ///   · 顺序**归一到 <see cref="All"/>**，与用户的点击顺序无关。
    ///
    /// 返回空集合表示"全部可见"：默认值不是"九项全隐藏"，这是这项设置能安全上线的前提——
    /// 老用户的设置文件里没有这个字段，读出来就该是原来的样子。
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? keys)
    {
        if (keys is null) return [];

        var wanted = new HashSet<string>(
            keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0) return [];

        return [.. All.Where(wanted.Contains)];
    }
}
