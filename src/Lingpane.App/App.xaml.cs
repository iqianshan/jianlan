using System.IO;
using System.Windows;
using Lingpane.App.Design;
using Lingpane.App.Typography;
using Lingpane.App.Views;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.Settings;

namespace Lingpane.App;

public partial class App : Application
{
    /// <summary>当前设置。读取失败时是默认值，程序照常启动。</summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>设置文件为高版本（只读）时为 true，M1 设置界面据此禁用写入。</summary>
    public static bool SettingsReadOnly { get; private set; }

    /// <summary>设置读取告警，非空时应在状态栏提示，但不得中断启动。</summary>
    public static string? SettingsWarning { get; private set; }

    /// <summary>诊断探针模式：不得写设置文件，否则会污染测量并覆盖用户窗口尺寸。</summary>
    private static bool _isProbeMode;

    public App()
    {
        // 进程内最早的可控埋点位置
        StartupTrace.Begin();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupTrace.Mark("resources");

        // 首帧前只读 appearance 与 session 两组（settings-schema §4）：
        // 这两组决定窗口尺寸与配色，首帧后再读会看到尺寸跳变与主题闪变。
        // 文件约 2KB，缺失时只有一次 File.Exists。
        var loaded = SettingsStore.Load();
        Settings = loaded.Settings;
        SettingsReadOnly = loaded.IsReadOnly;
        SettingsWarning = loaded.Warning;
        if (loaded.Warning is not null)
        {
            System.Diagnostics.Debug.WriteLine($"[settings] {loaded.Warning}");
        }
        StartupTrace.Mark("settings");

        ThemeService.Apply(Settings.Appearance.Theme);

        FontScaleService.Apply(
            Settings.Appearance.FontSizePt,
            Settings.Appearance.Density == DensityKind.Compact);
        StartupTrace.Mark("fontscale");

        var window = new MainWindow
        {
            Width = Settings.Session.WindowWidth,
            Height = Settings.Session.WindowHeight,
            WindowState = Settings.Session.WindowMaximized
                ? WindowState.Maximized
                : WindowState.Normal,
        };

        // 诊断模式：--perf-probe <目录> [--perf-frames N]，见 Views/MainWindow.PerfProbe.cs
        var (probePath, probeFrames) = ParseProbeArgs(e.Args);
        if (probePath is not null)
        {
            window.ProbePath = probePath;
            window.ProbeFrames = probeFrames;
            _isProbeMode = true;
        }

        // M0 实测结论：窗口激活时创建 IME 上下文在当前环境耗时约 500ms。
        // 浏览文件不需要输入法，故启动阶段关闭，首次进入文本输入场景（重命名/地址栏）再开启。
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(window, false);

        StartupTrace.Mark("window");

        window.Show();
        StartupTrace.Mark("show");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PersistSession();
        base.OnExit(e);
    }

    /// <summary>
    /// 退出时保存窗口尺寸。只在真的变了才写盘——
    /// 规范要求"首次改动设置时才创建文件"，用户什么都没改就不该凭空多一个 settings.json。
    /// </summary>
    private static void PersistSession()
    {
        if (_isProbeMode || SettingsReadOnly) return;
        if (Current?.MainWindow is not Window w) return;

        // 最大化时存还原尺寸，否则下次取消最大化会得到一个占满屏幕的"普通"窗口
        Rect bounds = w.WindowState == WindowState.Normal
            ? new Rect(w.Left, w.Top, w.Width, w.Height)
            : w.RestoreBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        int width = (int)Math.Round(bounds.Width);
        int height = (int)Math.Round(bounds.Height);
        bool maximized = w.WindowState == WindowState.Maximized;

        var s = Settings.Session;
        if (s.WindowWidth == width && s.WindowHeight == height && s.WindowMaximized == maximized)
        {
            return;
        }

        s.WindowWidth = width;
        s.WindowHeight = height;
        s.WindowMaximized = maximized;

        try
        {
            SettingsStore.Save(Settings, isReadOnly: SettingsReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写失败只在状态栏提示，不弹窗、不阻塞退出
            System.Diagnostics.Debug.WriteLine($"[settings] 保存失败：{ex.Message}");
        }
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
