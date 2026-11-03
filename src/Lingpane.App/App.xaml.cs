using System.Windows;
using Lingpane.App.Typography;
using Lingpane.App.Views;
using Lingpane.Core.Diagnostics;

namespace Lingpane.App;

public partial class App : Application
{
    public App()
    {
        // 进程内最早的可控埋点位置
        StartupTrace.Begin();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupTrace.Mark("resources");

        // 字号跟随系统（FR-5.1.6 默认值）。设置持久化在 M1 接入。
        FontScaleService.Apply(points: null);
        StartupTrace.Mark("fontscale");

        var window = new MainWindow();

        // 诊断模式：--perf-probe <目录> [--perf-frames N]，见 Views/MainWindow.PerfProbe.cs
        var (probePath, probeFrames) = ParseProbeArgs(e.Args);
        if (probePath is not null)
        {
            window.ProbePath = probePath;
            window.ProbeFrames = probeFrames;
        }

        // M0 实测结论：窗口激活时创建 IME 上下文在当前环境耗时约 500ms。
        // 浏览文件不需要输入法，故启动阶段关闭，首次进入文本输入场景（重命名/地址栏）再开启。
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(window, false);

        StartupTrace.Mark("window");

        window.Show();
        StartupTrace.Mark("show");
    }

    private static (string? Path, int Frames) ParseProbeArgs(string[] args)
    {
        string? path = null;
        int frames = 240;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--perf-probe" && i + 1 < args.Length)
            {
                path = args[++i];
            }
            else if (args[i] == "--perf-frames" && i + 1 < args.Length &&
                     int.TryParse(args[i + 1], out int n) && n >= 0)
            {
                frames = n;
                i++;
            }
        }

        return (path, frames);
    }
}
