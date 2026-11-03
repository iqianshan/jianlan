using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace WpfBaseline;

/// <summary>
/// 最小 WPF 空窗口基线。
///
/// 用途：判断"启动变慢"是 Lingpane 的代码回归，还是整台机器/系统状态的问题。
/// 这个程序不含任何业务代码——只有一个空窗口，做了与 Lingpane 相同的 IME 关闭处理。
/// 若它的启动耗时也同步上涨，说明环境变了，任何性能数字都不可信。
///
/// 输出：每次启动写到 %TEMP%\wpf-baseline.log，一行一个毫秒数（到 Loaded）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string logPath = Path.Combine(Path.GetTempPath(), "wpf-baseline.log");
        if (args.Length > 0 && args[0] == "--reset" && File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        var sw = Stopwatch.StartNew();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window
        {
            Title = "WpfBaseline",
            Width = 480,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        // 与 Lingpane 一致：启动期关闭 IME，排除输入法上下文创建的干扰
        InputMethod.SetIsInputMethodEnabled(window, false);

        window.Loaded += (_, _) =>
        {
            sw.Stop();
            try
            {
                File.AppendAllText(logPath, sw.ElapsedMilliseconds + Environment.NewLine);
            }
            catch (IOException)
            {
                // 日志写失败不影响退出
            }

            // 稳定后再退出，顺便采一次内存。
            // 口径与 Lingpane 的 NF-05 一致：私有提交（任务管理器"提交大小"）为主，工作集为辅。
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    using var p = Process.GetCurrentProcess();
                    p.Refresh();
                    long priv = p.PrivateMemorySize64 / (1024 * 1024);
                    long ws = p.WorkingSet64 / (1024 * 1024);
                    File.AppendAllText(logPath, $"mem priv={priv}MB ws={ws}MB" + Environment.NewLine);
                }
                catch (IOException)
                {
                    // 忽略
                }
                app.Shutdown();
            };
            timer.Start();
        };

        window.Show();
        app.Run();
    }
}
