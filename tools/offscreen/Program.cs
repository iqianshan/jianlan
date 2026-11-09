using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lingpane.App;
using Lingpane.App.ViewModels;
using Lingpane.App.Views;
using Lingpane.Core.Tagging;

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
/// 数据目录由 exe 旁边的 portable.flag + data\ 决定（便携模式），演示数据自己放进去。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] argv)
    {
        // 输出被 Python 脚本按 UTF-8 读，不设这个会得到一屏乱码
        Console.OutputEncoding = System.Text.Encoding.UTF8;

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

                if (surfacePath is not null) SaveSurface(window, surfacePath);

                if (printState) PrintState(window);

                if (dumpTree) DumpTree(window);

                Save(window, outPath);
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
            Console.Error.WriteLine("没找到处理器窗口（LingpanePreviewSurface）");
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

            if (!string.Equals(name.ToString(), "LingpanePreviewSurface", StringComparison.Ordinal)) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
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
    }
}

