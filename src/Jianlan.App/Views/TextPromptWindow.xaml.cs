using System.Windows;
using System.Windows.Input;
using Jianlan.App.Shell;

namespace Jianlan.App.Views;

/// <summary>
/// 单行文本输入窗口，供"新建文件夹"与"重命名"共用。
/// 校验由调用方以委托传入，窗口只负责显示错误并保持打开。
///
/// 窗口**常驻复用**（<see cref="ModalSession"/>）。这个窗口是 AC-03 崩溃链上最可疑的一环：
/// 它是整个应用里唯一"每次都是新 HWND + 带可编辑输入框"的窗口，TSF 会为它建一份
/// 真正的文本存储与组字界面，关闭即销毁。压测 69 分钟里这种开关约发生上百次。
/// </summary>
public partial class TextPromptWindow : Window
{
    private static TextPromptWindow? _instance;

    private Action<bool?>? _finish;
    private Func<string, string?>? _validate;
    private bool _selectStem;

    private TextPromptWindow()
    {
        InitializeComponent();

        // 与主窗口同一策略（App.xaml.cs 的 SetIsInputMethodEnabled）：窗口级不开
        // IME 客户端，改由输入框在获得焦点时按需创建上下文（详见 ConfirmWindow 注释）。
        InputMethod.SetIsInputMethodEnabled(this, false);
        InputMethod.SetIsInputMethodEnabled(Input, true);

        // 真正销毁（应用退出）时摘掉 IMM 关联；平时隐藏由 ModalSession 只撤组字。
        ImeFocusRelease.Attach(this);

        // 手动模态下 Esc 不再由 IsCancel 按钮兜底（窗口不销毁、不设 DialogResult）。
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private static TextPromptWindow Instance => _instance ??= new TextPromptWindow();

    public string Value => Input.Text.Trim();

    /// <param name="selectStem">true 时只选中主名（不含扩展名），重命名文件时更顺手。</param>
    /// <returns>用户输入的名称；取消返回 null。</returns>
    public static string? Ask(Window? owner, string title, string prompt, string initialValue,
        Func<string, string?>? validate = null, bool selectStem = false)
    {
        TextPromptWindow window = Instance;

        bool? result = ModalSession.Run(
            window,
            owner,
            finish =>
            {
                window._finish = finish;
                window._validate = validate;
                window._selectStem = selectStem;
                window.Title = title;
                window.PromptText.Text = prompt;
                window.Input.Text = initialValue;
                // 上一轮的校验错误会留在复用窗口上，每轮清掉。
                window.ErrorText.Text = string.Empty;
                window.ErrorText.Visibility = Visibility.Collapsed;
            },
            () => window.FocusInput());

        return result == true ? window.Value : null;
    }

    /// <summary>
    /// 抢焦点并选中初值。原来挂在 <c>Loaded</c> 上，但复用窗口的 Loaded 只触发一次，
    /// 改成每次显示后由 <see cref="ModalSession"/> 在输入优先级上调用。
    /// </summary>
    private void FocusInput()
    {
        Input.Focus();

        int length = Input.Text.Length;
        int selectionLength = length;

        if (_selectStem)
        {
            int dot = Input.Text.LastIndexOf('.');
            if (dot > 0) selectionLength = dot;
        }

        Input.Select(0, selectionLength);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        _finish?.Invoke(false);
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Commit();

    private void OnCancel(object sender, RoutedEventArgs e) => _finish?.Invoke(false);

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

        _finish?.Invoke(true);
    }
}
