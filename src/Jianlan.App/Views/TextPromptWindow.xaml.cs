using System.Windows;
using System.Windows.Input;

namespace Jianlan.App.Views;

/// <summary>
/// 单行文本输入窗口，供"新建文件夹"与"重命名"共用。
/// 校验由调用方以委托传入，窗口只负责显示错误并保持打开。
/// </summary>
public partial class TextPromptWindow : Window
{
    private Func<string, string?>? _validate;

    private TextPromptWindow()
    {
        InitializeComponent();

        // 与主窗口同一策略（App.xaml.cs 的 SetIsInputMethodEnabled）：窗口级不开
        // IME 客户端，改由输入框在获得焦点时按需创建上下文。本窗口每次 F2/新建
        // 都要新建并销毁，窗口级注册会让 TSF 在"打开→关闭"之间各做一次上下文切换，
        // 而 TSF 的焦点通知正是 DirectUI 崩溃栈的入口（详见 ConfirmWindow 注释）。
        InputMethod.SetIsInputMethodEnabled(this, false);
        InputMethod.SetIsInputMethodEnabled(Input, true);
    }

    public string Value => Input.Text.Trim();

    /// <param name="selectStem">true 时只选中主名（不含扩展名），重命名文件时更顺手。</param>
    /// <returns>用户输入的名称；取消返回 null。</returns>
    public static string? Ask(Window? owner, string title, string prompt, string initialValue,
        Func<string, string?>? validate = null, bool selectStem = false)
    {
        var window = new TextPromptWindow
        {
            Owner = owner,
            Title = title,
            _validate = validate,
        };

        window.PromptText.Text = prompt;
        window.Input.Text = initialValue;

        window.Loaded += (_, _) =>
        {
            window.Input.Focus();

            int length = window.Input.Text.Length;
            int selectionLength = length;

            if (selectStem)
            {
                int dot = window.Input.Text.LastIndexOf('.');
                if (dot > 0) selectionLength = dot;
            }

            window.Input.Select(0, selectionLength);
        };

        return window.ShowDialog() == true ? window.Value : null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Commit();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        Commit();
        e.Handled = true;
    }

    private void Commit()
    {
        string value = Value;

        if (_validate?.Invoke(value) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            Input.Focus();
            return;
        }

        DialogResult = true;
    }
}
