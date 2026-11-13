using System.Windows;
using Jianlan.Core.Settings;

namespace Jianlan.App.Design;

/// <summary>
/// 主题切换。规范见《界面实现规则》§15.3 与 docs/settings-schema.md §6：
/// 只替换 DesignTokens 内 MergedDictionaries[0] 的 Source，界面不得直接改控件颜色。
/// </summary>
public static class ThemeService
{
    public enum EffectiveTheme
    {
        Light,
        Dark,
        HighContrast,
    }

    private const string LightUri = "Themes/Theme.Light.xaml";
    private const string DarkUri = "Themes/Theme.Dark.xaml";
    private const string HighContrastUri = "Themes/Theme.HighContrast.xaml";

    public static EffectiveTheme Current { get; private set; } = EffectiveTheme.Light;

    /// <summary>
    /// 应用主题并返回实际生效的主题。
    /// 系统高对比度开启时强制使用 HighContrast，忽略用户设置（settings-schema §3）。
    /// </summary>
    public static EffectiveTheme Apply(ThemeKind kind)
    {
        EffectiveTheme target = SystemParameters.HighContrast
            ? EffectiveTheme.HighContrast
            : Resolve(kind);

        if (!SwapDictionary(target))
        {
            // 找不到主题字典时保持现状，宁可主题不对也不要白屏
            return Current;
        }

        Current = target;

        // 底色变了，强调色的"浅底"那一项要按新底色重算（FR-5.1.3）。
        // 放在这里而不是让每个调用方自己记得调：漏一处就是"换主题后强调色底块发灰"。
        AccentService.Reapply();

        return Current;
    }

    /// <summary>系统主题变化后调用，仅在设置为 System 时才切换。</summary>
    public static void ApplyIfFollowingSystem(ThemeKind kind)
    {
        if (kind == ThemeKind.System) Apply(kind);
    }

    private static EffectiveTheme Resolve(ThemeKind kind) => kind switch
    {
        ThemeKind.Light => EffectiveTheme.Light,
        ThemeKind.Dark => EffectiveTheme.Dark,
        ThemeKind.HighContrast => EffectiveTheme.HighContrast,
        _ => IsSystemLightTheme() ? EffectiveTheme.Light : EffectiveTheme.Dark,
    };

    /// <summary>
    /// 读取系统"应用模式"。WPF 没有公开 API，只能读注册表；
    /// 键缺失（老系统或策略限制）时按浅色处理。
    /// </summary>
    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool SwapDictionary(EffectiveTheme theme)
    {
        if (Application.Current is null) return false;

        var uri = new Uri(theme switch
        {
            EffectiveTheme.Dark => DarkUri,
            EffectiveTheme.HighContrast => HighContrastUri,
            _ => LightUri,
        }, UriKind.Relative);

        // 注意：Theme.Name 在 DesignTokens **内部**合并的主题字典里，
        // 顶层 MergedDictionaries 的 Dictionary.Contains 只看自己的键，查不到。
        // 所以必须扫一层嵌套（DesignTokens.xaml 注释里的 .First(x => x.Contains(...)) 是错的）。
        foreach (var outer in Application.Current.Resources.MergedDictionaries)
        {
            var inner = outer.MergedDictionaries;
            for (int i = 0; i < inner.Count; i++)
            {
                if (!inner[i].Contains("Theme.Name")) continue;

                inner[i].Source = uri;
                return true;
            }
        }

        return false;
    }
}
