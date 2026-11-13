using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Jianlan.App.Views;

namespace Jianlan.App.Shell;

/// <summary>
/// 给对话框装上自绘标题条（FR-10.11）：在窗口内容上方插一行 <see cref="WindowHeader"/>，
/// 并把窗口切成 <see cref="WindowChromeHelper"/> 的无边框形态。
///
/// 为什么用附加属性包一层，而不是在每个窗口的 XAML 里手写 Grid：
/// 五个对话框的根元素各不相同（Grid / StackPanel / ScrollViewer），
/// 逐个改根元素等于把每份布局的最外层缩进整<em>体</em>改一遍，
/// 差异只在"有没有标题条"这一件事上，不值当。这里在 Initialized 时做同样的包装，
/// 五处调用点各一行 <c>shell:ImmersiveChrome.Enabled="True"</c>。
///
/// 时机选 Initialized 而不是 Loaded：XAML 已解析完（Content 就位）、窗口还没显示，
/// 包装引起的重新布局发生在首次呈现之前，用户看不到跳一下。
/// </summary>
public static class ImmersiveChrome
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(ImmersiveChrome),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true) return;

        // 设置里关掉时整条链都不动作：窗口保持系统标题栏，也不插入标题条
        if (!App.Settings.Appearance.ImmersiveTitleBar) return;

        if (window.IsInitialized)
        {
            Wrap(window);
        }
        else
        {
            window.Initialized += OnWindowInitialized;
        }
    }

    private static void OnWindowInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;

        window.Initialized -= OnWindowInitialized;
        Wrap(window);
    }

    private static void Wrap(Window window)
    {
        // 幂等：Initialized 与后续可能的重复赋值都走这里
        if (window.Content is Border { Tag: WindowHeaderMarker }) return;
        if (window.Content is not UIElement content) return;

        bool resizable = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

        window.Content = null;

        var header = new WindowHeader { ShowMaximize = resizable };
        header.SetBinding(WindowHeader.TitleProperty,
            new Binding(nameof(Window.Title)) { Source = window });

        var root = new Grid { Tag = WindowHeaderMarker };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(header, 0);
        Grid.SetRow(content, 1);

        root.Children.Add(header);
        root.Children.Add(content);

        // 一圈 1px 外描边。系统标题栏没了之后，对话框与主窗口的背景同为 Surface.Window，
        // 叠在一起只剩 DWM 的投影在分界——深色主题下那点投影几乎看不见，弹窗看上去像
        // 主窗口里长出来的一块。描边只在对话框加：主窗口最大化时贴着屏幕边，画线反而多余。
        var outline = new Border
        {
            Tag = WindowHeaderMarker,
            BorderThickness = new Thickness(1),
            Child = root,
        };
        outline.SetResourceReference(Border.BorderBrushProperty, "Brush.Border.Strong");

        window.Content = outline;

        // 顺序不能反：WindowChrome 要在窗口创建前设好附着的 chrome，
        // 而 WindowStyle 一旦建了窗口就改不回来
        WindowChromeHelper.Attach(window, resizable);
    }

    /// <summary>包装过的标记。用字符串而不是引用类型：Grid.Tag 是 object，赋值即打标，无需额外字段。</summary>
    private const string WindowHeaderMarker = "Jianlan.Chrome";
}
