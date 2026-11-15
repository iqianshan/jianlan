// i18n-ignore-file：本文件的中文字符串只进日志/诊断输出，不进界面，不参与翻译。
using System.IO;
using System.Text;
using System.Windows;
using Jianlan.App.Design;
using Jianlan.Core.Diagnostics;

// 崩溃日志是给人看的纯文本，插值的都是字符串与整数，区域设置不影响可读性，
// 也没人会用不同 culture 去解析它。逐个传 InvariantCulture 只会让代码更难读。
#pragma warning disable CA1305

namespace Jianlan.App.Diagnostics;

/// <summary>
/// 崩溃记录（docs/logging.md §5）。目标：出问题后凭一份日志能定位，
/// 而不是让用户口述"它卡了一下"。不弹报告框、不联网。
/// </summary>
internal static class CrashLogger
{
    public static void Attach(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    private static void OnDispatcherUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Write("DispatcherUnhandledException", e.Exception);

        // 窗口已经显示出来才尝试续命：此时用户有界面可操作，吞掉异常比直接消失好。
        // 启动期（窗口还没出来）吞掉只会留下一个看不见的进程，更糟，故让它退出。
        bool hasVisibleWindow = Application.Current?.MainWindow is { IsVisible: true };
        e.Handled = hasVisibleWindow;
    }

    private static void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        Write("UnhandledException", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Write("UnobservedTaskException", e.Exception);
        e.SetObserved();   // 标记已观察，避免影响进程
    }

    private static void Write(string kind, Exception? ex)
    {
        try
        {
            string dir = AppPaths.LogsDir;
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder(1024);
            sb.AppendLine($"===== {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {kind} =====");
            // 版本号曾经写死成 0.1.0，压测到 0.2.1 时 crash.log 仍自称 0.1.0——
            // 崩溃记录里最该可信的一行反而不可信，改用 AppInfo 的唯一出口。
            sb.AppendLine($"版本   {AppInfo.Version} · OS {Environment.OSVersion.Version} · .NET {Environment.Version} · 进程 {Environment.ProcessId}");

            if (Application.Current?.MainWindow is Views.MainWindow)
            {
                sb.AppendLine($"主题   {ThemeService.Current}");
            }

            sb.AppendLine($"启动   {StartupTrace.Summary()}");

            if (ex is null)
            {
                sb.AppendLine("异常   无（异常对象为空）");
            }
            else
            {
                sb.AppendLine($"异常   {ex.GetType().FullName}: {ex.Message}");
                sb.AppendLine(ex.StackTrace);
                if (ex.InnerException is not null)
                {
                    sb.AppendLine($"内层   {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                }
            }

            sb.AppendLine();
            File.AppendAllText(Path.Combine(dir, "crash.log"), sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 崩溃记录本身失败不能再抛
        }
    }
}
