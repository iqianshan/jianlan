using System.Windows;
using System.Windows.Input;
using Jianlan.App.Shell;
using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// 通用确认/提示窗口，替代 <see cref="MessageBox"/>。
///
/// 换掉 MessageBox 的唯一原因：它的按钮文字跟随 **Windows 系统语言**，
/// 不跟随应用界面语言。中文系统上把界面切成英文，MessageBox 仍弹"是(Y)/否(N)"，
/// 与整窗英文文案割裂（AC-11 走查发现）。按钮文案改走资源键后两边一致。
///
/// 窗口**常驻复用**（<see cref="ModalSession"/>）：不再每次 <c>new</c> + <c>ShowDialog()</c>。
/// 压测里属性面板与各种提示每次都是一次新的 HWND，TSF 会随之建/毁输入上下文，
/// 这是 AC-03 原生崩溃（<c>dui70+0x65642</c>）的触发面。
/// </summary>
public partial class ConfirmWindow : Window
{
    private static ConfirmWindow? _instance;

    private Action<bool?>? _finish;

    private ConfirmWindow()
    {
        InitializeComponent();

        // 本窗口只有一段文本和按钮，没有任何输入需求，但独立窗口默认会注册
        // TSF 输入法客户端：每次打开、关闭都要创建和销毁一次 IME 上下文，
        // 进而触发 TSF 的 InputFocusChanged。主窗口早已按同一理由关掉（App.xaml.cs）。
        InputMethod.SetIsInputMethodEnabled(this, false);

        // 真正销毁（应用退出）时摘掉 IMM 关联；平时隐藏由 ModalSession 只撤组字。
        ImeFocusRelease.Attach(this);

        // 常驻窗**只有应用退出时**才允许真被关闭。
        //
        // 这条守卫不能挂在 ModalSession 里：那里只在"某一轮模态进行中"订阅 Closing，
        // 一轮结束就摘掉，于是窗口隐藏期间完全没有守卫——此时任何 WM_CLOSE
        // （任务栏关闭、外部 PostMessage、驱动收窗）都会把 HWND 销毁。
        // 而 WPF 对已关闭的窗口调 Show() 会抛"关闭窗口后，无法设置可见性"，
        // 单例又不会重建，结果就是此后所有确认/提示全部哑掉。
        // 8 小时长跑（2026-11-16 11:42 轮）里正是这样：第 20 分钟窗口被关掉，
        // 之后 7.5 小时 189 次无效调用，属性面板与删除确认一次都没弹出来过。
        Closing += (_, e) =>
        {
            if (Application.Current?.MainWindow is { IsVisible: true }) e.Cancel = true;
        };

        // 万一真被关掉（应用退出路径），把单例清空，下一次调用重建一个新窗口，
        // 宁可多一次 HWND 建毁，也不能让提示链路永久失效。
        Closed += (_, _) => _instance = null;

        // 手动模态下 Esc/Enter 不再由 ShowDialog 的对话框语义兜底，得自己接。
        // 原来靠按钮的 IsCancel/IsDefault，现在窗口不销毁、不设 DialogResult，
        // 那套机制不会把窗口收回去，所以统一在隧道阶段拦掉。
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _finish?.Invoke(false);
                break;
            case Key.Enter:
                // 与原来 IsDefault 的行为一致：不管焦点落在哪个按钮，回车都是"是/确定"。
                e.Handled = true;
                _finish?.Invoke(true);
                break;
        }
    }

    private static ConfirmWindow Instance => _instance ??= new ConfirmWindow();

    /// <summary>
    /// 二选一确认。返回 <c>true</c> 表示用户点了"是"。
    /// 默认按钮是"是"，Esc 等同"否"——与 MessageBox 的键盘习惯保持一致。
    /// </summary>
    public static bool Ask(Window? owner, string title, string message)
    {
        ConfirmWindow window = Instance;

        bool? result = ModalSession.Run(window, owner, finish =>
        {
            window._finish = finish;
            window.Title = title;
            window.MessageText.Text = message;
            // 按钮文案每轮都重设：窗口复用，上一轮的可见性与文案会留着，
            // 且界面语言可能中途切换过（AC-11）。
            window.CancelButton.Content = Strings.Dialog_No;
            window.CancelButton.Visibility = Visibility.Visible;
            window.ConfirmButton.Content = Strings.Dialog_Yes;
        });

        return result == true;
    }

    /// <summary>只有"确定"一个按钮的提示框（属性、错误说明）。</summary>
    public static void Notify(Window? owner, string title, string message)
    {
        ConfirmWindow window = Instance;

        ModalSession.Run(window, owner, finish =>
        {
            window._finish = finish;
            window.Title = title;
            window.MessageText.Text = message;
            window.CancelButton.Content = Strings.Dialog_No;
            window.CancelButton.Visibility = Visibility.Collapsed;
            window.ConfirmButton.Content = Strings.Dialog_Ok;
        });
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => _finish?.Invoke(true);

    private void OnCancel(object sender, RoutedEventArgs e) => _finish?.Invoke(false);
}
