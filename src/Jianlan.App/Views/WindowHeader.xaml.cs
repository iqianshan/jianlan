using System.Windows;
using System.Windows.Controls;

namespace Jianlan.App.Views;

/// <summary>
/// 对话框标题条（FR-10.11）。只解决一件事：把 Window.Title 显示出来，
/// 并把"能不能最大化"转给右侧的窗口键。
///
/// 标题取自 Window.Title 而不是构造函数参数：Title 在运行时会被改
/// （另存日志、标签导出这些场景都会换标题），走依赖属性绑定才能跟着变。
/// </summary>
public partial class WindowHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(WindowHeader),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty ShowMaximizeProperty =
        DependencyProperty.Register(
            nameof(ShowMaximize),
            typeof(bool),
            typeof(WindowHeader),
            new PropertyMetadata(false, OnShowMaximizeChanged));

    public WindowHeader()
    {
        InitializeComponent();
    }

    /// <summary>标题文字。页面里通常绑到所属窗口的 Title。</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>不可缩放的窗口保持 false：最大化一个不能拉伸的窗口没有意义。</summary>
    public bool ShowMaximize
    {
        get => (bool)GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    private static void OnShowMaximizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowHeader header)
        {
            header.Buttons.ShowMaximize = (bool)e.NewValue;
        }
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowHeader header)
        {
            header.Caption.Text = e.NewValue as string ?? string.Empty;
        }
    }
}
