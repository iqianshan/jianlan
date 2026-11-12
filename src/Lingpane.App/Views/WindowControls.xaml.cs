using System.Windows;
using System.Windows.Controls;
using Lingpane.App.Shell;
using Lingpane.Core.Localization;

namespace Lingpane.App.Views;

/// <summary>
/// 窗口键（FR-10.11）。所有自绘标题栏共用这一份，主窗口与对话框的按钮位置、尺寸、
/// 悬停行为因此不可能走样。
///
/// 按钮点到的是所属 Window，不是参数传进来的——控件被放进哪棵可视树就用哪个窗口，
/// 少一个"忘记把 Window 传进来"的失配点。
/// </summary>
public partial class WindowControls : UserControl
{
    public static readonly DependencyProperty ShowMaximizeProperty =
        DependencyProperty.Register(
            nameof(ShowMaximize),
            typeof(bool),
            typeof(WindowControls),
            new PropertyMetadata(true, OnShowMaximizeChanged));

    private Window? _window;

    public WindowControls()
    {
        InitializeComponent();

        // 这两项不随窗口状态变，初始化时写一次就够；
        // 最大/还原那颗要跟着状态换图标与提示，见 ApplyState。
        MinGlyph.Text = IconGlyphs.WindowMinimize;
        MinButton.ToolTip = Strings.A11y_WindowMinimize;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 不可缩放的窗口（确认框、文本输入框）没有最大化语义，
    /// 留着按钮只会点出一个不能拉伸的"最大化窗口"。
    /// </summary>
    public bool ShowMaximize
    {
        get => (bool)GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    private static void OnShowMaximizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowControls controls)
        {
            controls.MaxButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null) return;

        _window.StateChanged -= OnWindowStateChanged;
        _window.StateChanged += OnWindowStateChanged;

        ApplyState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is null) return;

        _window.StateChanged -= OnWindowStateChanged;
        _window = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => ApplyState();

    /// <summary>
    /// 最大 / 还原共用一颗按钮：图标与提示文字一起换。
    /// 只换图标不换提示，键盘与读屏用户听到的还是"最大化"。
    /// </summary>
    private void ApplyState()
    {
        bool maximized = _window?.WindowState == WindowState.Maximized;

        MaxGlyph.Text = maximized ? IconGlyphs.WindowRestore : IconGlyphs.WindowMaximize;
        MaxButton.ToolTip = maximized ? Strings.A11y_WindowRestore : Strings.A11y_WindowMaximize;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (_window is null) return;

        _window.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (_window is null) return;

        // 最小化状态下点"最大化"应该先还原：直接把 Minimized 改成 Maximized
        // 不会把窗口带到前台，用户会以为按钮没反应。
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _window?.Close();
}
