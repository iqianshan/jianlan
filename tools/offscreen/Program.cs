using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
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
        string outPath = argv.Length > 0
            ? Path.GetFullPath(argv[0])
            : Path.Combine(Environment.CurrentDirectory, "shot.png");

        int waitMs = argv.Length > 1 && int.TryParse(argv[1], out int parsed) ? parsed : 4000;

        string? tagName = null;
        int activateIndex = -1;
        int[] selectIndexes = [];
        bool enablePreview = false;
        bool disablePreview = false;

        for (int i = 2; i < argv.Length; i++)
        {
            switch (argv[i])
            {
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

                    // 300ms 防抖 + 读盘/解码，1.5s 足够拍到结果
                    await Task.Delay(1500);
                }

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
}

