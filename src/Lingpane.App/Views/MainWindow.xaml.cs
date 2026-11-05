using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lingpane.App.Commands;
using Lingpane.App.Services;
using Lingpane.App.ViewModels;
using Lingpane.Core.Commands;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.Settings;

using Lingpane.Core.Localization;

namespace Lingpane.App.Views;

/// <summary>
/// 窗口壳。持有导航栏、命令表、快捷键钩子、系统热键与会话保存，
/// 浏览区（工具栏/地址栏/列头/列表/状态栏）在 <see cref="Pane"/> 里。
///
/// 分工边界：
///   · 窗口级：窗口标题与尺寸、导航栏、快捷键派发、Ctrl+, 热键、设置窗口、启动计时、会话
///   · 窗格级：一切"作用于某个目录"的东西，见 PaneView
///
/// 命令表现阶段仍在这里（按钮用 <c>AncestorType=Window</c> 取命令），
/// 但每个命令的 Execute 都指向 <see cref="Pane"/>，所以语义已经从"窗口的命令"
/// 变成"作用于当前窗格的命令"。下一步搬进 PaneView 时不需要改调用点。
/// </summary>
public partial class MainWindow : Window
{
    private readonly CommandRegistry _commands = new();
    private readonly TabHostViewModel _tabs;
    private bool _flushed;

    /// <summary>
    /// 首帧之后才允许读盘（P1 首帧优先）。标签切换时的懒加载要靠它区分
    /// "启动期还没开始"与"运行期用户切过去了"。
    /// </summary>
    private bool _started;

    /// <summary>正在按当前目录同步导航栏高亮：此时的选择变化不是用户操作。</summary>
    private bool _syncingNavSelection;

    // 窗口级命令只有这些。窗格级命令（刷新、文件操作、视图切换）
    // 在 PaneView 自己的表里，见 PaneView.Commands.cs。
    public CommandAdapter ThemeCommand { get; }

    public MainWindow()
    {
        InitializeComponent();

        // 标签集合归窗口：双窗格上线后两个窗格共用同一排标签，
        // 标签不能属于某一边。浏览区的视图模型由它创建、由窗格 Attach。
        _tabs = new TabHostViewModel(App.Settings, ResolveStartPath);
        _tabs.ActiveChanged += OnActiveTabChanged;
        _tabs.CloseWindowRequested += Close;
        TabBar.Host = _tabs;

        RestoreTabs();
        StartupTrace.Mark("vm");
        StartupTrace.Mark("xaml");

        // 窗口级命令。窗格级的那二十条在 PaneView.Commands.cs 里，
        // 因为它们读的是"当前目录"与"当前选中项"——双窗格下这两个状态各有一份。
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CycleTheme,
            TitleFactory = () => Strings.Command_CycleTheme,
            // 不能用 Ctrl+T / Ctrl+Shift+T：docs/shortcuts.md 已把它们分配给标签页
            Gesture = "Ctrl+Shift+D",
            Execute = () => App.CycleTheme(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.OpenSettings,
            TitleFactory = () => Strings.Command_OpenSettings,
            Gesture = "Ctrl+,",
            Execute = OpenSettings,
        });

        ThemeCommand = new CommandAdapter(_commands.Get(CommandIds.CycleTheme));

        RegisterTabCommands();

        // 窗格工具栏上的设置按钮走这里：窗格只发意图，窗口负责 Show
        Pane.SettingsRequested += OpenSettings;
        StartupTrace.Mark("cmd");

        // 快捷键不用 Window.InputBindings：KeyGesture 对 , 这类 OEM 键的匹配依赖布局，
        // 实测 Ctrl+, 不触发。改为一张显式表 + PreviewKeyDown 派发，见 Input/KeyBindings.cs。
        PreviewKeyDown += OnPreviewKeyDown;

        // 目录监听的订阅在 PaneView.Attach 里——监听的目标是"当前窗格正在看的目录"，
        // 跟着视图模型走，不再由窗口替它挂。

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        CompositionTarget.Rendering += OnFirstRender;

        // 记录最后目录与标签列表供下次启动使用。这个处理器先于 App 的保存处理器执行，
        // 因此退出时写到盘上的是本窗口刚更新的值。
        Closing += (_, _) =>
        {
            // 热键是系统级资源，进程退出前主动交还，避免句柄被回收前的短暂占用
            UnregisterSettingsHotkey();

            if (!string.IsNullOrEmpty(Vm.CurrentPath))
            {
                App.Settings.Session.LastPath = Vm.CurrentPath;
            }

            // 会话恢复（FR-2.11）：只记路径与活动位置，不记滚动与选中
            List<string> paths = _tabs.SnapshotPaths();
            App.Settings.Session.Tabs = paths;
            App.Settings.Session.ActiveTabIndex = Math.Clamp(_tabs.ActiveIndex, 0, Math.Max(0, paths.Count - 1));
        };
    }

    /// <summary>本窗口的浏览区视图模型（= 当前活动标签）。窗格在切换时附加，此后恒不为 null。</summary>
    private PaneViewModel Vm => Pane.ViewModel!;

    // ── 标签页（FR-2） ──────────────────────────────────────────────────────

    /// <summary>
    /// 建立初始标签。会话恢复的全部读盘动作都在这里避开：只给路径、不枚举目录，
    /// 非活动标签一直懒到被激活才读（FR-2.11「不阻塞首帧」的实质）。
    /// 12 个标签的构造成本是 12 个对象 + 12 次 <c>Directory.Exists</c>。
    /// </summary>
    private void RestoreTabs()
    {
        SessionSettings session = App.Settings.Session;

        List<string> saved = App.Settings.Behavior.RestoreLastSession
            ? session.Tabs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        if (saved.Count == 0)
        {
            // 没有可恢复的会话，或恢复项全已失效：开一个默认目录的标签
            _tabs.Activate(_tabs.AddPending(ResolveStartPath()));
            return;
        }

        foreach (string path in saved) _tabs.AddPending(path);

        _tabs.ActivateAt(Math.Clamp(session.ActiveTabIndex, 0, saved.Count - 1));
    }

    private void RegisterTabCommands()
    {
        _commands.Register(new AppCommand
        {
            Id = CommandIds.NewTab,
            Gesture = "Ctrl+T",
            Execute = () => _tabs.Add(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CloseTab,
            Gesture = "Ctrl+W",
            Execute = () => _tabs.Close(_tabs.Active),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ReopenTab,
            Gesture = "Ctrl+Shift+T",
            Execute = _tabs.ReopenClosed,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.NextTab,
            Gesture = "Ctrl+Tab",
            Execute = _tabs.ActivateNext,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.PreviousTab,
            Gesture = "Ctrl+Shift+Tab",
            Execute = _tabs.ActivatePrevious,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.LastTab,
            Gesture = "Ctrl+9",
            Execute = _tabs.ActivateLast,
        });

        // Ctrl+1…8：跳到第 N 个。Ctrl+9 是"最后一个"，不参与这个循环。
        for (int n = 1; n <= 8; n++)
        {
            int index = n - 1;
            _commands.Register(new AppCommand
            {
                Id = CommandIds.GotoTab(n),
                Gesture = $"Ctrl+{n}",
                Execute = () => _tabs.ActivateAt(index),
            });
        }
    }

    /// <summary>
    /// 活动标签变了：把唯一那份浏览区重新绑到新的视图模型。
    /// 滚动位置与选中项在 <see cref="PaneView.Attach"/> 里前后搬移。
    /// </summary>
    private void OnActiveTabChanged(PaneViewModel? previous, PaneViewModel? current)
    {
        if (current is null) return;

        Pane.Attach(current);

        // 窗口标题与导航栏的绑定都挂在窗口 DataContext 上，跟着活动标签走
        DataContext = current;

        SyncNavSelection(current.CurrentPath);

        // 首帧之前不读盘：启动时由 OnLoaded 开始第一次导航（P1 首帧优先）
        if (_started && current.HasPendingContent) _ = NavigateTabAsync(current);
    }

    /// <summary>
    /// 让导航栏的高亮跟上当前目录。
    /// 切标签换了目录，但导航栏的选中项还是上一个标签留下的那一条——
    /// 看起来像是"导航栏指向 A，列表显示 B"。
    /// </summary>
    private void SyncNavSelection(string path)
    {
        if (!App.Settings.Appearance.ShowNavPane) return;

        _syncingNavSelection = true;
        try
        {
            QuickAccessList.SelectedItem = QuickAccessList.Items
                .OfType<NavItem>()
                .FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncingNavSelection = false;
        }
    }

    /// <summary>只读盘、不改历史栈的首次导航（懒加载标签与启动首屏共用）。</summary>
    private static async Task NavigateTabAsync(PaneViewModel tab)
    {
        try
        {
            await tab.NavigateAsync(tab.CurrentPath);
        }
        catch (Exception ex)
        {
            tab.ReportError(ex);
        }
    }

    /// <summary>供单实例转交使用：把当前标签切到指定目录。</summary>
    public void NavigateTo(string path)
    {
        // 首屏还没起来时转交请求先到：直接改当前标签的待办路径
        if (!_started && Vm.HasPendingContent)
        {
            Vm.PreparePendingPath(path);
            return;
        }

        Pane.NavigateTo(path);
    }

    // ── 与设置页的接口 ──────────────────────────────────────────────────────
    // 设置页只认 MainWindow，不认 PaneView。字号变化要按"项目索引"还原滚动位置，
    // 因此这里保留两个转发方法，让设置页不必知道窗格的存在。

    /// <inheritdoc cref="PaneView.CaptureScrollAnchor"/>
    public (int Index, double Offset) CaptureScrollAnchor() => Pane.CaptureScrollAnchor();

    /// <inheritdoc cref="PaneView.RestoreScrollAnchor"/>
    public void RestoreScrollAnchor((int Index, double Offset) anchor) => Pane.RestoreScrollAnchor(anchor);

    /// <summary>设置改动后重新套用受影响的界面状态（不重建窗口）。</summary>
    public void ApplySettingsToUi()
    {
        AppSettings s = App.Settings;

        NavColumn.Width = s.Appearance.ShowNavPane
            ? new GridLength(1, GridUnitType.Auto)
            : new GridLength(0);
        QuickAccessList.Visibility = s.Appearance.ShowNavPane ? Visibility.Visible : Visibility.Collapsed;

        // 导航栏底部的占位条跟随状态栏：状态栏隐藏时它也不该留着一条空横线
        NavStatusSpacer.Visibility = s.Appearance.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;

        Pane.ApplyAppearance(s);

        if (KeyDebug)
        {
            WriteKeyLog($"ui statusBar={s.Appearance.ShowStatusBar} " +
                        $"visibility={Pane.StatusBarVisibility} " +
                        $"statusText=\"{Vm.StatusText}\"");
        }
    }

    private void OpenSettings()
    {
        // 单例：重复点"设置"不应叠出多个窗口
        if (Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow { Owner = this };
        window.Show();
    }

    // ── 快捷键 ──────────────────────────────────────────────────────────────

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const ModifierKeys RelevantModifiers =
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    /// <summary>
    /// 按键诊断开关：`--keydebug` 命令行参数或环境变量 <c>LINGPANE_KEYDEBUG=1</c>。
    /// 开启后把每条按键消息写进 logs/keydebug.log，用于排查输入法吃键。
    /// </summary>
    public static bool KeyDebug { get; set; } =
        Environment.GetEnvironmentVariable("LINGPANE_KEYDEBUG") == "1";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AttachKeyHook();
    }

    /// <summary>
    /// 快捷键的主入口是窗口消息，不是 WPF 的按键事件：
    /// 输入法（TSF/IME）在 WPF 之前改写 Key，Ctrl+, 这类标点组合经常收不到 PreviewKeyDown；
    /// 而物理键码（VK）在同一条 WM_KEYDOWN 里始终准确。
    ///
    /// 两个时机都要挂：<c>OnSourceInitialized</c> 时 HwndSource 已建好，
    /// 但窗口首次 <c>Show()</c> 若触发了 source 重建，早先挂的钩子就留在旧对象上。
    /// <c>RemoveHook</c> 保证重复挂不会执行两次。
    /// </summary>
    private void AttachKeyHook()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source is null) return;

        source.RemoveHook(OnWindowMessage);
        source.AddHook(OnWindowMessage);

        if (KeyDebug) WriteKeyLog($"hook attached hwnd=0x{handle:X} source=0x{source.Handle:X}");
    }

    // ── 窗口热键：Ctrl+, 的最后一道保障 ────────────────────────────────────
    //
    // 实测结论（2026-11-04）：注入 WM_KEYDOWN 能触发，真实按键盘不能。
    // 已排除"其它程序占用了 Ctrl+, 全局热键"（RegisterHotKey 在裸进程里注册成功）。
    // 剩下的可能都在输入链路的下层：输入法 hook、键盘布局、以及 WPF 对 OEM 键的改写。
    // 这些都无法从进程内部断言，所以不再猜——直接改走系统热键通道：
    //
    //   RegisterHotKey 由系统在把按键分发给前台窗口之前处理，命中时投递 WM_HOTKEY
    //   到注册者窗口，按键不再作为普通输入下发。输入法、WPF 的 Key 归一化、
    //   TranslateMessage 全被绕过。
    //
    // 只在窗口激活期间注册，失活立即注销：热键是系统级的，长期持有会把
    // 其它程序的 Ctrl+, 也抢过来。这是"本窗口内生效"与"不打扰别人"的折中。

    private const int HotkeySettings = 0x4C50;          // 任意固定 id，仅本窗口使用
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;            // 长按不重复触发
    private const int VkOemComma = 0xBC;

    private IntPtr _hotkeyWindow;
    private bool _hotkeyRegistered;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RegisterSettingsHotkey();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        UnregisterSettingsHotkey();
        base.OnDeactivated(e);
    }

    private void RegisterSettingsHotkey()
    {
        if (_hotkeyRegistered) return;

        // 探针进程不注册：它是系统级热键，诊断期间抢走用户的 Ctrl+, 属于副作用。
        // 顺带让 AC-05 的探针与用户实例并存时不再刷 E7001（err=1409 已被占用）。
        if (ProbePath is not null) return;

        _hotkeyWindow = new WindowInteropHelper(this).Handle;
        if (_hotkeyWindow == IntPtr.Zero) return;

        if (RegisterHotKey(_hotkeyWindow, HotkeySettings, ModControl | ModNoRepeat, VkOemComma))
        {
            _hotkeyRegistered = true;
            if (KeyDebug) WriteKeyLog("hotkey registered: Ctrl+,");
            return;
        }

        // 注册失败几乎只有两种原因：别的程序已占用该组合，或窗口句柄失效。
        // 这条一定要落盘——它决定了用户报告的"按了没反应"到底怪谁。
        // 成功路径不写盘（每次激活都会走这里），失败才留痕。
        int error = Marshal.GetLastWin32Error();
        Diagnostics.AppLog.Warn("hotkey.register", "E7001", $"Ctrl+, 注册失败 err={error}");  // i18n-ignore：只进 app.log
        if (KeyDebug) WriteKeyLog($"hotkey register FAILED err={error}");
    }

    private void UnregisterSettingsHotkey()
    {
        if (!_hotkeyRegistered) return;

        UnregisterHotKey(_hotkeyWindow, HotkeySettings);
        _hotkeyRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            if (wParam.ToInt32() != HotkeySettings) return IntPtr.Zero;

            if (KeyDebug) WriteKeyLog("wm_hotkey -> app.openSettings");
            handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Dispatch(CommandIds.OpenSettings));
            return IntPtr.Zero;
        }

        if (msg is not (WmKeyDown or WmSysKeyDown)) return IntPtr.Zero;

        if ((lParam.ToInt64() & (1L << 30)) != 0) return IntPtr.Zero;   // 长按重复，忽略

        int vk = wParam.ToInt32();
        ModifierKeys modifiers = Keyboard.Modifiers & RelevantModifiers;
        bool listFocused = Pane.IsListFocused;

        if (KeyDebug)
        {
            WriteKeyLog($"vk=0x{vk:X2} mods={modifiers} listFocus={listFocused} " +
                        $"focus={Keyboard.FocusedElement?.GetType().Name ?? "null"}");
        }

        if (Keyboard.FocusedElement is TextBoxBase)
        {
            // 输入框里：文本编辑键与所有无修饰键都归输入框，
            // 否则地址栏的 Delete 会变成"删除文件"、Enter 会变成"打开选中项"。
            if (Input.KeyBindings.IsTextEditingKey(vk) || modifiers == ModifierKeys.None)
            {
                return IntPtr.Zero;
            }
        }

        // 无修饰键只在列表有焦点时接管：焦点在按钮上按 Enter 应触发按钮，而不是"打开选中项"
        if (modifiers == ModifierKeys.None && !listFocused) return IntPtr.Zero;

        if (!Input.KeyBindings.TryMatchVirtualKey(vk, out string commandId)) return IntPtr.Zero;

        if (KeyDebug) WriteKeyLog($"  -> {commandId}");

        // 匹配到就吞掉，即使命令此刻不可执行——否则 ListBox 会把它当成"首字母定位"
        handled = true;

        // 消息泵里直接执行可能重入（命令会 Show 窗口），交回 Dispatcher 排队
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Dispatch(commandId));
        return IntPtr.Zero;
    }

    /// <summary>兜底：没有物理键的合成输入（自动化脚本等）不会经过 WM_KEYDOWN 匹配。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase) return;

        if (KeyDebug) WriteKeyLog($"preview key={e.Key} sys={e.SystemKey} ime={e.ImeProcessedKey}");

        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        if (!Input.KeyBindings.TryMatchKey(key, out string commandId)) return;

        if (KeyDebug) WriteKeyLog($"  preview -> {commandId}");

        e.Handled = true;
        Dispatch(commandId);
    }

    /// <summary>
    /// 快捷键派发。**先查窗格、再查窗口。**
    ///
    /// 判据是"这条命令注册在哪张表里"，不是"此刻能不能执行"——
    /// 用可执行性判断会让 F5 在加载中被误判给窗口表（那里根本没有 F5），
    /// 也会让"删除"在没有选中时落到别处。
    /// </summary>
    private void Dispatch(string commandId)
    {
        if (Pane.HasCommand(commandId))
        {
            Pane.TryInvoke(commandId);
            return;
        }

        _commands.Invoke(commandId);
    }

    internal static void WriteKeyLog(string message)    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "keydebug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 诊断日志写不进去就放弃
        }
    }

    /// <summary>首次渲染回调：比 ContentRendered 更早、更准确反映"画面出现"。</summary>
    private void OnFirstRender(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnFirstRender;
        StartupTrace.Mark("render");
    }

    /// <summary>首帧落屏时间：用户"看到窗口"的时刻。</summary>
    private void OnContentRendered(object? sender, EventArgs e)
    {
        StartupTrace.Mark("firstframe");
        Pane.LogColumnLayout();
        FlushOnce();
    }

    /// <summary>窗口加载后异步读目录：先出窗口，再出内容（P1 首帧优先）。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        StartupTrace.Mark("loaded");

        // Show() 之后再挂一次：source 若被重建，钩子要落在新对象上
        AttachKeyHook();

        ApplySettingsToUi();
        StartupTrace.Mark("uisetup");

        // 诊断模式接管：测完自行退出，不写启动日志的 normal 路径
        if (ProbePath is not null)
        {
            await RunPerfProbeAsync();
            return;
        }

        // 从这里开始，切换标签才允许读盘
        _started = true;

        try
        {
            // 只导航当前标签；其余标签保持 pending，切过去时才枚举
            await NavigateTabAsync(Vm);
            StartupTrace.Mark("firstcontent");
            FlushOnce();
        }
        catch (Exception ex)
        {
            Vm.ReportError(ex);
        }
    }

    private async void OnQuickAccessSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切标签时同步高亮会走到这里：那是"跟着目录改高亮"，不是"用户点了导航项"
        if (_syncingNavSelection) return;

        if (QuickAccessList.SelectedItem is not NavItem item) return;
        if (string.Equals(item.Path, Vm.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            await Vm.NavigateAsync(item.Path);
        }
        catch (Exception ex)
        {
            Vm.ReportError(ex);
        }
    }

    /// <summary>
    /// 启动目录：上次路径优先（session.lastPath），其次"文档"，最后用户目录。
    /// 上次路径可能已被删除，因此必须校验存在性，否则首屏是一片"无法打开"。
    /// </summary>
    private static string ResolveStartPath()
    {
        // 命令行带的目录优先：用户双击文件/快捷方式指定了位置，不该被上次的目录覆盖
        if (!string.IsNullOrEmpty(App.StartupDirectory)) return App.StartupDirectory;

        string? last = App.Settings.Session.LastPath;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents) && Directory.Exists(documents)) return documents;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : AppContext.BaseDirectory;
    }

    private void FlushOnce()
    {
        if (_flushed) return;
        _flushed = true;
        StartupTrace.Flush();
    }
}
