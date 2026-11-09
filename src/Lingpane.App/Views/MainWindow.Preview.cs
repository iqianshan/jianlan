using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Lingpane.App.ViewModels;
using Lingpane.Core.Commands;
using Lingpane.Core.Localization;
using Lingpane.Core.Settings;

namespace Lingpane.App.Views;

/// <summary>
/// 预览窗格（FR-9.1）的窗口侧接线。
///
/// 预览是**窗口级**的一栏，不是窗格级的：分屏时两个窗格共用一个预览，
/// 它永远反映焦点窗格的选中项——用户在右窗格点文件、预览就该换，而不是各给一份。
/// 因此宽度、开关、分隔条都挂在窗口这一层，与分屏（FR-3）同层。
/// </summary>
public partial class MainWindow
{
    private readonly PreviewViewModel _preview = new();

    /// <summary>
    /// 用户"想把预览打开"的意图。它与"此刻是否真的显示"不是一回事：
    /// 主页与标签结果页没有可预览的选中项，那两种形态下一律收起，
    /// 但切回目录标签要自动回来——把意图和显示混成一个字段，切一次标签就得重新按一遍 Alt+P。
    /// </summary>
    private bool _previewOpen;

    private void InitializePreview()
    {
        _previewOpen = App.Settings.Appearance.ShowPreview;
        _preview.AutoPreview = App.Settings.Behavior.PreviewAutoOpen;
        Preview.Attach(_preview);

        // 选中变化由窗格自己发（含"清空"与"恢复上次选中"两种来源），窗口只负责转交。
        // 不在窗口里挂每个 PaneViewModel 的属性通知：窗格会随标签被换掉，
        // 那份订阅要在四处解绑，漏一处就是"切标签后预览不更新"。
        Pane.SelectionChanged += OnPaneSelectionChanged;
        Pane2.SelectionChanged += OnPaneSelectionChanged;
    }

    private void OnPaneSelectionChanged(PaneView view)
    {
        // 只有焦点窗格的选中项算数：另一个窗格里的选中是"上次点过"，不是"我此刻在看"
        if (!ReferenceEquals(view, ActiveView)) return;

        RefreshPreview();
    }

    /// <summary>把焦点窗格的当前选中项交给预览窗格。选中为空即回到空态。</summary>
    private void RefreshPreview()
    {
        // 关着的时候不取内容也不做清理：一次预览要读盘/解码，看不见的结果不该花钱；
        // 而清理已经在 TogglePreview 关掉的那一刻做过一次了。
        if (!_previewOpen) return;

        _preview.Update(Vm.SelectedEntries);
    }

    /// <summary>
    /// 按"用户意图 + 当前标签形态"重排预览那一栏。
    ///
    /// 两列宽度一起改：只把窗格藏起来而留着分隔条，会看到一条贴在窗口右缘的竖线，
    /// 拖动它还能把看不见的窗格拉宽。
    /// </summary>
    private void ApplyPreviewLayout()
    {
        bool visible = _previewOpen && Tab.Kind == TabKind.Files;

        if (!visible)
        {
            PreviewSplitterColumn.Width = new GridLength(0);
            PreviewColumn.Width = new GridLength(0);
            PreviewSplitter.Visibility = Visibility.Collapsed;
            Preview.Visibility = Visibility.Collapsed;
            return;
        }

        double width = Math.Clamp(
            App.Settings.Layout.PreviewWidth, AppSettings.MinPreviewWidth, AppSettings.MaxPreviewWidth);

        double gutter = Application.Current?.TryFindResource("Size.Splitter.Thickness") is double value && value > 0
            ? value
            : 6;

        PreviewSplitterColumn.Width = new GridLength(gutter);
        PreviewColumn.Width = new GridLength(width);
        PreviewSplitter.Visibility = Visibility.Visible;
        Preview.Visibility = Visibility.Visible;
    }

    /// <summary>Alt+P / Ctrl+Shift+P：开关预览（FR-9.1.1）。</summary>
    private void TogglePreview()
    {
        _previewOpen = !_previewOpen;
        App.Settings.Appearance.ShowPreview = _previewOpen;

        ApplyPreviewLayout();

        if (_previewOpen)
        {
            RefreshPreview();
        }
        else
        {
            // 关掉立即释放 Handler 与位图（FR-9.2.6）。
            // 不做这一步的话，"关了预览内存却没降"会在性能走查里变成一个查不出来的账。
            _preview.Release();
        }

        App.SaveSettings();
    }

    /// <summary>拖完分隔条才落盘：拖动过程中每一帧都写一次设置文件是可笑的。</summary>
    private void OnPreviewSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (PreviewColumn.ActualWidth <= 0) return;

        App.Settings.Layout.PreviewWidth = Math.Clamp(
            PreviewColumn.ActualWidth, AppSettings.MinPreviewWidth, AppSettings.MaxPreviewWidth);

        App.SaveSettings();
    }

    /// <summary>双击分隔条回默认宽度。分屏那边的分隔条是"回 50%"，这里是"回 320"。</summary>
    private void OnPreviewSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        App.Settings.Layout.PreviewWidth = AppSettings.DefaultPreviewWidth;
        ApplyPreviewLayout();
        App.SaveSettings();
        e.Handled = true;
    }

    /// <summary>设置页改完预览相关的项后调用（<see cref="ApplySettingsToUi"/> 的一部分）。</summary>
    private void ApplyPreviewSettings()
    {
        _preview.AutoPreview = App.Settings.Behavior.PreviewAutoOpen;
        _previewOpen = App.Settings.Appearance.ShowPreview;
        ApplyPreviewLayout();
    }

    private void RegisterPreviewCommands()
    {
        _commands.Register(new AppCommand
        {
            Id = CommandIds.TogglePreview,
            TitleFactory = () => Strings.Command_TogglePreview,
            Gesture = "Alt+P",
            Execute = TogglePreview,
        });
    }
}
