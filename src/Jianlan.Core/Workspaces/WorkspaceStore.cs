// i18n-ignore-file：本文件的中文字符串只进日志/诊断输出，不进界面，不参与翻译。
using System.Text;
using System.Text.Json;
using Jianlan.Core.Diagnostics;
using Jianlan.Core.Settings;

namespace Jianlan.Core.Workspaces;

/// <summary>保存工作区的结果。分开返回是为了让界面能说清楚"为什么没存上"（FR-8.1 / FR-8.5）。</summary>
public enum WorkspaceSaveResult
{
    /// <summary>已保存。</summary>
    Saved,

    /// <summary>已经有同名工作区。界面应提示并询问是否覆盖（FR-8.3 的"覆盖更新"）。</summary>
    Duplicate,

    /// <summary>已达 <see cref="WorkspacesDocument.MaxWorkspaces"/>，拒绝新增（FR-8.5）。</summary>
    Full,

    /// <summary>名字为空，或没有任何可保存的标签。</summary>
    Invalid,
}

/// <summary>
/// 工作区的读写（FR-8）。数据落在独立的 <c>workspaces.json</c>，
/// **不参与冷启动默认路径**——需求明确要求它只在"打开工作区面板"或
/// "启用启动时自动恢复"时才读。
///
/// 与 <see cref="Navigation.BookmarkStore"/> 的差别：收藏是 settings.json 里的一段，
/// 存盘由调用方统一做；工作区自成一个文件，所以这里自己负责读写。
/// 读永不抛异常（文件损坏就当没有工作区，不能让程序打不开），写是原子的。
/// </summary>
public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private readonly WorkspacesDocument _doc;
    private readonly string _path;

    private WorkspaceStore(WorkspacesDocument doc, string path, bool readOnly, string? warning)
    {
        _doc = doc;
        _path = path;
        IsReadOnly = readOnly;
        LoadWarning = warning;
    }

    /// <summary>文件版本高于本程序时只读，避免抹掉新版本写下的字段。</summary>
    public bool IsReadOnly { get; }

    /// <summary>读入时的问题（无法读取 / 解析失败 / 版本过高），由调用方记日志并提示。</summary>
    public string? LoadWarning { get; }

    public IReadOnlyList<Workspace> Items => _doc.Items;

    public int Count => _doc.Items.Count;

    public bool IsFull => _doc.Items.Count >= WorkspacesDocument.MaxWorkspaces;

    /// <summary>
    /// 读入 <c>workspaces.json</c>。永不抛异常：缺失返回空集合，
    /// 损坏时把原文件备份成 <c>.bak</c> 再返回空集合。
    /// </summary>
    /// <param name="path">用于测试注入；默认走 AppPaths。</param>
    public static WorkspaceStore Load(string? path = null)
    {
        path ??= AppPaths.WorkspacesFile;

        if (!File.Exists(path)) return new WorkspaceStore(new WorkspacesDocument(), path, false, null);

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorkspaceStore(new WorkspacesDocument(), path, false,
                $"工作区文件无法读取：{ex.Message}");
        }

        WorkspacesDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<WorkspacesDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            string? warn = $"工作区文件解析失败，已备份为 .bak：{ex.Message}";
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                LogSink.Warn("workspace.load", "E9001", $"备份失败：{copyEx.Message}");  // i18n-ignore：只进 app.log
            }

            return new WorkspaceStore(new WorkspacesDocument(), path, false, warn);
        }

        if (parsed is null) return new WorkspaceStore(new WorkspacesDocument(), path, false, null);

        bool readOnly = parsed.SchemaVersion > WorkspacesDocument.CurrentSchemaVersion;
        string? versionWarn = readOnly
            ? $"工作区文件版本 {parsed.SchemaVersion} 高于本程序支持的 {WorkspacesDocument.CurrentSchemaVersion}，本次以只读方式使用"
            : null;

        parsed.Normalize();
        return new WorkspaceStore(parsed, path, readOnly, versionWarn);
    }

    /// <summary>原子写入：先写 .tmp，再 File.Replace 并保留 .bak（与 settings.json 同一套）。</summary>
    /// <returns>true 表示已落盘。</returns>
    public bool Save()
    {
        if (IsReadOnly) return false;

        _doc.Normalize();

        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(_doc, Options),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, _path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSink.Warn("workspace.save", "E9002", $"保存工作区失败：{ex.Message}");  // i18n-ignore：只进 app.log
            return false;
        }
    }

    public bool Contains(string name)
        => _doc.Items.Any(w => Same(w.Name, name));

    public Workspace? Find(string name)
        => _doc.Items.FirstOrDefault(w => Same(w.Name, name));

    /// <summary>
    /// 用当前标签新建一份工作区（FR-8.1）。同名时返回
    /// <see cref="WorkspaceSaveResult.Duplicate"/> 并且**不改动**已有的那份——
    /// "保存"和"覆盖"是两个动作，混在一起会让"手滑用了同名"直接毁掉旧工作区。
    /// </summary>
    public WorkspaceSaveResult SaveAs(string name, IEnumerable<WorkspaceTab> tabs,
        int activeIndex = 0, DateTimeOffset? at = null)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return WorkspaceSaveResult.Invalid;

        List<WorkspaceTab> list = Clean(tabs);
        if (list.Count == 0) return WorkspaceSaveResult.Invalid;

        if (Contains(name)) return WorkspaceSaveResult.Duplicate;
        if (IsFull) return WorkspaceSaveResult.Full;

        _doc.Items.Add(Build(name, list, activeIndex, at));
        Save();
        return WorkspaceSaveResult.Saved;
    }

    /// <summary>用当前标签覆盖已有工作区（FR-8.3）。名字不存在时按新建处理。</summary>
    public bool Overwrite(string name, IEnumerable<WorkspaceTab> tabs,
        int activeIndex = 0, DateTimeOffset? at = null)
    {
        List<WorkspaceTab> list = Clean(tabs);
        if (list.Count == 0) return false;

        Workspace? existing = Find(name);
        if (existing is null) return SaveAs(name, list, activeIndex, at) == WorkspaceSaveResult.Saved;

        // 原地改而不是"删了重建"：工作区在列表里的位置是用户熟悉的顺序
        existing.SavedAt = at ?? DateTimeOffset.UtcNow;
        existing.Tabs = list;
        existing.ActiveTabIndex = Math.Clamp(activeIndex, 0, list.Count - 1);
        Save();
        return true;
    }

    public bool Rename(string from, string to)
    {
        to = (to ?? string.Empty).Trim();
        if (to.Length == 0) return false;

        Workspace? ws = Find(from);
        if (ws is null || Same(from, to)) return false;

        // 改名撞上另一份工作区就拒绝：静默合并两份工作区没有第二种合理解释
        if (Contains(to)) return false;

        ws.Name = to;
        Save();
        return true;
    }

    public bool Remove(string name)
    {
        Workspace? ws = Find(name);
        if (ws is null) return false;

        _doc.Items.Remove(ws);
        Save();
        return true;
    }

    /// <summary>把最近保存的那份排到最前？不做：顺序按用户创建/保存的顺序来，界面不做隐式重排。</summary>
    private static Workspace Build(string name, List<WorkspaceTab> tabs, int activeIndex, DateTimeOffset? at) => new()
    {
        Name = name,
        SavedAt = at ?? DateTimeOffset.UtcNow,
        Tabs = tabs,
        ActiveTabIndex = Math.Clamp(activeIndex, 0, tabs.Count - 1),
    };

    /// <summary>去掉空路径、按上限截断。上限到了不提示也不报错：程序自身标签上限是 12，够不到 50。</summary>
    private static List<WorkspaceTab> Clean(IEnumerable<WorkspaceTab> tabs)
        => tabs
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Path))
            .Take(WorkspacesDocument.MaxTabsPerWorkspace)
            .ToList();

    private static bool Same(string? a, string? b)
        => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            TypeInfoResolver = SettingsJsonContext.Default,
        };

        return o;
    }
}
