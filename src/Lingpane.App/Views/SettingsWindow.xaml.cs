using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingpane.App.Design;
using Lingpane.App.Typography;
using Lingpane.Core.Diagnostics;
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

        AboutText.Text =
            $"Lingpane 预览版（M1）\n" +
            $"运行时：.NET {Environment.Version}\n" +
            $"数据目录：{AppPaths.Root}\n" +
            $"开源许可：Apache-2.0";

        Loaded += async (_, _) => await LoadFontsAsync();
    }

    private AppSettings Settings => App.Settings;

    private MainWindow? Owner_ => Owner as MainWindow;

    // ── 初始化 ──────────────────────────────────────────────────────────────

    private void LoadSettingsIntoUi()
    {
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

        ShowNavCheck.IsChecked = Settings.Appearance.ShowNavPane;
        ShowStatusCheck.IsChecked = Settings.Appearance.ShowStatusBar;
        RememberPathCheck.IsChecked = Settings.Privacy.HistoryEnabled;

        FollowSystemFontCheck.IsChecked = Settings.Appearance.FontSizePt is null;
        UpdateFontSizeText();
    }

    private async Task LoadFontsAsync()
    {
        _fonts = await FontCatalog.LoadAsync();

        // 第一项固定为"默认字体"，让用户能显式回到默认栈
        var options = new List<FontOption> { new("（默认字体）", null!) };
        options.AddRange(_fonts);

        FontCombo.ItemsSource = options;

        FontFamily? saved = FontCatalog.Resolve(_fonts, Settings.Appearance.FontFamily);
        if (saved is null)
        {
            FontCombo.SelectedIndex = 0;
            if (!string.IsNullOrEmpty(Settings.Appearance.FontFamily))
            {
                // FONT-03：换机器后字体可能不存在，回落默认但要让用户知道
                FontHint.Text = $"原字体「{Settings.Appearance.FontFamily}」不可用，已回落默认字体";
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
            : $"跟随系统（{FontScaleService.Current.Points:0} pt）";
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
        FontHint.Text = "已恢复系统默认字体与字号";
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

    // ── 行为 ────────────────────────────────────────────────────────────────

    private void OnDefaultViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        // 界面只提供两种视图，Grid 在 M1 未实现（见 MainViewModel.SetViewMode）
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
        FontHint.Text = "已清空记录的路径";
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
            MessageBox.Show(this, $"无法打开数据目录：{ex.Message}", "Lingpane", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
