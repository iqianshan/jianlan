using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lingpane.App.Services;
using Lingpane.App.Typography;
using Lingpane.App.ViewModels;
using Lingpane.Core.Navigation;
using Lingpane.Core.Settings;

namespace Lingpane.App.Views;

/// <summary>
/// 列显示配置（FR-6.4）：四个数据列的显隐、顺序、宽度，以及"按目录类型记忆"。
///
/// 为什么不把列定义做成真正的数据驱动 ItemsControl：行模板要虚拟化到万项，
/// 每行再套一层 ItemsControl 就等于每行多一个面板与四个容器，
/// 实测这条路径比直接写死 Grid 慢一个数量级（见 docs/perf 的万项门槛）。
/// 所以这里的"数据驱动"落在资源令牌上：布局结构写死，槽位/宽度/显隐从配置算出后
/// 写进窗格自己的资源字典，DynamicResource 会推动列头与所有可见行一起改。
///
/// 令牌写在窗格级而不是应用级：双窗格可以同时显示两个不同类型的目录
/// （左边图片、右边代码），应用级令牌只能有一份，两边必然互相覆盖。
/// </summary>
public partial class PaneView
{
    /// <summary>当前生效的列配置档位。路径变化时由 <see cref="SyncColumnProfile"/> 重算。</summary>
    private ColumnProfileKind _columnKind = ColumnProfileKind.Default;

    /// <summary>右键落在哪一列上（名称列为 null）。菜单打开时按它决定"左右移"是否可用。</summary>
    private PaneColumn? _menuColumn;

    /// <summary>任一窗格改了列配置，其余窗格跟着重算——否则双窗格看同类型目录会不一样。</summary>
    private static event Action? ColumnSettingsChanged;

    private void InitializeColumns()
    {
        ApplyColumns();
        FontScaleService.Changed += OnFontScaleChanged;
        ColumnSettingsChanged += OnColumnSettingsChanged;
    }

    private void ReleaseColumns()
    {
        FontScaleService.Changed -= OnFontScaleChanged;
        ColumnSettingsChanged -= OnColumnSettingsChanged;
    }

    private void OnFontScaleChanged(object? sender, FontMetrics metrics) => ApplyColumns();

    private void OnColumnSettingsChanged() => ApplyColumns();

    /// <summary>路径一变就重判档位（导航、会话恢复、切标签都会走到这里）。</summary>
    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneViewModel.CurrentPath)) return;
        SyncColumnProfile((sender as PaneViewModel)?.CurrentPath);
    }

    /// <summary>
    /// 导航后重新判定档位。按路径而不是按"是否已知文件夹"判，
    /// 因为子目录（相册、下载里的子文件夹）应当沿用父类的配置。
    /// </summary>
    private void SyncColumnProfile(string? path)
    {
        ColumnProfileKind kind = ColumnProfiles.Resolve(path, KnownFolderService.All);
        if (kind == _columnKind) return;

        _columnKind = kind;
        ApplyColumns();
    }

    private ColumnProfileSettings CurrentColumns => App.Settings.Layout.Get(_columnKind);

    // ── 应用配置 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 把当前档位的配置写进窗格资源。三步：算槽位（顺序）、算显隐、算宽度（含窄窗格收缩）。
    ///
    /// 槽位与显隐无关：隐藏列也占一个槽位，只是元素 Collapsed、列定义 Auto 塌缩。
    /// 若让隐藏列让出槽位，可见列的槽位会随显隐变化，DynamicResource 就要
    /// 在"隐藏一列"时改三个键，出错的机会多一倍。
    /// </summary>
    private void ApplyColumns(double? paneWidth = null)
    {
        ColumnProfileSettings profile = CurrentColumns;
        double scale = FontScaleService.Current.Scale;
        double width = paneWidth ?? ActualWidth;

        double[] widths =
        [
            profile.DateWidth * scale,
            profile.TypeWidth * scale,
            profile.SizeWidth * scale,
        ];

        double[] defaults =
        [
            AppSettings.DefaultColumnDateWidth * scale,
            AppSettings.DefaultColumnTypeWidth * scale,
            AppSettings.DefaultColumnSizeWidth * scale,
        ];

        bool[] shown = [profile.ShowDate, profile.ShowType, profile.ShowSize];

        ApplyResponsiveShrink(widths, defaults, shown, width);

        PaneColumn[] columns = [PaneColumn.Date, PaneColumn.Type, PaneColumn.Size];
        for (int i = 0; i < columns.Length; i++)
        {
            string prefix = ColumnPrefix(columns[i]);

            Resources[$"Column.{prefix}.Slot"] = profile.SlotOf(columns[i]);
            Resources[$"Column.{prefix}.Visible"] = shown[i] ? Visibility.Visible : Visibility.Collapsed;
            Resources[$"Column.{prefix}.Width"] = Math.Round(widths[i], 1);
        }

        if (MainWindow.KeyDebug)
        {
            MainWindow.WriteKeyLog(
                $"columns profile={_columnKind} pane={width:0} " +
                $"shown={shown[0]}/{shown[1]}/{shown[2]} " +
                $"width={widths[0]:0}/{widths[1]:0}/{widths[2]:0} order={string.Join(',', profile.Order)}");
        }
    }

    /// <summary>
    /// 窗格窄到放不下时的收缩顺序：**先缩回默认宽度，再按 大小 → 类型 → 日期 折叠**
    /// （ui-guidelines A-05：先扩列宽，再折叠列，最后才允许省略）。
    ///
    /// 双窗格把这个问题的出现门槛从"窗口很窄"变成了"一直"：1100px 窗口分屏后每栏约 460px，
    /// 三列按用户拖过的宽度（比如 220+156+113）会把名称列挤成 0——文件名整个看不见。
    ///
    /// 为什么先缩再折：用户把日期列拖到 220 是为了看完整时间戳，但"看完整时间戳"的
    /// 优先级低于"看得见文件名"；缩回默认 110 还能保住这一列，直接折掉就一点信息都没了。
    ///
    /// 折叠只影响本次显示，不动配置：拉宽窗口列就回来，用户设置里那份宽度也没被改写。
    /// </summary>
    private static void ApplyResponsiveShrink(double[] widths, double[] defaults, bool[] shown, double paneWidth)
    {
        if (paneWidth <= 0 || double.IsNaN(paneWidth)) return;

        // 名称列至少要留这么多，否则不如把这列折掉换给文件名
        double nameMinWidth = MinimumNameWidth();
        const double Padding = 44;          // 列头左右各 8 + 行内边距 + 图标槽位

        double available = paneWidth - Padding;

        double Fixed() => shown.Select((on, i) => on ? widths[i] : 0).Sum();

        // ① 缩：把"超过默认值"的列逐个缩回默认，缩到名称列放得下为止
        while (Fixed() > available - nameMinWidth)
        {
            int widest = -1;
            for (int i = 0; i < widths.Length; i++)
            {
                if (!shown[i] || widths[i] <= defaults[i]) continue;
                if (widest < 0 || widths[i] - defaults[i] > widths[widest] - defaults[widest]) widest = i;
            }

            if (widest < 0) break;
            widths[widest] = defaults[widest];
        }

        // ② 折：从最不重要的列开始丢，日期留到最后
        foreach (int i in new[] { 2, 1, 0 })
        {
            if (Fixed() <= available - nameMinWidth) break;
            shown[i] = false;
        }
    }

    /// <summary>名称列的最小可视宽度，跟着字号走（24pt 下 160px 只装得下五个字）。</summary>
    private static double MinimumNameWidth()
    {
        return Application.Current?.TryFindResource("Column.Name.MinWidth") is double value && value > 0
            ? value
            : 160d;
    }

    private static string ColumnPrefix(PaneColumn column) => column switch
    {
        PaneColumn.Date => "Date",
        PaneColumn.Type => "Type",
        PaneColumn.Size => "Size",
        _ => "Name",
    };

    private void ApplyColumnWidths() => ApplyColumns();

    // ── 列宽拖拽 ────────────────────────────────────────────────────────────
    //
    // 拖动改的是**逻辑宽度**（设置里那份），资源里放的是逻辑宽度 × 字号缩放。
    // 若直接把显示宽度写回设置，用户在 150% 缩放下拖一次，换回 100% 就变成 1.5 倍宽。

    private void OnColumnResize(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string key }) return;
        if (!ColumnLayout.TryParseKey(key, out PaneColumn column) || column == PaneColumn.Name) return;

        double scale = FontScaleService.Current.Scale;
        if (scale <= 0) scale = 1;

        ColumnProfileSettings profile = App.Settings.Layout.GetOrAdd(_columnKind);
        double current = profile.WidthOf(column) * scale;

        // 把手挂在列的左边缘：向右拖（HorizontalChange > 0）= 把分隔线右推，
        // 本列变窄、左邻列变宽；向左拖则相反。取反后分隔线始终跟着鼠标，
        // 而不是"列宽变了、边界却不动"。
        double next = current - e.HorizontalChange;

        // 只有本列变宽时受限：最多吃掉名称列让出的富余宽度，避免溢出
        if (next > current)
        {
            double nameSlack = HeaderGrid.ColumnDefinitions[ColumnLayout.NameSlot].ActualWidth - MinimumNameWidth();
            next = Math.Min(next, current + Math.Max(0, nameSlack));
        }

        next = Math.Clamp(next, AppSettings.MinColumnWidth * scale, AppSettings.MaxColumnWidth * scale);

        if (Math.Abs(next - current) < 0.5) return;

        profile.SetWidth(column, next / scale);
        Resources[$"Column.{ColumnPrefix(column)}.Width"] = Math.Round(next, 1);
    }

    private void OnColumnResizeDone(object sender, DragCompletedEventArgs e)
    {
        if (sender is not Thumb thumb) return;

        // 一次拖拽只落盘一次，不跟随每个 DragDelta 写文件
        App.SaveSettings();
        ColumnSettingsChanged?.Invoke();

        if (MainWindow.KeyDebug)
        {
            ColumnProfileSettings p = App.Settings.Layout.Get(_columnKind);
            MainWindow.WriteKeyLog($"columns done {thumb.Tag} profile={_columnKind} delta={e.HorizontalChange:0.#} " +
                        $"out={p.DateWidth:0.#}/{p.TypeWidth:0.#}/{p.SizeWidth:0.#}");
        }
    }

    // ── 列头右键菜单（FR-6.4 的界面入口之一）────────────────────────────────

    private void OnColumnHeaderRightDown(object sender, MouseButtonEventArgs e)
    {
        _menuColumn = HitTestColumn(e.OriginalSource as DependencyObject);
    }

    private void OnColumnMenuOpened(object sender, RoutedEventArgs e)
    {
        ColumnProfileSettings profile = CurrentColumns;

        ColumnNameItem.IsChecked = true;
        ColumnDateItem.IsChecked = profile.ShowDate;
        ColumnTypeItem.IsChecked = profile.ShowType;
        ColumnSizeItem.IsChecked = profile.ShowSize;

        bool movable = _menuColumn is { } column && column != PaneColumn.Name;
        int index = movable ? profile.Order.IndexOf(ColumnLayout.KeyOf(_menuColumn!.Value)) : -1;

        ColumnMoveLeftItem.IsEnabled = movable && index > 0;
        ColumnMoveRightItem.IsEnabled = movable && index >= 0 && index < profile.Order.Count - 1;
    }

    /// <summary>
    /// 从点击位置往上找带列名的元素。列头 Button 的模板里有 ContentPresenter 等三层，
    /// 点到的是模板内部元素，不是 Button 自己。
    /// </summary>
    private static PaneColumn? HitTestColumn(DependencyObject? node)
    {
        for (int depth = 0; node is not null && depth < 10; depth++)
        {
            if (node is FrameworkElement { Tag: string key }
                && ColumnLayout.TryParseKey(key, out PaneColumn column)
                && column != PaneColumn.Name)
            {
                return column;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : null;
        }

        return null;
    }

    private void OnToggleColumn(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        if (!ColumnLayout.TryParseKey(key, out PaneColumn column)) return;
        if (column == PaneColumn.Name) return;

        ColumnProfileSettings profile = App.Settings.Layout.GetOrAdd(_columnKind);

        // 三列全关是允许的：剩下的就是文件名，那是"列表视图"，不是坏配置。
        profile.SetVisible(column, !profile.IsVisible(column));

        ApplyColumns();
        App.SaveSettings();
        ColumnSettingsChanged?.Invoke();
    }

    private void OnMoveColumn(object sender, RoutedEventArgs e)
    {
        if (_menuColumn is not { } column) return;
        if (sender is not MenuItem { Tag: string delta }) return;

        ColumnProfileSettings profile = App.Settings.Layout.GetOrAdd(_columnKind);
        if (!profile.Move(column, delta == "1" ? 1 : -1)) return;

        ApplyColumns();
        App.SaveSettings();
        ColumnSettingsChanged?.Invoke();
    }

    private void OnResetColumns(object sender, RoutedEventArgs e)
    {
        App.Settings.Layout.GetOrAdd(_columnKind).Reset();

        ApplyColumns();
        App.SaveSettings();
        ColumnSettingsChanged?.Invoke();
    }
}
