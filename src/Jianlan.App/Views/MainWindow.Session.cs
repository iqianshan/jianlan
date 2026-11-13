using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Jianlan.App.Diagnostics;
using Jianlan.Core.Diagnostics;
using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// "上次没有正常退出"提示条（FR-10.6）。
///
/// 只做三件事：把标记里的时间写进一行字、给一个日志入口、允许关掉。
/// 不弹模态框、不自动发任何东西——用户可能只是关了电源，看到一大段技术说明
/// 只会让他下次绕开这个程序。日志入口放在这里，是因为刚出事的那一刻最想找的就是它。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 首帧之前决定要不要显示提示条。放构造期而不是 Loaded：提示条会把工具栏往下推一格，
    /// 出现在首帧之后就是一次可见的跳动；构造期给好，第一帧就是最终形态。
    /// </summary>
    private void ShowSessionBannerIfNeeded()
    {
        if (App.PreviousSession is not { } stamp) return;

        CrashBannerText.Text = stamp.StartedAt == DateTimeOffset.MinValue
            ? Strings.Crash_Banner_TextUnknown
            : string.Format(
                CultureInfo.CurrentCulture,
                Strings.Crash_Banner_Text,
                stamp.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));

        CrashBanner.Visibility = Visibility.Visible;

        // 提示条本身不写日志（app.log 里已经有一条 app.start 记着这次启动），
        // 但"上次是谁没退干净"值得留一条，用来对上 pid 与启动时间。
        AppLog.Warn(
            "session.recovered", "E7003",
            $"上次会话未正常结束 · pid={stamp.ProcessId} start={stamp.StartedAt:O} version={stamp.Version}");  // i18n-ignore：只进 app.log
    }

    /// <summary>
    /// 打开日志文件夹。目录可能还不存在（首次运行、日志级别关闭），先建再开；
    /// 打不开就只留日志，不把提示条变成错误对话框。
    /// </summary>
    private void OnCrashBannerOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            Process.Start(new ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("session.openlogs", "E7003", $"{ex.GetType().Name}: {ex.Message}");  // i18n-ignore：只进 app.log
        }
    }

    /// <summary>关掉提示条。本次会话不再出现——标记已经在启动时重写，下次启动不会重复提示。</summary>
    private void OnCrashBannerDismiss(object sender, RoutedEventArgs e)
        => CrashBanner.Visibility = Visibility.Collapsed;
}
