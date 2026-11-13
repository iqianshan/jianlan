using System.Text.Json;

namespace Jianlan.Core.Workspaces;

/// <summary>
/// 一份命名工作区（FR-8）。与"会话恢复"（FR-2.11）的区别：
/// 会话是隐式、自动、只有一份；工作区是显式、命名、可以有多份。
///
/// 名字就是身份，不额外发 Guid —— <c>workspaces.json</c> 是用户会打开看甚至手改的文件，
/// 多一层 <c>id → name</c> 映射只会让手改更难，也让"重命名"变成改两处。
/// </summary>
public sealed class Workspace
{
    public string Name { get; set; } = string.Empty;

    /// <summary>最后一次保存的时间。管理界面要显示它（FR-8.3）。</summary>
    public DateTimeOffset SavedAt { get; set; }

    public List<WorkspaceTab> Tabs { get; set; } = [];

    public int ActiveTabIndex { get; set; }

    /// <summary>没被本程序认识的字段原样保留，改写文件时不丢别人的东西。</summary>
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// 工作区里的一个标签。第二窗格只记路径：窗格本身等标签被激活时才建，
/// 否则打开一个 12 标签的工作区会在首帧就把 24 个窗格全建出来（同 FR-2.11 的口径）。
/// </summary>
public sealed class WorkspaceTab
{
    public string Path { get; set; } = string.Empty;

    /// <summary>双窗格另一侧的路径；空表示这个标签没有分屏。</summary>
    public string? Second { get; set; }

    public bool Pinned { get; set; }

    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// <c>workspaces.json</c> 的顶层结构。独立成文件而不是塞进 settings.json：
/// 需求（FR-8 性能约束）要求工作区**不参与冷启动默认路径**——
/// 放进 settings.json 就等于每次启动都解析一遍所有工作区的标签列表。
/// </summary>
public sealed class WorkspacesDocument
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>工作区数量上限（FR-8.5）。</summary>
    public const int MaxWorkspaces = 50;

    /// <summary>单个工作区的标签上限（FR-8.5）。</summary>
    public const int MaxTabsPerWorkspace = 50;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<Workspace> Items { get; set; } = [];

    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// 读了就洗一遍：空名、重名（忽略大小写）、超量、空路径的标签全部去掉。
    /// 这是一份可能被手改的文件，规范化必须放在读入侧，
    /// 否则"第 3 个工作区名叫空字符串"这种状态会一路带到界面上。
    /// </summary>
    public void Normalize()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<Workspace>(Math.Min(Items.Count, MaxWorkspaces));

        foreach (Workspace? ws in Items)
        {
            if (ws is null) continue;

            ws.Name = (ws.Name ?? string.Empty).Trim();
            if (ws.Name.Length == 0 || !seen.Add(ws.Name)) continue;

            ws.Tabs ??= [];
            ws.Tabs.RemoveAll(t => t is null || string.IsNullOrWhiteSpace(t.Path));
            if (ws.Tabs.Count > MaxTabsPerWorkspace)
            {
                ws.Tabs.RemoveRange(MaxTabsPerWorkspace, ws.Tabs.Count - MaxTabsPerWorkspace);
            }

            foreach (WorkspaceTab tab in ws.Tabs)
            {
                tab.Path = tab.Path.Trim();
                tab.Second = string.IsNullOrWhiteSpace(tab.Second) ? null : tab.Second.Trim();
            }

            // 活动下标指向被截掉的标签时别留一个越界值
            ws.ActiveTabIndex = Math.Clamp(ws.ActiveTabIndex, 0, Math.Max(0, ws.Tabs.Count - 1));

            kept.Add(ws);
            if (kept.Count >= MaxWorkspaces) break;
        }

        Items = kept;
    }
}
