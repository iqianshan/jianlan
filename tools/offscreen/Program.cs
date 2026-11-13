using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Jianlan.App;
using Jianlan.App.ViewModels;
using Jianlan.App.Views;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Tagging;

namespace OffscreenShot;

/// <summary>
/// 离屏截图：按真实启动路径把主窗口建起来，等它就绪后用 RenderTargetBitmap 画一张图。
///
/// 为什么需要它：桌面被锁或占着别的程序时，抓屏只能拍到壁纸，而 WPF 内容由 DWM 合成，
/// PrintWindow 也拿不到（试过，全白）。在自己的进程里渲染不依赖桌面是否可见。
///
/// 用法：
///   OffscreenShot.exe &lt;输出png&gt; [等待毫秒] [--tag 颜色名] [--activate 序号]
///                                        [--preview] [--select 序号] [--close-preview]
///                                        [--search 关键词]
///                                        [--settings [--settings-page 0..5] [--scroll-to 控件名]]
///                                        [--about] [--about-license license|notice|thirdparty]
///                                        [--tab N] [--lang 语言] [--theme 主题]
/// 数据目录由 exe 旁边的 portable.flag + data\ 决定（便携模式），演示数据自己放进去。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] argv)
    {
        // 输出被 Python 脚本按 UTF-8 读，不设这个会得到一屏乱码
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 回收站探针：不启动界面，只把回收站里的条目与可用动词打出来。
        // 还原动词是本地化的，靠猜没用——先看清楚这台机器上到底叫什么
        if (argv.Contains("--bin-probe"))
        {
            return BinProbe(argv.SkipWhile(a => a != "--bin-probe").Skip(1).FirstOrDefault());
        }

        // 窗口图标探针：读一个**已经跑起来的进程**交给系统的图标，落成 PNG。
        // --icon-probe 看的是本工具自己建的窗口，受宿主影响；这个看的是真实 exe 在真实桌面上
        // 被任务栏取走的那一张，两者不是一回事——"代码里设了"和"任务栏显示了"中间还隔着系统
        if (argv.Contains("--probe-window-icon"))
        {
            string[] rest = argv.SkipWhile(a => a != "--probe-window-icon").Skip(1).ToArray();

            if (rest.Length < 2 || !int.TryParse(rest[0], out int targetPid))
            {
                Console.WriteLine("用法：--probe-window-icon <pid> <out.png>");
                return 1;
            }

            return ProbeWindowIcon(targetPid, Path.GetFullPath(rest[1]));
        }

        // 桌面截屏：进程内状态能打印，"任务栏到底画了什么"只能看屏幕
        if (argv.Contains("--screen-shot"))
        {
            string[] rest = argv.SkipWhile(a => a != "--screen-shot").Skip(1).ToArray();

            if (rest.Length < 1)
            {
                Console.WriteLine("用法：--screen-shot <out.png> [x y w h]");
                return 1;
            }

            int[] rect = [.. rest.Skip(1).Take(4).Select(v => int.TryParse(v, out int n) ? n : 0)];

            return ScreenShot(
                Path.GetFullPath(rest[0]),
                rect.Length > 0 ? rect[0] : 0,
                rect.Length > 1 ? rect[1] : 0,
                rect.Length > 2 ? rect[2] : 0,
                rect.Length > 3 ? rect[3] : 0);
        }

        // 建快捷方式：任务栏对没有 shell 快捷方式的桌面程序会退回默认图标，
        // 用它验证「开始菜单里有 lnk 之后图标是否恢复正常」
        if (argv.Contains("--make-shortcut"))
        {
            string[] rest = argv.SkipWhile(a => a != "--make-shortcut").Skip(1).ToArray();

            if (rest.Length < 2)
            {
                Console.WriteLine("用法：--make-shortcut <targetExe> <out.lnk>");
                return 1;
            }

            return MakeShortcut(Path.GetFullPath(rest[0]), Path.GetFullPath(rest[1]));
        }

        // 进程顶层窗口清单：确认任务栏按钮对应的是哪个窗口，
        // 以及那个窗口有没有 WS_EX_APPWINDOW（没有就不会出现在任务栏）
        if (argv.Contains("--probe-process-windows"))
        {
            string[] rest = argv.SkipWhile(a => a != "--probe-process-windows").Skip(1).ToArray();

            if (rest.Length < 1 || !int.TryParse(rest[0], out int targetPid))
            {
                Console.WriteLine("用法：--probe-process-windows <pid>");
                return 1;
            }

            return ProbeProcessWindows(targetPid);
        }

        // exe 图标探针：问的是**系统眼里这个 exe 是什么图标**，与窗口自己设的 Icon 无关。
        // 任务栏按钮、资源管理器、快捷方式用的都是这一份，返回缓存值也照样反映出来
        if (argv.Contains("--probe-exe-icon"))
        {
            string[] rest = argv.SkipWhile(a => a != "--probe-exe-icon").Skip(1).ToArray();

            if (rest.Length < 2)
            {
                Console.WriteLine("用法：--probe-exe-icon <exe> <out.png>");
                return 1;
            }

            return ProbeExeIcon(Path.GetFullPath(rest[0]), Path.GetFullPath(rest[1]));
        }

        string outPath = argv.Length > 0
            ? Path.GetFullPath(argv[0])
            : Path.Combine(Environment.CurrentDirectory, "shot.png");

        int waitMs = argv.Length > 1 && int.TryParse(argv[1], out int parsed) ? parsed : 4000;

        string? tagName = null;
        int activateIndex = -1;
        int[] selectIndexes = [];
        bool enablePreview = false;
        bool disablePreview = false;
        string? surfacePath = null;
        bool dumpTree = false;
        bool printState = false;
        int thenIndex = -1;
        string? searchQuery = null;
        bool focusSearch = false;
        double navResizeDx = 0;
        bool navReset = false;
        string? navMiddleLabel = null;
        string? navLeftLabel = null;
        bool navPlacesMenu = false;
        List<string> navPlacesToggle = [];
        bool printPaneStatus = false;
        bool printRect = false;
        int xButton = 0;
        double[]? rubberBand = null;
        Rect? bandRect = null;
        List<string> tabDropPaths = [];
        int tabDropTarget = -1;
        string? thenSearch = null;
        string? navPath = null;
        bool enableSplit = false;
        bool disableSplit = false;
        bool thenRefresh = false;
        string? thenPath = null;
        bool thenEscape = false;
        bool showSettings = false;
        string? scrollTo = null;
        int settingsPage = -1;
        bool settingsMetrics = false;
        int fontSizePt = 0;
        int cacheLimitMb = -1;
        bool showAbout = false;
        string? licenseView = null;
        int tabSteps = -1;
        string? langSetting = null;
        string? themeName = null;
        string? cuesShotPath = null;
        int focusStop = -1;
        string? postKey = null;
        int postCount = 1;
        bool printTabs = false;
        int activeTabIndex = -1;
        string? extraTabPath = null;
        string? newTabMode = null;
        string[] tagToggles = [];
        bool newWindow = false;
        int newWindowTab = -1;
        bool printSettingsState = false;
        bool showConfirmDemo = false;
        string? tagExportPath = null;
        string? tagImportPath = null;
        double[]? sortOptions = null;
        string? pane1View = null;
        string? pane1Sort = null;
        string? pane2View = null;
        string? pane2Sort = null;
        bool printPaneState = false;

        // 预览操作 / 可执行确认 / 撤销（本批）
        bool printPreviewActions = false;
        string? previewClick = null;
        string? warnExecPath = null;
        bool warnExecAnswer = false;
        bool warnExecDefault = false;
        string? undoPasteSource = null;
        bool undoDelete = false;
        string? undoModifiedSource = null;
        string? navMenuTitle = null;
        bool iconProbe = false;
        int aboutWidth = 0;
        string? pasteSource = null;
        string? pasteTarget = null;
        bool pasteConfirm = true;

        for (int i = 2; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--surface" when i + 1 < argv.Length:
                    // 预览处理器画在自己的子窗口里（HwndHost 的空域），RenderTargetBitmap 拍不到，
                    // 只能单独对这个窗口 PrintWindow
                    surfacePath = Path.GetFullPath(argv[++i]);
                    break;
                case "--then" when i + 1 < argv.Length:
                    // 选中另一个序号再等一会儿，用来验证"切走之后处理器有没有被卸载"
                    _ = int.TryParse(argv[++i], out thenIndex);
                    break;
                case "--state":
                    // 直接把预览视图模型的状态打出来，比看图判断"有没有提示文字"可靠
                    printState = true;
                    break;
                case "--tree":
                    // 打印窗口树：处理器可能把内容画在自己的子窗口里，先看清画在哪
                    dumpTree = true;
                    break;
                case "--tag" when i + 1 < argv.Length:
                    tagName = argv[++i];
                    break;
                case "--activate" when i + 1 < argv.Length:
                    _ = int.TryParse(argv[++i], out activateIndex);
                    break;
                case "--select" when i + 1 < argv.Length:
                    // 逗号分隔可多选，用来验证"多选只报数量与总大小"（FR-9.1.6）
                    selectIndexes = argv[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(int.Parse)
                        .ToArray();
                    break;
                case "--preview":
                    enablePreview = true;
                    break;
                case "--close-preview":
                    disablePreview = true;
                    break;
                case "--nav-resize" when i + 1 < argv.Length:
                    // 合成一次分隔条拖动（FR-1.2.4）。不走真实鼠标：离屏进程没有可拖的桌面，
                    // 但 GridSplitter 的全部逻辑都挂在 Thumb 的三个拖动事件上，直接抛事件
                    // 与真拖的区别只剩"WPF 有没有把鼠标捕获交给它"。
                    _ = double.TryParse(argv[++i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out navResizeDx);
                    break;
                case "--nav-reset":
                    // 双击分隔条回默认宽度（FR-1.2.4）。同样走控件的处理器，不调内部方法
                    navReset = true;
                    break;
                case "--nav-middle" when i + 1 < argv.Length:
                    // 导航栏中键（FR-1.2.6）：按显示名找行，抛一个中键 MouseDown 给它
                    navMiddleLabel = argv[++i];
                    break;
                case "--nav-left" when i + 1 < argv.Length:
                    // 左键对照：同样的行，左键不该新开标签
                    navLeftLabel = argv[++i];
                    break;
                case "--nav-places-menu":
                    // 打开「位置」标题行尾的齿轮菜单并打印每项的勾选/可用状态（FR-1.2.9）。
                    // 弹出层是独立的 HWND，截不进 RenderTargetBitmap，所以只能这样验：
                    // 菜单项有没有生成、勾选态有没有按设置算对、底部"恢复全部"的可用性对不对
                    navPlacesMenu = true;
                    break;
                case "--nav-places-toggle" when i + 1 < argv.Length:
                    // 直接触发某个菜单项的 Click（走的是真处理器），验"取消勾选 → 设置落盘 → 该行消失"
                    navPlacesToggle.Add(argv[++i]);
                    break;
                case "--band-rect" when i + 4 < argv.Length:
                    // 直接喂一个矩形给框选的命中逻辑（绕过鼠标）。
                    // 投递鼠标消息在离屏窗口里无效（WPF 要求窗口已激活），
                    // 而"矩形 → 选中哪些项"才是这块功能真正要验证的部分
                    bandRect = new Rect(
                        double.Parse(argv[++i], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(argv[++i], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(argv[++i], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(argv[++i], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "--rubber-band" when i + 4 < argv.Length:
                    // 框选（FR-6.7）：四点相对 FileList 的逻辑坐标，投递真实的
                    // 按下/移动/松开消息。合成 WPF 事件拿不到位置，只有窗口消息带坐标
                    rubberBand =
                    [
                        double.Parse(argv[++i], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(argv[++i], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(argv[++i], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(argv[++i], System.Globalization.CultureInfo.InvariantCulture),
                    ];
                    break;
                case "--xbutton" when i + 1 < argv.Length && int.TryParse(argv[i + 1], out int xb):
                    // 鼠标侧键（FR-6.2）：向真实窗口投递 WM_XBUTTONDOWN，
                    // 与键盘那条路同一个思路——合成的 WPF 事件拿不到真实鼠标位置，
                    // 只能走窗口消息，让框架当成真的按了一下
                    xButton = xb;
                    i++;
                    break;
                case "--tab-drop" when i + 1 < argv.Length:
                    // 拖到标签栏打开（FR-6.11）：可重复，第一次出现的路径进目标标签。
                    // 走 GetFullPath 收口——命令行的斜杠经过各种转义后可能带重复分隔符，
                    // 而真实的拖放数据永远是规范化的 FullPath
                    tabDropPaths.Add(Path.GetFullPath(argv[++i]));
                    break;
                case "--tab-drop-target" when i + 1 < argv.Length && int.TryParse(argv[i + 1], out int tdTarget):
                    // 落在第几个标签上（0 起始，-1 = 空白处开新标签）
                    tabDropTarget = tdTarget;
                    i++;
                    break;
                case "--rect":
                    // 窗口矩形（FR-10.2 越界居中）：坐标在图上量不出来，只能打出来
                    printRect = true;
                    break;
                case "--status":
                    // 打完动作后把窗格的状态栏与路径打一行：超时、无法访问这类结论
                    // 只看图会以为是"空目录"
                    printPaneStatus = true;
                    break;
                case "--focus-search":
                    // 聚焦搜索框，用来验证压缩态"收起 40 / 聚焦展开 150"这条链
                    focusSearch = true;
                    break;
                case "--search" when i + 1 < argv.Length:
                    // 当前目录搜索（FR-6.13）。空串表示"清空搜索"，
                    // 这样"清空后列表回满"也能用同一张图证明
                    searchQuery = argv[++i];
                    break;
                case "--path" when i + 1 < argv.Length:
                    // 先导航到指定目录再拍。走查大目录（性能夹具）时不能靠会话恢复，
                    // 那取决于上一次运行停在哪
                    navPath = Path.GetFullPath(argv[++i]);
                    break;
                case "--then-search" when i + 1 < argv.Length:
                    // 先把 --search 拍完，再改成这个值。用来验证"清空后列表回满"——
                    // Esc 走的也是 SearchQuery 置空这一条链，没有桌面可按键时这就是等价路径
                    thenSearch = argv[++i];
                    break;
                case "--no-split":
                    // 分屏状态是落盘的，上一条用例跑完还是分屏；关掉才能拍单窗格的 Tab 顺序
                    disableSplit = true;
                    break;
                case "--split":
                    // 分屏后每格工具栏只剩半个窗口，用来核对搜索框收窄与地址栏是否还够用
                    enableSplit = true;
                    break;
                case "--then-refresh":
                    // 同目录刷新（F5 / 文件操作后的重读盘）。验证搜索词是否被保留——
                    // 丢了的话用户"删掉一条搜索结果，搜索框自己空了"
                    thenRefresh = true;
                    break;
                case "--then-path" when i + 1 < argv.Length:
                    // 换目录。验证搜索词被清掉：搜索的范围是"这个目录里的这些条目"
                    thenPath = Path.GetFullPath(argv[++i]);
                    break;
                case "--then-escape":
                    // 直接调搜索框的 KeyDown 处理器。离屏进程没有可发送按键的桌面，
                    // 但 Esc 走的就是这一个函数，调它等价于"在搜索框里按了 Esc"
                    thenEscape = true;
                    break;
                case "--settings":
                    // 设置页。改了关于分节之后要能自己出一张图，不用每次手工开窗口
                    showSettings = true;
                    break;
                case "--confirm-demo":
                    // 确认框（FR-12）。它是唯一 SizeToContent=Height 的自绘标题栏窗口，
                    // 高度由内容撑开，最容易在无边框改造后出问题，所以单独留一个入口。
                    // 只是把窗口开出来，不执行任何操作。
                    showConfirmDemo = true;
                    break;
                case "--scroll-to" when i + 1 < argv.Length:
                    // 滚到设置页里某个 x:Name 的控件。设置页分页之后，
                    // 要先用 --settings-page 切到那一页，否则控件在 Collapsed 的页里滚不动
                    scrollTo = argv[++i];
                    break;
                case "--settings-page" when i + 1 < argv.Length:
                    // 设置页分类（0 常规 / 1 外观 / 2 文件列表 / 3 操作与确认 / 4 隐私与存储 / 5 关于）
                    _ = int.TryParse(argv[++i], out settingsPage);
                    break;
                case "--settings-metrics":
                    // 逐页打印内容高度 / 可视高度。判断"这一页要不要滚"靠看图会看错——
                    // 差 20px 时滚动条一样很细，必须量出来才知道超多少
                    settingsMetrics = true;
                    break;
                case "--font-size" when i + 1 < argv.Length && int.TryParse(argv[i + 1], out int fs):
                    // 界面字号（pt）。AC-06 要在 9 / 14 / 24pt 三档下各走一遍，
                    // 不设这个就只能去改设置文件再重启
                    fontSizePt = fs;
                    i++;
                    break;
                case "--cache-limit" when i + 1 < argv.Length:
                    // 在下拉框里选一档（FR-5.3.5）。选完会真的触发 Trim 与占用刷新，
                    // 用来验"调低上限是不是立刻删文件"，不需要反射调私有方法
                    _ = int.TryParse(argv[++i], out cacheLimitMb);
                    break;
                case "--about":
                    // 关于页（FR-10.8）。拍的是关于窗口本身，不是主窗口
                    showAbout = true;
                    break;
                case "--icon-probe":
                    // 打印各窗口实际拿到的 Icon：类型、像素尺寸、ico 帧数。
                    // 用来确认 WPF 从 ico 里挑的是小尺寸帧，而不是把 256 缩下来
                    iconProbe = true;
                    break;
                case "--about-width" when i + 1 < argv.Length && int.TryParse(argv[i + 1], out int aw):
                    // 把关于页拉到指定宽度（最低 420）：验头部品牌位在窄窗下不挤、不裁
                    aboutWidth = aw;
                    i++;
                    break;
                case "--about-license" when i + 1 < argv.Length:
                    // 许可证查看窗。参数取 license / notice / thirdparty
                    licenseView = argv[++i];
                    break;
                case "--tab" when i + 1 < argv.Length:
                    // 按 Tab 走 N 步并打印焦点链（FR-10.7）。走的是 WPF 处理 Tab 时
                    // 同一个 MoveFocus(Next)，因此链的顺序就是真实按键的顺序
                    _ = int.TryParse(argv[++i], out tabSteps);
                    break;
                case "--lang" when i + 1 < argv.Length:
                    // 界面语言。英文走查要拍英文版，不能靠"这台机器是中文系统"
                    langSetting = argv[++i];
                    break;
                case "--theme" when i + 1 < argv.Length:
                    // 浅色 / 深色。默认跟随系统，走查三主题时必须显式指定
                    themeName = argv[++i];
                    break;
                case "--cues-shot" when i + 1 < argv.Length:
                    // 焦点视觉的像素级证据（FR-10.7 / AC-08）。先按当前状态拍一张，
                    // 再翻窗口的 UI state（WM_UPDATEUISTATE）拍第二张——两张逐像素相减，
                    // 相差的那几圈就是焦点框。WPF 不给程序化开关，但这条消息是系统层入口。
                    cuesShotPath = Path.GetFullPath(argv[++i]);
                    break;
                case "--focus-stop" when i + 1 < argv.Length:
                    // 把焦点停在第 N 个 Tab 停靠点上（从 0 起）。按下标而不是按控件名，
                    // 是因为要在"完全不知道界面结构"的前提下也能复现
                    _ = int.TryParse(argv[++i], out focusStop);
                    break;
                case "--post-key" when i + 1 < argv.Length:
                    // 往真实 HWND 投递按键（tab / enter / space / left / right …）。
                    // 与 --tab 的区别：--tab 直接调 MoveFocus，只证明"链的顺序"；
                    // 这条走 WPF 自己的消息处理，能证明按键绑定真的接上了
                    postKey = argv[++i];
                    break;
                case "--post-count" when i + 1 < argv.Length:
                    _ = int.TryParse(argv[++i], out postCount);
                    break;
                case "--tabs-state":
                    // 打印当前活动标签序号与标签数，用来证明按键真的切换了标签
                    printTabs = true;
                    break;
                case "--active-tab" when i + 1 < argv.Length:
                    // 把活动标签钉到第 N 个再开始按键。活动标签是落盘的，
                    // 不钉住的话上一次走查的结果就是下一次的起点，方向键测试会时对时错
                    _ = int.TryParse(argv[++i], out activeTabIndex);
                    break;
                case "--extra-tab" when i + 1 < argv.Length:
                    // 再开一个标签并导航到指定目录。方向键切换至少要有两个标签，
                    // 而标签数是会话恢复出来的，不显式造就没有稳定的测试前提
                    extraTabPath = Path.GetFullPath(argv[++i]);
                    break;
                case "--new-tab-mode" when i + 1 < argv.Length:
                    // 新标签开在哪（FR-5.2.6）。home / path / last 三档，走的是
                    // "改设置 → 存盘 → 点新建标签"这条用户链，不直接 new TabViewModel
                    newTabMode = argv[++i];
                    break;
                case "--tag-toggle" when i + 1 < argv.Length:
                    // 对当前选中项切换颜色（FR-4.1.2）。逗号分隔可多次切换，
                    // 用来证明"第二次点另一色是叠加不是替换"——单色测试看不出这个区别
                    tagToggles = argv[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--sort-options" when i + 1 < argv.Length:
                    // 自然排序 / 区分大小写两个开关（FR-5.2.3），逗号分隔两位：natural,case。
                    // 走设置页勾选框的同一条链（改设置 → ApplySortOptionsToPanes → 重排）
                    sortOptions = argv[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture))
                        .ToArray();
                    break;
                case "--new-window" when i + 1 < argv.Length:
                    // 标签在新窗口打开（FR-2.12）。触发的是标签条菜单那一项背后的事件，
                    // 不是直接 new MainWindow——要证的是"菜单 → 事件 → 副窗口"整条通了
                    _ = int.TryParse(argv[++i], out newWindowTab);
                    newWindow = true;
                    break;
                case "--tag-export" when i + 1 < argv.Length:
                    // 标签库导出（FR-4.2.7 / FR-5.3.6）。写到指定路径，再打一行摘要
                    tagExportPath = Path.GetFullPath(argv[++i]);
                    break;
                case "--tag-import" when i + 1 < argv.Length:
                    // 标签库导入。导入的是上一行导出的同一个文件，用来做往返验证
                    tagImportPath = Path.GetFullPath(argv[++i]);
                    break;
                case "--settings-state":
                    // 打印设置页本批新增控件的实际取值。图上看得出"有个下拉框"，
                    // 看不出"它现在选的是哪一档"——而后者才是要证的东西
                    printSettingsState = true;
                    break;
                case "--pane2-view" when i + 1 < argv.Length:
                    // 第二窗格的视图（FR-3.7）。两侧各记各的才是重点，
                    // 所以这个参数永远与 --pane1-* 对照着用
                    pane2View = argv[++i];
                    break;
                case "--pane2-sort" when i + 1 < argv.Length:
                    pane2Sort = argv[++i];
                    break;
                case "--pane1-view" when i + 1 < argv.Length:
                    pane1View = argv[++i];
                    break;
                case "--pane1-sort" when i + 1 < argv.Length:
                    pane1Sort = argv[++i];
                    break;
                case "--paste-conflict" when i + 3 < argv.Length:
                    // 粘贴重名（FR-5.2.8）。三个参数：源目录、目标目录、是否开着"覆盖前确认"。
                    // 关掉确认那一档必须能证明"不问、直接覆盖"——只截图看不出这个区别
                    pasteSource = Path.GetFullPath(argv[++i]);
                    pasteTarget = Path.GetFullPath(argv[++i]);
                    pasteConfirm = argv[++i] == "1";
                    break;
                case "--pane-state":
                    // 只打印、不改动。用来验"重开之后两侧还是各是各的"——
                    // 改完就退出只能证明改动了，证明不了记住
                    printPaneState = true;
                    break;
                case "--preview-actions":
                    // 打印预览窗格那三颗操作按钮与关闭按钮的存在/可见/可用状态（FR-9.1.5）。
                    // 图只证明"画出来了几颗"，证明不了"没有选中时它们该不该出现"
                    printPreviewActions = true;
                    break;
                case "--preview-click" when i + 1 < argv.Length:
                    // 点预览窗格里的某一颗按钮。走真正的 Click 事件，不反射调私有方法——
                    // 反射能证明"逻辑对"，证明不了"按钮接上了"
                    previewClick = argv[++i];
                    break;
                case "--warn-exec" when i + 2 < argv.Length:
                    // 双击可执行文件的确认（FR-5.2.11）。参数：要打开的文件、答"是"还是"否"。
                    // 弹窗由 DispatcherTimer 自动作答——离屏进程没有人可点，但模态循环照样跑
                    warnExecPath = Path.GetFullPath(argv[++i]);
                    // 第三档 "default" 不碰设置，验出厂状态下不拦
                    warnExecDefault = argv[++i] == "default";
                    warnExecAnswer = argv[i] == "yes";
                    break;
                case "--undo-paste" when i + 1 < argv.Length:
                    // 粘贴后撤销（FR-6.8）：把源目录里的文件粘进当前目录，再按 Ctrl+Z
                    undoPasteSource = Path.GetFullPath(argv[++i]);
                    break;
                case "--undo-delete":                    // 删除后撤销。这一档会真的走回收站：还原不了就说明整条链路不成立
                    undoDelete = true;
                    break;
                case "--undo-modified" when i + 1 < argv.Length:
                    // 粘贴、改副本、再撤销。撤销必须**拒绝**，否则就是二次破坏
                    undoModifiedSource = Path.GetFullPath(argv[++i]);
                    break;
                case "--nav-menu" when i + 1 < argv.Length:
                    // 左键点导航栏某个分区标题，应弹出该分区的管理菜单
                    navMenuTitle = argv[++i];
                    break;
            }
        }

        var app = new App();
        app.InitializeComponent();

        // 首帧之后再等一会儿：标签库、工作区都是"首帧后补"，早拍就看不到它们
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            await Task.Delay(waitMs);

            MainWindow? window = app.Windows.OfType<MainWindow>().FirstOrDefault();
            if (window is null)
            {
                Console.Error.WriteLine("找不到主窗口");
                app.Shutdown(2);
                return;
            }

            try
            {
                if (navPath is not null)
                {
                    Console.WriteLine($"nav.slow={Jianlan.Core.FileSystem.SlowLocations.NeedsTimeout(navPath)} " +
                                      $"unc={Jianlan.Core.FileSystem.SlowLocations.IsUnc(navPath)} path={navPath}");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    await GetPane(window).ViewModel!.NavigateAsync(navPath);
                    Console.WriteLine($"nav.elapsed={clock.ElapsedMilliseconds}ms");
                    await Task.Delay(500);
                }

                if (langSetting is not null)
                {
                    // 走的是设置页切语言的那条链：改设置 + 立即生效 + 存盘
                    App.ApplyLanguage(langSetting);
                    await Task.Delay(400);
                }

                if (themeName is not null)
                {
                    ApplyTheme(themeName);
                    await Task.Delay(400);
                }

                if (enableSplit || disableSplit)
                {
                    // 是"确保分屏"而不是"切换"：设置是落盘的，上一次跑完就已经是分屏，
                    // 再 toggle 一次反而把它关掉（与 --preview 同一个坑）
                    FieldInfo? splitField = typeof(MainWindow).GetField(
                        "_splitEnabled", BindingFlags.NonPublic | BindingFlags.Instance);

                    bool isSplit = splitField?.GetValue(window) is true;

                    if (isSplit != enableSplit)
                    {
                        MethodInfo? toggle = typeof(MainWindow).GetMethod(
                            "ToggleSplit", BindingFlags.NonPublic | BindingFlags.Instance);

                        if (toggle is null) throw new MissingMethodException("MainWindow.ToggleSplit");

                        toggle.Invoke(window, null);
                    }

                    await Task.Delay(800);
                }

                if (tagName is not null)
                {
                    OpenTagResults(window, tagName);
                    await Task.Delay(600);
                }

                if (activateIndex >= 0)
                {
                    ActivateResult(window, activateIndex);
                    await Task.Delay(1500);
                }

                if (enablePreview || disablePreview)
                {
                    SetPreview(window, open: enablePreview);
                    await Task.Delay(400);
                }

                if (selectIndexes.Length > 0)
                {
                    SelectFile(window, selectIndexes);

                    // 300ms 防抖 + 读盘/解码，1.5s 足够拍到结果；
                    // 走处理器的类型还要拉起 prevhost.exe，多留一倍时间
                    await Task.Delay(surfacePath is null ? 1500 : 3000);
                }

                if (thenIndex >= 0)
                {
                    SelectFile(window, [thenIndex]);
                    await Task.Delay(1500);
                }

                if (searchQuery is not null)
                {
                    ApplySearch(window, searchQuery);

                    // 250ms 防抖 + 后台过滤，600ms 足够看到结果；取整留一点余量
                    await Task.Delay(700);
                    PrintSearchState(window);
                }

                if (navResizeDx != 0)
                {
                    ResizeNav(window, navResizeDx);
                    await Task.Delay(400);
                }

                if (navReset)
                {
                    ResetNav(window);
                    await Task.Delay(300);
                }

                if (navLeftLabel is not null)
                {
                    ClickNav(window, navLeftLabel, System.Windows.Input.MouseButton.Left);
                    await Task.Delay(900);
                }

                if (navMiddleLabel is not null)
                {
                    ClickNav(window, navMiddleLabel, System.Windows.Input.MouseButton.Middle);
                    await Task.Delay(900);
                }

                if (navPlacesMenu || navPlacesToggle.Count > 0)
                {
                    ExerciseNavPlaces(window, navPlacesMenu, navPlacesToggle);
                    await Task.Delay(300);
                }

                if (focusSearch)
                {
                    FocusSearch(window);
                    await Task.Delay(400);
                }

                if (bandRect is { } rect)
                {
                    ExerciseBandSelect(window, rect);
                }

                if (rubberBand is not null)
                {
                    await ExerciseRubberBand(window, rubberBand);
                }

                if (xButton > 0)
                {
                    await ExerciseMouseButton(window, xButton);
                }

                if (tabDropPaths.Count > 0)
                {
                    await ExerciseTabDrop(window, tabDropPaths, tabDropTarget);
                    PrintTabPaths(window, "after-drop");
                }

                if (printRect)
                {
                    Console.WriteLine(
                        $"window.left={window.Left:0.#} top={window.Top:0.#} " +
                        $"width={window.Width:0.#} height={window.Height:0.#} state={window.WindowState}");
                    Console.WriteLine(
                        $"screen.virtual={SystemParameters.VirtualScreenLeft:0.#},{SystemParameters.VirtualScreenTop:0.#} " +
                        $"{SystemParameters.VirtualScreenWidth:0.#}x{SystemParameters.VirtualScreenHeight:0.#}");
                    Console.WriteLine($"screen.work={SystemParameters.WorkArea}");
                }

                if (printPaneStatus)
                {
                    PaneViewModel pane = GetPane(window).ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");
                    Console.WriteLine($"pane.path={pane.CurrentPath}");
                    Console.WriteLine($"pane.status={pane.StatusText}");
                    Console.WriteLine($"pane.error={pane.LastNavigationError}");
                    Console.WriteLine($"pane.count={pane.TotalCount}");
                }

                if (thenSearch is not null)
                {
                    ApplySearch(window, thenSearch);
                    await Task.Delay(700);
                    Console.WriteLine("-- after --");
                    PrintSearchState(window);
                }

                if (thenRefresh)
                {
                    await GetPane(window).ViewModel!.RefreshAsync();
                    await Task.Delay(700);
                    Console.WriteLine("-- after refresh --");
                    PrintSearchState(window);
                }

                if (thenPath is not null)
                {
                    await GetPane(window).ViewModel!.NavigateAsync(thenPath);
                    await Task.Delay(700);
                    Console.WriteLine("-- after navigate --");
                    PrintSearchState(window);
                }

                if (thenEscape)
                {
                    RaiseSearchKey(window, System.Windows.Input.Key.Escape);
                    await Task.Delay(300);
                    Console.WriteLine("-- after escape --");
                    PrintSearchState(window);
                }

                if (surfacePath is not null) SaveSurface(window, surfacePath);

                if (printState) PrintState(window);

                if (dumpTree) DumpTree(window);

                if (tabSteps >= 0) WalkTabChain(window, tabSteps);

                Window target = window;

                if (pasteSource is not null && pasteTarget is not null)
                {
                    await ExercisePasteConflict(window, pasteSource, pasteTarget, pasteConfirm);
                }

                if (printPaneState) PrintPaneStates(window);

                if (printPreviewActions) PrintPreviewActions(window);

                if (previewClick is not null)
                {
                    await ClickPreviewButton(window, previewClick);
                    await Task.Delay(1500);
                    PrintPreviewState(window);
                }

                if (warnExecPath is not null)
                {
                    await ExerciseWarnExec(window, warnExecPath, warnExecAnswer, warnExecDefault);
                }

                if (undoPasteSource is not null)
                {
                    await ExerciseUndoPaste(window, undoPasteSource);
                }

                if (undoDelete)
                {
                    await ExerciseUndoDelete(window);
                }

                if (undoModifiedSource is not null)
                {
                    await ExerciseUndoModified(window, undoModifiedSource);
                }

                if (navMenuTitle is not null)
                {
                    await ExerciseNavMenu(window, navMenuTitle);
                }

                if (pane1View is not null || pane1Sort is not null || pane2View is not null || pane2Sort is not null)
                {
                    await ExercisePaneViews(window, pane1View, pane1Sort, pane2View, pane2Sort);
                }

                if (tagExportPath is not null) PrintTagExport(tagExportPath);

                if (tagImportPath is not null) PrintTagImport(tagImportPath);

                if (newTabMode is not null) await ExerciseNewTabMode(window, newTabMode);

                if (tagToggles.Length > 0) await ExerciseTagToggle(window, tagToggles);

                if (sortOptions is { Length: 2 })
                {
                    await ExerciseSortOptions(window, sortOptions[0] != 0, sortOptions[1] != 0);
                }

                if (newWindow)
                {
                    // 拍的是副窗口：主窗口的样子到处都是，这张图要证的是"另开了一个"
                    if (ExerciseNewWindow(window, newWindowTab) is { } opened) target = opened;
                }

                if (showSettings)
                {
                    if (fontSizePt > 0)
                    {
                        Jianlan.App.App.Settings.Appearance.FontSizePt = fontSizePt;
                        Jianlan.App.Typography.FontScaleService.Apply(fontSizePt, compact: false);
                        await Task.Delay(300);
                    }

                    target = OpenSettings(window);
                    await Task.Delay(700);

                    if (settingsPage >= 0)
                    {
                        SelectSettingsPage(target, settingsPage);
                        await Task.Delay(300);
                    }

                    if (settingsMetrics)
                    {
                        PrintSettingsMetrics(target);
                        await Task.Delay(100);
                    }

                    if (scrollTo is not null)
                    {
                        ScrollToElement(target, scrollTo);
                        await Task.Delay(300);
                    }

                    if (cacheLimitMb > 0)
                    {
                        SetCacheLimit(target, cacheLimitMb);
                        await Task.Delay(1500);
                        PrintCacheLine(target);
                    }

                    if (printSettingsState) PrintSettingsState(target);
                }

                if (showAbout || licenseView is not null)
                {
                    target = OpenAbout(window);
                    await Task.Delay(600);
                }

                if (showConfirmDemo)
                {
                    target = OpenConfirmDemo(window);
                    await Task.Delay(600);
                }

                if (aboutWidth > 0 && target?.Tag as string == AboutHostTag)
                {
                    target.Width = aboutWidth;
                    target.UpdateLayout();
                    await Task.Delay(300);
                    Console.WriteLine($"about.width={target.ActualWidth:0} height={target.ActualHeight:0}");
                }

                if (iconProbe)
                {
                    // 逐个窗口报 Icon：null 就是"根本没设"，帧数 >1 说明 WPF 直接吃下了整个 ico
                    PrintIcoFrames();

                    foreach (Window w in Application.Current.Windows)
                    {
                        PrintIconState(w);
                    }
                }

                if (licenseView is not null)
                {
                    // 走关于页按钮自己的处理函数：这样读嵌入资源、建查看窗两条链都被真正跑过
                    target = OpenLicense(target, licenseView);
                    await Task.Delay(600);
                }

                if (extraTabPath is not null)
                {
                    MethodInfo? newTab = typeof(MainWindow).GetMethod("NewTab",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    if (newTab is null) throw new MissingMethodException("MainWindow.NewTab");

                    newTab.Invoke(window, null);
                    await Task.Delay(600);

                    if (typeof(MainWindow).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance)
                            ?.GetValue(window) is Jianlan.App.ViewModels.TabHostViewModel tabs)
                    {
                        tabs.ActivateAt(tabs.Tabs.Count - 1);
                        await tabs.Tabs[^1].ActivePane.NavigateAsync(extraTabPath);
                        await Task.Delay(800);
                        Console.WriteLine($"tabs.extra={tabs.Tabs.Count} index={tabs.ActiveIndex}");
                    }
                }

                if (activeTabIndex >= 0)
                {
                    FieldInfo? tabsField = typeof(MainWindow).GetField("_tabs",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    if (tabsField?.GetValue(window) is Jianlan.App.ViewModels.TabHostViewModel host)
                    {
                        host.ActivateAt(Math.Clamp(activeTabIndex, 0, Math.Max(0, host.Tabs.Count - 1)));
                        await Task.Delay(500);
                        Console.WriteLine($"tabs.set={host.ActiveIndex}");
                    }
                }

                if (focusStop >= 0)
                {
                    FocusStop(window, focusStop);
                    await Task.Delay(250);
                }

                if (printTabs)
                {
                    PrintTabs(window, "before");
                }

                if (postKey is not null)
                {
                    await PostKeys(window, postKey, postCount);

                    if (printTabs) PrintTabs(window, "after");
                }

                if (cuesShotPath is not null)
                {
                    // 先拍"没有焦点框"的那张，再翻 UI state 拍第二张。
                    // 两张逐像素相减，多出来的那几圈就是键盘焦点视觉——
                    // 这样既能证明它确实画出来了，也能证明它平时确实不画。
                    Save(window, cuesShotPath);
                    Console.WriteLine($"已保存 {cuesShotPath}（未开启键盘线索）");

                    EnableKeyboardCues(window);

                    // 焦点视觉是挂在"焦点变化"那一刻的：已经聚焦的元素在
                    // ShowKeyboardCues 翻转后不会自己补一圈。必须让焦点重新走一遍。
                    if (focusStop >= 0)
                    {
                        FocusStop(window, 0);
                        FocusStop(window, focusStop);
                    }

                    await Task.Delay(500);

                    Save(window, outPath);
                    Console.WriteLine($"已保存 {outPath}（键盘线索已开启）");
                    app.Shutdown(0);
                    return;
                }

                Save(target, outPath);
                Console.WriteLine($"已保存 {outPath}");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                app.Shutdown(3);
            }
        }));

        return app.Run();
    }

    /// <summary>
    /// 把预览窗格摆到指定状态。**不**用"切换"：设置是落盘的，上一张图跑完
    /// showPreview 就已经是 true，再切换一次反而把它关掉——第一次踩到这个坑时，
    /// 第二张图里的预览整栏不见了。
    /// </summary>
    private static void SetPreview(MainWindow window, bool open)
    {
        FieldInfo? field = typeof(MainWindow).GetField(
            "_previewOpen", BindingFlags.NonPublic | BindingFlags.Instance);

        if (field?.GetValue(window) is bool current && current == open) return;

        MethodInfo? method = typeof(MainWindow).GetMethod(
            "TogglePreview", BindingFlags.NonPublic | BindingFlags.Instance);

        if (method is null) throw new MissingMethodException("MainWindow.TogglePreview");

        method.Invoke(window, null);
    }

    /// <summary>
    /// 在主窗格里"选中第 index 个文件"。直接改 ListBox 的 SelectedIndex：
    /// 走的是用户点一行时的同一条 <c>SelectionChanged</c> 链，因此预览会被真正触发。
    /// </summary>
    private static void SelectFile(MainWindow window, int[] indexes)
    {
        FieldInfo? paneField = typeof(MainWindow).GetField("Pane", BindingFlags.NonPublic | BindingFlags.Instance);
        if (paneField?.GetValue(window) is not PaneView pane) throw new MissingMemberException("Pane");

        FieldInfo? listField = typeof(PaneView).GetField(
            "FileList", BindingFlags.NonPublic | BindingFlags.Instance);

        if (listField?.GetValue(pane) is not System.Windows.Controls.ListBox list)
        {
            throw new MissingMemberException("FileList");
        }

        // 先清空：--then 的语义是"再选另一个"，不是"加选"
        list.SelectedItems.Clear();

        foreach (int index in indexes)
        {
            if (index >= list.Items.Count) throw new ArgumentOutOfRangeException(nameof(indexes), "序号超出列表项数");

            list.SelectedItems.Add(list.Items[index]);
        }

        list.ScrollIntoView(list.Items[indexes[0]]);
    }

    /// <summary>
    /// 往工具栏搜索框里写字。走 TextBox.Text 而不是直接改视图模型：
    /// 绑定是 <c>UpdateSourceTrigger=PropertyChanged</c>，设 Text 就是用户逐字输入的同一条链，
    /// 防抖、后台过滤、状态栏刷新全部会真实发生。
    /// </summary>
    private static void ApplySearch(MainWindow window, string query)
    {
        if (GetPane(window).FindName("SearchBox") is not System.Windows.Controls.TextBox box)
        {
            throw new MissingMemberException("SearchBox");
        }

        box.Text = query;
    }

    /// <summary>
    /// 给导航栏某一行抛一次鼠标按下（FR-1.2.6 的走查口）。按显示名找行：行是模板生成的，
    /// 拿不到引用，只能从 Items 里按 DataContext 的名字认。
    /// 抛在容器上而不是列表上——真实点击的 OriginalSource 就是容器内部的那一层。
    /// </summary>
    private static void ClickNav(MainWindow window, string label, System.Windows.Input.MouseButton button)
    {
        if (window.FindName("QuickAccessList") is not System.Windows.Controls.ListBox list)
        {
            throw new MissingMemberException("QuickAccessList");
        }

        System.Windows.Controls.ListBoxItem? target = null;
        foreach (object item in list.Items)
        {
            if (LabelOf(item) != label) continue;
            list.UpdateLayout();
            target = list.ItemContainerGenerator.ContainerFromItem(item) as System.Windows.Controls.ListBoxItem;
            break;
        }

        if (target is null) throw new MissingMemberException($"导航行 {label}");

        FieldInfo? tabsField = typeof(MainWindow).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance);
        var host = (TabHostViewModel?)tabsField?.GetValue(window);
        Console.WriteLine($"nav.click={label} button={button} tabs.before={host?.Tabs.Count}");

        var args = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, button)
        {
            RoutedEvent = System.Windows.UIElement.MouseDownEvent,
        };

        target.RaiseEvent(args);

        Console.WriteLine($"nav.click={label} handled={args.Handled} tabs.after={host?.Tabs.Count}");
    }

    /// <summary>行的显示名：系统节点取 Name，其余取 Title（都带数量后缀的那几个正好是标题）不好统一，
    /// 这里只认系统节点、此电脑、驱动器与标签行，够走查用。</summary>
    private static string? LabelOf(object item) => item switch
    {
        NavItem nav => nav.Name,

        // 此电脑（FR-1.2.2）：容器节点按标题点，展开后驱动器按盘符点
        NavThisPcRow thisPc => thisPc.Title,
        NavDriveRow drive => drive.Letter,

        NavTagRow tag => tag.Name,
        NavWorkspaceRow workspace => workspace.Name,
        _ => null,
    };

    /// <summary>
    /// 合成一次导航栏分隔条的拖动，并打印拖动前后的列宽与落盘值（FR-1.2.4）。
    /// 参数是水平位移（正值 = 变宽）。
    /// </summary>
    private static void ResizeNav(MainWindow window, double dx)
    {
        if (window.FindName("NavSplitter") is not System.Windows.Controls.GridSplitter splitter ||
            window.FindName("NavColumn") is not System.Windows.Controls.ColumnDefinition column)
        {
            throw new MissingMemberException("NavSplitter/NavColumn");
        }

        Console.WriteLine($"nav.before={column.ActualWidth:0}");

        splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
        splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(dx, 0));
        window.UpdateLayout();
        Console.WriteLine($"nav.during={column.ActualWidth:0}");

        splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(dx, 0, false));
        window.UpdateLayout();
        Console.WriteLine($"nav.after={column.ActualWidth:0} saved={App.Settings.Layout.NavWidth:0}");
    }

    /// <summary>双击分隔条回默认宽度，打印回弹前后的列宽与落盘值（FR-1.2.4）。</summary>
    private static void ResetNav(MainWindow window)
    {
        if (window.FindName("NavSplitter") is not System.Windows.Controls.GridSplitter splitter ||
            window.FindName("NavColumn") is not System.Windows.Controls.ColumnDefinition column)
        {
            throw new MissingMemberException("NavSplitter/NavColumn");
        }

        Console.WriteLine($"nav.reset.before={column.ActualWidth:0}");

        var args = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent,
        };

        splitter.RaiseEvent(args);
        window.UpdateLayout();

        Console.WriteLine($"nav.reset.after={column.ActualWidth:0} saved={App.Settings.Layout.NavWidth:0}");
    }

    /// <summary>
    /// 聚焦工具栏搜索框，并把展开前后的宽度打出来（FR-1.2.4 之后分屏地址栏的配套改动）。
    /// 光看截图只能看到"框变宽了"，宽度值才能说明收起态是 40、展开后是 150。
    /// </summary>
    private static void FocusSearch(MainWindow window)
    {
        PaneView pane = GetPane(window);

        if (pane.FindName("SearchHost") is not FrameworkElement host ||
            pane.FindName("SearchBox") is not System.Windows.Controls.TextBox box ||
            pane.FindName("BreadcrumbHost") is not FrameworkElement breadcrumb)
        {
            throw new MissingMemberException("SearchHost/SearchBox/BreadcrumbHost");
        }

        Console.WriteLine($"search.compact={pane.IsCompactPane}");
        Console.WriteLine($"search.width.collapsed={host.Width:0} address.collapsed={breadcrumb.ActualWidth:0}");

        // 窗格先拿焦点：窗口没有活动焦点时 TextBox.Focus 会被 WPF 忽略
        pane.Focus();
        box.Focus();
        System.Windows.Input.Keyboard.Focus(box);
        pane.UpdateLayout();

        Console.WriteLine($"search.width.focused={host.Width:0} address.focused={breadcrumb.ActualWidth:0} " +
                          $"keyboardFocused={box.IsKeyboardFocused}");
    }

    /// <summary>
    /// 把搜索态的四个观察点打到 stdout。图只能看出"列表短了"，
    /// 但"命中几项/状态栏怎么说/是不是还在搜索态"必须读值才确定。
    /// </summary>
    private static void PrintSearchState(MainWindow window)
    {
        PaneView pane = GetPane(window);
        PaneViewModel vm = pane.ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        FieldInfo? listField = typeof(PaneView).GetField("FileList", BindingFlags.NonPublic | BindingFlags.Instance);
        if (listField?.GetValue(pane) is not System.Windows.Controls.ListBox list)
        {
            throw new MissingMemberException("FileList");
        }

        Console.WriteLine($"search.query={vm.SearchQuery}");
        Console.WriteLine($"search.isSearching={vm.IsSearching}");
        Console.WriteLine($"search.visible={list.Items.Count}");
        Console.WriteLine($"search.total={vm.TotalCount}");
        Console.WriteLine($"search.status={vm.StatusText}");
    }

    /// <summary>
    /// 把一次按键交给搜索框的处理函数。用合成的 <see cref="KeyEventArgs"/> 而不是 SendInput：
    /// 离屏进程没有可发送按键的桌面，而 Esc 的全部逻辑就在 <c>OnSearchKeyDown</c> 里，
    /// 直接调它与真实按下的区别只剩"WPF 有没有把事件路由到这里"，那一段是框架行为。
    /// </summary>
    private static void RaiseSearchKey(MainWindow window, System.Windows.Input.Key key)
    {
        PaneView pane = GetPane(window);

        if (pane.FindName("SearchBox") is not System.Windows.Controls.TextBox box)
        {
            throw new MissingMemberException("SearchBox");
        }

        PresentationSource source = PresentationSource.FromVisual(pane)
                                    ?? throw new InvalidOperationException("pane 还没有 PresentationSource");

        var args = new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
        };

        MethodInfo? handler = typeof(PaneView).GetMethod(
            "OnSearchKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);

        if (handler is null) throw new MissingMethodException("PaneView.OnSearchKeyDown");

        handler.Invoke(pane, [box, args]);
    }

    private static PaneView GetPane(MainWindow window)
    {
        FieldInfo? paneField = typeof(MainWindow).GetField("Pane", BindingFlags.NonPublic | BindingFlags.Instance);
        if (paneField?.GetValue(window) is not PaneView pane) throw new MissingMemberException("Pane");

        return pane;
    }

    // ── 关于页与可访问性走查（FR-10.8 / FR-10.7）────────────────────────────

    private static void ApplyTheme(string name)
    {
        Jianlan.Core.Settings.ThemeKind kind = name.ToLowerInvariant() switch
        {
            "light" => Jianlan.Core.Settings.ThemeKind.Light,
            "dark" => Jianlan.Core.Settings.ThemeKind.Dark,
            "highcontrast" => Jianlan.Core.Settings.ThemeKind.HighContrast,
            _ => Jianlan.Core.Settings.ThemeKind.System,
        };

        App.Settings.Appearance.Theme = kind;
        Jianlan.App.Design.ThemeService.Apply(kind);
    }

    private static Window OpenSettings(Window owner)
    {
        MethodInfo? method = typeof(MainWindow).GetMethod(
            "OpenSettings", BindingFlags.NonPublic | BindingFlags.Instance);

        if (method is null) throw new MissingMethodException("MainWindow.OpenSettings");

        method.Invoke(owner, null);

        Window settings = Application.Current.Windows.OfType<SettingsWindow>().LastOrDefault()
                          ?? throw new InvalidOperationException("设置页没有出现");

        // 设置页分页之后不再需要"滚到底"：内容各归各页，切页由 --settings-page 指定
        settings.UpdateLayout();

        return settings;
    }

    /// <summary>
    /// 逐页量一遍"内容高度 / 可视高度"。切页是真实路径（设导航 SelectedIndex），
    /// 量到的是那一页在当前字号、当前语言下的真实布局。
    /// </summary>
    private static void PrintSettingsMetrics(Window window)
    {
        (string Name, string Label)[] pages =
        [
            ("PageGeneral", "常规"),
            ("PageAppearance", "外观"),
            ("PageFiles", "文件列表"),
            ("PageActions", "操作与确认"),
            ("PagePrivacy", "隐私与存储"),
            ("PageAbout", "关于"),
        ];

        if (window.FindName("NavList") is not System.Windows.Controls.ListBox nav)
        {
            Console.WriteLine("METRIC 没有找到 NavList");
            return;
        }

        for (int i = 0; i < pages.Length; i++)
        {
            nav.SelectedIndex = i;
            window.UpdateLayout();

            if (window.FindName(pages[i].Name) is not ScrollViewer viewer) continue;

            double ratio = viewer.ViewportHeight > 0 ? viewer.ExtentHeight / viewer.ViewportHeight : 0;
            string verdict = ratio > 1.02 ? "要滚" : "一屏";

            Console.WriteLine(
                $"METRIC {pages[i].Label,-6} 内容={viewer.ExtentHeight,6:F0}  可视={viewer.ViewportHeight,6:F0}  倍数={ratio:F2}  {verdict}");
        }
    }

    /// <summary>
    /// 设 SelectedIndex 等于用户点了导航项，切页走的是真实那条路径。
    /// </summary>
    private static void SelectSettingsPage(Window window, int index)
    {
        if (window.FindName("NavList") is not System.Windows.Controls.ListBox nav)
        {
            Console.WriteLine("PAGE 没有找到 NavList");
            return;
        }

        int max = nav.Items.Count - 1;
        if (max < 0)
        {
            Console.WriteLine("PAGE 导航为空");
            return;
        }

        nav.SelectedIndex = Math.Clamp(index, 0, max);
        window.UpdateLayout();

        Console.WriteLine($"PAGE 已切到第 {nav.SelectedIndex} 页");
    }

    /// <summary>
    /// 把设置页滚到指定 x:Name 的控件。用 BringIntoView 而不是算偏移：
    /// 各个分节的高度随字号与语言变，算出来的偏移量换个语言就不准。
    /// </summary>
    private static void ScrollToElement(Window window, string name)
    {
        if (window.FindName(name) is not FrameworkElement element)
        {
            Console.WriteLine($"SCROLL 没有找到 {name}");
            return;
        }

        element.BringIntoView();
        window.UpdateLayout();

        Console.WriteLine($"SCROLL 已滚到 {name}");
    }

    /// <summary>
    /// 改缓存上限（FR-5.3.5）。给下拉框设 SelectedIndex 就等于用户选了一档，
    /// SelectionChanged 会真的走到 OnCacheLimitChanged——含 Trim 与占用刷新。
    /// </summary>
    private static void SetCacheLimit(Window window, int megabytes)
    {
        if (window.FindName("CacheLimitCombo") is not ComboBox combo)
        {
            Console.WriteLine("CACHE 没有找到 CacheLimitCombo");
            return;
        }

        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && int.TryParse(item.Content?.ToString(), out int value)
                && value == megabytes)
            {
                Console.WriteLine($"CACHE 选中 {megabytes} MB（第 {i} 档）");
                combo.SelectedIndex = i;
                window.UpdateLayout();
                return;
            }
        }

        Console.WriteLine($"CACHE 下拉框里没有 {megabytes} 这一档");
    }

    /// <summary>把占用行与状态行原样打出来：截图只能看，断言要读文本。</summary>
    private static void PrintCacheLine(Window window)
    {
        if (window.FindName("ThumbCacheText") is TextBlock text) Console.WriteLine($"CACHE 占用：{text.Text}");

        if (window.FindName("PrivacyStatus") is TextBlock status) Console.WriteLine($"CACHE 状态：{status.Text}");
    }

    /// <summary>打开关于页。调用的是设置页按钮走的那一个入口。</summary>
    /// <summary>
    /// 打印窗口实际拿到的任务栏/标题栏图标。null 表示压根没设；
    /// 尺寸落在 16 附近说明 WPF 从 ico 里挑了小帧，直接把 256 缩下来则会报 256。
    /// </summary>
    private static void PrintIconState(Window window)
    {
        string name = window.GetType().Name;

        if (window.Icon is not { } icon)
        {
            Console.WriteLine($"icon.{name}=null");
            return;
        }

        Console.WriteLine($"icon.{name}={icon.GetType().Name} {icon.Width:0}x{icon.Height:0}");
    }

    /// <summary>ico 里到底有几档。窗口只用其中一帧，数不到的档等于白做。</summary>
    private static void PrintIcoFrames()
    {
        var uri = new Uri("pack://application:,,,/Jianlan;component/Assets/Brand/jianlan.ico");
        var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        Console.WriteLine($"icon.ico.frames={decoder.Frames.Count} " +
                          $"sizes=[{string.Join(",", decoder.Frames.Select(f => f.PixelWidth))}]");
    }

    /// <summary>
    /// 打开一个空的确认框（FR-12 走查用）。
    /// 构造函数是私有的（对外只有 Ask/Notify），这里反射拿无参构造；
    /// 用 Show 而不是 ShowDialog——模态会把本方法的后续截图挡在外面。
    /// </summary>
    private static Window OpenConfirmDemo(Window owner)
    {
        var ctor = typeof(ConfirmWindow).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null) ?? throw new InvalidOperationException("ConfirmWindow 没有无参构造");

        var dialog = (Window)ctor.Invoke(null);
        dialog.Owner = owner;
        dialog.Title = "确认操作";

        if (dialog.FindName("MessageText") is System.Windows.Controls.TextBlock text)
        {
            text.Text = "这是一次界面走查用的确认框，不会执行任何操作。";
        }

        // 按钮文案平时由 Ask/Notify 填（走可本地化的资源键）。
        // 这里直接 new，所以自己补一份，否则按钮宽度为 0、截图里只剩一条空白
        if (dialog.FindName("ConfirmButton") is System.Windows.Controls.Button ok) ok.Content = "是";
        if (dialog.FindName("CancelButton") is System.Windows.Controls.Button no) no.Content = "否";

        dialog.Show();

        Console.WriteLine($"confirm.size={dialog.ActualWidth:0}x{dialog.ActualHeight:0} " +
                          $"title={dialog.Title} state={dialog.WindowState}");

        return dialog;
    }

    /// <summary>关于内容宿主窗口的标记。关于内容已经不是一扇独立窗口了（AboutView 用户控件），
    /// 离屏截图要自己套一个壳，靠这个标记认出"这是我自己搭的"。</summary>
    private const string AboutHostTag = "offscreen.about-host";

    /// <summary>
    /// 关于内容。原先是打开 AboutWindow；现在内容是设置页里的一块用户控件，
    /// 这里套一个最小外壳（外框 + 滚动）把它渲染出来，尺寸与设置页内容区接近。
    /// </summary>
    private static Window OpenAbout(Window owner)
    {
        var view = new AboutView();

        var host = new Window
        {
            Title = "关于",
            Owner = owner,
            Width = 480,
            Height = 680,
            Tag = AboutHostTag,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["Brush.Surface.Window"],
            Content = new System.Windows.Controls.ScrollViewer
            {
                Content = view,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Padding = new Thickness(20),
            },
        };

        host.Show();
        PrintScrollExtent(host);

        return host;
    }

    /// <summary>从关于宿主窗口里取出 AboutView（反射调用它的私有事件处理器要用）。</summary>
    private static AboutView AboutViewOf(Window host) =>
        (host.Content as System.Windows.Controls.ScrollViewer)?.Content as AboutView
        ?? throw new InvalidOperationException("关于宿主里没有 AboutView");

    /// <summary>
    /// 关于页要不要滚动，靠读值而不是看图：截图只能看到首屏，
    /// ExtentHeight - ViewportHeight 才是"还差多少像素"。
    /// </summary>
    private static void PrintScrollExtent(Window window)
    {
        window.UpdateLayout();

        var pending = new System.Collections.Generic.Queue<DependencyObject>();
        pending.Enqueue(window);

        while (pending.Count > 0)
        {
            DependencyObject node = pending.Dequeue();

            if (node is System.Windows.Controls.ScrollViewer viewer)
            {
                Console.WriteLine(
                    $"ABOUT window={window.ActualWidth:F0}x{window.ActualHeight:F0} " +
                    $"extent={viewer.ExtentHeight:F0} viewport={viewer.ViewportHeight:F0} " +
                    $"overflow={viewer.ExtentHeight - viewer.ViewportHeight:F0}");

                // 各区块高度：找"哪一段最占地方"比盯着截图猜快
                if (viewer.Content is System.Windows.Controls.Panel panel)
                {
                    foreach (UIElement child in panel.Children)
                    {
                        string label = child switch
                        {
                            System.Windows.Controls.TextBlock tb => tb.Text,
                            _ => child.GetType().Name,
                        };

                        Console.WriteLine(
                            $"ABOUT   {child.GetType().Name,-12} h={((FrameworkElement)child).ActualHeight,4:F0}  {Truncate(label, 30)}");
                    }
                }

                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) pending.Enqueue(VisualTreeHelper.GetChild(node, i));
        }

        Console.WriteLine("ABOUT 没有找到 ScrollViewer");
    }

    /// <summary>
    /// 打开许可证查看窗。不直接调 <c>LicenseWindow.ShowText</c>：
    /// 那会跳过"读嵌入资源"这一步，而这一步恰恰是最可能被构建配置弄坏的地方。
    /// </summary>
    private static Window OpenLicense(Window about, string which)
    {
        string handler = which.ToLowerInvariant() switch
        {
            "notice" => "OnViewNotice",
            "thirdparty" => "OnViewThirdParty",
            _ => "OnViewLicense",
        };

        AboutView view = AboutViewOf(about);

        MethodInfo? method = typeof(AboutView).GetMethod(
            handler, BindingFlags.NonPublic | BindingFlags.Instance);

        if (method is null) throw new MissingMethodException($"AboutView.{handler}");

        method.Invoke(view, [view, new RoutedEventArgs()]);

        return Application.Current.Windows.OfType<LicenseWindow>().LastOrDefault()
               ?? throw new InvalidOperationException("许可证窗口没有出现");
    }

    /// <summary>
    /// 按 Tab 走 N 步并打印焦点链。用的是 WPF 处理 Tab 时调用的同一个
    /// <c>MoveFocus(Next)</c>，因此链的顺序就是真实按键的顺序。
    /// 走完停在最后一步上，截图里能直接看到焦点框。
    /// </summary>
    private static void WalkTabChain(Window window, int steps)
    {
        window.UpdateLayout();
        window.MoveFocus(new System.Windows.Input.TraversalRequest(
            System.Windows.Input.FocusNavigationDirection.First));

        for (int i = 0; i < steps; i++)
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
            Console.WriteLine($"TAB {i:D2} {DescribeFocus(focused)}");

            // 从"当前焦点所在的元素"往下一步走，而不是每次都从窗口走：
            // 与 WPF 处理 Tab 键的方式一致，也能正确地跨出嵌套的 tab scope
            var request = new System.Windows.Input.TraversalRequest(
                System.Windows.Input.FocusNavigationDirection.Next);

            if (System.Windows.Input.Keyboard.FocusedElement is UIElement element)
            {
                element.MoveFocus(request);
            }
            else
            {
                window.MoveFocus(request);
            }
        }
    }

    // ---- 键盘可访问性（FR-10.7 / AC-08）的三个系统层入口 ----

    private const int WM_UPDATEUISTATE = 0x0128;
    private const int UIS_CLEAR = 0x0002;
    private const int UISF_HIDEFOCUS = 0x0001;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 把焦点停在第 <paramref name="index"/> 个停靠点上（0 起）。
    /// 走的是 MoveFocus，所以"第几个"与真实按 Tab 的次序一致。
    /// </summary>
    private static void FocusStop(Window window, int index)
    {
        window.UpdateLayout();
        window.MoveFocus(new System.Windows.Input.TraversalRequest(
            System.Windows.Input.FocusNavigationDirection.First));

        for (int i = 0; i < index; i++)
        {
            var request = new System.Windows.Input.TraversalRequest(
                System.Windows.Input.FocusNavigationDirection.Next);

            if (System.Windows.Input.Keyboard.FocusedElement is UIElement element) element.MoveFocus(request);
            else window.MoveFocus(request);
        }

        Console.WriteLine($"focus.stop={index} {DescribeFocus(System.Windows.Input.Keyboard.FocusedElement as DependencyObject)}");
    }

    /// <summary>
    /// 拖到标签栏打开（FR-6.11）。真实的 OLE 拖放没法在离屏窗口里合成——
    /// 与框选、侧键同一个限制（窗口未激活时 WPF 不处理输入）。
    /// 但"落在哪个标签上"之后的处理才是这段功能的主体：反射调用窗口的处理函数，
    /// 就能验证"进目标标签 / 开新标签 / 满额提示"三条分支。
    /// </summary>
    private static async Task ExerciseTabDrop(Window window, List<string> paths, int targetIndex)
    {
        FieldInfo? tabsField = typeof(MainWindow).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance);
        var host = tabsField?.GetValue(window) as Jianlan.App.ViewModels.TabHostViewModel;

        object? target = null;
        if (host is not null && targetIndex >= 0 && targetIndex < host.Tabs.Count)
        {
            target = host.Tabs[targetIndex];
        }

        MethodInfo? drop = typeof(MainWindow).GetMethod("OnTabsDropped", BindingFlags.NonPublic | BindingFlags.Instance);
        if (drop is null)
        {
            Console.WriteLine("tabdrop=(找不到 OnTabsDropped)");
            return;
        }

        Console.WriteLine($"tabdrop.target={(target is null ? "空白处" : targetIndex.ToString())} paths={paths.Count}");
        drop.Invoke(window, [paths.ToArray(), target]);

        await Task.Delay(1500);   // 导航是异步的，等它落定
    }

    /// <summary>逐个标签打印路径——拖放之后要看清"哪个标签去了哪"，只报总数看不出。</summary>
    private static void PrintTabPaths(Window window, string when)
    {
        FieldInfo? field = typeof(MainWindow).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance);

        if (field?.GetValue(window) is not Jianlan.App.ViewModels.TabHostViewModel host)
        {
            Console.WriteLine($"tabs.{when}=(读不到 _tabs)");
            return;
        }

        Console.WriteLine($"tabs.{when}=index:{host.ActiveIndex} count:{host.Tabs.Count}");

        for (int i = 0; i < host.Tabs.Count; i++)
        {
            Console.WriteLine($"tabs.{when}.{i}={host.Tabs[i].Primary.CurrentPath}");
        }
    }

    /// <summary>
    /// 打印活动标签序号与总数。按键走查时它比截图可靠：
    /// "按了右键"和"标签真的换了"是两件事，只有序号能分开。
    /// </summary>
    private static void PrintTabs(Window window, string when)
    {
        FieldInfo? field = typeof(MainWindow).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance);
        object? tabs = field?.GetValue(window);

        if (tabs is Jianlan.App.ViewModels.TabHostViewModel host)
        {
            Console.WriteLine($"tabs.{when}=index:{host.ActiveIndex} count:{host.Tabs.Count}");
            return;
        }

        Console.WriteLine($"tabs.{when}=(读不到 _tabs)");
    }

    /// <summary>
    /// 向真实窗口句柄投递按键，走 WPF 自己的消息处理链。
    ///
    /// 与 <see cref="WalkTabChain"/> 的区别很重要：那个直接调 <c>MoveFocus</c>，
    /// 只能证明"停靠点顺序是那样"；这条消息进的是 <c>HwndSource</c>，
    /// 能被 <c>OnPreviewKeyDown</c> 与控件自己的按键处理器收到，
    /// 因此能证明"按键绑定接上了"。投递不经过全局钩子——那是设计如此，
    /// 见 `docs/shortcuts.md` §10 最后一条。
    /// </summary>
    private static async Task PostKeys(Window window, string spec, int count)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        Console.WriteLine($"key.start focus={DescribeFocus(System.Windows.Input.Keyboard.FocusedElement as DependencyObject)}");

        foreach (string name in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int vk = name.ToLowerInvariant() switch
            {
                "tab" => 0x09,
                "enter" => 0x0D,
                "space" => 0x20,
                "left" => 0x25,
                "up" => 0x26,
                "right" => 0x27,
                "down" => 0x28,
                "esc" => 0x1B,
                "delete" => 0x2E,
                _ => throw new ArgumentOutOfRangeException(nameof(spec), name, "未知键名"),
            };

            for (int i = 0; i < count; i++)
            {
                _ = PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)1);
                _ = PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, unchecked((IntPtr)(int)0xC0000001));

                // 我们在 dispatcher 回调里，不交出控制权消息泵就不会转，
                // 焦点看起来"根本没动"——这个 await 是功能性的，不是等待
                await Task.Delay(120);

                Console.WriteLine($"key.{name.ToLowerInvariant()}[{i}] focus={DescribeFocus(System.Windows.Input.Keyboard.FocusedElement as DependencyObject)}");
            }
        }
    }

    /// <summary>
    /// 翻窗口的键盘线索位，让 WPF 画焦点视觉。
    ///
    /// WPF 把"用户是不是在用键盘"存在窗口的 UI state 里（WM_UPDATEUISTATE），
    /// 不是自己的开关，<c>KeyboardNavigation.ShowKeyboardCues</c> 也是 internal。
    /// 但既然是系统层的窗口状态，就能发消息改——这就是像素级确认能在此完成的原因。
    /// </summary>
    private static void EnableKeyboardCues(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        IntPtr wParam = (IntPtr)((UIS_CLEAR << 16) | UISF_HIDEFOCUS);

        _ = SendMessage(hwnd, WM_UPDATEUISTATE, wParam, IntPtr.Zero);

        // 发消息不够用：HwndSource 不处理 WM_UPDATEUISTATE（实测两张图逐像素相同）。
        // 真正决定画不画的是 KeyboardNavigation 上一个 internal 的 attached DP，
        // 它标了 Inherits，并且不是只读 DP——拿到引用就能直接设，设完往下传。
        FieldInfo? dpField = typeof(System.Windows.Input.KeyboardNavigation).GetField(
            "ShowKeyboardCuesProperty",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);

        if (dpField?.GetValue(null) is not DependencyProperty dp)
        {
            Console.WriteLine("cues.dp=missing");
            return;
        }

        window.SetValue(dp, true);
        UIElement? focused = System.Windows.Input.Keyboard.FocusedElement as UIElement;
        focused?.SetValue(dp, true);

        // 焦点视觉画在 AdornerLayer 上。窗口模板里没有 AdornerDecorator 的话，
        // FocusVisualStyle 挂得再对也画不出来——这正是像素级确认要回答的问题。
        var layer = focused is null ? null : System.Windows.Documents.AdornerLayer.GetAdornerLayer(focused);
        int adorners = layer is null || focused is null ? -1 : layer.GetAdorners(focused)?.Length ?? -1;

        Console.WriteLine($"cues.dp=set value={window.GetValue(dp)} " +
                          $"layer={(layer is null ? "none" : "ok")} adorners={adorners} hwnd=0x{hwnd:X}");
    }

    private static string DescribeFocus(DependencyObject? element)
    {
        if (element is null) return "(none)";

        var parts = new System.Collections.Generic.List<string>
        {
            element.GetType().Name,
        };

        if (element is FrameworkElement { Name.Length: > 0 } named) parts.Add($"#{named.Name}");

        string automation = System.Windows.Automation.AutomationProperties.GetName(element);
        if (automation.Length > 0) parts.Add($"name=\"{automation}\"");

        // 焦点视觉（FR-10.7）：自绘模板里不再写 FocusVisualStyle={x:Null}，
        // 每个停靠点都应该挂着一条自定义焦点样式。这里只能证明"挂上了"，
        // 画出来什么样得有交互桌面时人眼看（WPF 只在真按过键之后才画）。
        if (element is System.Windows.Controls.Control focusable)
        {
            parts.Add(focusable.FocusVisualStyle is null ? "focus=default" : "focus=custom");
        }

        string content = element switch
        {
            System.Windows.Controls.TextBox box => box.Text,
            System.Windows.Controls.ContentControl control => control.Content?.ToString() ?? string.Empty,
            _ => string.Empty,
        };

        if (content.Length > 0)
        {
            parts.Add($"text=\"{Truncate(content, 40)}\"");
        }

        return string.Join("  ", parts);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// 打开某一色的结果页。走的是侧栏点击调用的同一个方法，只是不经过鼠标——
    /// 离屏进程里没有可点的桌面。
    /// </summary>
    private static void OpenTagResults(MainWindow window, string tagName)
    {
        TagColors color = Enum.Parse<TagColors>(tagName, ignoreCase: true);

        string localized = TagPresets.Colors.FirstOrDefault(p => p.Value == color)?.Name
                           ?? color.ToString();

        MethodInfo? method = typeof(MainWindow).GetMethod(
            "OpenTagResults", BindingFlags.NonPublic | BindingFlags.Instance);

        if (method is null) throw new MissingMethodException("MainWindow.OpenTagResults");

        method.Invoke(window, [color, localized, false]);
    }

    /// <summary>
    /// 模拟"双击结果里第 index 条"。同样走窗口自己的处理函数，
    /// 用来验证跳转那条最容易被时序搞坏的路径（切形态 → 导航 → 选中）。
    /// </summary>
    private static void ActivateResult(MainWindow window, int index)
    {
        var field = typeof(MainWindow).GetField("_tagResults", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(window) is not TagResultsViewModel vm) throw new MissingMemberException("_tagResults");

        TagResultItem item = vm.Items[index];

        MethodInfo? method = typeof(MainWindow).GetMethod(
            "OnTagResultActivated", BindingFlags.NonPublic | BindingFlags.Instance);

        if (method is null) throw new MissingMethodException("MainWindow.OnTagResultActivated");

        method.Invoke(window, [item]);
    }

    private static void Save(Window window, string outPath)
    {
        // 布局必须先定下来，否则 ActualWidth 还是 0，渲染出来是一张空白
        window.UpdateLayout();

        double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        DpiScale dpi = VisualTreeHelper.GetDpi(window);

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);

        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using FileStream stream = File.Create(outPath);
        encoder.Save(stream);
    }

    /// <summary>
    /// 单独拍预览处理器的窗口。
    ///
    /// 为什么不能靠 <see cref="Save"/>：处理器画在一个真实的子窗口里（HwndHost），
    /// WPF 的渲染管线完全不包含它——RenderTargetBitmap 拍出来的那块区域是空的。
    /// 只能按窗口句柄自己 PrintWindow。
    /// </summary>
    private static void SaveSurface(Window window, string outPath)
    {
        IntPtr host = new WindowInteropHelper(window).Handle;
        IntPtr surface = FindSurface(host);

        if (surface == IntPtr.Zero)
        {
            Console.Error.WriteLine("没找到处理器窗口（JianlanPreviewSurface）");
            return;
        }

        // 处理器往往不在我们给的那个窗口上画，而是在里面再开一个自己的窗口
        // （搜狗 PDF 用 Qt，就多一层 Qt5152QWindowIcon）。两个都拍一张。
        Capture(surface, outPath);

        IntPtr content = FindContentWindow(surface);
        if (content != IntPtr.Zero && content != surface)
        {
            Capture(content, Path.Combine(
                Path.GetDirectoryName(outPath)!,
                Path.GetFileNameWithoutExtension(outPath) + "-content" + Path.GetExtension(outPath)));
        }
    }

    private static void Capture(IntPtr hwnd, string outPath)
    {
        IntPtr surface = hwnd;

        Native.GetWindowRect(surface, out Native.NativeRect rect);

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
        {
            Console.Error.WriteLine($"处理器窗口尺寸非法：{width}x{height}");
            return;
        }

        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr memory = Native.CreateCompatibleDC(screen);
        IntPtr bitmap = Native.CreateCompatibleBitmap(screen, width, height);
        IntPtr previous = Native.SelectObject(memory, bitmap);

        try
        {
            // PW_RENDERFULLCONTENT(2)：不加这个标志，DirectComposition 渲染的窗口会得到全黑
            bool captured = Native.PrintWindow(surface, memory, 2);

            // Qt（搜狗 PDF 预览就是）不处理 WM_PRINT，PrintWindow 直接返回 false。
            // 退一步直接从窗口 DC 上 BitBlt：它的像素确实落在窗口 DC 上，只是不肯"配合打印"。
            if (!captured)
            {
                IntPtr windowDc = Native.GetWindowDC(surface);

                try
                {
                    captured = Native.BitBlt(memory, 0, 0, width, height, windowDc, 0, 0, Native.SrcCopy);
                }
                finally
                {
                    Native.ReleaseDC(surface, windowDc);
                }

                Console.WriteLine($"PrintWindow=false → BitBlt={(captured ? "OK" : "false")}  {width}x{height}");
            }
            else
            {
                Console.WriteLine($"PrintWindow=OK  {width}x{height}");
            }

            BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            using FileStream stream = File.Create(outPath);
            encoder.Save(stream);

            Console.WriteLine($"已保存处理器窗口 {outPath}");
        }
        finally
        {
            Native.SelectObject(memory, previous);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// 打印预览视图模型的关键状态。判断"处理器是否接管、是否已渲染完、是否退到缩略图"
    /// 靠这些字段，比在截图里找一行灰字可靠得多。
    /// </summary>
    /// <summary>
    /// 投递一次鼠标侧键，并打印按下前后的路径（FR-6.2）。
    /// 侧键没有对应的键盘消息，也没有办法用 WPF 合成事件伪造位置，
    /// 所以直接往窗口句柄投 WM_XBUTTONDOWN，让框架按真实输入处理。
    /// </summary>
    private static async Task ExerciseMouseButton(MainWindow window, int button)
    {
        PaneViewModel pane = GetPane(window).ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        Console.WriteLine($"xbutton.before path={pane.CurrentPath} canBack={pane.CanGoBack} canForward={pane.CanGoForward}");

        // wParam 高字是 XBUTTON1(1) / XBUTTON2(2)，低字是设备键状态，这里给 0
        IntPtr wParam = (IntPtr)(button << 16);

        PostMessage(hwnd, 0x020B, wParam, IntPtr.Zero);   // WM_XBUTTONDOWN
        await Task.Delay(300);
        PostMessage(hwnd, 0x020C, wParam, IntPtr.Zero);   // WM_XBUTTONUP
        await Task.Delay(900);

        Console.WriteLine($"xbutton.after path={pane.CurrentPath} canBack={pane.CanGoBack} canForward={pane.CanGoForward}");
    }

    /// <summary>
    /// 模拟一次框选（FR-6.7）。坐标是相对文件列表的逻辑像素，
    /// 这里换算成窗口客户区的设备像素再投递——lParam 要的就是后者。
    /// </summary>
    private static async Task ExerciseRubberBand(MainWindow window, double[] box)
    {
        // FileList 住在 PaneView 里，不在窗口的命名范围里
        if (GetPane(window).FindName("FileList") is not ListBox list) throw new MissingMemberException("FileList");

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        Point start = ToClient(list, hwnd, new Point(box[0], box[1]));
        Point end = ToClient(list, hwnd, new Point(box[2], box[3]));

        Console.WriteLine($"band.before selected={list.SelectedItems.Count}");
        Console.WriteLine($"band.start={start.X:0},{start.Y:0} end={end.X:0},{end.Y:0}");

        PostMouse(hwnd, 0x0201, start);   // WM_LBUTTONDOWN

        const int steps = 6;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            PostMouse(hwnd, 0x0200, new Point(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t));
            await Task.Delay(60);
        }

        await Task.Delay(120);
        Console.WriteLine($"band.dragging selected={list.SelectedItems.Count}");

        PostMouse(hwnd, 0x0202, end);     // WM_LBUTTONUP
        await Task.Delay(400);

        Console.WriteLine($"band.after selected={list.SelectedItems.Count}");
    }

    /// <summary>
    /// 直接调用框选的命中逻辑（FR-6.7）：给一个矩形，看选中了哪些项。
    /// 走反射是因为它本来就是私有的——为了测试把它改成公开，等于让生产代码
    /// 为一个诊断工具让路。
    /// </summary>
    private static void ExerciseBandSelect(MainWindow window, Rect band)
    {
        PaneView pane = GetPane(window);
        if (pane.FindName("FileList") is not ListBox list) throw new MissingMemberException("FileList");

        MethodInfo? apply = typeof(PaneView).GetMethod(
            "ApplyBandSelection", BindingFlags.NonPublic | BindingFlags.Instance);

        if (apply is null) throw new MissingMethodException("PaneView.ApplyBandSelection");

        Console.WriteLine($"bandrect.before selected={list.SelectedItems.Count} band={band}");

        apply.Invoke(pane, [band]);

        Console.WriteLine($"bandrect.after selected={list.SelectedItems.Count}");
        foreach (object item in list.SelectedItems) Console.WriteLine($"  picked {item}");
    }

    private static Point ToClient(Visual reference, IntPtr hwnd, Point local)
    {
        Point screen = reference.PointToScreen(local);
        var point = new POINT { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
        ScreenToClient(hwnd, ref point);
        return new Point(point.X, point.Y);
    }

    private static void PostMouse(IntPtr hwnd, int msg, Point client)
    {
        int packed = ((int)client.Y << 16) | ((int)client.X & 0xFFFF);
        PostMessage(hwnd, msg, msg == 0x0201 ? (IntPtr)1 : IntPtr.Zero, (IntPtr)packed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    private static void PrintState(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_preview", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(window) is not PreviewViewModel vm)
        {
            Console.Error.WriteLine("取不到 _preview");
            return;
        }

        Console.WriteLine(
            $"STATE handler={vm.HasHandler} handlerPath={vm.HandlerPath} " +
            $"image={vm.HasImage} text={vm.HasText} note={vm.HasNote} empty={vm.IsEmpty} multiple={vm.IsMultiple}");

        Console.WriteLine($"STATE footer={vm.FooterText}");
        Console.WriteLine($"STATE noteText={vm.NoteText}");
    }

    /// <summary>打印整棵子窗口树。处理器可能不在我们给的那个窗口上画，得先看清层次。</summary>
    private static void DumpTree(Window window)
    {
        IntPtr host = new WindowInteropHelper(window).Handle;
        var seen = new System.Collections.Generic.HashSet<IntPtr>();

        void Walk(IntPtr parent, int depth)
        {
            Native.EnumChildWindows(parent, (hwnd, _) =>
            {
                // EnumChildWindows 给的是所有后代，递归会把同一句打很多遍
                if (!seen.Add(hwnd)) return true;

                var name = new StringBuilder(128);
                Native.GetClassName(hwnd, name, name.Capacity);
                Native.GetWindowRect(hwnd, out Native.NativeRect rect);

                Console.WriteLine(
                    $"TREE {depth} {name} {rect.Right - rect.Left}x{rect.Bottom - rect.Top} " +
                    $"visible={Native.IsWindowVisible(hwnd)}");

                Walk(hwnd, depth + 1);
                return true;
            }, IntPtr.Zero);
        }

        Walk(host, 0);
    }

    /// <summary>surface 里面积最大的可见子窗口——通常才是真正画内容的那一个。</summary>
    private static IntPtr FindContentWindow(IntPtr surface)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;

        Native.EnumChildWindows(surface, (hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;

            Native.GetWindowRect(hwnd, out Native.NativeRect rect);
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);

            if (area <= bestArea) return true;

            best = hwnd;
            bestArea = area;
            return true;
        }, IntPtr.Zero);

        return best;
    }

    private static IntPtr FindSurface(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;

        Native.EnumChildWindows(parent, (hwnd, _) =>
        {
            var name = new StringBuilder(64);
            Native.GetClassName(hwnd, name, name.Capacity);

            if (!string.Equals(name.ToString(), "JianlanPreviewSurface", StringComparison.Ordinal)) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// 两侧窗格各设一套视图与排序（FR-3.7）。要证的不是"能设"，
    /// 而是"设了一侧另一侧不动、且下一次打开还各是各的"——所以两行一起打。
    /// 走的是工具栏按钮背后那两个方法，排序与切视图各自会落盘。
    /// </summary>
    private static async Task ExercisePaneViews(MainWindow window, string? view1, string? sort1,
        string? view2, string? sort2)
    {
        PaneView pane1 = GetPane(window);

        if (typeof(MainWindow).GetField("Pane2", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(window) is not PaneView pane2)
        {
            throw new MissingMemberException("MainWindow.Pane2");
        }

        ApplyPaneState(pane1, view1, sort1);
        ApplyPaneState(pane2, view2, sort2);

        await Task.Delay(600);

        PaneViewModel first = pane1.ViewModel ?? throw new MissingMemberException("Pane.ViewModel");
        PaneViewModel second = pane2.ViewModel ?? throw new MissingMemberException("Pane2.ViewModel");

        Console.WriteLine($"pane1.slot={pane1.Slot} view={first.ViewMode} field={first.Sort.Field} desc={first.Sort.Descending}");
        Console.WriteLine($"pane2.slot={pane2.Slot} view={second.ViewMode} field={second.Sort.Field} desc={second.Sort.Descending}");
    }

    /// <summary>
    /// 粘贴到同名项上的两种结局（FR-5.2.8）。这里只跑"关掉确认"那一档：
    /// 开着的那一档会弹出模态确认窗，离屏进程没有人可点，会一直挂在那儿。
    /// 关掉确认必须**直接覆盖**而不是退回自动改名——否则"想覆盖"的用户就没有入口了。
    /// </summary>
    private static async Task ExercisePasteConflict(MainWindow window, string source, string target, bool confirm)
    {
        App.Settings.Behavior.ConfirmOverwrite = confirm;

        string[] files = Directory.GetFiles(source);

        if (files.Length == 0) throw new InvalidOperationException($"源目录没有文件：{source}");

        Console.WriteLine($"paste.confirmOverwrite={confirm} files={files.Length}");
        Console.WriteLine($"paste.from={source}");
        Console.WriteLine($"paste.to={target}");

        if (confirm)
        {
            // 这条路要弹确认窗；离屏没有人能点，只报出问句本身
            Console.WriteLine($"paste.question={Jianlan.Core.Localization.Strings.Dialog_OverwriteOne}");
            return;
        }

        PaneView pane = GetPane(window);
        PaneViewModel model = pane.ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        await model.NavigateAsync(target);
        await Task.Delay(400);

        // 剪贴板是应用自己的那份（不是系统的），内部类型，只能反射
        Type clip = typeof(MainWindow).Assembly.GetType("Jianlan.App.Services.FileClipboard")
            ?? throw new MissingMemberException("FileClipboard");

        MethodInfo? set = clip.GetMethod("Set", BindingFlags.Public | BindingFlags.Static);
        if (set is null) throw new MissingMethodException("FileClipboard.Set");

        set.Invoke(null, [files, false]);

        MethodInfo? paste = typeof(PaneView).GetMethod(
            "PasteAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        if (paste is null) throw new MissingMethodException("PaneView.PasteAsync");

        if (paste.Invoke(pane, null) is Task task) await task;

        await Task.Delay(600);

        Console.WriteLine($"paste.status={model.StatusText}");

        foreach (string file in Directory.GetFiles(target).OrderBy(Path.GetFileName))
        {
            Console.WriteLine($"paste.list={Path.GetFileName(file)} content={File.ReadAllText(file).Trim()}");
        }
    }

    /// <summary>只读地打印两侧窗格当前状态，用来验"重开之后还是各是各的"。</summary>
    private static void PrintPaneStates(MainWindow window)
    {
        PaneView pane1 = GetPane(window);

        if (typeof(MainWindow).GetField("Pane2", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(window) is not PaneView pane2)
        {
            throw new MissingMemberException("MainWindow.Pane2");
        }

        PaneViewModel? first = pane1.ViewModel;
        PaneViewModel? second = pane2.ViewModel;

        Console.WriteLine($"pane1.slot={pane1.Slot} view={first?.ViewMode} " +
                          $"field={first?.Sort.Field} desc={first?.Sort.Descending}");
        Console.WriteLine($"pane2.slot={pane2.Slot} view={second?.ViewMode} " +
                          $"field={second?.Sort.Field} desc={second?.Sort.Descending}");
    }

    private static void ApplyPaneState(PaneView view, string? viewName, string? sortField)
    {
        PaneViewModel model = view.ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        if (sortField is not null)
        {
            if (!Enum.TryParse<Jianlan.Core.Settings.SortFieldKind>(sortField, ignoreCase: true, out var field))
            {
                throw new ArgumentException($"未知排序字段：{sortField}");
            }

            model.SortBy(field);
        }

        if (viewName is null) return;

        if (!Enum.TryParse<Jianlan.Core.Settings.DefaultViewKind>(viewName, ignoreCase: true, out var kind))
        {
            throw new ArgumentException($"未知视图：{viewName}");
        }

        // 与点"列表/详细"按钮同一个入口：它还会顺手存盘。
        // 这个方法是 internal 的（设置页与本程序集之外不该乱调），所以只能反射
        MethodInfo? apply = typeof(PaneView).GetMethod(
            "ApplyViewMode", BindingFlags.NonPublic | BindingFlags.Instance);

        if (apply is null) throw new MissingMethodException("PaneView.ApplyViewMode");

        apply.Invoke(view, [kind]);
    }

    /// <summary>
    /// 新标签开在哪（FR-5.2.6）。走"改设置 → 存盘 → 点新建标签"这条用户链，
    /// 而不是直接 <c>new TabViewModel</c>：要证的正是"处理器读到了这个设置"。
    /// 三档的差别只有一处——<c>Add()</c> 在没有显式路径时怎么解析目标目录。
    /// </summary>
    private static async Task ExerciseNewTabMode(MainWindow window, string mode)
    {
        if (!Enum.TryParse<Jianlan.Core.Settings.NewTabModeKind>(mode, ignoreCase: true, out var kind))
        {
            throw new ArgumentException($"未知的新标签模式：{mode}（可选 home / path / last）");
        }

        App.Settings.General.NewTabMode = kind;
        App.SaveSettings();

        MethodInfo? newTab = typeof(MainWindow).GetMethod(
            "NewTab", BindingFlags.NonPublic | BindingFlags.Instance);

        if (newTab is null) throw new MissingMethodException("MainWindow.NewTab");

        newTab.Invoke(window, null);
        await Task.Delay(700);

        Jianlan.App.ViewModels.TabHostViewModel host = GetTabs(window);
        Jianlan.App.ViewModels.TabViewModel tab = host.Active ?? throw new MissingMemberException("TabHostViewModel.Active");

        Console.WriteLine($"newtab.mode={kind}");
        Console.WriteLine($"newtab.tabs={host.Tabs.Count} index={host.ActiveIndex}");
        Console.WriteLine($"newtab.kind={tab.Kind} isHome={tab.IsHome}");
        Console.WriteLine($"newtab.path={(string.IsNullOrEmpty(tab.Primary.CurrentPath) ? "(空)" : tab.Primary.CurrentPath)}");
        Console.WriteLine(
            $"newtab.secondary={(string.IsNullOrEmpty(tab.Secondary?.CurrentPath) ? "(无)" : tab.Secondary!.CurrentPath)}");
    }

    /// <summary>
    /// 对选中项切换颜色标签（FR-4.1.2）。多色用"切换"而不是"设成"：
    /// 连续点两个色，第二次若替换了第一次，行上就只有一个点——这正是要看出来的。
    /// </summary>
    private static async Task ExerciseTagToggle(MainWindow window, string[] colors)
    {
        PaneViewModel pane = GetPane(window).ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        foreach (string name in colors)
        {
            if (!Enum.TryParse<Jianlan.Core.Tagging.TagColors>(name, ignoreCase: true, out var color))
            {
                throw new ArgumentException($"未知颜色：{name}");
            }

            await pane.ToggleTagAsync(color);
            await Task.Delay(150);
            Console.WriteLine($"tag.toggle={color} mask={pane.SelectionTagMask}");
        }

        Console.WriteLine($"tag.selected={pane.SelectedEntries.Count} dotsMax={Jianlan.Core.Tagging.TagPalette.MaxDots}");

        foreach (Jianlan.Core.FileSystem.FileEntry entry in pane.SelectedEntries)
        {
            Console.WriteLine(
                $"tag.entry={entry.Name} tags={entry.Tags} " +
                $"dotCount={entry.ColorDots.Count} dots={string.Join('+', entry.ColorDots)}");
        }
    }

    /// <summary>
    /// 自然排序 / 区分大小写（FR-5.2.3）。写设置后调窗口那个"重排所有窗格"的方法，
    /// 与设置页勾选后发生的事完全一样。名字串直接打出来，比截图比对快得多。
    /// </summary>
    private static async Task ExerciseSortOptions(MainWindow window, bool natural, bool caseSensitive)
    {
        App.Settings.Behavior.NaturalSort = natural;
        App.Settings.Behavior.CaseSensitiveSort = caseSensitive;

        // 这是 public 的（设置页直接调），不必反射
        window.ApplySortOptionsToPanes();
        await Task.Delay(700);

        PaneViewModel pane = GetPane(window).ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        Console.WriteLine(
            $"sort.natural={pane.Sort.Natural} case={pane.Sort.CaseSensitive} " +
            $"field={pane.Sort.Field} desc={pane.Sort.Descending} foldersFirst={pane.Sort.FoldersFirst}");

        Console.WriteLine("sort.order=" + string.Join(" | ", pane.Entries.Take(12).Select(e => e.Name)));
    }

    /// <summary>
    /// 标签在新窗口打开（FR-2.12）。调的是菜单项背后那个方法：
    /// 副窗口是**同一进程里的第二个窗口**，不是新进程——新进程会被单实例守卫接走，
    /// 结果"开新窗口"变成"让已有窗口跳过去"。所以判据是
    /// <c>Application.Windows</c> 里多了一个 MainWindow，而且它落在指定目录上。
    /// </summary>
    private static MainWindow? ExerciseNewWindow(MainWindow window, int tabIndex)
    {
        Jianlan.App.ViewModels.TabHostViewModel host = GetTabs(window);

        int before = Application.Current.Windows.OfType<MainWindow>().Count();
        int index = tabIndex >= 0 ? Math.Clamp(tabIndex, 0, host.Tabs.Count - 1) : host.ActiveIndex;

        Console.WriteLine($"newwin.before={before} from={index} path={host.Tabs[index].Primary.CurrentPath}");

        // 走标签条菜单那一项自己的处理器（sender 的 DataContext 就是被点的标签）：
        // 这样"菜单项 → 事件 → 副窗口"整条链都真的跑过一遍。
        // 直接调 OpenTabInNewWindow 只能证明后半截。
        object? bar = typeof(MainWindow).GetField("TabBar", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(window);

        MethodInfo? handler = bar?.GetType().GetMethod(
            "OnTabOpenWindow", BindingFlags.NonPublic | BindingFlags.Instance);

        if (bar is not null && handler is not null)
        {
            var sender = new System.Windows.Controls.ContentControl { DataContext = host.Tabs[index] };

            // 处理器里会写 e.Handled = true，而 RoutedEventArgs 没有关联事件时不许写这个属性。
            // 菜单项点下来带的就是 MenuItem.ClickEvent，照它造一个才等价
            handler.Invoke(bar, [sender, new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent)]);
        }
        else
        {
            MethodInfo? open = typeof(MainWindow).GetMethod(
                "OpenTabInNewWindow", BindingFlags.NonPublic | BindingFlags.Instance);

            if (open is null) throw new MissingMethodException("MainWindow.OpenTabInNewWindow");

            open.Invoke(window, [host.Tabs[index]]);
        }

        // 副窗口的 Show 是同步的，但布局与首帧要走一次调度；等它空闲下来再数窗口
        Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        MainWindow[] windows = [.. Application.Current.Windows.OfType<MainWindow>()];

        Console.WriteLine($"newwin.after={windows.Length}");

        for (int i = 0; i < windows.Length; i++)
        {
            Jianlan.App.ViewModels.TabHostViewModel each = GetTabs(windows[i]);
            Console.WriteLine(
                $"newwin.window.{i}=tabs:{each.Tabs.Count} index:{each.ActiveIndex} " +
                $"path:{each.Active?.Primary.CurrentPath}");
        }

        // 副窗口就是那个不是原来那个的窗口
        return windows.FirstOrDefault(w => !ReferenceEquals(w, window));
    }

    /// <summary>
    /// 标签库导出（FR-4.2.7 / FR-5.3.6）。导出的是**状态快照**而不是操作日志：
    /// 磁盘上的 tags.jsonl 是 set/del 行，拿它当交换格式对方还得重放一遍。
    /// </summary>
    private static void PrintTagExport(string path)
    {
        App.Tags.EnsureLoaded();

        int written = Jianlan.Core.Tagging.TagArchive.Write(App.Tags.Snapshot(), path);
        Console.WriteLine($"tag.export={path} written={written} store={App.Tags.Count}");
    }

    /// <summary>
    /// 标签库导入。往返验证：导出的同一个文件再导回来，
    /// 记录数应当全部落在"跳过"（新旧一样）而不是"新增"，否则主键没对上。
    /// </summary>
    private static void PrintTagImport(string path)
    {
        Jianlan.Core.Tagging.TagArchiveDocument document = Jianlan.Core.Tagging.TagArchive.Read(path);
        List<Jianlan.Core.Tagging.TagRecord> records = Jianlan.Core.Tagging.TagArchive.ToRecords(document);

        Jianlan.Core.Tagging.TagImportResult result = App.Tags.Import(records);

        Console.WriteLine(
            $"tag.import={path} kind={document.Kind} version={document.Version} records={records.Count}");
        Console.WriteLine(
            $"tag.import.added={result.Added} updated={result.Updated} skipped={result.Skipped} " +
            $"total={result.Total} store={App.Tags.Count}");
    }

    /// <summary>
    /// 设置页本批新增控件的实际取值。截图能证明"控件在那儿"，
    /// 但"下拉框选的是哪一档、八个颜色名现在叫什么"只有打印出来才看得准。
    /// </summary>
    private static void PrintSettingsState(Window settings)
    {
        if (settings.FindName("NewTabModeCombo") is System.Windows.Controls.ComboBox mode)
        {
            Console.WriteLine($"settings.newTabMode=index:{mode.SelectedIndex} text:{mode.Text}");
        }

        if (settings.FindName("NaturalSortCheck") is System.Windows.Controls.CheckBox natural)
        {
            Console.WriteLine($"settings.naturalSort={natural.IsChecked}");
        }

        if (settings.FindName("CaseSensitiveCheck") is System.Windows.Controls.CheckBox sensitive)
        {
            Console.WriteLine($"settings.caseSensitive={sensitive.IsChecked}");
        }

        if (settings.FindName("ConfirmOverwriteCheck") is System.Windows.Controls.CheckBox overwrite)
        {
            Console.WriteLine($"settings.confirmOverwrite={overwrite.IsChecked}");
        }

        if (settings.FindName("TagColorNamePanel") is not System.Windows.Controls.Panel panel) return;

        Console.WriteLine($"settings.tagColorNameRows={panel.Children.Count}");

        foreach (object child in panel.Children)
        {
            if (child is not System.Windows.Controls.Panel row) continue;

            foreach (object item in row.Children)
            {
                if (item is System.Windows.Controls.TextBox { Tag: Jianlan.Core.Tagging.TagColors color } box)
                {
                    Console.WriteLine($"settings.tagColorName.{color}={box.Text}");
                }
            }
        }
    }

    private static Jianlan.App.ViewModels.TabHostViewModel GetTabs(MainWindow window)
    {
        FieldInfo? field = typeof(MainWindow).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance);

        return field?.GetValue(window) as Jianlan.App.ViewModels.TabHostViewModel
            ?? throw new MissingMemberException("MainWindow._tabs");
    }


    // ── 预览操作 / 可执行确认 / 撤销（FR-9.1.5 / FR-5.2.11 / FR-6.8）──────────

    private static PreviewPane GetPreview(MainWindow window)
        => window.FindName("Preview") as PreviewPane ?? throw new MissingMemberException("MainWindow.Preview");

    private static void PrintPreviewActions(MainWindow window)
    {
        PreviewPane preview = GetPreview(window);
        PreviewViewModel? model = preview.DataContext as PreviewViewModel;

        Console.WriteLine(
            $"preview.visible={preview.Visibility} canAct={model?.CanAct} " +
            $"current={model?.Current?.Name ?? "(null)"}");

        foreach (string name in new[] { "OpenButton", "CopyPathButton", "DeleteButton", "CloseButton" })
        {
            if (preview.FindName(name) is not Button button)
            {
                Console.WriteLine($"preview.button.{name}=missing");
                continue;
            }

            Console.WriteLine(
                $"preview.button.{name}=found visible={button.IsVisible} enabled={button.IsEnabled}");
        }
    }

    private static async Task ClickPreviewButton(MainWindow window, string which)
    {
        PreviewPane preview = GetPreview(window);

        string name = which.ToLowerInvariant() switch
        {
            "open" => "OpenButton",
            "copypath" => "CopyPathButton",
            "delete" => "DeleteButton",
            "close" => "CloseButton",
            _ => which,
        };

        if (preview.FindName(name) is not Button button)
        {
            Console.WriteLine($"preview.click 找不到 {name}");
            return;
        }

        // 删除会走完整链路，包括"确认删除"。离屏没有人可点，挂个定时器自动答"是"——
        // 顺带证明这颗按钮接的是正常的删除路径，不是另开一条
        var confirmed = new List<string>();
        DispatcherTimer? timer = null;

        if (string.Equals(name, "DeleteButton", StringComparison.Ordinal))
        {
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                foreach (Window each in Application.Current.Windows)
                {
                    if (each is not ConfirmWindow confirm || !confirm.IsVisible) continue;

                    confirmed.Add(confirm.Title);
                    confirm.DialogResult = true;
                    return;
                }
            };
            timer.Start();
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Console.WriteLine($"preview.click={name}");

        // 删除是异步的，确认窗要过一会儿才冒出来。等它、答它，再收尾
        if (timer is not null) await Task.Delay(1200);

        if (confirmed.Count > 0) Console.WriteLine($"preview.click.confirmed={confirmed[0]}");
        timer?.Stop();
    }

    private static void PrintPreviewState(MainWindow window)
    {
        PreviewPane preview = GetPreview(window);
        PreviewViewModel? model = preview.DataContext as PreviewViewModel;

        Console.WriteLine($"preview.visible={preview.Visibility}");
        Console.WriteLine($"setting.showPreview={App.Settings.Appearance.ShowPreview}");
        Console.WriteLine($"preview.current={model?.Current?.Name ?? "(null)"}");
        Console.WriteLine($"pane.count={GetPane(window).ViewModel?.Entries.Count}");

        Console.WriteLine($"pane.status={GetPane(window).ViewModel?.StatusText}");
        Console.WriteLine($"clipboard.text=[{ReadClipboard()}]");

        if (Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() is { } settings
            && settings.FindName("ShowPreviewCheck") is CheckBox check)
        {
            // 设置页与主窗口同时开着时，这颗勾选框必须跟着预览的开关走
            Console.WriteLine($"settings.showPreviewCheck={check.IsChecked}");
        }
    }

    /// <summary>读剪贴板。被别的程序占着时不要炸掉整个取证。</summary>
    private static string ReadClipboard()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return "(剪贴板被占用)";
        }
    }

    private static int IndexOfEntry(IReadOnlyList<FileEntry> entries, FileEntry entry)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (ReferenceEquals(entries[i], entry)) return i;
        }

        return -1;
    }

    private static bool InvokeCommand(PaneView pane, string id)
    {
        MethodInfo? method = typeof(PaneView).GetMethod(
            "TryInvoke", BindingFlags.NonPublic | BindingFlags.Instance);

        if (method is null) throw new MissingMethodException("PaneView.TryInvoke");

        return method.Invoke(pane, [id]) is true;
    }

    private static void SetFileClipboard(string[] paths)
    {
        Type type = typeof(MainWindow).Assembly.GetType("Jianlan.App.Services.FileClipboard")
            ?? throw new MissingMemberException("FileClipboard");

        MethodInfo? set = type.GetMethod("Set", BindingFlags.Public | BindingFlags.Static);
        if (set is null) throw new MissingMethodException("FileClipboard.Set");

        set.Invoke(null, [paths, false]);
    }

    /// <summary>
    /// 双击可执行文件的确认（FR-5.2.11）。
    ///
    /// 模态弹窗没有人可点，但 ShowDialog 会跑嵌套消息循环——所以这里是"先挂一个
    /// DispatcherTimer 自动作答，再调 OpenEntry"。这样验的是真实链路：
    /// 弹窗真的出现、按钮真的接上、答"是"才真的启动。
    /// </summary>
    private static async Task ExerciseWarnExec(MainWindow window, string path, bool answerYes, bool useDefault)
    {
        // 默认档不碰设置：要证明的正是"出厂状态下不拦、不弹窗"
        if (!useDefault) App.Settings.Behavior.WarnOnExecutable = true;

        var titles = new List<string>();

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };

        timer.Tick += (_, _) =>
        {
            foreach (Window each in Application.Current.Windows)
            {
                if (each is not ConfirmWindow confirm || !confirm.IsVisible) continue;

                titles.Add(confirm.Title);
                confirm.DialogResult = answerYes;
                return;
            }
        };

        timer.Start();

        PaneView pane = GetPane(window);
        PaneViewModel model = pane.ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        FileEntry? entry = model.Entries.FirstOrDefault(
            e => string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            Console.WriteLine($"warn.exec 没找到 {path}");
            timer.Stop();
            return;
        }

        SelectFile(window, [IndexOfEntry(model.Entries, entry)]);
        await Task.Delay(400);

        MethodInfo? open = typeof(PaneView).GetMethod("OpenEntry", BindingFlags.NonPublic | BindingFlags.Instance);
        if (open is null) throw new MissingMethodException("PaneView.OpenEntry");

        open.Invoke(pane, [entry]);

        await Task.Delay(800);
        timer.Stop();

        Console.WriteLine($"warn.setting={App.Settings.Behavior.WarnOnExecutable}");
        Console.WriteLine($"warn.dialog={(titles.Count > 0 ? titles[0] : "(none)")}");
        Console.WriteLine($"warn.answer={(useDefault ? "(default)" : answerYes ? "yes" : "no")}");
        Console.WriteLine($"warn.dialogLeft={Application.Current.Windows.OfType<ConfirmWindow>().Count()}");
    }

    private static async Task ExerciseUndoPaste(MainWindow window, string source)
    {
        string[] files = Directory.GetFiles(source);
        if (files.Length == 0)
        {
            Console.WriteLine("undo.paste 源目录是空的");
            return;
        }

        PaneView pane = GetPane(window);
        PaneViewModel model = pane.ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        // 要数的是**落点**的副本，不是源文件。数源目录等于自己数自己，
        // 撤销成功也好失败也好都是同一个数，这条断言就白写了
        string[] copies = files.Select(f => Path.Join(model.CurrentPath, Path.GetFileName(f))).ToArray();

        SetFileClipboard(files);

        bool pasted = InvokeCommand(pane, "file.paste");
        await Task.Delay(1800);

        Console.WriteLine($"undo.paste.invoked={pasted} copiesBefore={copies.Count(File.Exists)} canUndo={pane.Undo?.CanUndo}");
        Console.WriteLine($"undo.paste.status={model.StatusText}");

        bool undone = InvokeCommand(pane, "file.undo");
        await Task.Delay(1800);

        Console.WriteLine($"undo.undo.invoked={undone} copiesAfter={copies.Count(File.Exists)} " +
                          $"sourcesIntact={files.Count(File.Exists)} canUndo={pane.Undo?.CanUndo}");
        Console.WriteLine($"undo.paste.statusAfter={model.StatusText}");
    }

    private static async Task ExerciseUndoDelete(MainWindow window)
    {
        // 确认窗会挡住自动流程，关掉。这本来就是"删除确认"的另一条路，不影响撤销的验证
        App.Settings.Behavior.ConfirmDelete = false;
        App.Settings.Behavior.ConfirmBatch = false;

        PaneView pane = GetPane(window);
        PaneViewModel model = pane.ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        FileEntry? target = model.Entries.FirstOrDefault(e => !e.IsDirectory);
        if (target is null)
        {
            Console.WriteLine("undo.delete 目录里没有文件");
            return;
        }

        string path = target.FullPath;

        SelectFile(window, [IndexOfEntry(model.Entries, target)]);
        await Task.Delay(400);

        bool deleted = InvokeCommand(pane, "file.delete");
        await Task.Delay(1500);
        Console.WriteLine($"undo.delete.invoked={deleted} existsAfterDelete={File.Exists(path)} canUndo={pane.Undo?.CanUndo}");

        bool undone = InvokeCommand(pane, "file.undo");

        // 回收站还原要走 Shell，等它把文件挪回来
        await Task.Delay(3000);

        Console.WriteLine($"undo.delete.undoInvoked={undone} existsAfterUndo={File.Exists(path)}");
        Console.WriteLine($"undo.delete.status={model.StatusText}");
    }

    private static async Task ExerciseUndoModified(MainWindow window, string source)
    {
        string[] files = Directory.GetFiles(source);
        if (files.Length == 0)
        {
            Console.WriteLine("undo.modified 源目录是空的");
            return;
        }

        PaneView pane = GetPane(window);
        PaneViewModel model = pane.ViewModel ?? throw new MissingMemberException("PaneView.ViewModel");

        string directory = model.CurrentPath;
        string[] copies = files.Select(f => Path.Join(directory, Path.GetFileName(f))).ToArray();

        SetFileClipboard(files);
        InvokeCommand(pane, "file.paste");
        await Task.Delay(1800);

        Console.WriteLine($"undo.modified.pasted={copies.Count(File.Exists)}");

        // 改一处副本：内容与时间都变，两道指纹都对不上
        foreach (string copy in copies.Where(File.Exists))
        {
            File.AppendAllText(copy, "用户后来又改了这一段");
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddMinutes(3));
        }

        InvokeCommand(pane, "file.undo");
        await Task.Delay(1800);

        Console.WriteLine($"undo.modified.stillThere={copies.Count(File.Exists)}");
        Console.WriteLine($"undo.modified.status={model.StatusText}");
    }

    /// <summary>
    /// 回收站探针。列出条目及每条可用的动词名，用于确认还原动词的真实写法。
    /// 传入 <paramref name="target"/> 时会尝试用每个动词还原，并报告文件有没有真的回到原位。
    /// </summary>
    /// <summary>
    /// 列出进程的顶层窗口，逐个报类名、标题、可见性、扩展样式与图标句柄。
    /// 用途是判断「任务栏上那个按钮对应哪个窗口」：只有带 WS_EX_APPWINDOW、
    /// 或虽带 WS_EX_TOOLWINDOW 但被显式排除的窗口才会进任务栏。
    /// </summary>
    private static int ProbeProcessWindows(int pid)
    {
        var rows = new List<string>();

        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != (uint)pid) return true;

            var cls = new StringBuilder(256);
            Native.GetClassName(hwnd, cls, cls.Capacity);

            var title = new StringBuilder(256);
            Native.GetWindowText(hwnd, title, title.Capacity);

            long ex = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle).ToInt64();
            IntPtr big = Native.SendMessage(hwnd, Native.WmGetIcon, new IntPtr(1), IntPtr.Zero);
            IntPtr small = Native.SendMessage(hwnd, Native.WmGetIcon, IntPtr.Zero, IntPtr.Zero);

            rows.Add(string.Join("  ", [
                $"hwnd=0x{hwnd.ToInt64():X}",
                $"cls={cls}",
                $"visible={Native.IsWindowVisible(hwnd)}",
                $"appWindow={(ex & Native.WsExAppWindow) != 0}",
                $"toolWindow={(ex & Native.WsExToolWindow) != 0}",
                $"big=0x{big.ToInt64():X}",
                $"small=0x{small.ToInt64():X}",
                $"title=\"{title}\"",
            ]));

            return true;
        }, IntPtr.Zero);

        Console.WriteLine($"process={pid} 顶层窗口 {rows.Count} 个");

        foreach (string row in rows) Console.WriteLine(row);

        return rows.Count == 0 ? 2 : 0;
    }

    /// <summary>
    /// 问系统要 exe 的图标（SHGetFileInfo，大图标）。任务栏按钮用的就是这一份，
    /// 不是窗口自己 WM_SETICON 的那一份——两者不一致时，用户看到的一定是这一份。
    /// </summary>
    private static int ProbeExeIcon(string exePath, string outPath)
    {
        if (!File.Exists(exePath))
        {
            Console.WriteLine($"exe 不存在：{exePath}");
            return 1;
        }

        var info = new Native.ShellFileInfo();

        IntPtr result = Native.SHGetFileInfo(
            exePath, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.ShellFileInfo>(),
            Native.ShgfiIcon | Native.ShgfiLargeIcon);

        Console.WriteLine($"exe={exePath}");
        Console.WriteLine($"exe.size={new FileInfo(exePath).Length}");
        Console.WriteLine($"exe.mtime={File.GetLastWriteTime(exePath):yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"shgfi.result={result} iconIndex={info.iIcon}");

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            Console.WriteLine("系统没有返回图标");
            return 2;
        }

        System.Windows.Media.Imaging.BitmapSource source =
            System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        Console.WriteLine($"icon.size={source.PixelWidth}x{source.PixelHeight}");

        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);

        int width = source.PixelWidth;
        int height = source.PixelHeight;

        int blue = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (b > 120 && b > r + 40) blue++;
        }

        Console.WriteLine($"icon.blueRatio={(double)blue / (width * height):P1}");

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));

        using (FileStream stream = File.Create(outPath)) encoder.Save(stream);

        Native.DestroyIcon(info.hIcon);

        Console.WriteLine($"已保存 {outPath}");
        return 0;
    }

    /// <summary>
    /// 抓桌面。不带矩形就抓整屏。
    /// 进程内的字段能打印，但「任务栏上到底画了什么」只有真的看屏幕才知道——
    /// 图标设置成功与图标显示正确是两件事，中间隔着系统缩放与缓存。
    /// </summary>
    private static int ScreenShot(string outPath, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            x = 0;
            y = 0;
            width = Native.GetSystemMetrics(0);
            height = Native.GetSystemMetrics(1);
        }

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
        IntPtr bitmap = Native.CreateCompatibleBitmap(screenDc, width, height);
        IntPtr previous = Native.SelectObject(memoryDc, bitmap);

        bool ok = Native.BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, Native.SrcCopy);

        Native.SelectObject(memoryDc, previous);

        if (!ok)
        {
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            Console.WriteLine("截屏失败：BitBlt 返回 false");
            return 2;
        }

        System.Windows.Media.Imaging.BitmapSource source =
            System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));

        using (FileStream stream = File.Create(outPath)) encoder.Save(stream);

        Native.DeleteObject(bitmap);
        Native.DeleteDC(memoryDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);

        Console.WriteLine($"screen={width}x{height} origin=({x},{y})");
        Console.WriteLine($"已保存 {outPath}");
        return 0;
    }

    /// <summary>
    /// 从运行中的进程主窗口取图标，回答的是「任务栏实际会画哪一张」。
    /// 三个来源分开报：
    ///   · <c>WM_GETICON</c> 的 ICON_BIG / ICON_SMALL —— WPF 的 <c>Window.Icon</c> 就落在这里，
    ///     任务栏按钮取 BIG，标题栏取 SMALL；
    ///   · <c>GetClassLongPtr(GCLP_HICON)</c> —— 窗口**类**上登记的图标，窗口自己没设时才轮到它。
    /// 全为 0 说明窗口没有图标，系统会画默认那张空白窗口图。
    /// </summary>
    private static int ProbeWindowIcon(int pid, string outPath)
    {
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
        process.Refresh();

        IntPtr hwnd = process.MainWindowHandle;
        Console.WriteLine($"window.pid={pid}");
        Console.WriteLine($"window.title={process.MainWindowTitle}");

        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("window.hwnd=0 —— 主窗口还没建好，或进程已退出");
            return 2;
        }

        Console.WriteLine($"window.hwnd=0x{hwnd.ToInt64():X}");

        IntPtr big = Native.SendMessage(hwnd, Native.WmGetIcon, new IntPtr(1), IntPtr.Zero);
        IntPtr small = Native.SendMessage(hwnd, Native.WmGetIcon, IntPtr.Zero, IntPtr.Zero);
        IntPtr small2 = Native.SendMessage(hwnd, Native.WmGetIcon, new IntPtr(2), IntPtr.Zero);
        IntPtr cls = Native.GetClassLongPtr(hwnd, Native.GclpHicon);

        Console.WriteLine($"icon.big=0x{big.ToInt64():X}    (任务栏按钮用这个)");
        Console.WriteLine($"icon.small=0x{small.ToInt64():X}  (标题栏用这个)");
        Console.WriteLine($"icon.small2=0x{small2.ToInt64():X}");
        Console.WriteLine($"icon.class=0x{cls.ToInt64():X}  (窗口类登记的)");

        IntPtr chosen = big != IntPtr.Zero ? big
            : small != IntPtr.Zero ? small
            : small2 != IntPtr.Zero ? small2
            : cls;

        if (chosen == IntPtr.Zero)
        {
            Console.WriteLine("icon.result=窗口没有设图标，系统会画默认那张空白窗口图");
            return 0;
        }

        System.Windows.Media.Imaging.BitmapSource source =
            System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                chosen, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        Console.WriteLine($"icon.size={source.PixelWidth}x{source.PixelHeight}");

        // 采几个点看底色：蓝底反白版四周应接近 #245EEB，透明底版四周 alpha 为 0
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);

        (int X, int Y)[] probes =
        [
            (2, 2),
            (source.PixelWidth / 2, source.PixelHeight / 2),
            (source.PixelWidth - 3, source.PixelHeight - 3),
        ];

        foreach ((int x, int y) in probes)
        {
            int i = ((y * source.PixelWidth) + x) * 4;
            Console.WriteLine(
                $"icon.pixel({x},{y})=#{pixels[i + 2]:X2}{pixels[i + 1]:X2}{pixels[i]:X2} a={pixels[i + 3]}");
        }

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));

        using (FileStream stream = File.Create(outPath)) encoder.Save(stream);

        Console.WriteLine($"已保存 {outPath}");
        return 0;
    }

    private static int BinProbe(string? target)
    {
        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            Console.WriteLine("bin.probe=找不到 Shell.Application");
            return 3;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic? bin = shell.NameSpace(10);
        if (bin is null)
        {
            Console.WriteLine("bin.probe=Namespace(10) 为 null");
            return 3;
        }

        string? wanted = target is null ? null : Path.GetFullPath(target);

        foreach (object item in bin.Items())
        {
            dynamic entry = item;
            string name = entry.Name as string ?? string.Empty;
            string location = bin.GetDetailsOf(item, 1) as string ?? string.Empty;
            string full = location.Length == 0 ? name : Path.Join(location, name);

            var verbs = new List<string>();
            foreach (object verb in entry.Verbs())
            {
                var each = (dynamic)verb;
                verbs.Add(each.Name as string ?? "?");
            }

            Console.WriteLine($"bin.item={full}");
            Console.WriteLine($"bin.verbs=[{string.Join(" | ", verbs)}]");

            if (wanted is null || !string.Equals(full, wanted, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (object verb in entry.Verbs())
            {
                var each = (dynamic)verb;
                string verbName = each.Name as string ?? "?";
                each.DoIt();
                Thread.Sleep(600);
                bool back = File.Exists(full) || Directory.Exists(full);
                Console.WriteLine($"bin.try={verbName} back={back}");
                if (back) break;
            }
        }

        return 0;
    }

    /// <summary>
    /// 左键点导航栏某个分区标题。带管理动作的分区（收藏、工作区）应当当场弹出菜单。
    /// 「工作区」那一段在侧栏最底部，窗口不够高时只露半行——只靠右键等于没有入口，
    /// 所以这条链路必须真的走一遍左键。
    /// </summary>
    private static async Task ExerciseNavMenu(MainWindow window, string title)
    {
        if (window.FindName("QuickAccessList") is not ListBox list)
        {
            Console.WriteLine("navmenu.list=找不到 QuickAccessList");
            return;
        }

        list.UpdateLayout();

        TextBlock? label = FindSectionLabel(list, title);

        if (label is null)
        {
            Console.WriteLine($"navmenu.label=找不到「{title}」");
            Console.Out.Flush();
            return;
        }

        // 先写出去再点：弹出菜单在某些会话下会卡住消息循环，事后打印就来不及了
        Console.WriteLine($"navmenu.label={title}");
        Console.Out.Flush();

        label.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        });

        await Task.Delay(300);

        ContextMenu? menu = label.ContextMenu;
        bool open = menu?.IsOpen == true;

        Console.WriteLine($"navmenu.opened={open}");
        Console.WriteLine($"navmenu.items=[{AllMenuItems(menu)}]");
        Console.Out.Flush();

        // 不收起来会挡住后面要拍的整窗，也可能让进程退不干净
        if (open && menu is not null) menu.IsOpen = false;
    }

    /// <summary>
    /// 菜单里每一项的名字与可见性。只列可见项看不出"本该藏起来的藏没藏"，
    /// 而这个分区的菜单是靠 IsBookmarks / IsWorkspaces 互斥的，藏错了就等于点错动作。
    /// </summary>
    private static string AllMenuItems(ContextMenu? menu)
    {
        if (menu is null) return string.Empty;

        return string.Join(" · ", menu.Items
            .OfType<MenuItem>()
            .Select(item =>
            {
                string header = (item.Header as string ?? "?").Replace("_", string.Empty, StringComparison.Ordinal);
                return item.Visibility == Visibility.Visible ? header : $"{header}(藏)";
            }));
    }

    /// <summary>
    /// 走一遍「位置」的齿轮菜单（FR-1.2.9）。
    ///
    /// 为什么不像别的用例那样只截图：菜单是独立的弹出 HWND，不进主窗口的视觉树，
    /// RenderTargetBitmap 拍不到。所以这里验的是"看不见但会坏"的部分——
    /// 齿轮在不在、菜单生成了没有、勾选态有没有按设置算对、"恢复全部"该不该可用，
    /// 以及取消勾选之后那一行是不是真的从列表里消失了。
    /// </summary>
    private static void ExerciseNavPlaces(Window window, bool openMenu, List<string> toggleTags)
    {
        Button? button = FindPlacesButton(window);
        if (button is null)
        {
            Console.WriteLine("PLACES 未找到齿轮按钮");
            return;
        }

        Console.WriteLine($"PLACES 齿轮 可见={button.Visibility} 宽={button.ActualWidth:0} 高={button.ActualHeight:0}");

        if (openMenu)
        {
            // 走真处理器：它负责 PlacementTarget 与 IsOpen，等价于用户点了一下
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }

        ContextMenu? menu = button.ContextMenu;
        if (menu is null)
        {
            Console.WriteLine("PLACES 没有挂菜单");
            return;
        }

        Console.WriteLine($"PLACES 菜单 已打开={menu.IsOpen}");
        foreach (object entry in menu.Items)
        {
            if (entry is not MenuItem item) continue;

            Console.WriteLine(
                $"PLACES 项 tag={item.Tag ?? "(恢复全部)"} 勾选={item.IsChecked} 可用={item.IsEnabled} 标题={item.Header}");
        }

        foreach (string tag in toggleTags)
        {
            MenuItem? item = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == tag);
            if (item is null)
            {
                Console.WriteLine($"PLACES 未找到 {tag}");
                continue;
            }

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Console.WriteLine($"PLACES 已切换 {tag}");
        }

        if (menu.IsOpen) menu.IsOpen = false;

        PrintNavPlaceRows(window);
    }

    /// <summary>打印「位置」分区当前列出的项，确认隐藏/恢复真的落到界面上。</summary>
    private static void PrintNavPlaceRows(Window window)
    {
        if (window.FindName("QuickAccessList") is not ListBox list)
        {
            Console.WriteLine("PLACES 找不到 QuickAccessList");
            return;
        }

        string[] names = [.. list.Items.OfType<NavItem>().Where(item => !item.IsHome).Select(item => item.Name)];
        Console.WriteLine($"PLACES 列表 {names.Length} 项：{string.Join(" / ", names)}");
    }

    /// <summary>在导航列表里找齿轮按钮。它在 NavSectionRow 的模板里，只能按名字在容器里翻。</summary>
    private static Button? FindPlacesButton(Window window)
    {
        if (window.FindName("QuickAccessList") is not ListBox list) return null;

        list.UpdateLayout();

        foreach (object item in list.Items)
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not DependencyObject container) continue;

            var pending = new Queue<DependencyObject>();
            pending.Enqueue(container);

            while (pending.Count > 0)
            {
                DependencyObject node = pending.Dequeue();

                if (node is Button { Name: "PlacesConfigButton" } found
                    && found.Visibility == Visibility.Visible)
                {
                    return found;
                }

                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++) pending.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        return null;
    }

    private static TextBlock? FindSectionLabel(DependencyObject root, string title)
    {
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            DependencyObject node = pending.Dequeue();

            if (node is TextBlock text
                && text.DataContext is NavSectionRow row
                && string.Equals(row.Title, title, StringComparison.Ordinal))
            {
                return text;
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) pending.Enqueue(VisualTreeHelper.GetChild(node, i));
        }

        return null;
    }

    /// <summary>
    /// 用 IShellLink 建一个 .lnk。PowerShell 那条 WScript.Shell 的路被安全策略挡着，
    /// 这里直接走 COM 接口，参数可控。
    /// </summary>
    private static int MakeShortcut(string target, string linkPath)
    {
        if (!File.Exists(target))
        {
            Console.WriteLine($"目标不存在：{target}");
            return 1;
        }

        string? directory = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? string.Empty);
        link.SetDescription("简览");
        link.SetIconLocation(target, 0);

        ((IPersistFile)link).Save(linkPath, true);

        Console.WriteLine($"已创建 {linkPath}");
        Console.WriteLine($"target={target}");
        Console.WriteLine($"exists={File.Exists(linkPath)}");
        Console.WriteLine($"iconLocation={target},0");
        return 0;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int capacity, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    private static class Native
    {
        public delegate bool EnumChildProc(IntPtr hwnd, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowDC(IntPtr hwnd);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(
            IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, uint operation);

        public const uint SrcCopy = 0x00CC_0020;

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr dc);

        /// <summary>WM_GETICON。任务栏按钮取的是 ICON_BIG(1)，标题栏取 ICON_SMALL(0)。</summary>
        public const int WmGetIcon = 0x007F;

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        // GetClassLongPtr 只有 64 位 user32 导出；32 位进程必须走 GetClassLong
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtrNative(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
        private static extern uint GetClassLongNative(IntPtr hwnd, int index);

        public static IntPtr GetClassLongPtr(IntPtr hwnd, int index)
            => IntPtr.Size == 8 ? GetClassLongPtrNative(hwnd, index) : new IntPtr(GetClassLongNative(hwnd, index));

        /// <summary>GCLP_HICON：窗口**类**上登记的图标。窗口自己没设图标时，任务栏就用这个。</summary>
        public const int GclpHicon = -14;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ShellFileInfo
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        public const uint ShgfiIcon = 0x0000_0100;
        public const uint ShgfiLargeIcon = 0x0000_0000;
        public const uint ShgfiSmallIcon = 0x0000_0001;
        public const uint ShgfiSysIconIndex = 0x0000_4000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(
            string path, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);

        /// <summary>GWL_EXSTYLE。判断 WS_EX_APPWINDOW / WS_EX_TOOLWINDOW 用。</summary>
        public const int GwlExStyle = -20;

        public const long WsExToolWindow = 0x0000_0080;
        public const long WsExAppWindow = 0x0004_0000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtrNative64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLongNative32(IntPtr hwnd, int index);

        public static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
            => IntPtr.Size == 8
                ? GetWindowLongPtrNative64(hwnd, index)
                : new IntPtr(GetWindowLongNative32(hwnd, index));
    }
}

