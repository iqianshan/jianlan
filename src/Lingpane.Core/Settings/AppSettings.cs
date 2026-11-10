using System.Text.Json.Serialization;

namespace Lingpane.Core.Settings;

/// <summary>
/// 主题选择。系统高对比度开启时由界面层强制 HighContrast，不在此处处理。
///
/// <see cref="HighContrast"/> 是用户**手动**开启高对比度的路径（FR-5.1.2）：
/// 不改系统设置也能用上高对比度配色，适合"只是想看得清"或临时投屏/演示的场合。
/// 系统自己开着高对比度时仍然以系统为准——那个开关的语义是"我看不清"，
/// 比应用内的偏好更强，不能被应用覆盖掉。
/// </summary>
public enum ThemeKind
{
    System = 0,
    Light = 1,
    Dark = 2,
    HighContrast = 3,
}

public enum DensityKind
{
    Comfortable = 0,
    Compact = 1,
}

public enum DefaultViewKind
{
    Details = 0,
    List = 1,
    Grid = 2,
}

public enum SortFieldKind
{
    Name = 0,
    Date = 1,
    Size = 2,
    Type = 3,
}

/// <summary>
/// 新标签页开在哪（FR-5.2.6）。三选一：
/// <see cref="Home"/> 开主页、<see cref="Path"/> 开指定目录、<see cref="Last"/> 接着上次待的目录。
///
/// 为什么把"上次路径"单独列出来：它的语义是"接着干活"，与"每次固定回某处"是两种习惯，
/// 而空路径回落下载夹是第三种（默认值）。前两版只有一个路径输入框，用户要"主页"只能
/// 把路径填成某个目录，语义上是错的。
/// </summary>
public enum NewTabModeKind
{
    Home = 0,
    Path = 1,
    Last = 2,
}

/// <summary>
/// 窗格槽位（FR-3.7）。双窗格各自记忆视图、排序与列配置，靠它区分是哪一侧。
/// 不用 bool：写 <c>slot: true</c> 的调用点读起来没有含义。
/// </summary>
public enum PaneSlot
{
    Primary = 0,
    Secondary = 1,
}

/// <summary>
/// settings.json 的强类型模型（schemaVersion = 1）。
/// 字段定义见 docs/settings-schema.md，改动必须同步该文档与 SettingsMigrations。
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>窗口最小尺寸，与 MainWindow.xaml 的 MinWidth/MinHeight 保持一致。</summary>
    public const int MinWindowWidth = 720;
    public const int MinWindowHeight = 480;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public GeneralSettings General { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public BehaviorSettings Behavior { get; set; } = new();

    public PrivacySettings Privacy { get; set; } = new();

    public SessionSettings Session { get; set; } = new();

    public LayoutSettings Layout { get; set; } = new();

    /// <summary>
    /// 最近访问（FR-1.1.2）。独立成段而不是塞进 session：session 是"下次怎么打开"，
    /// 历史是"用户去过哪里"，两者的生命周期与清空入口都不同
    /// （FR-5.3.2 只清历史，不动会话）。
    /// </summary>
    public HistorySettings History { get; set; } = new();

    /// <summary>收藏（FR-7）。主页收藏区与导航栏收藏区读同一份（FR-1.1.5）。</summary>
    public BookmarkSettings Bookmarks { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>
    /// 把越界值夹取到合法范围（settings-schema §3）。
    /// 读取后必须调用一次；写入前无需重复调用。
    /// </summary>
    public void Normalize()
    {
        General ??= new GeneralSettings();
        Appearance ??= new AppearanceSettings();
        Behavior ??= new BehaviorSettings();
        Privacy ??= new PrivacySettings();
        Session ??= new SessionSettings();
        Layout ??= new LayoutSettings();
        History ??= new HistorySettings();
        Bookmarks ??= new BookmarkSettings();

        if (!GeneralSettings.IsKnownLanguage(General.Language)) General.Language = GeneralSettings.LanguageAuto;

        // 空白等同于"没设置"（回落系统下载）：留一个 "" 在设置文件里，读的人还要再判一次空串
        General.NewTabPath = string.IsNullOrWhiteSpace(General.NewTabPath) ? null : General.NewTabPath.Trim();

        // 认不出来的模式回落"指定路径"：老文件里根本没有这个字段，默认值就是它；
        // 手改坏了也不该让新标签开到不知哪里去。
        if (!Enum.IsDefined(General.NewTabMode)) General.NewTabMode = NewTabModeKind.Path;

        // 路径缓存：空值等于"这一项解析失败"，留着只会让读取方多判一次。
        // 键是已知文件夹的枚举名，手改配置文件时大小写常有出入——按不区分大小写重建一份，
        // 否则大小写不匹配的那条会永久留在文件里（每次启动都判未命中、再补一条新的）。
        General.KnownFolderPaths ??= [];
        General.KnownFolderPaths = General.KnownFolderPaths
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);

        if (!Enum.IsDefined(Appearance.Theme)) Appearance.Theme = ThemeKind.System;
        if (!Enum.IsDefined(Appearance.Density)) Appearance.Density = DensityKind.Comfortable;
        if (!Enum.IsDefined(Behavior.DefaultView)) Behavior.DefaultView = DefaultViewKind.Details;
        if (!Enum.IsDefined(Behavior.SortBy)) Behavior.SortBy = SortFieldKind.Name;

        // 启动恢复二选一（FR-8.4）：设了工作区就关掉会话恢复。
        // 两者都开着会把工作区的标签和上次会话的标签叠在一起，同一批目录出现两遍；
        // 这里让显式选择的工作区获胜——它是用户主动指定的，会话恢复只是默认值。
        Behavior.StartupWorkspace = string.IsNullOrWhiteSpace(Behavior.StartupWorkspace)
            ? null
            : Behavior.StartupWorkspace.Trim();
        if (Behavior.StartupWorkspace is not null) Behavior.RestoreLastSession = false;

        if (Appearance.FontSizePt is int pt)
        {
            Appearance.FontSizePt = Math.Clamp(pt, 8, 24);
        }

        if (string.IsNullOrWhiteSpace(Appearance.FontFamily))
        {
            Appearance.FontFamily = null;
        }

        Appearance.Accent = AppearanceSettings.NormalizeAccent(Appearance.Accent);

        // 颜色自定义名称（FR-4.1.7）：认不出来的键、空值、超长都清掉（见 NormalizeTagColorNames）
        Appearance.TagColorNames = AppearanceSettings.NormalizeTagColorNames(Appearance.TagColorNames);

        Privacy.HistoryLimit = Math.Clamp(Privacy.HistoryLimit, 0, 500);
        Privacy.CacheLimitMb = Math.Clamp(Privacy.CacheLimitMb, 64, 4096);

        // 历史的条数上限就是隐私设置里的那一项：两处各存一份必然会对不上，
        // 而"设置里写 50 条、文件里留着 100 条"是最难解释的那种不一致。
        History.Normalize(Privacy.HistoryLimit);
        Bookmarks.Normalize();

        Session.WindowWidth = Math.Max(Session.WindowWidth, MinWindowWidth);
        Session.WindowHeight = Math.Max(Session.WindowHeight, MinWindowHeight);

        // 会话标签：空值、空串与超量都归一到可直接使用的形态。
        // 路径合法性不在归一化里判——盘符可能暂时不在（U 盘），判了就把标签吃掉，
        // 真正导航时再失败并显示原因。
        Session.Tabs ??= [];
        Session.Tabs.RemoveAll(string.IsNullOrWhiteSpace);
        if (Session.Tabs.Count > SessionSettings.MaxTabs)
        {
            Session.Tabs = Session.Tabs.Take(SessionSettings.MaxTabs).ToList();
        }

        Session.ActiveTabIndex = Math.Clamp(Session.ActiveTabIndex, 0, Math.Max(0, Session.Tabs.Count - 1));

        // 第二窗格路径与 Tabs 是平行数组：长度必须与 Tabs 一致，
        // 否则读的人要自己判错位。这里补齐/截断，空串表示"这个标签没开分屏"。
        Session.TabSecondPaths ??= [];
        if (Session.TabSecondPaths.Count < Session.Tabs.Count)
        {
            // 补空串而不是补 null：null 会把"没开分屏"和"没写这个字段"混成一种，
            // 下游一律按空串判，少一个分支。
            Session.TabSecondPaths.AddRange(
                Enumerable.Repeat(string.Empty, Session.Tabs.Count - Session.TabSecondPaths.Count));
        }
        else if (Session.TabSecondPaths.Count > Session.Tabs.Count)
        {
            Session.TabSecondPaths = Session.TabSecondPaths.Take(Session.Tabs.Count).ToList();
        }

        // 固定标记同理：平行数组，短了补 false（默认不固定），长了截断
        Session.TabPinned ??= [];
        if (Session.TabPinned.Count < Session.Tabs.Count)
        {
            Session.TabPinned.AddRange(
                Enumerable.Repeat(false, Session.Tabs.Count - Session.TabPinned.Count));
        }
        else if (Session.TabPinned.Count > Session.Tabs.Count)
        {
            Session.TabPinned = Session.TabPinned.Take(Session.Tabs.Count).ToList();
        }

        // 标签栏位置：认不出来的值回落顶部（手改坏了不至于把标签栏弄丢）
        if (!AppearanceSettings.IsKnownTabPosition(Appearance.TabStripPosition))
        {
            Appearance.TabStripPosition = AppearanceSettings.TabTop;
        }

        // 列配置（FR-6.4）：档位名归一成小写，手改配置写成 "Pictures" 也能认，
        // 否则那条配置永远读不到、用户会以为"我改了但没生效"。
        Layout.Columns ??= [];
        Layout.Columns = Layout.Columns
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value is not null)
            .GroupBy(p => p.Key.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

        foreach (ColumnProfileSettings profile in Layout.Columns.Values)
        {
            profile.Normalize();
        }

        // 旧版只有三个全局宽度字段（columnDateWidth 等）。它们现在进了 Extra，
        // 这里捡回来填进默认档——不然升级之后用户的列宽会毫无理由地归位。
        if (!Layout.Columns.ContainsKey("default") && Layout.Extra is { } legacy)
        {
            double? date = ReadLegacyWidth(legacy, "columnDateWidth");
            double? type = ReadLegacyWidth(legacy, "columnTypeWidth");
            double? size = ReadLegacyWidth(legacy, "columnSizeWidth");

            if (date is not null || type is not null || size is not null)
            {
                ColumnProfileSettings standard = Layout.Standard;
                if (date is not null) standard.DateWidth = ColumnLayout.ClampWidth(date.Value, DefaultColumnDateWidth);
                if (type is not null) standard.TypeWidth = ColumnLayout.ClampWidth(type.Value, DefaultColumnTypeWidth);
                if (size is not null) standard.SizeWidth = ColumnLayout.ClampWidth(size.Value, DefaultColumnSizeWidth);
            }
        }

        Layout.Standard.Normalize();

        // 第二窗格的列配置（FR-3.7）：与第一窗格同一套归一（键小写、宽度夹取）
        Layout.SecondaryColumns ??= [];
        Layout.SecondaryColumns = Layout.SecondaryColumns
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value is not null)
            .GroupBy(p => p.Key.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

        foreach (ColumnProfileSettings profile in Layout.SecondaryColumns.Values)
        {
            profile.Normalize();
        }

        // 窗格状态里的枚举值同样要判合法：手改成 "grid" 之外的值会让启动时视图回落到哪都没有定义
        Layout.PrimaryPane ??= new PaneViewStateSettings();
        Layout.SecondaryPane ??= new PaneViewStateSettings();
        NormalizePaneState(Layout.PrimaryPane);
        NormalizePaneState(Layout.SecondaryPane);

        if (double.IsNaN(Layout.SplitRatio) || double.IsInfinity(Layout.SplitRatio))
        {
            Layout.SplitRatio = DefaultSplitRatio;
        }

        Layout.SplitRatio = Math.Clamp(Layout.SplitRatio, MinSplitRatio, MaxSplitRatio);

        if (double.IsNaN(Layout.PreviewWidth) || double.IsInfinity(Layout.PreviewWidth) || Layout.PreviewWidth <= 0)
        {
            Layout.PreviewWidth = DefaultPreviewWidth;
        }

        Layout.PreviewWidth = Math.Clamp(Layout.PreviewWidth, MinPreviewWidth, MaxPreviewWidth);

        if (double.IsNaN(Layout.NavWidth) || double.IsInfinity(Layout.NavWidth) || Layout.NavWidth <= 0)
        {
            Layout.NavWidth = DefaultNavWidth;
        }

        Layout.NavWidth = Math.Clamp(Layout.NavWidth, MinNavWidth, MaxNavWidth);
    }

    // ── 恢复默认值（FR-5.3.8）─────────────────────────────────────────────
    // 分类恢复直接换掉整段对象，而不是逐字段赋默认值：逐字段写会漏掉将来新增的项，
    // 而"恢复默认"这个入口最不该漏。
    //
    // 只重置**偏好**，不动用户数据：History.Entries 与 Bookmarks 是用户攒下来的东西，
    // 用"恢复默认"把它们删掉是不可接受的。所以 History 段整体保留，
    // Privacy 也只是逐字段重置——VisitHistory / BookmarkStore 还持有那两段的引用，
    // 换对象会让它们继续读写旧实例。

    public void ResetAppearance() => Appearance = new AppearanceSettings();

    public void ResetBehavior()
    {
        Behavior = new BehaviorSettings();

        // FR-3.7：窗格各自记着的视图与排序也属于"行为偏好"，一并清掉。
        // 只换 Behavior 的话，恢复默认之后两侧还停在旧视图上，用户会以为按钮没生效。
        Layout.PrimaryPane = new PaneViewStateSettings();
        Layout.SecondaryPane = new PaneViewStateSettings();
    }

    /// <summary>窗格状态里的枚举值判合法；null 保持 null（= 沿用全局默认）。</summary>
    private static void NormalizePaneState(PaneViewStateSettings state)
    {
        if (state.View is { } view && !Enum.IsDefined(view)) state.View = null;
        if (state.SortBy is { } sort && !Enum.IsDefined(sort)) state.SortBy = null;
    }

    public void ResetPrivacy()
    {
        var fresh = new PrivacySettings();
        Privacy.HistoryEnabled = fresh.HistoryEnabled;
        Privacy.HistoryLimit = fresh.HistoryLimit;
        Privacy.CacheLimitMb = fresh.CacheLimitMb;
    }

    public void ResetAll()
    {
        ResetAppearance();
        ResetBehavior();
        ResetPrivacy();
        ResetLayout();
    }

    /// <summary>布局恢复默认：列配置、分屏形态都回出厂值。</summary>
    public void ResetLayout()
    {
        Layout.ResetColumns();
        Layout.SplitEnabled = false;
        Layout.SplitRatio = DefaultSplitRatio;
        Layout.SplitVertical = false;
        Layout.PreviewWidth = DefaultPreviewWidth;
        Layout.NavWidth = DefaultNavWidth;
    }

    /// <summary>
    /// 读旧版的三列宽度并从 Extra 里摘掉：留着会一直被写回 settings.json，
    /// 文件里就有两份真值，下次读的人分不清哪份算数。
    /// </summary>
    private static double? ReadLegacyWidth(Dictionary<string, System.Text.Json.JsonElement> extra, string key)
    {
        if (!extra.TryGetValue(key, out System.Text.Json.JsonElement element)) return null;

        extra.Remove(key);
        return element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetDouble(out double value)
            ? value
            : null;
    }

    /// <summary>分屏比例的上下限：低于 15% 的那一栏连列头都放不下，等于"分了个看不见的屏"。</summary>
    public const double MinSplitRatio = 0.15;    public const double MaxSplitRatio = 0.85;
    public const double DefaultSplitRatio = 0.5;

    /// <summary>预览窗格宽度的上下限。下限是"还能看清一张缩略图"，上限是别让它把列表挤没。</summary>
    public const double MinPreviewWidth = 240;
    public const double MaxPreviewWidth = 720;
    public const double DefaultPreviewWidth = 320;

    /// <summary>
    /// 导航栏宽度的上下限（FR-1.2.4）。与 DesignTokens 里的 Size.Nav.Width.* 同值：
    /// 下限 160 是"图标 + 四个汉字"还放得下，上限 320 是侧栏不该比内容区还宽。
    /// </summary>
    public const double MinNavWidth = 160;
    public const double MaxNavWidth = 320;
    public const double DefaultNavWidth = 184;

    public const double MinColumnWidth = 56;
    public const double MaxColumnWidth = 400;

    public const double DefaultColumnDateWidth = 110;
    public const double DefaultColumnTypeWidth = 110;
    public const double DefaultColumnSizeWidth = 90;
}

/// <summary>
/// 应用级设置（与外观/行为分开：这些项与界面渲染无关，改完不影响布局）。
/// 见 docs/settings-schema.md §4。
/// </summary>
public sealed class GeneralSettings
{
    public const string LanguageAuto = "auto";
    public const string LanguageChinese = "zh-Hans";
    public const string LanguageEnglish = "en";

    /// <summary>
    /// 界面语言。auto = 跟随系统安装语言；其余为显式指定。
    /// 用字符串而不是枚举：settings.json 是给人看的，zh-Hans 比数字好懂，
    /// 且新增语言不需要改枚举定义。
    /// </summary>
    public string Language { get; set; } = LanguageAuto;

    /// <summary>
    /// 新标签开在哪（FR-5.2.6）。默认 <see cref="NewTabModeKind.Path"/>，
    /// 也就是"沿用 <see cref="NewTabPath"/>，没填就用系统下载夹"——与 0.2.0 完全一致，
    /// 老用户的习惯不因为多了两个选项而改变。
    /// </summary>
    public NewTabModeKind NewTabMode { get; set; } = NewTabModeKind.Path;

    /// <summary>
    /// 新标签页的默认目录（FR-2.1）。空 = 用系统「下载」目录。
    ///
    /// 为什么空值代表"系统下载"而不是把下载夹的绝对路径写进来：设置文件会跟着漫游/备份走，
    /// 写死路径换台机器就指向别人的目录；空值每次启动重新问系统，才是"当前系统的下载"。
    ///
    /// 归一化只去空白、不校验存在性：路径可能在移动硬盘上，暂时不可用不该把用户的选择抹掉
    /// （与 session.tabs 的处理一致）。真正的回落发生在解析时。
    /// </summary>
    public string? NewTabPath { get; set; }

    /// <summary>
    /// 已知文件夹路径缓存（FR-1.1 实现约束）：kind 名 → 绝对路径。
    ///
    /// 需求要求"启动从缓存读，仅在检测到系统变化时刷新"。这不是为了省几毫秒——
    /// <c>SHGetKnownFolderPath</c> 首次调用会加载 shell32 并读注册表，属于启动路径上的
    /// 不确定项；缓存命中时这一段完全不碰外壳，冷启动才可预测。
    /// 校验放在首帧之后的后台任务里（见 KnownFolderService）。
    /// </summary>
    public Dictionary<string, string> KnownFolderPaths { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public static bool IsKnownLanguage(string? value)
        => value is LanguageAuto or LanguageChinese or LanguageEnglish;
}

public sealed class AppearanceSettings
{
    public ThemeKind Theme { get; set; } = ThemeKind.System;

    /// <summary>null = 跟随系统；否则 8–24。</summary>
    public int? FontSizePt { get; set; }

    /// <summary>null = 默认字体栈；否则为系统字体族名。</summary>
    public string? FontFamily { get; set; }

    /// <summary>
    /// 强调色（FR-5.1.3）：null = 跟随系统，否则 <c>#RRGGBB</c>。
    ///
    /// 只存"用户选了哪个"，四个派生色（常态/悬停/按下/浅底）由
    /// <c>AccentService</c> 按当前主题算——把派生色也写进设置文件，
    /// 换主题后那几项就是脏的，还得判断哪几项该重算。
    /// </summary>
    public string? Accent { get; set; }

    public DensityKind Density { get; set; } = DensityKind.Comfortable;

    public bool ShowNavPane { get; set; } = true;

    public bool ShowStatusBar { get; set; } = true;

    /// <summary>
    /// 预览窗格默认是否展开（FR-5.2.10）。默认关闭——它是本应用内存占用最大的可选功能，
    /// 不该在用户没要求的情况下一直占着一栏。
    /// 与 <see cref="LayoutSettings.PreviewWidth"/> 分开：一个是"开不开"，一个是"多宽"，
    /// 关掉再打开应当回到原来的宽度，而不是回到默认值。
    /// </summary>
    public bool ShowPreview { get; set; }

    /// <summary>
    /// 标签栏位置（FR-2.4）：<c>top</c>（默认）/ <c>bottom</c>。
    /// 用字符串而不是枚举的理由同 <see cref="GeneralSettings.Language"/>。
    /// </summary>
    public string TabStripPosition { get; set; } = TabTop;

    public const string TabTop = "top";
    public const string TabBottom = "bottom";

    public static bool IsKnownTabPosition(string? value) => value is TabTop or TabBottom;

    /// <summary>
    /// 八色的自定义名称（FR-4.1.7）：键是 <c>TagColors</c> 的枚举名（<c>Red</c>…），值是用户写的名字。
    /// 没写的那一色继续用界面语言里的默认名字——所以这里只存"被改过的"，
    /// 不是一张完整映射表：存全表意味着切语言之后那八个名字还是旧语言的。
    ///
    /// 键用枚举名而不是位值：settings.json 是给人看的，<c>"Red"</c> 比 <c>1</c> 好认；
    /// 顺便也不怕将来调整位序（位序是磁盘上的标签数据要用的，不该被界面文案绑住）。
    /// </summary>
    public Dictionary<string, string> TagColorNames { get; set; } = [];

    /// <summary>自定义名长度上限。名字进的是右键菜单与侧栏，写一整句话会把菜单撑开。</summary>
    public const int MaxTagColorNameLength = 24;

    /// <summary>
    /// 归一化颜色名：去掉认不出来的键、空值、首尾空白与超长文本。
    /// 名字本身不做转义——它只出现在界面文本里，不走任何格式化路径。
    /// </summary>
    public static Dictionary<string, string> NormalizeTagColorNames(Dictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;

        foreach ((string key, string value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;

            // 认不出来的键（手改配置写错颜色）直接丢掉：留着它永远不会被读到，
            // 而用户会以为"我设了名字却没生效"
            if (!Enum.TryParse<Tagging.TagColors>(key.Trim(), ignoreCase: true, out Tagging.TagColors color)
                || color == Tagging.TagColors.None)
            {
                continue;
            }

            string name = value.Trim();
            if (name.Length > MaxTagColorNameLength) name = name[..MaxTagColorNameLength];
            result[color.ToString()] = name;
        }

        return result;
    }

    /// <summary>
    /// 强调色只接受 <c>#RRGGBB</c>。手改成 <c>red</c> 或 <c>#F00</c> 一律当"跟随系统"——
    /// 与其让界面去猜颜色名，不如回落到系统色，至少不会出现一块读不出来的黑洞。
    /// </summary>
    public static string? NormalizeAccent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string trimmed = value.Trim();
        if (trimmed.Length != 7 || trimmed[0] != '#') return null;

        for (int i = 1; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i])) return null;
        }

        return trimmed.ToUpperInvariant();
    }

    /// <summary>未知字段原样保留，避免降级版本吃掉新版本写入的设置（settings-schema §4）。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class BehaviorSettings
{
    public DefaultViewKind DefaultView { get; set; } = DefaultViewKind.Details;

    public SortFieldKind SortBy { get; set; } = SortFieldKind.Name;

    public bool SortDescending { get; set; }

    public bool FoldersFirst { get; set; } = true;

    /// <summary>
    /// 自然排序（FR-5.2.3）：<c>file2</c> 排在 <c>file10</c> 之前。默认开。
    ///
    /// 关掉之后退回逐字符比较（.NET 的 <c>StringComparer.OrdinalIgnoreCase</c> 口径），
    /// 结果是 <c>file10</c> 在 <c>file2</c> 之前——那正是老式工具的行为，
    /// 有些人排版本号/日期串时需要它。默认值不动，因为资源管理器就是自然排序。
    /// </summary>
    public bool NaturalSort { get; set; } = true;

    /// <summary>
    /// 名称区分大小写（FR-5.2.3）。默认不区分（与资源管理器一致）。
    /// 打开后 <c>Readme</c> 与 <c>readme</c> 不再相邻，按码位排——给"文件名大小写有意义"的场合用。
    /// </summary>
    public bool CaseSensitiveSort { get; set; }

    public bool ShowHidden { get; set; }

    /// <summary>
    /// 显示系统文件（FR-5.2.4）。默认关：<c>pagefile.sys</c>、<c>System Volume Information</c>
    /// 这类东西对日常浏览只是干扰。与 <see cref="ShowHidden"/> 各管一位，
    /// 两个属性都带上的条目（<c>desktop.ini</c>）要两个开关都开才出现。
    /// </summary>
    public bool ShowSystem { get; set; }

    public bool ShowExtensions { get; set; } = true;

    /// <summary>
    /// 选中文件后是否自动预览（FR-5.2.10 的"触发方式"）。
    ///
    /// 关掉之后预览窗格仍在，但要按窗格里的「立即预览」才动——这是给
    /// "目录里有几个几十 MB 的文件、一选中就卡一下"的场景留的开关。
    /// 网络路径与可移动设备不受这一项影响：那两类无论怎么设都不自动预览（FR-9.2.8）。
    /// </summary>
    public bool PreviewAutoOpen { get; set; } = true;

    public bool ConfirmDelete { get; set; } = true;

    /// <summary>
    /// 一次操作涉及多项时的确认（FR-5.2.8 的"批量操作"）。
    /// 与 <see cref="ConfirmDelete"/> 分开是为了让"单删不啰嗦、批量仍拦一道"成为可选：
    /// 只留一个总开关时，嫌单删弹窗烦的人会连批量保护一起关掉。
    /// </summary>
    public bool ConfirmBatch { get; set; } = true;

    /// <summary>
    /// 粘贴/拖入时目标处有同名项的确认（FR-5.2.8 的"覆盖"分项）。默认开。
    ///
    /// 语义与另外两项一致：**开 = 先问一句**。问的是"要不要用源覆盖目标"——
    /// 选"是"就覆盖，选"否"就两个都留下（自动改名，0.2.0 的行为）。
    /// 关掉之后不再询问，直接覆盖——所以这一项关之前，设置页的提示必须写清后果。
    ///
    /// 为什么不在关掉时退回"自动改名"：那等于"关掉确认 = 永远不覆盖"，
    /// 想覆盖的用户反而没有任何入口；而"确认"这两个字在本项目里一直是"弹一句问话"，
    /// 删除的 <see cref="ConfirmDelete"/> 就是同一个口径。
    /// </summary>
    public bool ConfirmOverwrite { get; set; } = true;

    /// <summary>
    /// 双击可执行文件时先确认（FR-5.2.11）。默认**关**。
    ///
    /// 默认关是刻意的：这一项解决的是"误双击"而非"防病毒"，
    /// 而它给每一次正常运行都加一道弹窗。老用户升级后如果发现打开程序要按两次，
    /// 第一反应是"这软件坏了"。需要它的人自己在设置里打开，成本只有一次。
    /// </summary>
    public bool WarnOnExecutable { get; set; }

    public bool DoubleClickOpens { get; set; } = true;

    /// <summary>
    /// 文件夹/`..` 是否在新标签打开（FR-2.10 的加强版）。默认关闭：
    /// 双击进目录是最常用的动作，默认改成开标签会把标签栏迅速撑满。
    /// </summary>
    public bool OpenFolderInNewTab { get; set; }

    /// <summary>
    /// 中键 / `Ctrl+左键` 开出的新标签是否留在后台（FR-2.10 的"可选"部分）。
    /// 默认关：手势做完画面什么都没变，用户会以为没生效。想连续开一批目录再回来
    /// （"后台打开"）的人可以打开它。
    /// </summary>
    public bool OpenNewTabInBackground { get; set; }

    /// <summary>启动时恢复上次会话（FR-2.11）。与 <see cref="StartupWorkspace"/> 互斥。</summary>
    public bool RestoreLastSession { get; set; } = true;

    /// <summary>
    /// 启动时自动打开的工作区名（FR-8.4）。非空表示"每次启动都还原这份工作区"，
    /// 此时 <see cref="RestoreLastSession"/> 必须为 false——两者同时生效会把
    /// 工作区的标签和上次会话的标签叠在一起，同一批目录出现两遍。
    ///
    /// 这里只存名字，工作区内容仍在 workspaces.json：冷启动不读那个文件，
    /// 只在真的要还原时才去读。
    /// </summary>
    public string? StartupWorkspace { get; set; }

    /// <summary>
    /// 关闭最后一个标签时的行为（FR-2.8）：true = 回到默认目录，false = 关闭窗口。
    /// 默认回到默认目录——关掉整个窗口是破坏性操作，不该由"关掉最后一个标签"顺手完成。
    /// </summary>
    public bool CloseLastTabToHome { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class PrivacySettings
{
    public bool HistoryEnabled { get; set; } = true;

    public int HistoryLimit { get; set; } = 100;

    /// <summary>
    /// 缩略图磁盘缓存上限（FR-5.3.5，单位 MB）。默认 500，与
    /// <see cref="Preview.ThumbnailCache.DefaultCapBytes"/> 是同一个数——
    /// 两处各写一个默认值必然对不上，而"设置里显示 500、实际按 512 淘汰"没人查得出来。
    /// </summary>
    public int CacheLimitMb { get; set; } = 500;

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class SessionSettings
{
    public int WindowWidth { get; set; } = 1100;

    public int WindowHeight { get; set; } = 720;

    public bool WindowMaximized { get; set; }

    /// <summary>
    /// 窗口左上角坐标（FR-10.2）。可空：0.2.0 之前只记尺寸，
    /// 旧设置文件里没有这两项，null 表示"没记过"，启动时仍按居中处理。
    /// </summary>
    public int? WindowLeft { get; set; }

    public int? WindowTop { get; set; }

    public string? LastPath { get; set; }

    /// <summary>
    /// 退出时打开的标签页路径（FR-2.11）。只存路径，不存滚动位置与选中项——
    /// 恢复的目标是"回到上次在看的目录"，不是逐像素复原现场。
    /// 顺序即标签顺序，长度受 <see cref="MaxTabs"/> 限制。
    /// </summary>
    public List<string> Tabs { get; set; } = [];

    /// <summary>下次启动时激活第几个标签（0 起始）。恢复失败时回落第一个。</summary>
    public int ActiveTabIndex { get; set; }

    /// <summary>
    /// 每个标签第二窗格的路径（FR-3.2 会话恢复）。与 <see cref="Tabs"/> 平行、等长：
    /// 下标 i 对应第 i 个标签，空串表示该标签只有单窗格。
    ///
    /// 为什么用平行数组而不是把 Tabs 改成对象数组：改结构要写迁移、要让旧版本读得回来；
    /// 平行数组只是"多一个字段"，归一化里补齐长度就不会错位（见 Normalize）。
    /// </summary>
    public List<string> TabSecondPaths { get; set; } = [];

    /// <summary>
    /// 每个标签是否固定（FR-2.9）。同样与 <see cref="Tabs"/> 平行等长。
    /// 固定状态属于"用户设定的意图"，丢了用户会以为功能坏了，因此必须落盘。
    /// </summary>
    public List<bool> TabPinned { get; set; } = [];

    /// <summary>会话恢复的标签数上限。再多也只是把启动拖慢，捡不回来的价值很低。</summary>
    public const int MaxTabs = 12;

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>
/// 最近访问过的目录（FR-1.1.2 / FR-5.3.2）。只记目录、不记文件：
/// 主页那一栏的用途是"回到刚才去过的地方"，文件级历史既列不下也用不上，
/// 还会把大量本地路径写进设置文件。
/// </summary>
public sealed class HistorySettings
{
    /// <summary>主页"最近访问"展示条数（FR-1.1.2 写死 12）。</summary>
    public const int HomePageLimit = 12;

    /// <summary>历史条数硬上限。隐私设置里的 <c>historyLimit</c> 被夹在这个范围内。</summary>
    public const int MaxEntries = 500;

    /// <summary>按访问时间**倒序**存放，最新的在前。顺序即主页的展示顺序。</summary>
    public List<HistoryEntry> Entries { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>
    /// 归一化：去空、去重（同一目录只留最新一次）、按时间倒序、按上限截断。
    /// 手改过的设置文件什么顺序都可能有，读进来必须先排一次，否则主页的顺序是随机的。
    /// </summary>
    public void Normalize(int limit)
    {
        Entries ??= [];
        Entries.RemoveAll(e => string.IsNullOrWhiteSpace(e?.Path));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Entries = Entries
            .OrderByDescending(e => e.VisitedAt)
            .Where(e => seen.Add(e.Path.Trim()))
            .Take(Math.Max(0, limit))
            .ToList();

        foreach (HistoryEntry entry in Entries) entry.Path = entry.Path.Trim();
    }
}

public sealed class HistoryEntry
{
    public string Path { get; set; } = string.Empty;

    /// <summary>最近一次访问的 UTC 时间。存 UTC 而不是本地时间：夏令时切换那天本地时间会有重复区间。</summary>
    public DateTimeOffset VisitedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>
/// 收藏（FR-7）。与 <see cref="HistorySettings"/> 同样是设置文件里的一段：
/// 需求明确"不新建数据库"，收藏上限 500 条，纯 JSON 完全放得下。
/// </summary>
public sealed class BookmarkSettings
{
    /// <summary>收藏总上限（FR-7.9）。超过时拒绝新增，由界面显式提示，不静默丢弃。</summary>
    public const int MaxItems = 500;

    /// <summary>主页收藏区最多展示几项，超出显示"查看全部"（FR-1.1.5）。</summary>
    public const int HomePageLimit = 8;

    /// <summary>分组上限。需求没定数字，取一个"够用且不至于把导航栏撑爆"的量。</summary>
    public const int MaxGroups = 50;

    public List<BookmarkEntry> Items { get; set; } = [];

    /// <summary>分组及其折叠状态，顺序即导航栏的展示顺序（FR-7.3 / FR-7.6）。</summary>
    public List<BookmarkGroup> Groups { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public void Normalize()
    {
        Items ??= [];
        Items.RemoveAll(b => string.IsNullOrWhiteSpace(b?.Path));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Items = Items
            .Where(b => seen.Add(b.Path.Trim()))
            .Take(MaxItems)
            .ToList();

        foreach (BookmarkEntry item in Items)
        {
            item.Path = item.Path.Trim();
            if (string.IsNullOrWhiteSpace(item.Name)) item.Name = null;
            if (string.IsNullOrWhiteSpace(item.Icon)) item.Icon = null;
            if (string.IsNullOrWhiteSpace(item.Color)) item.Color = null;

            // 空白分组名等同于"未分组"：留一个 "" 会让 UI 多出一个没有标题的分组
            if (string.IsNullOrWhiteSpace(item.Group)) item.Group = null;
            else item.Group = item.Group.Trim();
        }

        // 分组：去空、去重（保留先出现的顺序与折叠状态）、截断
        Groups ??= [];
        var groupSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Groups = Groups
            .Where(g => !string.IsNullOrWhiteSpace(g?.Name))
            .Where(g => groupSeen.Add(g.Name.Trim()))
            .Take(MaxGroups)
            .ToList();

        foreach (BookmarkGroup group in Groups) group.Name = group.Name.Trim();

        // 条目引用了不存在的分组时补一条，而不是把条目的分组清掉：
        // 手改过 JSON 的人写了 "group": "项目" 却没往 groups 里加，意图是明确的，
        // 悄悄降级成"未分组"等于吃掉用户数据。
        foreach (string name in Items
                     .Select(b => b.Group)
                     .OfType<string>()
                     .Where(n => !groupSeen.Contains(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Groups.Add(new BookmarkGroup { Name = name });
            groupSeen.Add(name);
        }
    }
}

public enum BookmarkKind
{
    Folder = 0,
    File = 1,
}

public sealed class BookmarkEntry
{
    public string Path { get; set; } = string.Empty;

    /// <summary>显示名。为空时用路径末段——重命名后不必回头改收藏。</summary>
    public string? Name { get; set; }

    public BookmarkKind Kind { get; set; }

    /// <summary>分组名（FR-7.3）。null = 未分组，UI 归到"未分组"那一块。</summary>
    public string? Group { get; set; }

    /// <summary>自定义图标（FR-7.5）：一个 Segoe MDL2 字形码。null = 按路径取系统图标。</summary>
    public string? Icon { get; set; }

    /// <summary>自定义颜色（FR-7.5），形如 <c>#RRGGBB</c>。null = 跟随主题强调色。</summary>
    public string? Color { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>
/// 收藏分组（FR-7.3）。分组名即身份：条目上存的就是名字，重命名时两边一起改。
/// 用名字而不是 Guid 是有意的——这是一份用户会自己打开看、甚至手改的 JSON，
/// 出现 <c>"group": "5f2c…"</c> 之后再配一张 id→名字的对照表，没人愿意维护。
/// </summary>
public sealed class BookmarkGroup
{
    public string Name { get; set; } = string.Empty;

    /// <summary>折叠状态（FR-7.3 验收：折叠状态持久化）。默认展开。</summary>
    public bool IsExpanded { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>
/// 布局与列表列配置。名称列是星号列，随窗口伸缩，因此不保存宽度；
/// 日期/类型/大小三列可显隐、可换序、可拖宽，并按目录类型分档记忆（FR-6.4）。
///
/// 为什么配置在这里而不是按标签各存一份：同一目录在两个标签里显示不同列宽，
/// 用户第一反应是"界面坏了"。列配置是**目录的属性**，不是标签的属性。
/// </summary>
public sealed class LayoutSettings
{
    /// <summary>档位名（<see cref="ColumnProfileKind"/> 的小写名）→ 列配置。</summary>
    public Dictionary<string, ColumnProfileSettings> Columns { get; set; } = [];

    /// <summary>
    /// 第二窗格自己的列配置（FR-3.7）。第一窗格仍用 <see cref="Columns"/>，两者互不影响。
    ///
    /// 为什么不给两个窗格各建一份 "columns"：旧文件里 <see cref="Columns"/> 的键是
    /// <c>default</c>/<c>pictures</c> 这样的档位名，加一层槽位就会变成 <c>0:default</c>，
    /// 已有的列宽全部读不到——为一个新功能顺手抹掉用户调过的列宽，不值。
    ///
    /// 空值语义：某个档位在这里没有条目时，**沿用第一窗格的同档配置**（见
    /// <see cref="Get(ColumnProfileKind, PaneSlot)"/>）。所以"没调过"不需要写盘，
    /// 文件里也就不会多出一整份与第一窗格一模一样的配置。
    /// </summary>
    public Dictionary<string, ColumnProfileSettings> SecondaryColumns { get; set; } = [];

    /// <summary>第一窗格上次用过的视图与排序（FR-3.7）。</summary>
    public PaneViewStateSettings PrimaryPane { get; set; } = new();

    /// <summary>第二窗格上次用过的视图与排序（FR-3.7）。</summary>
    public PaneViewStateSettings SecondaryPane { get; set; } = new();

    /// <summary>取某一侧窗格的视图/排序状态。</summary>
    public PaneViewStateSettings GetPaneState(PaneSlot slot)
        => slot == PaneSlot.Secondary ? SecondaryPane : PrimaryPane;

    /// <summary>
    /// 默认档。不存在时惰性创建——每次访问都判一次空并在读路径上写字典，
    /// 读的人会怀疑"这个字典什么时候被填的"；集中在这里就只有一处。
    ///
    /// 必须标 JsonIgnore：它只是 <see cref="Columns"/> 的访问器，
    /// 不标的话序列化会多写一份 "standard"，文件里就有两份默认档。
    /// </summary>
    [JsonIgnore]
    public ColumnProfileSettings Standard
    {
        get
        {
            string key = ProfileKey(ColumnProfileKind.Default);
            if (!Columns.TryGetValue(key, out ColumnProfileSettings? profile) || profile is null)
            {
                profile = new ColumnProfileSettings();
                Columns[key] = profile;
            }

            return profile;
        }
    }

    /// <summary>读配置：没有这一档就回落默认档（不写字典）。</summary>
    public ColumnProfileSettings Get(ColumnProfileKind kind)
        => Columns.TryGetValue(ProfileKey(kind), out ColumnProfileSettings? profile) && profile is not null
            ? profile
            : Standard;

    /// <summary>
    /// 按槽位读配置（FR-3.7）。第二窗格没有自己的同档配置时**沿用第一窗格的**——
    /// 用户没分屏时调过的列宽，第一次分屏出来就应当是那个样子，而不是"另一侧又回到出厂"。
    /// </summary>
    public ColumnProfileSettings Get(ColumnProfileKind kind, PaneSlot slot)
    {
        if (slot == PaneSlot.Secondary
            && SecondaryColumns.TryGetValue(ProfileKey(kind), out ColumnProfileSettings? own)
            && own is not null)
        {
            return own;
        }

        return Get(kind);
    }

    /// <summary>写配置：没有这一档就建一份，从默认档复制（继承默认的列宽与顺序）。</summary>
    public ColumnProfileSettings GetOrAdd(ColumnProfileKind kind) => GetOrAdd(kind, PaneSlot.Primary);

    /// <summary>
    /// 按槽位写配置（FR-3.7）。第二窗格第一次被写入时从"当前生效的那一份"克隆：
    /// 不克隆的话新条目会是出厂值，用户分屏后第一件事就是发现右侧列宽被打回原形。
    /// </summary>
    public ColumnProfileSettings GetOrAdd(ColumnProfileKind kind, PaneSlot slot)
    {
        Dictionary<string, ColumnProfileSettings> map =
            slot == PaneSlot.Secondary ? SecondaryColumns : Columns;
        string key = ProfileKey(kind);

        if (map.TryGetValue(key, out ColumnProfileSettings? existing) && existing is not null)
        {
            return existing;
        }

        ColumnProfileSettings created = Clone(Get(kind));
        map[key] = created;
        return created;
    }

    public void ResetColumns()
    {
        Columns.Clear();
        SecondaryColumns.Clear();
        Standard.Reset();
    }

    /// <summary>
    /// 兼容旧字段（0.2.0 之前只有三列共用的固定宽度）。只读不写：
    /// 三个旧字段已经并入 <c>columns.default</c>，再留一份写入路径必然分叉。
    /// </summary>
    [JsonIgnore]
    public double ColumnDateWidth
    {
        get => Standard.DateWidth;
        set => Standard.DateWidth = ColumnLayout.ClampWidth(value, AppSettings.DefaultColumnDateWidth);
    }

    [JsonIgnore]
    public double ColumnTypeWidth
    {
        get => Standard.TypeWidth;
        set => Standard.TypeWidth = ColumnLayout.ClampWidth(value, AppSettings.DefaultColumnTypeWidth);
    }

    [JsonIgnore]
    public double ColumnSizeWidth
    {
        get => Standard.SizeWidth;
        set => Standard.SizeWidth = ColumnLayout.ClampWidth(value, AppSettings.DefaultColumnSizeWidth);
    }

    public static string ProfileKey(ColumnProfileKind kind) => kind switch
    {
        ColumnProfileKind.Desktop => "desktop",
        ColumnProfileKind.Documents => "documents",
        ColumnProfileKind.Downloads => "downloads",
        ColumnProfileKind.Pictures => "pictures",
        ColumnProfileKind.Music => "music",
        ColumnProfileKind.Videos => "videos",
        _ => "default",
    };

    private static ColumnProfileSettings Clone(ColumnProfileSettings source) => new()
    {
        Order = [.. source.Order],
        ShowDate = source.ShowDate,
        ShowType = source.ShowType,
        ShowSize = source.ShowSize,
        DateWidth = source.DateWidth,
        TypeWidth = source.TypeWidth,
        SizeWidth = source.SizeWidth,
    };

    /// <summary>双窗格是否开启（FR-3.3）。关掉窗口再打开时保持上次的形态。</summary>
    public bool SplitEnabled { get; set; }

    /// <summary>第一窗格占的比例（0.15–0.85，默认 0.5）。分隔条拖完才落盘。</summary>
    public double SplitRatio { get; set; } = AppSettings.DefaultSplitRatio;

    /// <summary>true = 上下分割，false = 左右分割（FR-3.1，默认左右）。</summary>
    public bool SplitVertical { get; set; }

    /// <summary>
    /// 预览窗格宽度（FR-9.1.1）。只在用户拖过分隔条之后才是"真值"，
    /// 没拖过时落盘的就是默认值，所以这里不必区分"有没有设过"。
    /// </summary>
    public double PreviewWidth { get; set; } = AppSettings.DefaultPreviewWidth;

    /// <summary>
    /// 导航栏宽度（FR-1.2.4）。与预览宽度同理：没拖过就是默认值 184，
    /// 拖过之后才是用户的选择，双击分隔条可回到默认。
    /// </summary>
    public double NavWidth { get; set; } = AppSettings.DefaultNavWidth;

    /// <summary>
    /// 导航栏「此电脑」节点是否展开（FR-1.2.7）。默认折叠：
    /// 侧栏只有 184px 高时先摆目录与收藏，用户要盘符时点一下即可。
    /// </summary>
    public bool ThisPcExpanded { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>
/// 单侧窗格上次用过的视图与排序（FR-3.7）。
///
/// 三项都可空：<c>null</c> 表示"这一侧还没自己选过"，此时沿用
/// <see cref="BehaviorSettings.DefaultView"/> / <see cref="BehaviorSettings.SortBy"/>。
/// 用可空而不是照抄默认值，是为了保住设置页那两项"默认视图 / 默认排序"的意义——
/// 一旦这里写死初始值，用户改默认值就再也不起作用了。
/// </summary>
public sealed class PaneViewStateSettings
{
    public DefaultViewKind? View { get; set; }

    public SortFieldKind? SortBy { get; set; }

    public bool? SortDescending { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
