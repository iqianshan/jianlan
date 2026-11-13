using System.Windows;
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
