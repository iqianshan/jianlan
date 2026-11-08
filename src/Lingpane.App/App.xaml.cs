using System.IO;
using System.Windows;
using Lingpane.App.Design;
using Lingpane.App.Services;
using Lingpane.App.Typography;
using Lingpane.App.Views;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.Localization;
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

    private SingleInstanceGuard? _singleInstance;

    /// <summary>
    /// 主窗口引用，必须在创建时留存：窗口关闭后 Application.MainWindow 会变成 null，
    /// 而尺寸保存发生在关闭之后（OnExit 兜底路径）。
    /// </summary>
    private Window? _mainWindow;

    /// <summary>读入时的"最后目录"，用来判断本次会话有没有产生需要落盘的改动。</summary>
    private static string? _loadedLastPath;

    /// <summary>读入时的标签列表，判断口径同上。</summary>
    private static List<string> _loadedTabs = [];

    /// <summary>首次启动命令行里带的目录，优先于上次打开的目录。null 表示没带。</summary>
    public static string? StartupDirectory { get; private set; }

    /// <summary>
    /// 在 跟随系统 → 浅色 → 深色 之间循环，并把选择写回设置。
    /// 只读模式（settings.json 版本过高）只切不写。
    /// </summary>
    public static ThemeKind CycleTheme()
    {
        ThemeKind next = Settings.Appearance.Theme switch
        {
            ThemeKind.System => ThemeKind.Light,
            ThemeKind.Light => ThemeKind.Dark,
            _ => ThemeKind.System,
        };

        Settings.Appearance.Theme = next;
        ThemeService.Apply(next);
        SaveSettings();

        return next;
    }

    /// <summary>
    /// 保存设置。只读模式与探针模式不写盘；失败只留日志，不打断操作。
    /// 所有改动设置的路径都走这里，避免各自 try/catch 后行为不一致。
    /// </summary>
    /// <summary>
    /// 切换界面语言并立即存盘（FR-10.4：可手动覆盖，切换 &lt;200ms 且不重启）。
    /// 刷新由 <see cref="LocalizationService"/> 通知驱动，调用方不需要重建窗口。
    /// </summary>
    public static void ApplyLanguage(string setting)
    {
        Settings.General.Language = setting;
        LocalizationService.Instance.Apply(setting);
        SaveSettings();
    }

    public static void SaveSettings()
    {
        if (SettingsReadOnly || _isProbeMode) return;

        try
        {
            SettingsStore.Save(Settings, isReadOnly: SettingsReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Error("settings.save", "E4002", ex.Message);
        }
    }

    public App()
    {
        // 进程内最早的可控埋点位置
        StartupTrace.Begin();

        // Core 不能引用 App（依赖方向），写日志的出口在这里装配。
        // 不装配也不报错，只是 Core 侧的文件操作日志会静默丢失。
        Core.Diagnostics.LogSink.Write = Diagnostics.AppLog.Warn;

        // 崩溃记录要在任何业务代码之前挂上，否则启动期异常会静默消失
        Diagnostics.CrashLogger.Attach(this);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupTrace.Mark("resources");

        // 诊断模式：--perf-probe <目录> [--perf-frames N]，见 Views/MainWindow.PerfProbe.cs。
        // 先解析：探针必须能独立运行，不能因为已有实例在跑就退出。
        var (probePath, probeFrames, probeSplitPath) = ParseProbeArgs(e.Args);
        _isProbeMode = probePath is not null;

        // 按键诊断：必须在窗口构造前设置，静态初始化已经读过一次环境变量
        if (e.Args.Contains("--keydebug", StringComparer.Ordinal))
        {
            Views.MainWindow.KeyDebug = true;
        }

        // 首次启动带目录参数时，参数优先于"上次打开的目录"（FR-10.2）。
        // 之前只有单实例转交路径解析参数，直接 `Lingpane.exe D:\x` 会被忽略。
        StartupDirectory = e.Args.FirstOrDefault(a => !a.StartsWith('-') && Directory.Exists(a));

        // 单实例：已有窗口在运行时，把参数转交给它并立即退出（FR-10.1）。
        // 只有一次 Mutex 创建，管道监听在窗口显示后才启动，不进启动预算。
        if (!_isProbeMode)
        {
            if (!SingleInstanceGuard.TryAcquire(out var guard))
            {
                SingleInstanceGuard.TrySend(e.Args);
                Shutdown();
                return;
            }

            _singleInstance = guard;
        }
        StartupTrace.Mark("singleinstance");

        // 首帧前只读 appearance 与 session 两组（settings-schema §4）：
        // 这两组决定窗口尺寸与配色，首帧后再读会看到尺寸跳变与主题闪变。
        // 文件约 2KB，缺失时只有一次 File.Exists。
        var loaded = SettingsStore.Load();
        Settings = loaded.Settings;
        SettingsReadOnly = loaded.IsReadOnly;
        SettingsWarning = loaded.Warning;
        _loadedLastPath = Settings.Session.LastPath;
        _loadedTabs = [.. Settings.Session.Tabs];

        // 一条启动记录，回答"这份日志来自哪个版本、哪种数据目录"。
        // 便携模式下用户不知道数据落在解压目录里，这一行往往是他们唯一的线索；
        // 版本串带短 commit，能对上到底是哪一次构建，而 0.1.0.0 对不上。
        Diagnostics.AppLog.Info(
            "app.start",
            $"version={AppInfo.Version} portable={AppPaths.IsPortable} data=\"{AppPaths.Root}\"");

        if (loaded.Warning is not null)
        {
            Diagnostics.AppLog.Warn("settings.load", "E4001", loaded.Warning);
        }
        StartupTrace.Mark("settings");

        // 已知文件夹（FR-1.1 实现约束）：先读设置里的缓存，缓存命中就不碰 shell32。
        // 首次运行或新增 kind 时会在这里问一次外壳，那是唯一一次把外壳拉进启动路径的情况。
        // 校验放在窗口显示之后（见下方 ApplicationIdle），不占首帧。
        if (Services.KnownFolderService.Initialize(Settings.General.KnownFolderPaths))
        {
            SaveSettings();
        }
        StartupTrace.Mark("knownfolders");

        // 语言必须在创建视图模型之前生效：视图模型构造期就会取文案（列头、侧栏名称）。
        // 只做一次 ResourceManager 装配 + 三个 CultureInfo 赋值，约 1ms（docs/i18n.md §五）。
        LocalizationService.Instance.Apply(Settings.General.Language);
        StartupTrace.Mark("i18n");

        ThemeService.Apply(Settings.Appearance.Theme);

        FontScaleService.Apply(
            Settings.Appearance.FontSizePt,
            Settings.Appearance.Density == DensityKind.Compact);

        // 字体族必须在这里也应用一次。此前只有设置页下拉框的选中回调会调它，
        // 结果是重启后主窗口一直用默认字体，直到用户打开过一次设置页（AC-06 走查发现）。
        // 这里按名字应用，不枚举系统字体——FontCatalog 明确不参与启动路径。
        FontScaleService.ApplyFontFamilyByName(Settings.Appearance.FontFamily);
        StartupTrace.Mark("fontscale");

        var window = new MainWindow
        {
            Width = Settings.Session.WindowWidth,
            Height = Settings.Session.WindowHeight,
            WindowState = Settings.Session.WindowMaximized
                ? WindowState.Maximized
                : WindowState.Normal,
        };
        _mainWindow = window;

        // 尺寸要在窗口还活着时取。Closing 是最后一个能拿到有效 Width/Height 的时机，
        // 等 OnExit 再读，Application.MainWindow 已被置空。
        window.Closing += (_, _) => PersistWindowState(window);

        // 诊断模式：--perf-probe <目录> [--perf-frames N] [--perf-split <目录>]，
        // 见 Views/MainWindow.PerfProbe.cs
        if (probePath is not null)
        {
            window.ProbePath = probePath;
            window.ProbeFrames = probeFrames;
            window.ProbeSplitPath = probeSplitPath;
        }

        // 关掉 WPF 的输入法客户端注册。窗口激活时创建 IME 上下文要 820ms 与 74MB 工作集，
        // 落在 activated → loaded 这一段，窗口已经画出来了、只是还不能用（见 performance-gate §6.2）。
        //
        // 不需要在文本框获得焦点时再手动开启：地址栏、重命名框都是普通 TextBox，
        // 获得焦点时 WPF 会自行按需创建上下文。实测带这一行时中文输入与候选框一切正常
        // （D:\ac05\ac06\ime-3-addressbar-typed.png）。原注释写的"首次输入时再开启"从未实现，
        // 也不必要——那个承诺会让后来的人去找一个不存在的方法。
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(window, false);

        StartupTrace.Mark("window");

        // 切分 Show()：它曾出现 150ms 与 600ms 两种表现，靠墙钟分不清卡在哪一步
        window.SourceInitialized += (_, _) => StartupTrace.Mark("srcinit");
        window.Activated += (_, _) => StartupTrace.Mark("activated");

        StartupTrace.MarkCpu("preShow");
        window.Show();
        StartupTrace.MarkCpu("postShow");
        StartupTrace.Mark("show");

        // 管道监听放在窗口显示之后：第二次启动的转交请求宁可晚几十毫秒，
        // 也不要让管道创建进启动路径。
        _singleInstance?.StartListening(OnSecondInstance);

        // 已知文件夹的后台校验（FR-1.1）：首帧已经用缓存画完了，这里只比对系统当前值，
        // 有变化才刷新界面并落盘。用户重定向了下载夹、或换了台机器漫游回来，靠这一步跟上。
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (await Services.KnownFolderService.RefreshAsync()) SaveSettings();
            }
            catch (Exception ex)
            {
                // 校验失败不影响任何已画出来的东西，只留一条日志
                Diagnostics.AppLog.Warn("knownfolders.refresh", "E9001", $"{ex.GetType().Name}: {ex.Message}");  // i18n-ignore：只进 app.log
            }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 收到第二次启动的转交参数：激活已有窗口，若有目录参数则导航过去。
    /// 回调在管道线程上，必须切回 UI 线程。
    /// </summary>
    private void OnSecondInstance(string[] args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is not Views.MainWindow w) return;

            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;

            w.Show();
            w.Activate();
            w.Topmost = true;
            w.Topmost = false;

            string? dir = args.FirstOrDefault(a => !a.StartsWith('-') && Directory.Exists(a));
            if (dir is not null) w.NavigateTo(dir);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底：正常情况下 Closing 已经存过，这里只会走到"值未变直接返回"。
        // 不能依赖 Current.MainWindow——窗口关闭时 WPF 会把它置空，见 PersistWindowState 注释。
        PersistWindowState(_mainWindow);
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 保存窗口尺寸。调用点必须在窗口还活着的时候（Closing），
    /// 因为窗口一旦关闭，<see cref="Application.MainWindow"/> 会被 WPF 置空，
    /// OnExit 里再取就只能拿到 null——早期版本正是因此在第一步就返回，尺寸从未落盘。
    /// 只在真的变了才写盘：规范要求"首次改动设置时才创建文件"，用户什么都没改就不该凭空多一个 settings.json。
    /// </summary>
    private static void PersistWindowState(Window? w)
    {
        if (_isProbeMode || SettingsReadOnly) return;
        if (w is null) return;

        // 最大化时存还原尺寸，否则下次取消最大化会得到一个占满屏幕的"普通"窗口
        Rect bounds = w.WindowState == WindowState.Normal
            ? new Rect(w.Left, w.Top, w.Width, w.Height)
            : w.RestoreBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        int width = (int)Math.Round(bounds.Width);
        int height = (int)Math.Round(bounds.Height);
        bool maximized = w.WindowState == WindowState.Maximized;

        var s = Settings.Session;
        // 最后目录由窗口在 Closing 中写入；只要它与读入时不同，也属于"用户改动过"，需要落盘
        bool samePath = string.Equals(s.LastPath, _loadedLastPath, StringComparison.Ordinal);

        // 标签列表同理：开着三个标签、但没换目录、也没改窗口大小，仍然要记住标签
        bool sameTabs = s.Tabs.SequenceEqual(_loadedTabs, StringComparer.OrdinalIgnoreCase);

        if (s.WindowWidth == width && s.WindowHeight == height && s.WindowMaximized == maximized
            && samePath && sameTabs)
        {
            return;
        }

        s.WindowWidth = width;
        s.WindowHeight = height;
        s.WindowMaximized = maximized;

        try
        {
            SettingsStore.Save(Settings, isReadOnly: SettingsReadOnly);
            _loadedLastPath = s.LastPath;
            _loadedTabs = [.. s.Tabs];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 不弹窗、不阻塞退出，但必须留痕：设置写不进去用户是感知不到的
            Diagnostics.AppLog.Error("settings.save", "E4002", ex.Message);
        }
    }

    private static (string? Path, int Frames, string? SplitPath) ParseProbeArgs(string[] args)
    {
        string? path = null;
        string? splitPath = null;
        int frames = 240;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--perf-probe" && i + 1 < args.Length)
            {
                path = args[++i];
            }
            else if (args[i] == "--perf-split" && i + 1 < args.Length)
            {
                // 双窗格测量（NF-05R 双窗格档）：第二窗格单独给一个目录，
                // 两个大目录同时常驻才是要测的场景，传同一个目录测不出第二份数据层
                splitPath = args[++i];
            }
            else if (args[i] == "--perf-frames" && i + 1 < args.Length &&
                     int.TryParse(args[i + 1], out int n) && n >= 0)
            {
                frames = n;
                i++;
            }
        }

        return (path, frames, splitPath);
    }
}
