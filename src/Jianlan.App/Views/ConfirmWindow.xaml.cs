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
/// </summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow()
    {
        InitializeComponent();

        // 本窗口只有一段文本和按钮，没有任何输入需求，但独立窗口默认会注册
        // TSF 输入法客户端：每次打开、关闭都要创建和销毁一次 IME 上下文，
        // 进而触发 TSF 的 InputFocusChanged。稳定压测的原生崩溃栈正是
        // msctf!CtfImeToAsciiEx → textinputframework!InputFocusChanged →
        // DirectUI::HWNDElement::OnInput（DUI70+0x65642，摘除链表时前驱为 NULL），
        // 而崩溃前最后一个动作就是弹出本窗口（属性/F2 提示）。关掉它，
        // 这条链上就少一次无谓的焦点切换。主窗口早已按同一理由关掉（App.xaml.cs）。
        InputMethod.SetIsInputMethodEnabled(this, false);

        // 关窗前主动摘掉 IMM 关联。该窗只有文本和按钮，没有任何输入需求，
        // 却每次属性面板/提示都要新建一个 HWND；不松手就销毁，输入法的界面元素
        // 会留在已失效的输入上下文上（见 Shell/ImeFocusRelease.cs）。
        ImeFocusRelease.Attach(this);
    }

    /// <summary>
    /// 二选一确认。返回 <c>true</c> 表示用户点了"是"。
    /// 默认按钮是"是"，Esc 等同"否"——与 MessageBox 的键盘习惯保持一致。
    /// </summary>
    public static bool Ask(Window? owner, string title, string message)
    {
        var window = new ConfirmWindow { Owner = owner, Title = title };
        window.MessageText.Text = message;
        window.CancelButton.Content = Strings.Dialog_No;
        window.ConfirmButton.Content = Strings.Dialog_Yes;
        return window.ShowDialog() == true;
    }

    /// <summary>只有"确定"一个按钮的提示框（属性、错误说明）。</summary>
    public static void Notify(Window? owner, string title, string message)
    {
        var window = new ConfirmWindow { Owner = owner, Title = title };
        window.MessageText.Text = message;
        window.ConfirmButton.Content = Strings.Dialog_Ok;
        window.CancelButton.Visibility = Visibility.Collapsed;
        window.ShowDialog();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
