using System.Text.Json.Serialization;

namespace Lingpane.Core.Settings;

/// <summary>主题选择。系统高对比度开启时由界面层强制 HighContrast，不在此处处理。</summary>
public enum ThemeKind
{
    System = 0,
    Light = 1,
    Dark = 2,
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

        if (!GeneralSettings.IsKnownLanguage(General.Language)) General.Language = GeneralSettings.LanguageAuto;

        // 空白等同于"没设置"（回落系统下载）：留一个 "" 在设置文件里，读的人还要再判一次空串
        General.NewTabPath = string.IsNullOrWhiteSpace(General.NewTabPath) ? null : General.NewTabPath.Trim();

        if (!Enum.IsDefined(Appearance.Theme)) Appearance.Theme = ThemeKind.System;
        if (!Enum.IsDefined(Appearance.Density)) Appearance.Density = DensityKind.Comfortable;
        if (!Enum.IsDefined(Behavior.DefaultView)) Behavior.DefaultView = DefaultViewKind.Details;
        if (!Enum.IsDefined(Behavior.SortBy)) Behavior.SortBy = SortFieldKind.Name;

        if (Appearance.FontSizePt is int pt)
        {
            Appearance.FontSizePt = Math.Clamp(pt, 8, 24);
        }

        if (string.IsNullOrWhiteSpace(Appearance.FontFamily))
        {
            Appearance.FontFamily = null;
        }

        Privacy.HistoryLimit = Math.Clamp(Privacy.HistoryLimit, 0, 500);
        Privacy.CacheLimitMb = Math.Clamp(Privacy.CacheLimitMb, 64, 4096);

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

        Layout.ColumnDateWidth = ClampColumn(Layout.ColumnDateWidth, DefaultColumnDateWidth);
        Layout.ColumnTypeWidth = ClampColumn(Layout.ColumnTypeWidth, DefaultColumnTypeWidth);
        Layout.ColumnSizeWidth = ClampColumn(Layout.ColumnSizeWidth, DefaultColumnSizeWidth);

        if (double.IsNaN(Layout.SplitRatio) || double.IsInfinity(Layout.SplitRatio))
        {
            Layout.SplitRatio = DefaultSplitRatio;
        }

        Layout.SplitRatio = Math.Clamp(Layout.SplitRatio, MinSplitRatio, MaxSplitRatio);
    }

    /// <summary>分屏比例的上下限：低于 15% 的那一栏连列头都放不下，等于"分了个看不见的屏"。</summary>
    public const double MinSplitRatio = 0.15;
    public const double MaxSplitRatio = 0.85;
    public const double DefaultSplitRatio = 0.5;

    public const double MinColumnWidth = 56;
    public const double MaxColumnWidth = 400;

    public const double DefaultColumnDateWidth = 110;
    public const double DefaultColumnTypeWidth = 110;
    public const double DefaultColumnSizeWidth = 90;

    /// <summary>非有限值（NaN/Infinity）或被写成 0 的列宽一律回落到默认值。</summary>
    private static double ClampColumn(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return fallback;
        return Math.Clamp(value, MinColumnWidth, MaxColumnWidth);
    }
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
    /// 新标签页的默认目录（FR-2.1）。空 = 用系统「下载」目录。
    ///
    /// 为什么空值代表"系统下载"而不是把下载夹的绝对路径写进来：设置文件会跟着漫游/备份走，
    /// 写死路径换台机器就指向别人的目录；空值每次启动重新问系统，才是"当前系统的下载"。
    ///
    /// 归一化只去空白、不校验存在性：路径可能在移动硬盘上，暂时不可用不该把用户的选择抹掉
    /// （与 session.tabs 的处理一致）。真正的回落发生在解析时。
    /// </summary>
    public string? NewTabPath { get; set; }

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

    public DensityKind Density { get; set; } = DensityKind.Comfortable;

    public bool ShowNavPane { get; set; } = true;

    public bool ShowStatusBar { get; set; } = true;

    /// <summary>
    /// 标签栏位置（FR-2.4）：<c>top</c>（默认）/ <c>bottom</c>。
    /// 用字符串而不是枚举的理由同 <see cref="GeneralSettings.Language"/>。
    /// </summary>
    public string TabStripPosition { get; set; } = TabTop;

    public const string TabTop = "top";
    public const string TabBottom = "bottom";

    public static bool IsKnownTabPosition(string? value) => value is TabTop or TabBottom;

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

    public bool ShowHidden { get; set; }

    public bool ShowExtensions { get; set; } = true;

    public bool ConfirmDelete { get; set; } = true;

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

    public bool RestoreLastSession { get; set; } = true;

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

    public int CacheLimitMb { get; set; } = 512;

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class SessionSettings
{    public int WindowWidth { get; set; } = 1100;

    public int WindowHeight { get; set; } = 720;

    public bool WindowMaximized { get; set; }

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
/// 列表列宽。名称列是星号列，随窗口伸缩，因此不在此处保存；
/// 日期/类型/大小三列是固定宽度，可拖拽并记住（settings-schema §3.5）。
/// </summary>
public sealed class LayoutSettings
{
    public double ColumnDateWidth { get; set; } = AppSettings.DefaultColumnDateWidth;

    public double ColumnTypeWidth { get; set; } = AppSettings.DefaultColumnTypeWidth;

    public double ColumnSizeWidth { get; set; } = AppSettings.DefaultColumnSizeWidth;

    /// <summary>双窗格是否开启（FR-3.3）。关掉窗口再打开时保持上次的形态。</summary>
    public bool SplitEnabled { get; set; }

    /// <summary>第一窗格占的比例（0.15–0.85，默认 0.5）。分隔条拖完才落盘。</summary>
    public double SplitRatio { get; set; } = AppSettings.DefaultSplitRatio;

    /// <summary>true = 上下分割，false = 左右分割（FR-3.1，默认左右）。</summary>
    public bool SplitVertical { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
