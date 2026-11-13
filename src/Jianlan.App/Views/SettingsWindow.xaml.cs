using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Jianlan.App.Design;
using Jianlan.App.Typography;
using Jianlan.App.ViewModels;
using Jianlan.Core.Diagnostics;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Localization;
using Jianlan.Core.Preview;
using Jianlan.Core.Settings;
using Jianlan.Core.Tagging;

namespace Jianlan.App.Views;

/// <summary>
/// 设置窗口（FR-5）。刻意不引入 MVVM 与绑定框架：
/// 这里全是"改一个值 → 应用 → 存盘"的直线逻辑，用绑定反而要多维护一套通知。
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>初始化期间置位：避免控件赋初值时触发 SelectionChanged 反过来写盘。</summary>
    private bool _loading = true;

    private IReadOnlyList<FontOption> _fonts = [];

    /// <summary>排序下拉框的顺序与枚举顺序不同（类型在大小之前），用映射表而不是强转。</summary>
    private static readonly SortFieldKind[] SortOrder =
        [SortFieldKind.Name, SortFieldKind.Date, SortFieldKind.Type, SortFieldKind.Size];

    /// <summary>
    /// 左导航的六个分类。枚举值就是导航列表的索引顺序，切换靠强转，
    /// 两侧不会因为插入一项而错位（真要插，插在末尾）。
    /// </summary>
    private enum SettingsPage
    {
        General = 0,
        Appearance = 1,
        Files = 2,
        Actions = 3,
        Privacy = 4,
        Tags = 5,
        About = 6,
    }

    /// <summary>
    /// 本次运行内回到上次停留的页。**不落盘**：这是"刚才看到哪了"，
    /// 不是一项偏好——写进 settings.json 的话，用户下次打开设置会莫名停在上次的页，
    /// 而没有任何地方能改回"永远从外观开始"。
    /// </summary>
    private static SettingsPage _lastPage = SettingsPage.Appearance;

    public SettingsWindow()
    {
        InitializeComponent();

        LoadSettingsIntoUi();
        _loading = false;

        // 数据位置是只读展示，启动期就定了，不必等 Loaded
        DataDirText.Text = AppPaths.Root;
        ToolTipService.SetToolTip(DataDirText, AppPaths.Root);

        FitToFontScale();

        // 选中即切页（OnNavChanged）。放在所有控件就绪之后：
        // ShowPage 要给 PageTitle / PageSubtitle 赋值，早了会拿到 null。
        NavList.SelectedIndex = (int)_lastPage;

        // 字体枚举与标签库条数都不进构造：两者都要读盘/遍历系统字体，
        // 放在首帧之后跑，窗口才是"先出来再填"。缓存占用同理（要遍历上万个小文件）。
        Loaded += async (_, _) =>
        {
            await LoadFontsAsync();
            await RefreshTagCountAsync();
            await RefreshThumbCacheSizeAsync();
        };
    }

    private AppSettings Settings => App.Settings;

    private MainWindow? Owner_ => Owner as MainWindow;

    // ── 页面导航 ────────────────────────────────────────────────────────────

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = NavList.SelectedIndex;
        if (index < 0) return;

        ShowPage((SettingsPage)index);
    }

    /// <summary>
    /// 切页。六个 ScrollViewer 一直都在树里，只改 Visibility——
    /// 不重建内容，各页的滚动位置因此天然保留（切走再回来还在原处）。
    /// </summary>
    private void ShowPage(SettingsPage page)
    {
        _lastPage = page;

        PageGeneral.Visibility = Visible(page == SettingsPage.General);
        PageAppearance.Visibility = Visible(page == SettingsPage.Appearance);
        PageFiles.Visibility = Visible(page == SettingsPage.Files);
        PageActions.Visibility = Visible(page == SettingsPage.Actions);
        PagePrivacy.Visibility = Visible(page == SettingsPage.Privacy);
        PageTags.Visibility = Visible(page == SettingsPage.Tags);
        PageAbout.Visibility = Visible(page == SettingsPage.About);

        PageTitle.Text = TitleOf(page);
        PageSubtitle.Text = SubtitleOf(page);

        // 「关于」页没有可恢复的设置项，按钮留着只会让人点一下发现什么都没变
        ResetPageButton.Visibility = Visible(page != SettingsPage.About);

        static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string TitleOf(SettingsPage page) => page switch
    {
        SettingsPage.General => Strings.Settings_Page_General,
        SettingsPage.Appearance => Strings.Settings_Page_Appearance,
        SettingsPage.Files => Strings.Settings_Page_Files,
        SettingsPage.Actions => Strings.Settings_Page_Actions,
        SettingsPage.Privacy => Strings.Settings_Page_Privacy,
        SettingsPage.Tags => Strings.Settings_Page_Tags,
        _ => Strings.Settings_Page_About,
    };

    private static string SubtitleOf(SettingsPage page) => page switch
    {
        SettingsPage.General => Strings.Settings_Page_General_Desc,
        SettingsPage.Appearance => Strings.Settings_Page_Appearance_Desc,
        SettingsPage.Files => Strings.Settings_Page_Files_Desc,
        SettingsPage.Actions => Strings.Settings_Page_Actions_Desc,
        SettingsPage.Privacy => Strings.Settings_Page_Privacy_Desc,
        SettingsPage.Tags => Strings.Settings_Page_Tags_Desc,
        _ => Strings.Settings_Page_About_Desc,
    };

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab 在六页之间循环。
    /// 与主窗口切标签页是同一个手势——这个窗口开着时键盘在这里，
    /// 用户按下去想要的多半是"下一页设置"，不是"切背后的标签"。
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

        int count = NavList.Items.Count;
        if (count == 0) return;

        bool back = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        int next = NavList.SelectedIndex + (back ? -1 : 1);

        NavList.SelectedIndex = next < 0 ? count - 1 : next % count;
        e.Handled = true;
    }

    // ── 初始化 ──────────────────────────────────────────────────────────────

    private void LoadSettingsIntoUi()
    {
        LanguageCombo.SelectedIndex = Settings.General.Language switch
        {
            GeneralSettings.LanguageChinese => 1,
            GeneralSettings.LanguageEnglish => 2,
            _ => 0,
        };

        ThemeCombo.SelectedIndex = (int)Settings.Appearance.Theme;
        DensityCombo.SelectedIndex = (int)Settings.Appearance.Density;
        LoadAccentChoices();

        ViewCombo.SelectedIndex = Settings.Behavior.DefaultView == DefaultViewKind.List ? 1 : 0;

        int sortIndex = Array.IndexOf(SortOrder, Settings.Behavior.SortBy);
        SortCombo.SelectedIndex = sortIndex >= 0 ? sortIndex : 0;

        DescendingCheck.IsChecked = Settings.Behavior.SortDescending;
        FoldersFirstCheck.IsChecked = Settings.Behavior.FoldersFirst;
        NaturalSortCheck.IsChecked = Settings.Behavior.NaturalSort;
        CaseSensitiveCheck.IsChecked = Settings.Behavior.CaseSensitiveSort;
        ShowHiddenCheck.IsChecked = Settings.Behavior.ShowHidden;
        ShowSystemCheck.IsChecked = Settings.Behavior.ShowSystem;
        ShowExtensionsCheck.IsChecked = Settings.Behavior.ShowExtensions;
        ConfirmDeleteCheck.IsChecked = Settings.Behavior.ConfirmDelete;
        ConfirmBatchCheck.IsChecked = Settings.Behavior.ConfirmBatch;
        ConfirmOverwriteCheck.IsChecked = Settings.Behavior.ConfirmOverwrite;
        WarnOnExecutableCheck.IsChecked = Settings.Behavior.WarnOnExecutable;
        LoadColumnChecks();
        CloseLastToHomeCheck.IsChecked = Settings.Behavior.CloseLastTabToHome;
        OpenModeCombo.SelectedIndex = Settings.Behavior.DoubleClickOpens ? 0 : 1;
        OpenFolderInNewTabCheck.IsChecked = Settings.Behavior.OpenFolderInNewTab;
        BackgroundTabCheck.IsChecked = Settings.Behavior.OpenNewTabInBackground;

        LoadStartupChoices();

        TabStripCombo.SelectedIndex =
            Settings.Appearance.TabStripPosition == AppearanceSettings.TabBottom ? 1 : 0;

        // 空 = 用系统「下载」：把这个"当前实际生效的目录"直接显示出来，
        // 否则输入框空着，用户没法判断现在到底会开到哪
        NewTabPathBox.Text = Settings.General.NewTabPath ?? MainWindow.ResolveNewTabPath();
        NewTabModeCombo.SelectedIndex = Settings.General.NewTabMode switch
        {
            NewTabModeKind.Home => 0,
            NewTabModeKind.Last => 2,
            _ => 1,
        };
        UpdateNewTabPathAvailability();
        UpdateNewTabPathHint();

        BuildTagRows();

        ShowNavCheck.IsChecked = Settings.Appearance.ShowNavPane;
        ImmersiveTitleBarCheck.IsChecked = Settings.Appearance.ImmersiveTitleBar;
        ShowStatusCheck.IsChecked = Settings.Appearance.ShowStatusBar;
        ShowPreviewCheck.IsChecked = Settings.Appearance.ShowPreview;
        PreviewAutoCheck.IsChecked = Settings.Behavior.PreviewAutoOpen;
        RememberPathCheck.IsChecked = Settings.Privacy.HistoryEnabled;

        int limitIndex = Array.FindIndex(HistoryLimits, n => n >= Settings.Privacy.HistoryLimit);
        HistoryLimitCombo.SelectedIndex = limitIndex >= 0 ? limitIndex : HistoryLimits.Length - 1;

        // 缓存上限只由这里的下拉框修改，但旧版本留下的默认值是 512（不在档位里）。
        // 取"最接近的一档"并落盘归一：否则下拉框显示 1024、占用行写着上限 512 MB、
        // 实际也按 512 淘汰——三个数字各说一套，用户只能猜。
        int cacheIndex = NearestCacheLimitIndex(Settings.Privacy.CacheLimitMb);
        CacheLimitCombo.SelectedIndex = cacheIndex;

        if (Settings.Privacy.CacheLimitMb != CacheLimits[cacheIndex])
        {
            Settings.Privacy.CacheLimitMb = CacheLimits[cacheIndex];
            ThumbnailCache.CapBytes = Settings.Privacy.CacheLimitMb * 1024L * 1024;
            App.SaveSettings();
        }

        FollowSystemFontCheck.IsChecked = Settings.Appearance.FontSizePt is null;
        UpdateFontSizeText();
    }

    /// <summary>历史条数上限的可选值（FR-5.3.4）。</summary>
    private static readonly int[] HistoryLimits = [50, 100, 200, 500];

    /// <summary>缩略图缓存上限的可选值，单位 MB（FR-5.3.5）。</summary>
    private static readonly int[] CacheLimits = [128, 256, 500, 1024, 2048];

    /// <summary>离 value 最近的一档。差一样近时取小的：上限宁小勿大。</summary>
    private static int NearestCacheLimitIndex(int value)
    {
        int best = 0;

        for (int i = 1; i < CacheLimits.Length; i++)
        {
            if (Math.Abs(CacheLimits[i] - value) < Math.Abs(CacheLimits[best] - value)) best = i;
        }

        return best;
    }

    /// <summary>
    /// 语言切换立即生效，不需要重启窗口：界面上的 {loc:Tr} 绑定与视图模型都会收到通知。
    /// </summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        string value = LanguageCombo.SelectedIndex switch
        {
            1 => GeneralSettings.LanguageChinese,
            2 => GeneralSettings.LanguageEnglish,
            _ => GeneralSettings.LanguageAuto,
        };

        if (Settings.General.Language == value) return;
        App.ApplyLanguage(value);

        // 页标题与页说明是代码赋值的，不走 {loc:Tr} 绑定，切语言后得手动刷一遍；
        // 导航项本身是 XAML 里的 loc:Tr，会自己跟上
        ShowPage(_lastPage);
    }

    private async Task LoadFontsAsync()
    {
        _fonts = await FontCatalog.LoadAsync();

        // 第一项固定为"默认字体"，让用户能显式回到默认栈
        var options = new List<FontOption> { new(Strings.Settings_Appearance_Font_Default, null!) };
        options.AddRange(_fonts);

        FontCombo.ItemsSource = options;

        FontFamily? saved = FontCatalog.Resolve(_fonts, Settings.Appearance.FontFamily);
        if (saved is null)
        {
            FontCombo.SelectedIndex = 0;
            if (!string.IsNullOrEmpty(Settings.Appearance.FontFamily))
            {
                // FONT-03：换机器后字体可能不存在，回落默认但要让用户知道
                FontHint.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Settings_Appearance_Font_Fallback,
                    Settings.Appearance.FontFamily);
            }
        }
        else
        {
            int index = options.FindIndex(o => ReferenceEquals(o.Family, saved));
            FontCombo.SelectedIndex = index >= 0 ? index : 0;
        }
    }

    // ── 外观 ────────────────────────────────────────────────────────────────

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        Settings.Appearance.Theme = (ThemeKind)Math.Max(0, ThemeCombo.SelectedIndex);
        ThemeService.Apply(Settings.Appearance.Theme);
        App.SaveSettings();
    }

    // ── 强调色（FR-5.1.3）───────────────────────────────────────────────────

    /// <summary>强调色下拉的一项。<c>Hex</c> 为 null 表示"跟随系统"。</summary>
    private sealed record AccentChoice(string? Hex, string Name, Brush? Swatch, bool HasSwatch);

    private void LoadAccentChoices()
    {
        var choices = new List<AccentChoice>
        {
            new(null, Strings.Settings_Appearance_Accent_System, null, HasSwatch: false),
        };

        foreach ((string preset, string hex) in AccentService.Presets)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();

            choices.Add(new AccentChoice(hex, AccentName(preset), brush, HasSwatch: true));
        }

        AccentCombo.ItemsSource = choices;

        int index = Settings.Appearance.Accent is { } saved
            ? choices.FindIndex(c => string.Equals(c.Hex, saved, StringComparison.OrdinalIgnoreCase))
            : 0;

        AccentCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private static string AccentName(string preset) => preset switch
    {
        "Teal" => Strings.Accent_Teal,
        "Green" => Strings.Accent_Green,
        "Orange" => Strings.Accent_Orange,
        "Purple" => Strings.Accent_Purple,
        "Red" => Strings.Accent_Red,
        _ => Strings.Accent_Blue,
    };

    private void OnAccentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (AccentCombo.SelectedItem is not AccentChoice choice) return;

        Settings.Appearance.Accent = choice.Hex;
        AccentService.Apply(choice.Hex);
        App.SaveSettings();
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontCombo.SelectedItem is not FontOption option) return;

        // 存 Source 而不是显示名：显示名随系统语言变化，Source 不会
        Settings.Appearance.FontFamily = option.Family?.Source;

        var anchor = Owner_?.CaptureScrollAnchor();
        FontScaleService.ApplyFontFamily(option.Family);
        if (anchor is { } a) Owner_?.RestoreScrollAnchor(a);

        App.SaveSettings();
    }

    private void OnFontSizeDown(object sender, RoutedEventArgs e) => StepFontSize(-1);

    private void OnFontSizeUp(object sender, RoutedEventArgs e) => StepFontSize(+1);

    private void StepFontSize(int delta)
    {
        double current = Settings.Appearance.FontSizePt
                         ?? Math.Round(FontScaleService.Current.Points, MidpointRounding.AwayFromZero);

        double next = Math.Clamp(current + delta, FontScaleService.MinPoints, FontScaleService.MaxPoints);
        Settings.Appearance.FontSizePt = (int)Math.Round(next);
        FollowSystemFontCheck.IsChecked = false;

        ApplyFontScale();
        App.SaveSettings();
    }

    private void OnFollowSystemFontChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.Appearance.FontSizePt = FollowSystemFontCheck.IsChecked == true ? null : Settings.Appearance.FontSizePt;
        ApplyFontScale();
        App.SaveSettings();
    }

    private void OnDensityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        Settings.Appearance.Density = (DensityKind)Math.Max(0, DensityCombo.SelectedIndex);
        ApplyFontScale();
        App.SaveSettings();
    }

    private void ApplyFontScale()
    {
        var anchor = Owner_?.CaptureScrollAnchor();
        FontScaleService.Apply(
            Settings.Appearance.FontSizePt is int pt ? pt : null,
            Settings.Appearance.Density == DensityKind.Compact);
        if (anchor is { } a) Owner_?.RestoreScrollAnchor(a);

        UpdateFontSizeText();
        FitToFontScale();
    }

    /// <summary>
    /// 窗口尺寸跟着字号走。
    ///
    /// 24pt 下每一行都按比例变高，窗口不跟着长的话内容区只剩一百多像素——一屏两行，
    /// 滚动条细得没法拖。按字号比例放大窗口，再夹进屏幕工作区（留 6% 边距）。
    ///
    /// 比例直接取"当前行高 ÷ 设计基线行高（13px）"，不再按 pt 换算：
    /// 设置页里标签、下拉框、说明行的尺寸全是行高的函数，字号一档带来的实际
    /// 增量也只有行高说得准。按 pt 折算时 12pt（16px 行）算出 ratio=1，
    /// 窗口不长，可内容已经比设计稿高 10%，"一屏"就破了（AC-06 走查发现）。
    ///
    /// 不做缩小（ratio 小于 1 按 1 处理）——字号调小后窗口跟着缩只会更挤。
    /// </summary>
    private void FitToFontScale()
    {
        double ratio = Math.Clamp(
            FontScaleService.Current.RowPx / FontMetrics.BaseRowPx, 1d, 2d);

        Rect work = SystemParameters.WorkArea;

        // 基线 760×600：去掉全局页头后腾出 64px，600 高的内容区与 0.2.0 的 720 高基本持平。
        // 再小就要动各页的排版密度了，"常规"页放不下四组。
        Width = Math.Min(Math.Max(760 * ratio, MinWidth), work.Width * 0.94);
        Height = Math.Min(Math.Max(600 * ratio, MinHeight), work.Height * 0.94);
    }

    private void UpdateFontSizeText()
    {
        FontSizeText.Text = Settings.Appearance.FontSizePt is int pt
            ? $"{pt} pt"
            : string.Format(
                CultureInfo.CurrentCulture,
                Strings.Settings_Appearance_FontSize_FollowSystemValue,
                FontScaleService.Current.Points);
    }

    /// <summary>
    /// 「恢复本页默认」（FR-5.3.8 的分类恢复，粒度改成按页）。
    ///
    /// 重置后走一遍 <see cref="LoadSettingsIntoUi"/>，而不是逐控件赋初值：
    /// 新增设置项时不必回来补第二处，少一个漏网的。
    ///
    /// 为什么按页而不是按 Settings 的子对象：BehaviorSettings 被四页瓜分，
    /// 挂在任意一页上都会连带重置另外三页中看不见的字段（见 AppSettings 里的说明）。
    /// </summary>
    private void OnResetPage(object sender, RoutedEventArgs e)
    {
        switch (_lastPage)
        {
            case SettingsPage.General:
                ResetAndSync(Settings.ResetGeneralPage, Strings.Settings_ResetPage_Done, resetFont: false);
                break;

            case SettingsPage.Appearance:
                ResetAndSync(Settings.ResetAppearancePage, Strings.Settings_ResetPage_Done, resetFont: true);
                break;

            case SettingsPage.Files:
                ResetAndSync(Settings.ResetListPage, Strings.Settings_ResetPage_Done, resetFont: false);
                break;

            case SettingsPage.Actions:
                ResetAndSync(Settings.ResetActionPage, Strings.Settings_ResetPage_Done, resetFont: false);
                break;

            case SettingsPage.Privacy:
                ResetAndSync(Settings.ResetPrivacyPage, Strings.Settings_ResetPage_Done, resetFont: false);
                break;

            // 恢复默认只把标签名单换回默认八色，不动 tags.jsonl 里打过的标记
            case SettingsPage.Tags:
                ResetAndSync(Settings.ResetTagsPage, Strings.Settings_ResetPage_Done, resetFont: false);
                break;
        }
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        // 全部恢复会一起改主题、字号、视图、排序、各种开关——列出来问一句，
        // 而不是"点了就变"。收藏、历史、标签、工作区不受影响（那是用户数据）。
        if (!ConfirmWindow.Ask(this, Strings.Dialog_ResetAll_Title, Strings.Dialog_ResetAll_Body))
        {
            return;
        }

        ResetAndSync(Settings.ResetAll, Strings.Settings_ResetAll_Done, resetFont: true);
    }

    /// <param name="resetFont">
    /// 字体下拉的选项是异步填的，<see cref="LoadSettingsIntoUi"/> 不碰它。
    /// 重置外观时必须单独把它推回"默认字体"，否则下拉里还写着上一个字体，
    /// 而实际渲染已经回到默认栈——两者对不上。
    /// </param>
    private void ResetAndSync(Action reset, string message, bool resetFont)
    {
        reset();

        _loading = true;
        LoadSettingsIntoUi();
        if (resetFont && FontCombo.Items.Count > 0) FontCombo.SelectedIndex = 0;
        _loading = false;

        // 主题与字号不经过控件赋值，要显式套一次
        ThemeService.Apply(Settings.Appearance.Theme);
        FontScaleService.Apply(
            Settings.Appearance.FontSizePt,
            Settings.Appearance.Density == DensityKind.Compact);

        // 重置可能把字号改回默认，窗口尺寸也要跟着回来
        FitToFontScale();

        Owner_?.ApplySettingsToUi();
        App.SaveSettings();

        // 结果进底栏状态位。以前写的是 FontHint，于是"清空历史已完成"
        // 会出现在字体下拉下面，像一句字体说明。
        StatusText.Text = message;

        // 重置可能整份换掉标签定义，列表是手写的，得按新定义重建
        BuildTagRows();
    }

    private void OnShowNavChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Appearance.ShowNavPane = ShowNavCheck.IsChecked == true;
        Owner_?.ApplySettingsToUi();
        App.SaveSettings();
    }

    private void OnShowStatusChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Appearance.ShowStatusBar = ShowStatusCheck.IsChecked == true;
        Owner_?.ApplySettingsToUi();
        App.SaveSettings();
    }

    /// <summary>
    /// 隐藏系统标题栏（FR-10.11）。只写设置、不刷新界面：WindowStyle 只能在建窗口时定，
    /// 运行期切换要重建 HWND。强行让主窗口局部响应，只会得到"标题栏撤了、无边框还在"的半成品。
    /// </summary>
    private void OnImmersiveTitleBarChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.Appearance.ImmersiveTitleBar = ImmersiveTitleBarCheck.IsChecked == true;
        App.SaveSettings();
    }

    /// <summary>预览窗格的默认状态（FR-5.2.10）。与 Alt+P 改的是同一项，两边同步。</summary>
    private void OnShowPreviewChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Appearance.ShowPreview = ShowPreviewCheck.IsChecked == true;
        Owner_?.ApplySettingsToUi();
        App.SaveSettings();
    }

    /// <summary>
    /// 主窗口那边关掉（或打开）预览窗格后，把这里那颗勾选框拉回同一状态（FR-9.1.5）。
    ///
    /// 设置页是非模态的，两边可以同时开着。这里只改界面、不回写设置：
    /// 值本来就是那边刚写进去的，再走一遍 <see cref="OnShowPreviewChanged"/> 是绕圈。
    /// </summary>
    public void SyncShowPreview()
    {
        bool open = Settings.Appearance.ShowPreview;
        if (ShowPreviewCheck.IsChecked == open) return;

        _loading = true;
        try
        {
            ShowPreviewCheck.IsChecked = open;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>预览的触发方式（FR-5.2.10）。关掉后窗格仍在，但选中不自动出内容。</summary>
    private void OnPreviewAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        bool auto = PreviewAutoCheck.IsChecked == true;
        Settings.Behavior.PreviewAutoOpen = auto;

        // 「自动预览」只在预览窗格显示时才成立，而窗格默认是关的。
        // 勾这一项的人要的是"选中就能看到预览"，所以顺手把窗格打开——
        // 不打开的话勾完再选中文件什么都不会发生，用户看到的就是"设置没生效"。
        // 反向不联动：关掉窗格时保留这一项，下次打开仍然是自动。
        if (auto && !Settings.Appearance.ShowPreview)
        {
            Settings.Appearance.ShowPreview = true;

            // 用 _loading 包住：那颗勾选框的 Checked 事件会再走一遍
            // OnShowPreviewChanged（重复 ApplySettingsToUi + 存盘），这里已经替它做了。
            _loading = true;
            try { ShowPreviewCheck.IsChecked = true; }
            finally { _loading = false; }
        }

        Owner_?.ApplySettingsToUi();
        App.SaveSettings();
    }

    /// <summary>标签栏位置（FR-2.4）：主窗口只挪一侧边框、不重建标签栏，因此即时生效。</summary>
    private void OnTabStripPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        Settings.Appearance.TabStripPosition = TabStripCombo.SelectedIndex == 1
            ? AppearanceSettings.TabBottom
            : AppearanceSettings.TabTop;

        Owner_?.ApplySettingsToUi();
        App.SaveSettings();
    }

    // ── 行为 ────────────────────────────────────────────────────────────────

    private void OnDefaultViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        // 界面只提供两种视图，Grid 在 M1 未实现（见 PaneViewModel.SetViewMode）
        Settings.Behavior.DefaultView = ViewCombo.SelectedIndex == 1
            ? DefaultViewKind.List
            : DefaultViewKind.Details;
        App.SaveSettings();
    }

    private void OnDefaultSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        int index = Math.Clamp(SortCombo.SelectedIndex, 0, SortOrder.Length - 1);
        Settings.Behavior.SortBy = SortOrder[index];
        App.SaveSettings();
    }

    private void OnSortOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.Behavior.SortDescending = DescendingCheck.IsChecked == true;
        Settings.Behavior.FoldersFirst = FoldersFirstCheck.IsChecked == true;
        Settings.Behavior.NaturalSort = NaturalSortCheck.IsChecked == true;
        Settings.Behavior.CaseSensitiveSort = CaseSensitiveCheck.IsChecked == true;

        // 口径改了要立刻重排眼前这些列表（FR-5.2.3）：只在下次启动生效的话，
        // 用户勾完回到列表看不到任何变化，会以为没生效
        Owner_?.ApplySortOptionsToPanes();
        App.SaveSettings();
    }

    private void OnShowHiddenChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.ShowHidden = ShowHiddenCheck.IsChecked == true;
        Owner_?.ApplySettingsToUi();
        App.SaveSettings();
    }

    /// <summary>系统文件开关（FR-5.2.4）：与隐藏文件一样只在内存过滤，即时生效。</summary>
    private void OnShowSystemChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.ShowSystem = ShowSystemCheck.IsChecked == true;
        Owner_?.ApplySettingsToUi();
        App.SaveSettings();
    }

    private void OnShowExtensionsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.ShowExtensions = ShowExtensionsCheck.IsChecked == true;
        Owner_?.ApplySettingsToUi();
        App.SaveSettings();
    }

    private void OnConfirmDeleteChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.ConfirmDelete = ConfirmDeleteCheck.IsChecked == true;
        App.SaveSettings();
    }

    /// <summary>批量操作确认（FR-5.2.8）。与单删分开，允许"单删不弹、批量弹"。</summary>
    private void OnConfirmBatchChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.ConfirmBatch = ConfirmBatchCheck.IsChecked == true;
        App.SaveSettings();
    }

    /// <summary>覆盖确认（FR-5.2.8）。关掉即"直接覆盖"，所以这一项旁边必须有后果说明。</summary>
    private void OnConfirmOverwriteChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.ConfirmOverwrite = ConfirmOverwriteCheck.IsChecked == true;
        App.SaveSettings();
    }

    /// <summary>双击可执行文件时先确认（FR-5.2.11）。只管"下一次双击"，没有即时生效的界面可刷。</summary>
    private void OnWarnOnExecutableChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.WarnOnExecutable = WarnOnExecutableCheck.IsChecked == true;
        App.SaveSettings();
    }

    // ── 新标签默认目录（FR-5.2.6）────────────────────────────────────────

    private void OnNewTabModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        Settings.General.NewTabMode = NewTabModeCombo.SelectedIndex switch
        {
            0 => NewTabModeKind.Home,
            2 => NewTabModeKind.Last,
            _ => NewTabModeKind.Path,
        };

        UpdateNewTabPathAvailability();
        App.SaveSettings();
    }

    /// <summary>
    /// 「主页」与「上次待的目录」都不看路径，所以路径行在这一档里禁用而不是隐藏：
    /// 隐藏会让人以为"以前填的路径被清掉了"，禁用则一眼看出"这一档用不到它"。
    /// </summary>
    private void UpdateNewTabPathAvailability()
    {
        bool usesPath = Settings.General.NewTabMode == NewTabModeKind.Path;
        NewTabPathRow.IsEnabled = usesPath;
        NewTabPathRow.Opacity = usesPath ? 1 : 0.5;
    }

    // ── 标签管理（FR-4.1.7 扩展）────────────────────────────────────────

    /// <summary>新增标签后要聚焦的槽位。行是重建的，只能在这之后把焦点放回去。</summary>
    private int _focusSlot = -1;

    /// <summary>
    /// 标签列表：色块 + 名称输入框 + 删除。行由代码生成——项目数随用户增删变化，
    /// 在 XAML 里写死没法表达。
    ///
    /// 输入框里放的是**当前显示的名字**。提交时与默认名比较：一样就存 null
    /// （回落成跟随界面语言的默认名），不一样才存自定义名——这样没动过的标签
    /// 切成英文后仍然显示英文色名，而不是被钉死成中文。
    /// </summary>
    private void BuildTagRows()
    {
        TagListPanel.Children.Clear();

        foreach (TagPresets.ColorPreset preset in TagPresets.Colors)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.Hex)),
                BorderBrush = (Brush)FindResource("Brush.Border.Strong"),
                BorderThickness = new Thickness(0.5),
            };

            var box = new TextBox
            {
                Width = 168,
                Text = preset.Name,
                Tag = preset.Value,
                Style = (Style)FindResource("TextBox.Address"),
                ToolTip = Strings.Settings_Tags_RenameTip,
                VerticalAlignment = VerticalAlignment.Center,
            };

            box.LostFocus += OnTagNameCommitted;
            box.KeyDown += OnTagNameKeyDown;

            if (preset.Slot == _focusSlot)
            {
                // 刚新建的那一行：把焦点放进去、全选默认名，用户直接打字就替换掉
                box.Loaded += (_, _) =>
                {
                    box.Focus();
                    box.SelectAll();
                };
                _focusSlot = -1;
            }

            var remove = new Button
            {
                Content = Strings.Settings_Tags_Remove,
                Tag = preset.Value,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("Button.Link"),
            };

            remove.Click += OnRemoveTag;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(swatch, 0);
            Grid.SetColumn(box, 1);
            Grid.SetColumn(remove, 2);

            row.Children.Add(swatch);
            row.Children.Add(box);
            row.Children.Add(remove);

            TagListPanel.Children.Add(row);
        }

        // 32 个槽位用尽：把按钮灰掉并说明原因，而不是点了没反应
        bool full = TagPresets.NextFreeSlot() < 0;
        TagAddButton.IsEnabled = !full;
        TagLimitHint.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 新建一个标签：占一个还空着的槽位，名字先留空（显示为"标签 9"这样的占位），
    /// 焦点直接落到输入框里，用户打完回车就是它了。
    /// </summary>
    private void OnAddTag(object sender, RoutedEventArgs e)
    {
        int slot = TagPresets.NextFreeSlot();
        if (slot < 0) return;

        Settings.Appearance.Tags.Add(new TagLabelSetting { Slot = slot });
        _focusSlot = slot;

        SaveTagDefinitions();
    }

    /// <summary>
    /// 删除标签：只从定义里移除，**不动 tags.jsonl**。
    ///
    /// 确认框必须写清"记录还在"：用户看到"删除"两个字时默认以为标记也没了，
    /// 而这里的行为是"不再显示、但重新加回同一个颜色就能原样恢复"。
    /// 两者预期差得太远，不说明就是欺骗。
    /// </summary>
    private void OnRemoveTag(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TagColors color }) return;

        // 只剩一个时不让删：删到零会让右键菜单里的「标签」变成一个空弹层，
        // 而空弹层比没有入口更难解释
        if (Settings.Appearance.Tags.Count <= 1)
        {
            StatusText.Text = Strings.Settings_Tags_KeepOne;
            return;
        }

        string name = TagPresets.NameOf(color);
        int count = App.Tags.IsLoaded ? App.Tags.OfColor(color).Count : 0;

        string body = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Settings_Tags_RemoveBody,
            name,
            count);

        if (!ConfirmWindow.Ask(this, Strings.Settings_Tags_RemoveTitle, body)) return;

        Settings.Appearance.Tags.RemoveAll(item => item.Slot == TagPalette.SlotOf(color));

        SaveTagDefinitions();
    }

    private void OnTagNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;

        CommitTagName(box);
        e.Handled = true;
    }

    private void OnTagNameCommitted(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitTagName(box);
    }

    /// <summary>
    /// 把输入框里的名字写回定义。与默认名相同就存 null：
    /// 这样"红色"没改过的标签，切成英文后显示 "Red"，而不是被固定成中文。
    /// </summary>
    private void CommitTagName(TextBox box)
    {
        if (box.Tag is not TagColors color) return;

        string typed = box.Text.Trim();
        string fallback = TagPresets.DefaultNameOf(color);
        int slot = TagPalette.SlotOf(color);

        TagLabelSetting? target = Settings.Appearance.Tags.FirstOrDefault(item => item.Slot == slot);
        if (target is null) return;

        string? stored = typed.Length == 0 || string.Equals(typed, fallback, StringComparison.Ordinal)
            ? null
            : typed;

        if (string.Equals(target.Name, stored, StringComparison.Ordinal)) return;

        target.Name = stored;
        SaveTagDefinitions();
    }

    /// <summary>定义改完的统一出口：落盘、重建列表、让主窗口跟新（侧栏、菜单、行内色点）。</summary>
    private void SaveTagDefinitions()
    {
        App.SaveSettings();

        BuildTagRows();

        // 侧栏的标签节点、右键菜单、行内色点都按定义算，改完一处就要全刷一遍
        Owner_?.RefreshTagNames();
    }

    // ── 标签库导入导出（FR-4.2.7 / FR-5.3.6）────────────────────────────

    private async void OnExportTags(object sender, RoutedEventArgs e)
    {
        App.Tags.EnsureLoaded();

        int count = App.Tags.Count;
        if (count == 0)
        {
            StatusText.Text = Strings.Settings_Privacy_TagStore_Empty;
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.Settings_Privacy_TagExport_Title,
            Filter = "JSON (*.json)|*.json",
            DefaultExt = ".json",
            FileName = "jianlan-tags.json",
        };

        if (dialog.ShowDialog(this) != true) return;

        string path = dialog.FileName;
        IReadOnlyList<TagRecord> snapshot = App.Tags.Snapshot();

        try
        {
            int written = await Task.Run(() => TagArchive.Write(snapshot, path)).ConfigureAwait(true);
            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture, Strings.Settings_Privacy_TagExported, written);
        }
        catch (TagArchiveException ex)
        {
            // 文件系统原文只进日志，界面给译文 + 原因（与文件操作同一口径）
            Diagnostics.AppLog.Warn("tags.export", "E3010", ex.Message);
            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture, Strings.Settings_Privacy_TagExportFailed, ex.Message);
        }
    }

    private async void OnImportTags(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.Settings_Privacy_TagImport_Title,
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        string path = dialog.FileName;

        TagImportResult result;
        try
        {
            List<TagRecord> records = await Task.Run(() => TagArchive.ToRecords(TagArchive.Read(path)))
                .ConfigureAwait(true);

            result = await Task.Run(() => App.Tags.Import(records)).ConfigureAwait(true);
        }
        catch (TagArchiveException ex)
        {
            Diagnostics.AppLog.Warn("tags.import", "E3011", ex.Message);
            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture, Strings.Settings_Privacy_TagImportFailed, ex.Message);
            return;
        }

        StatusText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Settings_Privacy_TagImported,
            result.Total, result.Added, result.Updated, result.Skipped);

        await RefreshTagCountAsync();
    }

    // ── 列显示（FR-6.4）────────────────────────────────────────────────────
    //
    // 设置页改的是**默认档**：列配置按目录类型分档，而设置页没有"当前目录"这个上下文。
    // 在列头上改则只影响眼前这个档。提示语里写清楚这一点，否则用户会以为
    // "在这里取消勾选，图片目录也该跟着变"。

    private void LoadColumnChecks()
    {
        ColumnProfileSettings columns = Settings.Layout.Standard;
        ColumnDateCheck.IsChecked = columns.ShowDate;
        ColumnTypeCheck.IsChecked = columns.ShowType;
        ColumnSizeCheck.IsChecked = columns.ShowSize;
    }

    private void OnColumnVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        ColumnProfileSettings columns = Settings.Layout.Standard;
        columns.ShowDate = ColumnDateCheck.IsChecked == true;
        columns.ShowType = ColumnTypeCheck.IsChecked == true;
        columns.ShowSize = ColumnSizeCheck.IsChecked == true;

        Owner_?.ApplySettingsToUi();
        App.SaveSettings();
    }

    private void OnResetColumns(object sender, RoutedEventArgs e)
    {
        // 重置所有档位而不只是默认档：这个按钮存在的理由就是"我把列弄乱了，全部回原样"，
        // 只清默认档会留下几个还保持原样的目录类型，反而更难收拾。
        Settings.ResetLayout();

        _loading = true;
        LoadColumnChecks();
        _loading = false;

        Owner_?.ApplySettingsToUi();
        App.SaveSettings();

        StatusText.Text = Strings.Settings_ResetLayout_Done;
    }

    /// <summary>关闭最后一个标签时的行为（FR-2.8）。下一次关标签就生效，不用重启。</summary>
    private void OnCloseLastToHomeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.CloseLastTabToHome = CloseLastToHomeCheck.IsChecked == true;
        App.SaveSettings();
    }

    /// <summary>
    /// 打开方式（FR-5.2.5）。切换后列表的悬停高亮也随之改变行为：
    /// 单击模式下推迟 600ms 出现，见 PaneView.Hover.cs。
    /// </summary>
    private void OnOpenModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.DoubleClickOpens = OpenModeCombo.SelectedIndex == 0;
        App.SaveSettings();
    }

    // ── 启动（FR-5.2.9）─────────────────────────────────────────────────────
    //
    // 两项互斥：设了工作区就置 false 会话恢复，反之亦然。归一化里也有同一条规则
    // （AppSettings.Normalize），这里做的是"让界面当场跟上"，否则用户看到两个都开着，
    // 重启后只生效一个。

    private void LoadStartupChoices()
    {
        var choices = new List<WorkspaceChoice>
        {
            new(null, Strings.Settings_Behavior_Startup_Workspace_None),
        };

        foreach (var workspace in App.Workspaces.Items)
        {
            choices.Add(new WorkspaceChoice(workspace.Name, workspace.Name));
        }

        StartupWorkspaceCombo.ItemsSource = choices;

        int index = Settings.Behavior.StartupWorkspace is { } name
            ? choices.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            : 0;

        // 工作区可能已被删掉：落到"不设置"，同时把设置里那条失效引用清掉
        if (index < 0)
        {
            index = 0;
            Settings.Behavior.StartupWorkspace = null;
        }

        StartupWorkspaceCombo.SelectedIndex = index;
        StartupCombo.SelectedIndex = Settings.Behavior.RestoreLastSession ? 1 : 0;
    }

    private void OnStartupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        bool restore = StartupCombo.SelectedIndex == 1;
        Settings.Behavior.RestoreLastSession = restore;

        if (restore)
        {
            // 选"恢复上次会话"就是放弃固定工作区，否则两条恢复路径会一起跑
            Settings.Behavior.StartupWorkspace = null;

            _loading = true;
            StartupWorkspaceCombo.SelectedIndex = 0;
            _loading = false;
        }

        App.SaveSettings();
    }

    private void OnStartupWorkspaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (StartupWorkspaceCombo.SelectedItem is not WorkspaceChoice choice) return;

        Settings.Behavior.StartupWorkspace = choice.Name;

        if (choice.Name is not null)
        {
            Settings.Behavior.RestoreLastSession = false;

            _loading = true;
            StartupCombo.SelectedIndex = 0;
            _loading = false;
        }

        App.SaveSettings();
    }

    /// <summary>工作区下拉的一项。<c>Name</c> 为 null 表示"不设置"。</summary>
    private sealed record WorkspaceChoice(string? Name, string DisplayName);

    // ── 新标签页默认目录（FR-2.1）─────────────────────────────────────────
    //
    // 输入框里显示的是"当前实际生效的目录"：设置项为空时它就是系统「下载」，
    // 这样用户不用猜"空着到底会开到哪"。但空串有独立含义（跟随系统下载），
    // 所以只有用户真的改过（_dirty）才写盘，否则打开一次设置页就会把
    // "跟随系统"变成一条写死的路径。

    private bool _newTabPathDirty;

    private void OnNewTabPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        _newTabPathDirty = true;
        UpdateNewTabPathHint();
    }

    /// <summary>离开输入框或按回车才写盘：逐字保存等于每敲一个键写一次文件。</summary>
    private void OnNewTabPathCommitted(object sender, RoutedEventArgs e) => CommitNewTabPath();

    private void OnNewTabPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        CommitNewTabPath();
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // 点右上角关闭时输入框未必失焦，这里补一次
        CommitNewTabPath();
        base.OnClosed(e);
    }

    private void CommitNewTabPath()
    {
        if (!_newTabPathDirty) return;
        _newTabPathDirty = false;

        string text = NewTabPathBox.Text.Trim();

        // 空串存 null：留一个 "" 在设置文件里，读的人还得再判一次
        Settings.General.NewTabPath = text.Length == 0 ? null : text;
        App.SaveSettings();
        UpdateNewTabPathHint();
    }

    /// <summary>
    /// 提示行同时承担校验：目录不存在时说清楚"会回落到下载夹"，
    /// 而不是等到新标签页打开失败才让用户发现。
    /// </summary>
    private void UpdateNewTabPathHint()
    {
        string text = NewTabPathBox.Text.Trim();

        NewTabPathHint.Text = text.Length > 0 && !Directory.Exists(text)
            ? Strings.Settings_Tabs_NewTabPath_Missing
            : Strings.Settings_Tabs_NewTabPath_Hint;
    }

    private void OnBrowseNewTabPath(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Strings.Settings_Tabs_NewTabPath,
            Multiselect = false,
        };

        string current = NewTabPathBox.Text.Trim();
        dialog.InitialDirectory = Directory.Exists(current) ? current : MainWindow.ResolveNewTabPath();

        if (dialog.ShowDialog(this) != true) return;

        NewTabPathBox.Text = dialog.FolderName;
        CommitNewTabPath();
    }

    /// <summary>FR-2.10 的加强版：双击/回车进目录改成开标签。默认关，见 AppSettings 里的说明。</summary>
    private void OnOpenFolderInNewTabChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.OpenFolderInNewTab = OpenFolderInNewTabCheck.IsChecked == true;
        App.SaveSettings();
    }

    /// <summary>中键 / Ctrl+左键开出的新标签是否留在后台（FR-2.10 的可选项）。</summary>
    private void OnOpenNewTabInBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.OpenNewTabInBackground = BackgroundTabCheck.IsChecked == true;
        App.SaveSettings();
    }

    // ── 隐私 ────────────────────────────────────────────────────────────────

    private void OnRememberPathChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Settings.Privacy.HistoryEnabled = RememberPathCheck.IsChecked == true;
        if (!Settings.Privacy.HistoryEnabled)
        {
            // 关掉就立刻清空，而不是留着旧值等下次覆盖（FR-5.3.3）
            Settings.Session.LastPath = null;
        }

        App.SaveSettings();
    }

    private void OnHistoryLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        int index = Math.Clamp(HistoryLimitCombo.SelectedIndex, 0, HistoryLimits.Length - 1);
        Settings.Privacy.HistoryLimit = HistoryLimits[index];
        App.SaveSettings();

        // 上限调小要立即生效，否则"改成 50 条"之后文件里还留着 200 条，
        // 下次启动才被裁剪——用户会以为设置没生效。
        Owner_?.TrimHistory();
    }

    /// <summary>
    /// 一键清空历史（FR-5.3.2）。只清"最近访问"，不动会话、收藏与工作区：
    /// 三者的清空入口各自独立，一个按钮把它们一起删掉是最容易被当成数据丢失的一类行为。
    /// </summary>
    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        int removed = Owner_?.ClearHistory() ?? 0;
        StatusText.Text = string.Format(
            CultureInfo.CurrentCulture, Strings.Settings_Privacy_HistoryCleared, removed);
    }

    /// <summary>
    /// 清理标签库里的失效项（FR-4.2.6 / FR-5.3.6）。
    /// 每条一次文件系统查询，放在后台跑；破坏面（离线磁盘上的标记也会被清）
    /// 必须在动手之前说清楚，不能藏在"清理垃圾"的说法后面。
    /// </summary>
    private async void OnPruneTags(object sender, RoutedEventArgs e)
    {
        int total = await Task.Run(() =>
        {
            App.Tags.EnsureLoaded();
            return App.Tags.Count;
        }).ConfigureAwait(true);

        if (total == 0)
        {
            StatusText.Text = Strings.Settings_Privacy_TagStore_Empty;
            return;
        }

        if (!ConfirmWindow.Ask(this, Strings.Dialog_TagPrune_Title,
                string.Format(CultureInfo.CurrentCulture, Strings.Dialog_TagPrune_Body, total)))
        {
            return;
        }

        StatusText.Text = Strings.Settings_Privacy_TagStore_Pruning;

        int removed = await Task.Run(() => App.Tags.PruneStale()).ConfigureAwait(true);

        StatusText.Text = removed > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Settings_Privacy_TagStore_Pruned, removed)
            : Strings.Settings_Privacy_TagStore_PruneNone;

        await RefreshTagCountAsync();
    }

    /// <summary>标签库条数。库可能还没加载，所以先 EnsureLoaded 再读。</summary>
    private async Task RefreshTagCountAsync()
    {
        int count = await Task.Run(() =>
        {
            App.Tags.EnsureLoaded();
            return App.Tags.Count;
        }).ConfigureAwait(true);

        TagCountText.Text = string.Format(
            CultureInfo.CurrentCulture, Strings.Settings_Privacy_TagStore_Count, count);
    }

    // ── 缩略图缓存（FR-5.3.5）──────────────────────────────────────────────

    /// <summary>
    /// 统计缓存占用。遍历整个缓存目录（上万个小文件），必须在后台跑：
    /// 即便目录元数据多半已在系统缓存里，也是几十到几百毫秒的量级，
    /// 放 UI 线程上就是"打开设置页卡一下"。
    /// </summary>
    private async Task RefreshThumbCacheSizeAsync()
    {
        ThumbCacheText.Text = Strings.Settings_Privacy_ThumbCache_Measuring;

        long bytes = await Task.Run(ThumbnailCache.Measure).ConfigureAwait(true);

        UpdateThumbCacheText(bytes);
    }

    /// <summary>
    /// 占用行同时写出"用了多少"与"上限多少"。上限取设置里的真实值而不是下拉框选中项：
    /// 手改过 json 的非标准值会让两者不同，这时候宁可让数字准。
    /// </summary>
    private void UpdateThumbCacheText(long bytes) =>
        ThumbCacheText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Settings_Privacy_ThumbCache_Size,
            FileEntry.FormatSizeText(bytes),
            FileEntry.FormatSizeText(Settings.Privacy.CacheLimitMb * 1024L * 1024));

    /// <summary>
    /// 改上限（FR-5.3.5）。除了写设置，还要立刻按新上限清理一次：
    /// 把 2048 改成 128 之后磁盘上仍占着 2GB、等下一次攒满 200 次写入才动，
    /// 用户会当成"设置没生效"。
    /// </summary>
    private async void OnCacheLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        int index = Math.Clamp(CacheLimitCombo.SelectedIndex, 0, CacheLimits.Length - 1);
        int megabytes = CacheLimits[index];

        Settings.Privacy.CacheLimitMb = megabytes;
        App.SaveSettings();

        long cap = megabytes * 1024L * 1024;
        ThumbnailCache.CapBytes = cap;

        long bytes = await Task.Run(() =>
        {
            ThumbnailCache.Trim(cap);
            return ThumbnailCache.Measure();
        }).ConfigureAwait(true);

        UpdateThumbCacheText(bytes);

        StatusText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Settings_Privacy_ThumbCache_LimitApplied,
            FileEntry.FormatSizeText(cap),
            FileEntry.FormatSizeText(bytes));
    }

    /// <summary>
    /// 一键清除（FR-5.3.5）。先算一次占用是为了把"将删除多少"写进确认框，
    /// 也为了事后能说出"释放了多少"——只说"已清除"，用户无法判断这条命令到底有没有做。
    ///
    /// 删的只是缩略图，原文件不受影响；这一点必须写在确认框里，
    /// 否则"清除缓存"在文件管理器里听起来像会动文件。
    /// </summary>
    private async void OnClearThumbCache(object sender, RoutedEventArgs e)
    {
        long before = await Task.Run(ThumbnailCache.Measure).ConfigureAwait(true);

        if (before == 0)
        {
            UpdateThumbCacheText(0);
            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture,
                Strings.Settings_Privacy_ThumbCache_Cleared,
                FileEntry.FormatSizeText(0));
            return;
        }

        if (!ConfirmWindow.Ask(
                this,
                Strings.Settings_Privacy_ThumbCache_ClearConfirm_Title,
                string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Settings_Privacy_ThumbCache_ClearConfirm_Body,
                    FileEntry.FormatSizeText(before))))
        {
            return;
        }

        long after = await Task.Run(() =>
        {
            ThumbnailCache.Clear();
            return ThumbnailCache.Measure();
        }).ConfigureAwait(true);

        UpdateThumbCacheText(after);

        // Clear 吞掉所有 IO 异常（缓存清不掉不该影响任何可见功能），所以"清没清干净"
        // 只能靠清完再量一次来判断。剩下的是正被占用的文件，换个说法比报"失败"准确。
        StatusText.Text = after > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                Strings.Settings_Privacy_ThumbCache_ClearBlocked,
                FileEntry.FormatSizeText(after))
            : string.Format(
                CultureInfo.CurrentCulture,
                Strings.Settings_Privacy_ThumbCache_Cleared,
                FileEntry.FormatSizeText(before));
    }

    // ── 其他 ────────────────────────────────────────────────────────────────

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // 异常原文跟随系统语言，不进界面（见 FsErrors）
            string code = FsErrors.CodeOf(ex);
            Diagnostics.AppLog.Warn("settings.openDataDir", code, $"{ex.GetType().Name}: {ex.Message}");

            ConfirmWindow.Notify(
                this,
                Strings.Application_Name,
                string.Format(CultureInfo.CurrentCulture, Strings.Settings_OpenDataDir_Failed,
                    FsErrors.DescribeForOperation(code)));
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
