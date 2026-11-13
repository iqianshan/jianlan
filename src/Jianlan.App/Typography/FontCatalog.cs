using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jianlan.App.Typography;

/// <summary>字体下拉项。DisplayName 用于列表展示，Family 用于按该字体自身渲染预览（FR-5.1.5）。</summary>
public sealed record FontOption(string DisplayName, FontFamily Family)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// 系统字体目录（FONT-01 / FONT-02）。
///
/// 三条约束：
///   · 不参与启动路径——只在打开设置页时调用；
///   · 结果缓存到内存，重复打开设置不再枚举；
///   · 中文系统显示中文名（Microsoft YaHei → 微软雅黑），缺中文名回落英文名。
///
/// 枚举特意放在 UI 线程的 Background 优先级：FontFamily 是 Freezable，
/// 在后台线程创建后跨线程绑定容易踩线程亲和性，而这里只跑一次、用户不会感知。
/// </summary>
public static class FontCatalog
{
    private static IReadOnlyList<FontOption>? _cache;
    private static Task<IReadOnlyList<FontOption>>? _loading;

    public static Task<IReadOnlyList<FontOption>> LoadAsync()
    {
        if (_cache is not null) return Task.FromResult(_cache);
        if (_loading is not null) return _loading;

        var tcs = new TaskCompletionSource<IReadOnlyList<FontOption>>();

        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                _cache = Build();
                tcs.TrySetResult(_cache);
            }
            catch (Exception)
            {
                // 枚举失败时只保留"默认字体"，不能让设置页打不开（FONT-01）
                _cache = [];
                tcs.TrySetResult(_cache);
            }
        }));

        _loading = tcs.Task;
        return _loading;
    }

    private static IReadOnlyList<FontOption> Build()
    {
        var list = new List<FontOption>(300);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FontFamily family in Fonts.SystemFontFamilies)
        {
            string? localized = PickLocalizedName(family);
            string display = localized ?? family.Source;

            if (!seen.Add(display)) continue;
            list.Add(new FontOption(display, family));
        }

        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
        return list;
    }

    /// <summary>优先中文名，其次英文名，最后返回 null 由调用方回落到 Source。</summary>
    private static string? PickLocalizedName(FontFamily family)
    {
        IDictionary<XmlLanguage, string> names = family.FamilyNames;
        if (names.Count == 0) return null;

        foreach (var pair in names)
        {
            if (pair.Key.IetfLanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        foreach (var pair in names)
        {
            if (pair.Key.IetfLanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return names.Values.FirstOrDefault();
    }

    /// <summary>
    /// 按保存的字体名找到可用的字体（FONT-03：字体可能已被卸载）。
    /// 找不到返回 null，由调用方回落默认字体并在界面上标注。
    /// </summary>
    public static FontFamily? Resolve(IReadOnlyList<FontOption> options, string? savedName)
    {
        if (string.IsNullOrWhiteSpace(savedName)) return null;

        foreach (var option in options)
        {
            if (string.Equals(option.DisplayName, savedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.Family.Source, savedName, StringComparison.OrdinalIgnoreCase))
            {
                return option.Family;
            }
        }

        return null;
    }
}
