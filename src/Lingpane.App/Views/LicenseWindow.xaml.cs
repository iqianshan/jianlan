using System.Runtime.InteropServices;
using System.Windows;
using Lingpane.Core.Localization;

namespace Lingpane.App.Views;

/// <summary>
/// 只读文本查看窗（许可证 / NOTICE / 第三方清单）。非模态，一次开一扇：
/// 重复点同一个按钮时把已有窗口提到前面，而不是再叠一份同样的文本。
/// </summary>
public partial class LicenseWindow : Window
{
    private LicenseWindow() => InitializeComponent();

    public static void ShowText(Window owner, string title, string text)
    {
        // 只看标题判断"是不是同一份"：同一时刻最多三份文本，再加 id 去重不值当
        if (Application.Current.Windows.OfType<LicenseWindow>().FirstOrDefault(w => w.Title == title) is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new LicenseWindow { Owner = owner, Title = title };
        window.Body.Text = text;
        window.Show();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Body.Text);
            StatusText.Text = Strings.About_License_Copied;
        }
        catch (ExternalException)
        {
            StatusText.Text = Strings.About_Diagnostics_Failed;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
