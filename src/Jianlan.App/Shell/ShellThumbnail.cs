using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Jianlan.App.Shell;

/// <summary>
/// 系统缩略图（FR-9.2.1 的降级路径，同时覆盖 FR-6.6 的能力底座）。
///
/// 为什么不自己解析 PDF / Office / 视频：那些格式的首帧要各自的解码器，
/// 自己实现等于把一套文档引擎塞进包里。Shell 的 <c>IShellItemImageFactory</c>
/// 背后就是系统里已注册的缩略图提供者——PDF 浏览器、Office、媒体组件都在其中，
/// 取到的正是"PDF 首页""视频首帧"这类最有信息量的画面。
///
/// 代价是它不可控：提供者质量参差不齐，可能挂起。因此固定在一个独立 STA 线程上做，
/// 由调用方配 2s 超时（FR-9.2.3）。超时后线程自生自灭，我们不再等它。
/// </summary>
internal static class ShellThumbnail
{
    /// <summary>只接受真缩略图。没有提供者的类型直接失败，由调用方降级为图标 + 基本信息。</summary>
    private const int SIIGBF_THUMBNAILONLY = 0x0000_0008;

    /// <summary>允许返回比请求尺寸更大的图。请求 256 时系统可能给 512 的原图，清晰度更好。</summary>
    private const int SIIGBF_BIGGERSIZEOK = 0x0000_0001;

    /// <summary>系统缩略图缓存上限。16 张 256×256 约 4MB，比重新解析一遍便宜得多。</summary>
    private const int CacheCapacity = 16;

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();
    private static readonly object Gate = new();

    /// <summary>
    /// 取缩略图。失败（没有提供者、文件不可读、超时）一律返回 <c>null</c>，不抛异常——
    /// 预览是"看一眼"的功能，任何一步失败都不该让选中动作报错。
    ///
    /// 方法本身不阻塞：内部起 STA 线程，调用方用 <see cref="Task.WhenAny(Task, Task)"/> 配超时。
    /// </summary>
    public static Task<BitmapSource?> GetAsync(string path, int size)
    {
        string key = path + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out BitmapSource? cached)) return Task.FromResult(cached);
        }

        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            BitmapSource? image = null;

            try
            {
                // 提供者可能直接崩掉整个进程；异常至少要能在这一层被吃掉
                CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
                image = Extract(path, size);
            }
            catch (Exception)
            {
                image = null;
            }
            finally
            {
                CoUninitialize();
            }

            Store(key, image);
            tcs.TrySetResult(image);
        })
        {
            IsBackground = true,
            Name = "Jianlan.Thumbnail",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    /// <summary>丢掉缓存。切换"显示缩略图"或文件被改写后调用；窗口关闭时也调一次。</summary>
    public static void ClearCache()
    {
        lock (Gate)
        {
            Cache.Clear();
            CacheOrder.Clear();
        }
    }

    private static void Store(string key, BitmapSource? image)
    {
        lock (Gate)
        {
            if (Cache.ContainsKey(key)) return;

            Cache[key] = image;
            CacheOrder.Enqueue(key);

            while (CacheOrder.Count > CacheCapacity && CacheOrder.Count > 0)
            {
                Cache.Remove(CacheOrder.Dequeue());
            }
        }
    }

    private static BitmapSource? Extract(string path, int size)
    {
        var factory = CreateFactory(path);
        if (factory is null) return null;

        try
        {
            int hr = factory.GetImage(
                new SIZE(size, size), SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                // 跨线程交给 UI 用，必须冻结；不冻结会在赋值那一刻抛"另一个线程拥有它"
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(factory);
        }
    }

    private static IShellItemImageFactory? CreateFactory(string path)
    {
        Guid iid = IID_IShellItemImageFactory;

        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object item);
            return item as IShellItemImageFactory;
        }
        catch (Exception)
        {
            // 文件刚被删、路径过长、提供者注册损坏都会走到这里
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SIZE
    {
        public SIZE(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
        }

        public readonly int cx;
        public readonly int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int CoInitApartmentThreaded = 0x2;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
