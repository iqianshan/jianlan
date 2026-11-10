namespace Lingpane.Core.Tagging;

/// <summary>
/// 标签颜色（FR-4.1.1，预设八色）。做成标志枚举而不是"一个文件一个颜色"，
/// 是因为需求 FR-4.1.2 允许一项多色——数据模型先留出这个位置，
/// 界面上再逐步放开，不必为了"暂时只支持单色"把存储层写死成单值。
/// </summary>
[Flags]
public enum TagColors : byte
{
    None = 0,
    Red = 1,
    Orange = 2,
    Yellow = 4,
    Green = 8,
    Blue = 16,
    Purple = 32,
    Gray = 64,
    Black = 128,
}

/// <summary>
/// 一条标签记录（FR-4.2.2）。主键是 <c>(卷序列号, FileId)</c>，
/// <see cref="Path"/> 只是冗余——用于导出、清理失效项与界面展示，
/// **不参与匹配**：用路径匹配等于放弃"重命名后标签还在"这个唯一的存在理由。
/// </summary>
public sealed class TagRecord
{
    public TagRecord(ulong volume, ulong fileId, string path, TagColors colors, DateTimeOffset updatedAt)
    {
        Volume = volume;
        FileId = fileId;
        Path = path;
        Colors = colors;
        UpdatedAt = updatedAt;
    }

    public ulong Volume { get; }

    public ulong FileId { get; }

    /// <summary>记录时的路径。之后文件被重命名也不会更新（更新要扫盘），只当作线索用。</summary>
    public string Path { get; internal set; }

    public TagColors Colors { get; internal set; }

    public DateTimeOffset UpdatedAt { get; internal set; }

    public (ulong Volume, ulong FileId) Key => (Volume, FileId);

    /// <summary>单色时给出那一色，多色时给出位序最低的一色（列表圆点只画一个）。</summary>
    public TagColors Primary => TagPalette.Primary(Colors);
}

/// <summary>
/// 给某一项打标签所需的输入。<see cref="FileId"/> 为 0 表示这卷拿不到 FileId
/// （exFAT、部分网络盘），此时必须拒绝——否则所有条目的键都会是 <c>(卷, 0)</c>，
/// 给一个文件打标签会让整目录的文件一起变色，而这是最难解释的一类 bug。
/// </summary>
public readonly record struct TagTarget(ulong Volume, ulong FileId, string Path);

/// <summary>打标签的结果。分开返回是为了让状态栏说清楚"为什么没打上"。</summary>
public enum TagSetResult
{
    /// <summary>已写入。</summary>
    Applied,

    /// <summary>这一卷取不到 FileId，无法作为主键。</summary>
    NoFileId,

    /// <summary>已达 <see cref="TagStore.MaxRecords"/>，拒绝新增（显式降级，不静默丢弃）。</summary>
    Full,

    /// <summary>路径为空或颜色为空（<see cref="TagColors.None"/> 请走 <see cref="TagStore.Remove"/>）。</summary>
    Invalid,
}

/// <summary>
/// 标签颜色与展示值。<c>Hex</c> 固定不随主题变——"红色"在任何主题下都该是同一个红，
/// 跟着主题漂移会让人认不出自己打的标记；深色主题下"黑"看不见的问题靠圆点的一圈
/// 描边解决（见行模板），而不是把它换成灰。
///
/// 取名不在这里做：颜色名要跟着界面语言走，那是 App 层 <c>TagPresets</c> 的事。
/// </summary>
public static class TagPalette
{
    /// <summary>按菜单展示顺序排列的八个单色。位序即顺序，界面不必另存一张表。</summary>
    public static IReadOnlyList<TagColors> All { get; } =
    [
        TagColors.Red,
        TagColors.Orange,
        TagColors.Yellow,
        TagColors.Green,
        TagColors.Blue,
        TagColors.Purple,
        TagColors.Gray,
        TagColors.Black,
    ];

    /// <summary>取位序最低的一色。多色数据在只画一个圆点的地方用它，结果稳定可预期。</summary>
    public static TagColors Primary(TagColors colors)
    {
        foreach (TagColors single in All)
        {
            if ((colors & single) != 0) return single;
        }

        return TagColors.None;
    }

    /// <summary>
    /// 列表行里最多画几个色点（FR-4.1.2）。行高 28px、色点 6px + 2px 间距，
    /// 标签槽位 24px 宽，塞得下三个；八色全打上时按位序保留前三个。
    ///
    /// 不设成"全部"是因为行内空间是固定的，横向溢出会把文件名挤掉——
    /// 而"我到底给它打过哪几个颜色"在选中后打开右键菜单能看全（那里是可勾选的清单）。
    /// </summary>
    public const int MaxDots = 3;

    /// <summary>八色的展示值（<c>#RRGGBB</c>）。取自 Fluent 强调色阶，深浅主题下都够亮。</summary>
    public static string Hex(TagColors color) => color switch
    {
        TagColors.Red => "#E81123",
        TagColors.Orange => "#F7630C",
        TagColors.Yellow => "#EAA300",
        TagColors.Green => "#107C10",
        TagColors.Blue => "#0078D4",
        TagColors.Purple => "#8764B8",
        TagColors.Gray => "#8A8A8E",
        TagColors.Black => "#1F1F1F",
        _ => "#8A8A8E",
    };
}
