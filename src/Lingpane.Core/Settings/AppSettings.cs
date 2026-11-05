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

        Layout.ColumnDateWidth = ClampColumn(Layout.ColumnDateWidth, DefaultColumnDateWidth);
        Layout.ColumnTypeWidth = ClampColumn(Layout.ColumnTypeWidth, DefaultColumnTypeWidth);
        Layout.ColumnSizeWidth = ClampColumn(Layout.ColumnSizeWidth, DefaultColumnSizeWidth);
    }

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

    public bool OpenFolderInNewTab { get; set; }

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

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
