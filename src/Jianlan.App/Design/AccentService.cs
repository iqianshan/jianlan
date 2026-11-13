using System.Windows;
using System.Windows.Media;
using Jianlan.Core.Settings;

namespace Jianlan.App.Design;

/// <summary>
/// 强调色（FR-5.1.3）：跟随系统 / 六色预设。
///
/// 不改主题字典，而是在 <see cref="Application.Resources"/> **顶层**覆盖四个画刷键。
/// DynamicResource 的查找顺序是"元素 → 父级 → 应用资源（顶层先于合并字典）"，
/// 所以顶层这一点就够；而改主题字典会把改动写进那份共享字典，
/// 切一次主题就被换掉了。
///
/// 只覆盖四个 Accent 画刷，不碰 <c>Color.Accent.*</c>：那些是主题字典内部的
/// <c>StaticResource</c> 引用，改了也传不到已经冻结的画刷上。
/// </summary>
public static class AccentService
{
    /// <summary>预设六色（蓝、青、绿、橙、紫、红）。顺序即设置页下拉的顺序。</summary>
    public static readonly (string Name, string Hex)[] Presets =
    [
        ("Blue", "#2F6BE0"),
        ("Teal", "#0F7B8A"),
        ("Green", "#1F7A3F"),
        ("Orange", "#C2600F"),
        ("Purple", "#6B4FBB"),
        ("Red", "#C42B1C"),
    ];

    /// <summary>拿不到系统色时的回落值，与浅色主题的默认强调色一致。</summary>
    private const string FallbackHex = "#2F6BE0";

    /// <summary>用户的选择。null = 跟随系统。缓存下来是为了切主题时能原地重算。</summary>
    private static string? _requested;

    /// <summary>套用强调色。<paramref name="accent"/> 为 null 时跟随系统。</summary>
    public static void Apply(string? accent)
    {
        _requested = AppearanceSettings.NormalizeAccent(accent);
        ApplyCore();
    }

    /// <summary>主题切换后调用：底色变了，浅底那一项要按新底色重算。</summary>
    internal static void Reapply() => ApplyCore();

    private static void ApplyCore()
    {
        if (Application.Current is not { } app) return;

        Color accent = ResolveBase();

        app.Resources["Brush.Accent.Default"] = Freeze(new SolidColorBrush(accent));
        app.Resources["Brush.Accent.Hover"] = Freeze(new SolidColorBrush(Scale(accent, 0.88)));
        app.Resources["Brush.Accent.Pressed"] = Freeze(new SolidColorBrush(Scale(accent, 0.76)));
        app.Resources["Brush.Accent.Subtle"] = Freeze(new SolidColorBrush(Subtle(accent)));
    }

    private static Color ResolveBase()
    {
        if (_requested is { } hex && Parse(hex) is { } chosen) return chosen;

        // 跟随系统：DWM 的 AccentColor 是 ABGR 打包的 DWORD（不是 ARGB），
        // 而且系统主题常带"自动取色"（打开后这个值跟着壁纸变），拿到什么用什么。
        if (SystemAccent() is { } system) return system;

        return Parse(FallbackHex) ?? Colors.RoyalBlue;
    }

    private static Color? SystemAccent()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\DWM");

            if (key?.GetValue("AccentColor") is not int packed) return null;

            uint value = unchecked((uint)packed);

            // 0x00 或 0xFF000000 这类"没设过"的值直接放弃，交给回落色
            if (value == 0) return null;

            return Color.FromRgb(
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Color? Parse(string hex) => ColorConverter.ConvertFromString(hex) as Color?;

    /// <summary>按比例压暗。悬停与按下用它——比"再挑两个颜色"少维护两个常量。</summary>
    private static Color Scale(Color c, double factor) => Color.FromRgb(
        (byte)Math.Round(c.R * factor),
        (byte)Math.Round(c.G * factor),
        (byte)Math.Round(c.B * factor));

    /// <summary>浅底：强调色与当前主题底色混合。深色主题下混黑，浅色下混白。</summary>
    private static Color Subtle(Color accent)
    {
        Color baseColor = ThemeService.Current == ThemeService.EffectiveTheme.Light
            ? Color.FromRgb(0xFF, 0xFF, 0xFF)
            : Color.FromRgb(0x1F, 0x1F, 0x1F);

        double amount = 0.12;
        return Color.FromRgb(
            (byte)Math.Round(accent.R * amount + baseColor.R * (1 - amount)),
            (byte)Math.Round(accent.G * amount + baseColor.G * (1 - amount)),
            (byte)Math.Round(accent.B * amount + baseColor.B * (1 - amount)));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
