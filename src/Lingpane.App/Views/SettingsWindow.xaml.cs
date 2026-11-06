using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lingpane.App.Design;
using Lingpane.App.Typography;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Localization;
using Lingpane.Core.Settings;

namespace Lingpane.App.Views;

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

    public SettingsWindow()
    {
        InitializeComponent();

        LoadSettingsIntoUi();
        _loading = false;

        AboutText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.Settings_About_Content,
            Environment.Version,
            AppPaths.Root);

        Loaded += async (_, _) => await LoadFontsAsync();
    }

    private AppSettings Settings => App.Settings;

    private MainWindow? Owner_ => Owner as MainWindow;

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

        ViewCombo.SelectedIndex = Settings.Behavior.DefaultView == DefaultViewKind.List ? 1 : 0;

        int sortIndex = Array.IndexOf(SortOrder, Settings.Behavior.SortBy);
        SortCombo.SelectedIndex = sortIndex >= 0 ? sortIndex : 0;

        DescendingCheck.IsChecked = Settings.Behavior.SortDescending;
        FoldersFirstCheck.IsChecked = Settings.Behavior.FoldersFirst;
        ShowHiddenCheck.IsChecked = Settings.Behavior.ShowHidden;
        ShowExtensionsCheck.IsChecked = Settings.Behavior.ShowExtensions;
        ConfirmDeleteCheck.IsChecked = Settings.Behavior.ConfirmDelete;
        OpenFolderInNewTabCheck.IsChecked = Settings.Behavior.OpenFolderInNewTab;
        BackgroundTabCheck.IsChecked = Settings.Behavior.OpenNewTabInBackground;

        TabStripCombo.SelectedIndex =
            Settings.Appearance.TabStripPosition == AppearanceSettings.TabBottom ? 1 : 0;

        // 空 = 用系统「下载」：把这个"当前实际生效的目录"直接显示出来，
        // 否则输入框空着，用户没法判断现在到底会开到哪
        NewTabPathBox.Text = Settings.General.NewTabPath ?? MainWindow.ResolveNewTabPath();
        UpdateNewTabPathHint();

        ShowNavCheck.IsChecked = Settings.Appearance.ShowNavPane;
        ShowStatusCheck.IsChecked = Settings.Appearance.ShowStatusBar;
        RememberPathCheck.IsChecked = Settings.Privacy.HistoryEnabled;

        FollowSystemFontCheck.IsChecked = Settings.Appearance.FontSizePt is null;
        UpdateFontSizeText();
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

    private void OnResetAppearance(object sender, RoutedEventArgs e)
    {
        Settings.Appearance.FontFamily = null;
        Settings.Appearance.FontSizePt = null;
        Settings.Appearance.Density = DensityKind.Comfortable;

        _loading = true;
        FontCombo.SelectedIndex = 0;
        DensityCombo.SelectedIndex = 0;
        FollowSystemFontCheck.IsChecked = true;
        _loading = false;

        FontScaleService.ResetToDefault();
        UpdateFontSizeText();
        FontHint.Text = Strings.Settings_ResetAppearance_Done;
        App.SaveSettings();
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
        App.SaveSettings();
    }

    private void OnShowHiddenChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Behavior.ShowHidden = ShowHiddenCheck.IsChecked == true;
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

    private void OnClearRecentPath(object sender, RoutedEventArgs e)
    {
        Settings.Session.LastPath = null;
        App.SaveSettings();
        FontHint.Text = Strings.Settings_Privacy_Cleared;
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
