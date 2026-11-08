// i18n-ignore-file：本文件的中文字符串只进日志/诊断输出，不进界面，不参与翻译。
namespace Lingpane.Core.Commands;

/// <summary>
/// 命令标识。统一用常量，避免字符串在菜单、快捷键、按钮之间各写一份而拼错。
/// </summary>
public static class CommandIds
{
    public const string Refresh = "app.refresh";
    public const string GoUp = "nav.goUp";
    public const string GoBack = "nav.goBack";
    public const string GoForward = "nav.goForward";
    public const string CycleTheme = "app.cycleTheme";
    public const string OpenSettings = "app.openSettings";
    public const string ViewDetails = "view.details";
    public const string ViewList = "view.list";
    public const string FocusAddress = "nav.focusAddress";

    // 文件操作（M1 批 3）
    public const string OpenItem = "file.open";
    public const string NewFolder = "file.newFolder";
    public const string NewFile = "file.newFile";
    public const string Rename = "file.rename";
    public const string Delete = "file.delete";
    public const string DeletePermanent = "file.deletePermanent";
    public const string CopyItems = "file.copy";
    public const string CutItems = "file.cut";
    public const string Paste = "file.paste";
    public const string CopyPath = "file.copyPath";
    public const string Properties = "file.properties";
    public const string ShowContextMenu = "file.contextMenu";
    public const string SelectAll = "file.selectAll";

    // 文件夹大小（FR-6.16）。只有用户显式点菜单才会走到，没有任何自动触发路径。
    public const string CalculateSize = "file.calculateSize";

    // 系统 Shell 菜单（FR-6.9）。显式入口，日常右键仍走自建菜单。
    public const string MoreOptions = "file.moreOptions";

    // 标签页（FR-2）。窗口级：标签集合属于窗口，不属于某个窗格。
    public const string NewTab = "tab.new";
    public const string CloseTab = "tab.close";
    public const string ReopenTab = "tab.reopen";
    public const string NextTab = "tab.next";
    public const string PreviousTab = "tab.previous";
    public const string LastTab = "tab.last";

    /// <summary>Ctrl+1…8 跳转。<paramref name="n"/> 为 1 起始的序号。</summary>
    public static string GotoTab(int n) => $"tab.goto{n}";

    // 双窗格（FR-3）。窗口级：分屏是窗口形态，不是某个标签的属性——
    // 开着分屏切标签，新标签也照样是分屏，否则"切个标签分屏就没了"会被当成 bug。
    public const string ToggleSplit = "pane.toggleSplit";
    public const string FocusOtherPane = "pane.focusOther";

    // 收藏（FR-7.1）。窗口级：收藏是全局数据，跟着窗口存，不跟着某个窗格。
    public const string Bookmark = "bookmark.add";
}

/// <summary>
/// 一条命令的定义。不含任何 UI 类型——图标、按钮、菜单项都由界面层各自绑定，
/// Core 只描述"叫什么、什么时候能执行、执行做什么"。
/// </summary>
public sealed class AppCommand
{
    public required string Id { get; init; }

    /// <summary>
    /// 界面显示名，如"刷新"。用 <see cref="TitleFactory"/> 时以工厂结果为准，
    /// 这样切换语言后新读取到的名字就是新语言的（多语言落地方案见 docs/i18n.md）。
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>命令标题的文案工厂。设置后 <see cref="Title"/> 被忽略。</summary>
    public Func<string>? TitleFactory { get; init; }

    /// <summary>取当前语言下的显示名。</summary>
    public string ResolveTitle() => TitleFactory?.Invoke() ?? Title;

    /// <summary>快捷键的显示文本（仅用于提示，真正的绑定在界面层），如 "F5"。</summary>
    public string? Gesture { get; init; }

    public required Action Execute { get; init; }

    /// <summary>返回 false 时界面应禁用该命令。为空表示始终可执行。</summary>
    public Func<bool>? CanExecute { get; init; }

    public bool CanRun() => CanExecute?.Invoke() ?? true;
}

/// <summary>
/// 命令注册表。按 Id 查找，重复注册抛异常——命令 Id 冲突属于编码错误，
/// 应当尽早暴露，而不是让后注册的静默覆盖前一个。
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, AppCommand> _commands = new(StringComparer.Ordinal);

    public void Register(AppCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_commands.TryAdd(command.Id, command))
        {
            throw new InvalidOperationException($"命令 Id 重复：{command.Id}");
        }
    }

    public AppCommand Get(string id) =>
        _commands.TryGetValue(id, out var c)
            ? c
            : throw new KeyNotFoundException($"未注册的命令：{id}");

    /// <summary>
    /// 尝试取命令，不抛异常。多张命令表之间路由时用：
    /// "窗格有没有这条命令"决定按键落到谁身上，而不是看它此刻可不可执行。
    /// </summary>
    public bool TryGet(string id, out AppCommand command) => _commands.TryGetValue(id, out command!);

    /// <summary>执行命令。不可执行时不抛异常，返回 false（快捷键应静默忽略，不弹错）。</summary>
    public bool Invoke(string id)
    {
        if (!_commands.TryGetValue(id, out var c) || !c.CanRun()) return false;

        c.Execute();
        return true;
    }

    public IReadOnlyCollection<AppCommand> All => _commands.Values;
}
