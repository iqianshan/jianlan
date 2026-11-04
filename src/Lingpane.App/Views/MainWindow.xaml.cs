using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Lingpane.App.Commands;
using Lingpane.App.Services;
using Lingpane.App.ViewModels;
using Lingpane.Core.Commands;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Settings;

namespace Lingpane.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CommandRegistry _commands = new();
    private bool _flushed;

    public CommandAdapter UpCommand { get; }
    public CommandAdapter BackCommand { get; }
    public CommandAdapter ForwardCommand { get; }
    public CommandAdapter RefreshCommand { get; }
    public CommandAdapter ThemeCommand { get; }
    public CommandAdapter DetailsCommand { get; }
    public CommandAdapter ListViewCommand { get; }
    public CommandAdapter SettingsCommand { get; }
    public CommandAdapter OpenItemCommand { get; }
    public CommandAdapter NewFolderCommand { get; }
    public CommandAdapter RenameCommand { get; }
    public CommandAdapter DeleteCommand { get; }
    public CommandAdapter DeletePermanentCommand { get; }
    public CommandAdapter CopyItemsCommand { get; }
    public CommandAdapter CutItemsCommand { get; }
    public CommandAdapter PasteCommand { get; }
    public CommandAdapter CopyPathCommand { get; }
    public CommandAdapter PropertiesCommand { get; }

    public MainWindow()
    {
        // 设置必须在创建视图模型前就绪：默认视图、排序规则都来自设置
        _viewModel = new MainViewModel(App.Settings);

        InitializeComponent();
        DataContext = _viewModel;

        // 列宽要在首帧前就位，否则会看到一次"默认宽度 → 记忆宽度"的跳动
        ApplyColumnWidths();

        // 命令在此集中注册：菜单、快捷键、工具栏按钮都从同一张表取，
        // 避免同一功能在三处各写一份逻辑（ADR-0004 无容器，直接构造即可）。
        _commands.Register(new AppCommand
        {
            Id = CommandIds.GoUp,
            Title = "上级",
            Gesture = "Alt+↑",
            CanExecute = CanGoUp,
            Execute = GoUp,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.GoBack,
            Title = "后退",
            Gesture = "Alt+←",
            CanExecute = () => !_viewModel.IsLoading && _viewModel.CanGoBack,
            Execute = () => _ = _viewModel.GoBackAsync(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.GoForward,
            Title = "前进",
            Gesture = "Alt+→",
            CanExecute = () => !_viewModel.IsLoading && _viewModel.CanGoForward,
            Execute = () => _ = _viewModel.GoForwardAsync(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Refresh,
            Title = "刷新",
            Gesture = "F5",
            CanExecute = () => !_viewModel.IsLoading,
            Execute = () => _ = _viewModel.RefreshAsync(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CycleTheme,
            Title = "主题",
            // 不能用 Ctrl+T / Ctrl+Shift+T：docs/shortcuts.md 已把它们分配给标签页
            Gesture = "Ctrl+Shift+D",
            Execute = () => App.CycleTheme(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ViewDetails,
            Title = "详细信息视图",
            Gesture = "Ctrl+Shift+1",
            Execute = () => _viewModel.SetViewMode(DefaultViewKind.Details),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ViewList,
            Title = "列表视图",
            Gesture = "Ctrl+Shift+2",
            Execute = () => _viewModel.SetViewMode(DefaultViewKind.List),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.OpenSettings,
            Title = "设置",
            Gesture = "Ctrl+,",
            Execute = OpenSettings,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.FocusAddress,
            Title = "定位地址栏",
            Gesture = "Ctrl+L",
            Execute = BeginAddressEdit,
        });

        // 文件操作（M1 批 3）。可执行性由选中状态与当前目录决定，
        // 右键菜单与快捷键共用这一份判断，不各写一遍。
        _commands.Register(new AppCommand
        {
            Id = CommandIds.OpenItem,
            Title = "打开",
            Execute = OpenSelection,
            CanExecute = HasSingleSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.NewFolder,
            Title = "新建文件夹",
            Execute = () => _ = NewFolderAsync(),
            CanExecute = CanCreateInCurrentDirectory,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Rename,
            Title = "重命名",
            Execute = () => _ = RenameSelectionAsync(),
            CanExecute = HasSingleSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Delete,
            Title = "删除",
            Execute = () => _ = DeleteSelectionAsync(permanent: false),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.DeletePermanent,
            Title = "永久删除",
            Execute = () => _ = DeleteSelectionAsync(permanent: true),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CopyItems,
            Title = "复制",
            Execute = () => CopySelection(cut: false),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CutItems,
            Title = "剪切",
            Execute = () => CopySelection(cut: true),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Paste,
            Title = "粘贴",
            Execute = () => _ = PasteAsync(),
            CanExecute = CanPaste,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CopyPath,
            Title = "复制完整路径",
            Execute = CopyPathSelection,
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Properties,
            Title = "属性",
            Execute = ShowProperties,
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ShowContextMenu,
            Title = "更多操作",
            Execute = ShowContextMenu,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.SelectAll,
            Title = "全选",
            Execute = SelectAllItems,
            CanExecute = () => !_viewModel.IsLoading && FileList.Items.Count > 0,
        });

        UpCommand = new CommandAdapter(_commands.Get(CommandIds.GoUp));
        BackCommand = new CommandAdapter(_commands.Get(CommandIds.GoBack));
        ForwardCommand = new CommandAdapter(_commands.Get(CommandIds.GoForward));
        RefreshCommand = new CommandAdapter(_commands.Get(CommandIds.Refresh));
        ThemeCommand = new CommandAdapter(_commands.Get(CommandIds.CycleTheme));
        DetailsCommand = new CommandAdapter(_commands.Get(CommandIds.ViewDetails));
        ListViewCommand = new CommandAdapter(_commands.Get(CommandIds.ViewList));
        SettingsCommand = new CommandAdapter(_commands.Get(CommandIds.OpenSettings));
        OpenItemCommand = new CommandAdapter(_commands.Get(CommandIds.OpenItem));
        NewFolderCommand = new CommandAdapter(_commands.Get(CommandIds.NewFolder));
        RenameCommand = new CommandAdapter(_commands.Get(CommandIds.Rename));
        DeleteCommand = new CommandAdapter(_commands.Get(CommandIds.Delete));
        DeletePermanentCommand = new CommandAdapter(_commands.Get(CommandIds.DeletePermanent));
        CopyItemsCommand = new CommandAdapter(_commands.Get(CommandIds.CopyItems));
        CutItemsCommand = new CommandAdapter(_commands.Get(CommandIds.CutItems));
        PasteCommand = new CommandAdapter(_commands.Get(CommandIds.Paste));
        CopyPathCommand = new CommandAdapter(_commands.Get(CommandIds.CopyPath));
        PropertiesCommand = new CommandAdapter(_commands.Get(CommandIds.Properties));

        // 快捷键不用 Window.InputBindings：KeyGesture 对 , 这类 OEM 键的匹配依赖布局，
        // 实测 Ctrl+, 不触发。改为一张显式表 + PreviewKeyDown 派发，见 Input/KeyBindings.cs。
        PreviewKeyDown += OnPreviewKeyDown;

        // 目录一换就换监听目标
        _viewModel.NavigationCompleted += StartWatching;
        _viewModel.NavigationCompleted += ScheduleFolderIcons;

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        CompositionTarget.Rendering += OnFirstRender;

        // 记录最后目录供下次启动使用。这个处理器先于 App 的保存处理器执行，
        // 因此退出时写到盘上的是本窗口刚更新的值。
        Closing += (_, _) =>
        {
            // 热键是系统级资源，进程退出前主动交还，避免句柄被回收前的短暂占用
            UnregisterSettingsHotkey();

            if (!string.IsNullOrEmpty(_viewModel.CurrentPath))
            {
                App.Settings.Session.LastPath = _viewModel.CurrentPath;
            }
        };
    }

    /// <summary>
    /// 能否返回上级。注意 CurrentPath 在首次导航前是空串，
    /// 而按钮绑定会在窗口加载时就查询 CanExecute——此处必须挡住空值，
    /// 否则 Directory.GetParent("") 直接抛 ArgumentException，启动即崩。
    /// </summary>
    private bool CanGoUp()
    {
        string path = _viewModel.CurrentPath;
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            return Directory.GetParent(path) is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void GoUp()
    {
        if (!CanGoUp()) return;

        string parent = Directory.GetParent(_viewModel.CurrentPath)!.FullName;
        _ = _viewModel.NavigateAsync(parent);
    }

    /// <summary>供单实例转交使用：把已打开的窗口切到指定目录。</summary>
    public void NavigateTo(string path) => _ = _viewModel.NavigateAsync(path);

    /// <summary>设置改动后重新套用受影响的界面状态（不重建窗口）。</summary>
    public void ApplySettingsToUi()
    {
        AppSettings s = App.Settings;

        NavColumn.Width = s.Appearance.ShowNavPane
            ? new GridLength(1, GridUnitType.Auto)
            : new GridLength(0);
        QuickAccessList.Visibility = s.Appearance.ShowNavPane ? Visibility.Visible : Visibility.Collapsed;

        StatusBarHost.Visibility = s.Appearance.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;

        ApplyColumnWidths();

        _viewModel.ApplyShowExtensions(s.Behavior.ShowExtensions);
        _viewModel.ApplyFilter();

        if (KeyDebug)
        {
            WriteKeyLog($"ui statusBar={s.Appearance.ShowStatusBar} " +
                        $"visibility={StatusBarHost.Visibility} height={StatusBarHost.Height} " +
                        $"actual={StatusBarHost.ActualHeight} statusText=\"{_viewModel.StatusText}\"");
        }
    }

    // ── 列宽 ────────────────────────────────────────────────────────────────
    //
    // 列宽不放 ViewModel：它只影响显示，且行模板与列头共用同三个 DynamicResource
    // 令牌（Column.Date/Type/Size.Width），改令牌即全局生效，不需要重建列表。
    // 名称列是星号列，宽度随窗口伸缩，因此不参与拖拽。

    private void ApplyColumnWidths()
    {
        AppSettings s = App.Settings;
        Resources["Column.Date.Width"] = s.Layout.ColumnDateWidth;
        Resources["Column.Type.Width"] = s.Layout.ColumnTypeWidth;
        Resources["Column.Size.Width"] = s.Layout.ColumnSizeWidth;

        if (KeyDebug)
        {
            WriteKeyLog($"columns in={s.Layout.ColumnDateWidth}/{s.Layout.ColumnTypeWidth}/{s.Layout.ColumnSizeWidth}");
        }
    }

    /// <summary>布局诊断：列头实际宽度与可视位置，确认"列被裁掉"还是"列不存在"。</summary>
    private void LogColumnLayout()
    {
        if (!KeyDebug) return;

        WriteKeyLog($"layout window={ActualWidth:0}x{ActualHeight:0} list={FileList.ActualWidth:0} " +
                    $"header={HeaderGrid.ActualWidth:0} margin={HeaderGrid.Margin}");

        for (int i = 0; i < HeaderGrid.ColumnDefinitions.Count; i++)
        {
            WriteKeyLog($"layout col{i}={HeaderGrid.ColumnDefinitions[i].ActualWidth:0.#}");
        }

        foreach (Button button in new[] { NameHeaderButton, DateHeaderButton, TypeHeaderButton, SizeHeaderButton })
        {
            if (button is null) continue;

            Point origin = button.TranslatePoint(new Point(0, 0), FileList);
            Point screen = button.PointToScreen(new Point(0, 0));
            WriteKeyLog($"layout {button.Tag}: w={button.ActualWidth:0.#} x={origin.X:0.#} " +
                        $"screenX={screen.X:0.#} screenY={screen.Y:0.#} content=\"{button.Content}\"");
        }

        // 把手的实际屏幕矩形：拖拽验证（模拟输入）必须按这个坐标落点，
        // 按列头坐标推算容易在缩放/边框上差几像素而打空。
        foreach (Thumb thumb in HeaderGrid.Children.OfType<Thumb>())
        {
            Point p = thumb.PointToScreen(new Point(0, 0));
            WriteKeyLog($"layout thumb {thumb.Tag}: x={p.X:0.#} y={p.Y:0.#} " +
                        $"w={thumb.ActualWidth:0.#} h={thumb.ActualHeight:0.#}");
        }
    }

    // 名称列（星号列）的最小可视宽度，与 DesignTokens 的 Column.Name.MinWidth 对齐。
    // 拖宽其他列时不能把名称列压到这条线以下，否则文件名不可读、
    // 且星号列被压到 0 后 Grid 会整体溢出，把最右侧的列挤出可视区。
    private const double MinNameWidth = 160;

    private void OnColumnResize(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string token }) return;

        double current = ReadColumnWidth(token);

        // 把手挂在列的左边缘：向右拖（HorizontalChange > 0）= 把分隔线右推，
        // 本列变窄、左邻列变宽；向左拖则相反。取反后分隔线始终跟着鼠标，
        // 而不是"列宽变了、边界却不动"。
        double next = current - e.HorizontalChange;

        // 只有本列变宽时受限：最多吃掉名称列让出的富余宽度，避免溢出
        if (next > current)
        {
            double nameSlack = HeaderGrid.ColumnDefinitions[2].ActualWidth - MinNameWidth;
            next = Math.Min(next, current + Math.Max(0, nameSlack));
        }

        next = Math.Clamp(next, AppSettings.MinColumnWidth, AppSettings.MaxColumnWidth);

        if (Math.Abs(next - current) < 0.5) return;

        WriteColumnWidth(token, next);
        Resources[token] = next;
    }

    private void OnColumnResizeDone(object sender, DragCompletedEventArgs e)
    {
        if (sender is not Thumb thumb) return;

        // 一次拖拽只落盘一次，不跟随每个 DragDelta 写文件
        App.SaveSettings();

        if (KeyDebug)
        {
            AppSettings s = App.Settings;
            WriteKeyLog($"columns done {thumb.Tag} delta={e.HorizontalChange:0.#} " +
                        $"out={s.Layout.ColumnDateWidth:0.#}/{s.Layout.ColumnTypeWidth:0.#}/{s.Layout.ColumnSizeWidth:0.#}");
        }
    }

    private static double ReadColumnWidth(string token) => token switch
    {
        "Column.Date.Width" => App.Settings.Layout.ColumnDateWidth,
        "Column.Type.Width" => App.Settings.Layout.ColumnTypeWidth,
        _ => App.Settings.Layout.ColumnSizeWidth,
    };

    private static void WriteColumnWidth(string token, double value)
    {
        switch (token)
        {
            case "Column.Date.Width":
                App.Settings.Layout.ColumnDateWidth = value;
                break;
            case "Column.Type.Width":
                App.Settings.Layout.ColumnTypeWidth = value;
                break;
            default:
                App.Settings.Layout.ColumnSizeWidth = value;
                break;
        }
    }

    private void OpenSettings()
    {        // 单例：重复点"设置"不应叠出多个窗口
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
        Diagnostics.AppLog.Warn("hotkey.register", "E7001", $"Ctrl+, 注册失败 err={error}");
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
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _commands.Invoke(CommandIds.OpenSettings));
            return IntPtr.Zero;
        }

        if (msg is not (WmKeyDown or WmSysKeyDown)) return IntPtr.Zero;

        if ((lParam.ToInt64() & (1L << 30)) != 0) return IntPtr.Zero;   // 长按重复，忽略

        int vk = wParam.ToInt32();
        ModifierKeys modifiers = Keyboard.Modifiers & RelevantModifiers;
        bool listFocused = FileList.IsKeyboardFocusWithin;

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
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _commands.Invoke(commandId));
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
        _commands.Invoke(commandId);
    }

    private static void WriteKeyLog(string message)
    {
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

    /// <summary>键盘菜单键（VK_APPS）弹出上下文菜单，位置对齐列表。</summary>
    private void ShowContextMenu()
    {
        if (FileList.ContextMenu is not { } menu) return;

        menu.PlacementTarget = FileList;
        menu.Placement = PlacementMode.Center;
        menu.IsOpen = true;
    }

    /// <summary>WPF 的 ListBox 在 Extended 模式下没有内建 Ctrl+A，需要自己调 SelectAll。</summary>
    private void SelectAllItems()
    {
        FileList.SelectAll();
        CommandManager.InvalidateRequerySuggested();
    }

    // ── 命令可执行性 ────────────────────────────────────────────────────────

    private bool HasSelection() => !_viewModel.IsLoading && _viewModel.SelectedEntries.Count > 0;

    private bool HasSingleSelection() => !_viewModel.IsLoading && _viewModel.SelectedEntries.Count == 1;

    private bool CanCreateInCurrentDirectory()
        => !_viewModel.IsLoading && !string.IsNullOrEmpty(_viewModel.CurrentPath);

    private bool CanPaste()
        => !_viewModel.IsLoading && FileClipboard.HasData && !string.IsNullOrEmpty(_viewModel.CurrentPath);

    /// <summary>
    /// 记录列表滚动位置。字号/行高变化会导致偏移量失真，
    /// 设置页据此按项目索引还原，避免改字号后列表跳回顶部（FONT-05）。
    /// </summary>
    public (int Index, double Offset) CaptureScrollAnchor()
        => Typography.FontScaleService.CaptureAnchor(FindScrollViewer());

    public void RestoreScrollAnchor((int Index, double Offset) anchor)
        => Typography.FontScaleService.RestoreAnchor(FindScrollViewer(), anchor);

    private ScrollViewer? FindScrollViewer()
    {
        return FindDescendant<ScrollViewer>(FileList);

        static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                if (FindDescendant<T>(child) is { } nested) return nested;
            }

            return null;
        }
    }

    // ── 地址栏 ──────────────────────────────────────────────────────────────

    private void BeginAddressEdit()
    {
        if (string.IsNullOrEmpty(_viewModel.CurrentPath)) return;

        _viewModel.IsAddressEditing = true;
        AddressBox.Text = _viewModel.CurrentPath;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndAddressEdit() => _viewModel.IsAddressEditing = false;

    private async void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                string target = AddressBox.Text;
                EndAddressEdit();
                if (!string.IsNullOrWhiteSpace(target) && !Directory.Exists(target))
                {
                    _viewModel.SetHint($"路径不存在：{target}");
                    return;
                }

                await _viewModel.NavigateAsync(target);
                e.Handled = true;
                break;

            case Key.Escape:
                EndAddressEdit();
                e.Handled = true;
                break;
        }
    }

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndAddressEdit();

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && !string.IsNullOrEmpty(path))
        {
            _ = _viewModel.NavigateAsync(path);
        }
    }

    // ── 列表交互 ────────────────────────────────────────────────────────────

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field }) return;
        if (!Enum.TryParse<SortFieldKind>(field, ignoreCase: true, out var kind)) return;

        _viewModel.SortBy(kind);
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        long total = 0;
        int count = 0;
        var selected = new List<FileEntry>(FileList.SelectedItems.Count);

        foreach (object item in FileList.SelectedItems)
        {
            if (item is not FileEntry entry) continue;
            selected.Add(entry);
            count++;
            total += entry.Size;
        }

        _viewModel.SetSelection(count, total);
        _viewModel.SetSelectedEntries(selected);

        // 选中数变化影响"打开/重命名/删除"等命令的可执行性，右键菜单打开前必须已刷新
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// 右键菜单的键位提示从快捷键表里取，与真实绑定同源。
    /// 写在 XAML 里就会出现"菜单写着 Ctrl+,，实际却是别的键"这类不同步。
    /// </summary>
    private void ApplyMenuGestures()
    {
        if (FileList.ContextMenu is not { } menu) return;

        foreach (object item in menu.Items)
        {
            if (item is not MenuItem { Command: CommandAdapter adapter } menuItem) continue;

            menuItem.InputGestureText = Input.KeyBindings.Gestures.TryGetValue(adapter.Source.Id, out string? text)
                ? text
                : string.Empty;
        }
    }

    /// <summary>右键先选中行再弹菜单：不选中的话菜单里的操作对象是上一批选中项。</summary>
    private void OnFileRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source) is not { } item) return;

        if (!item.IsSelected)
        {
            FileList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source) is not { DataContext: FileEntry entry }) return;

        OpenEntry(entry);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node as T;
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
        LogColumnLayout();
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
        ApplyMenuGestures();

        // 诊断模式接管：测完自行退出，不写启动日志的 normal 路径
        if (ProbePath is not null)
        {
            await RunPerfProbeAsync();
            return;
        }

        try
        {
            await _viewModel.NavigateAsync(ResolveStartPath());
            StartupTrace.Mark("firstcontent");
            FlushOnce();
        }
        catch (Exception ex)
        {
            _viewModel.ReportError(ex);
        }
    }

    private async void OnQuickAccessSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QuickAccessList.SelectedItem is not NavItem item) return;
        if (string.Equals(item.Path, _viewModel.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            await _viewModel.NavigateAsync(item.Path);
        }
        catch (Exception ex)
        {
            _viewModel.ReportError(ex);
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
