using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Jianlan.Core.Localization;

/// <summary>
/// 界面语言的运行时切换（FR-10.4）。方案与取舍见 <c>docs/i18n.md</c>。
///
/// 两条取值路径：
///   1. XAML：<c>{loc:Tr Key=Menu.Open}</c> 编译成 <c>Binding("[Menu.Open]")</c>，绑到本类索引器。
///      语言一变 → 触发一次 <c>PropertyChanged</c> → 所有绑定重取，不需要重建窗口。
///   2. C#：<see cref="Strings"/> 的强类型属性（resx 生成的类），同样按当前 UI 文化取值。
///
/// 资源回退：中性资源是英文，中文走 <c>zh-Hans</c> 卫星程序集。卫星目录缺失时
/// <see cref="ResourceManager"/> 自动回落到中性资源——即"删掉 zh-Hans 目录后回落英文且不报错"（§十一）。
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    /// <summary>中性资源（英文）的清单名，等于 <c>&lt;RootNamespace&gt;.&lt;项目内路径&gt;</c>。</summary>
    private const string ResourceManifestName = "Jianlan.Core.Localization.Strings";

    private static readonly ResourceManager Manager = new(ResourceManifestName, typeof(LocalizationService).Assembly);

    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    public static readonly CultureInfo SimplifiedChinese = CultureInfo.GetCultureInfo("zh-Hans");

    /// <summary>设置项 <c>general.language</c> 允许的字面值，与 <c>docs/settings-schema.md</c> 一致。</summary>
    public const string SettingAuto = "auto";
    public const string SettingChinese = "zh-Hans";
    public const string SettingEnglish = "en";

    private CultureInfo _culture = English;

    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture => _culture;

    /// <summary>XAML 索引器绑定入口。<see cref="TrExtension"/> 生成的 Path 是 <c>[键名]</c>。</summary>
    public string this[string key] => Get(key);

    /// <summary>
    /// 按键取值。缺键返回 <c>!键名!</c> 而不是空串——空串会被当成"设计如此"漏过走查，
    /// 醒目占位能保证漏配在界面上一眼可见（§四）。
    /// </summary>
    public string Get(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        try
        {
            return Manager.GetString(key, _culture) ?? $"!{key}!";
        }
        catch (MissingManifestResourceException)
        {
            // 资源清单本身缺失（构建异常）：不抛，避免一处文案把整个界面带崩
            return $"!{key}!";
        }
    }

    /// <summary>带占位符的取值。<paramref name="args"/> 按 resx 里的 {0} {1} 顺序传入。</summary>
    public string Format(string key, params object?[] args)
    {
        string template = Get(key);
        try
        {
            return string.Format(_culture, template, args);
        }
        catch (FormatException)
        {
            // 译文把占位符写坏了：原样返回模板，至少能看出是哪条文案出错
            return template;
        }
    }

    /// <summary>按设置项的值切换语言；未识别的值按 <c>auto</c> 处理，不报错（§五）。</summary>
    public void Apply(string? setting) => Apply(Resolve(setting));

    /// <summary>
    /// 切换界面语言。<paramref name="culture"/> 同时作用于 UI 文化与格式文化：
    /// 日期/数字格式跟随语言是用户预期（英文界面下出现 2026/11/5 会显得拼装）。
    /// </summary>
    public void Apply(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        if (culture.Name == _culture.Name) return;

        // 三处都要设：进程级默认（新线程继承，后台线程取文案才不会退回英文）、
        // 当前线程 UI 文化（WPF 绑定与资源查找看它）、当前线程格式文化（日期/数字）。
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;

        _culture = culture;

        // "Item[]" 让索引器绑定重取；空名是兜底，WPF 视作"全部重取"。
        // 实测当前窗口规模（约 300 个可见绑定）刷新 <50ms，满足 FR-10.4 的 <200ms。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>
    /// 设置值 → 文化。<c>auto</c> 用"系统安装语言"判断，不用 <c>CurrentUICulture</c>：
    /// 后者可能被父进程或兼容性设置改掉（例如用兼容模式启动时变成英文）。
    /// </summary>
    public static CultureInfo Resolve(string? setting) => Resolve(setting, CultureInfo.InstalledUICulture);

    /// <summary>单元测试用重载：注入"系统安装语言"替身。</summary>
    public static CultureInfo Resolve(string? setting, CultureInfo installed)
    {
        switch (setting?.Trim())
        {
            case SettingChinese:
                return SimplifiedChinese;
            case SettingEnglish:
                return English;
            case SettingAuto:
            case null:
            case "":
                return IsChinese(installed) ? SimplifiedChinese : English;
            default:
                // 用户手改坏了（比如写成 "zh_CN"）：按 auto 走，不弹错
                return IsChinese(installed) ? SimplifiedChinese : English;
        }
    }

    private static bool IsChinese(CultureInfo culture)
        => string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase)
           || culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>把文化折回设置项的字面值，用于写回 settings.json。</summary>
    public static string ToSettingValue(CultureInfo culture)
        => IsChinese(culture) ? SettingChinese : SettingEnglish;
}
