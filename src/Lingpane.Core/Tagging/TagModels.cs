namespace Lingpane.Core.Tagging;

/// <summary>
/// 标签颜色（FR-4.1.1）。做成标志枚举而不是"一个文件一个颜色"，
/// 是因为需求 FR-4.1.2 允许一项多色——数据模型先留出这个位置，
/// 界面上再逐步放开，不必为了"暂时只支持单色"把存储层写死成单值。
///
/// **底层是 32 位**（原先 8 位）。前八个具名成员是内置的标准色，用户的
/// "红/蓝/绿"这些日常说法对应它们；第九位往后没有名字，按**槽位**访问
/// （<see cref="TagPalette.FromSlot"/>）：槽位就是"标签可以占的位置"，
/// 名称与是否启用归标签定义管（<c>Appearance.Tags</c>），位值只负责存储。
///
/// 为什么扩到 32：8 位在"增删标签"成为需求的那一刻就用满了。
/// 再往上扩（64 位）没有意义——32 个标签对文件管理器已经远超够用，
/// 而位宽每加一档，磁盘格式与导出格式都要跟着动一次。
/// </summary>
[Flags]
public enum TagColors : uint
{
    None = 0,
    Red = 1u << 0,
    Orange = 1u << 1,
    Yellow = 1u << 2,
    Green = 1u << 3,
    Blue = 1u << 4,
    Purple = 1u << 5,
    Gray = 1u << 6,
    Black = 1u << 7,
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
/// 标签位与展示值。<c>Hex</c> 固定不随主题变——"红色"在任何主题下都该是同一个红，
/// 跟着主题漂移会让人认不出自己打的标记；深色主题下"黑"看不见的问题靠圆点的一圈
/// 描边解决（见行模板），而不是把它换成灰。
///
/// 取名不在这里做：名字要跟着界面语言走、还要允许用户改，那是 App 层
/// <c>TagPresets</c> 的职责。这里只管"第几位对应哪个色"。
/// </summary>
public static class TagPalette
{
    /// <summary>可用槽位总数。位序即槽位号（第 0 位是红色）。</summary>
    public const int SlotCount = 32;

    /// <summary>
    /// 系统当前启用的槽位掩码。
    ///
    /// 存在的理由：行内色点（<c>FileEntry.Dots</c>）在 Core 层算，拿不到设置。
    /// 用户"删除"一个标签时记录仍然保留（仅隐藏），这些点也必须跟着消失——
    /// 否则侧栏没有那一项、行上却还画着那种颜色，等于留了个点不进去的标记。
    /// 由 App 层在启动与设置变更时写入，默认全开。
    /// </summary>
    private static uint _activeMask = uint.MaxValue;

    /// <summary>按槽位顺序排列的 32 个单色。位序即顺序，界面不必另存一张表。</summary>
    public static IReadOnlyList<TagColors> All { get; } =
        [.. Enumerable.Range(0, SlotCount).Select(FromSlot)];

    /// <summary>槽位号（0–31）。多色时给位序最低的那一个。</summary>
    public static int SlotOf(TagColors color)
    {
        uint bits = (uint)color;
        if (bits == 0) return 0;

        return System.Numerics.BitOperations.TrailingZeroCount(bits);
    }

    /// <summary>按槽位取位值。越界夹到 0——宁可给"红"，也不要在界面上出现空色块。</summary>
    public static TagColors FromSlot(int slot)
    {
        if (slot < 0) slot = 0;
        if (slot >= SlotCount) slot = SlotCount - 1;

        return (TagColors)(1u << slot);
    }

    /// <summary>把一组槽位折成掩码（标签定义变更后重设 <see cref="ActiveMask"/> 用）。</summary>
    public static uint Mask(IEnumerable<int> slots)
    {
        uint mask = 0;
        foreach (int slot in slots)
        {
            if (slot is >= 0 and < SlotCount) mask |= 1u << slot;
        }

        return mask;
    }

    /// <summary>当前启用掩码。</summary>
    public static uint ActiveMask => _activeMask;

    /// <summary>这一位是否处于启用状态（删除标签后为假，记录仍保留）。</summary>
    public static bool IsActive(TagColors color) => ((uint)color & _activeMask) != 0;

    /// <summary>重设启用掩码。只由 App 层在设置加载与标签定义变更后调用。</summary>
    public static void SetActiveMask(uint mask) => _activeMask = mask;

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
    /// 标签槽位 24px 宽，塞得下三个；多色全打上时按位序保留前三个。
    ///
    /// 不设成"全部"是因为行内空间是固定的，横向溢出会把文件名挤掉——
    /// 而"我到底给它打过哪几个颜色"在选中后打开右键菜单能看全。
    /// </summary>
    public const int MaxDots = 3;

    /// <summary>
    /// 32 个槽位的展示值（<c>#RRGGBB</c>）。
    ///
    /// 前八个是 Windows 量出来的标准色阶，深浅主题下都够亮；后二十四个按色相环
    /// 每 15° 取一格、明度两档交替——它们存在的意义是"新增标签时还有得挑"，
    /// 不是让人分辨 32 种颜色。真要区分，用户会改名字。
    ///
    /// 不随主题变：见类注释。深色主题下深色槽位靠圆点描边保证可见。
    /// </summary>
    private static readonly string[] HexBySlot =
    [
        /*  0 */ "#E81123",
        /*  1 */ "#F7630C",
        /*  2 */ "#EAA300",
        /*  3 */ "#107C10",
        /*  4 */ "#0078D4",
        /*  5 */ "#8764B8",
        /*  6 */ "#8A8A8E",
        /*  7 */ "#1F1F1F",
        /*  8 */ "#B92727",
        /*  9 */ "#D86A46",
        /* 10 */ "#B97027",
        /* 11 */ "#D8B346",
        /* 12 */ "#B9B927",
        /* 13 */ "#B3D846",
        /* 14 */ "#70B927",
        /* 15 */ "#6AD846",
        /* 16 */ "#27B927",
        /* 17 */ "#46D86A",
        /* 18 */ "#27B970",
        /* 19 */ "#46D8B3",
        /* 20 */ "#27B9B9",
        /* 21 */ "#46B3D8",
        /* 22 */ "#2770B9",
        /* 23 */ "#466AD8",
        /* 24 */ "#2727B9",
        /* 25 */ "#6A46D8",
        /* 26 */ "#7027B9",
        /* 27 */ "#B346D8",
        /* 28 */ "#B927B9",
        /* 29 */ "#D846B3",
        /* 30 */ "#B92770",
        /* 31 */ "#D8466A",
    ];

    /// <summary>某一位的展示值。空掩码给中性灰（调用方本来就该先判 None）。</summary>
    public static string Hex(TagColors color)
        => color == TagColors.None ? "#8A8A8E" : HexBySlot[SlotOf(color)];
}
