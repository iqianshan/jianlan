using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Lingpane.App.Shell;

/// <summary>
/// 把系统预览处理器（<c>IPreviewHandler</c>）嵌进界面（FR-9.1.3）。
///
/// 为什么值得引入这一层：没有它，PDF 只是"第一页的缩略图"、视频只是"首帧"，
/// 翻页与播放这两件事做不到。有了它，用户能在窗格里直接把 PDF 翻到第三页、
/// 把视频播起来——这正是需求里"可播放"三个字的全部含义。
///
/// 三个关键决定：
/// <list type="number">
/// <item><b>处理器跑在独立 STA 线程</b>。第三方处理器（搜狗 PDF、金山看图、WPS 压缩包）
///       都是别人写的 DLL，<c>DoPreview</c> 里做什么我们说了不算。放在 UI 线程上，
///       一个卡住的处理器就是整个窗口假死；放到独立线程，最坏情况是预览窗格一直空着，
///       文件列表照常能用。</item>
/// <item><b>不设加速键表</b>。站点回调 <c>GetWindowContext</c> 里返回空表，
///       处理器就不会抢走方向键和 Ctrl+C——预览窗格不该参与主窗口的键盘导航。</item>
/// <item><b>失败不抛异常，只发事件</b>。处理器没装、激活报错、渲染失败都是常态
///       （本机 <c>.docx</c> 就指向一个已不存在的 CLSID），退回缩略图即可。</item>
/// </list>
/// </summary>
internal sealed class PreviewHandlerHost : HwndHost
{
    private IntPtr _container;
    private HandlerSession? _session;

    /// <summary>处理器已经把内容画出来了。宿主据此收掉"正在准备"的提示。</summary>
    public event EventHandler? Ready;

    /// <summary>这一份文件预览失败，调用方应退回缩略图。</summary>
    public event EventHandler? Failed;

    /// <summary>宿主窗口是否已建好（<c>Open</c> 之前必须为 true）。</summary>
    public bool IsBuilt => _container != IntPtr.Zero;

    /// <summary>
    /// 让处理器接手这个文件。<paramref name="clsid"/> 来自 <see cref="PreviewHandlerRegistry"/>。
    /// 立即返回：激活与渲染都在后台线程上完成。
    /// </summary>
    public void Open(string path, Guid clsid)
    {
        // 同一个文件已经在渲染就不要再来一次：再开一次要重新拉起 prevhost.exe、
        // 重新解析文件，用户看到的是预览区闪一下白
        if (_session is { IsAlive: true } alive && alive.Matches(path, clsid)) return;

        Close();

        if (_container == IntPtr.Zero) return;

        _session = HandlerSession.Start(this, path, clsid, _container);
    }

    /// <summary>卸载处理器并释放 COM 对象（FR-9.2.6）。切走、关窗格、换标签都要调。</summary>
    public void Close(bool wait = false)
    {
        HandlerSession? session = _session;
        _session = null;
        session?.Dispose(wait);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // 容器窗口本身不画任何东西，只负责给处理器窗口当爹：
        // 尺寸与可见性由 HwndHost 帮我们摆，处理器窗口跟着容器的客户区走
        _container = Native.CreateWindowEx(
            0,
            "static",
            string.Empty,
            Native.WsChild | Native.WsVisible | Native.WsClipChildren | Native.WsClipSiblings,
            0, 0, 1, 1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // 先卸载再拆窗口：反过来的话处理器会在一个已经销毁的 HWND 上继续画。
        // 这里是唯一可以等一下的地方——窗口都要没了，等处理器把 COM 放干净比快更重要
        Close(wait: true);

        if (hwnd.Handle != IntPtr.Zero) Native.DestroyWindow(hwnd.Handle);

        _container = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);

        // HwndHost 挪的是容器窗口；处理器窗口是容器的子窗口，得自己跟一遍
        _session?.RequestResize();
    }

    private void RaiseReady()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RaiseReady));
            return;
        }

        Ready?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseFailed()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RaiseFailed));
            return;
        }

        Failed?.Invoke(this, EventArgs.Empty);
    }

    // ── 一个处理器会话 = 一个 STA 线程 ──────────────────────────────────────

    /// <summary>
    /// 处理器的生命周期全在这个类里，且全部发生在同一个 STA 线程上：
    /// COM 对象在哪个线程创建就得在哪个线程释放，跨线程释放会留下未封送的引用。
    /// </summary>
    private sealed class HandlerSession
    {
        private const uint WmResize = Native.WmApp + 1;
        private const uint WmUnload = Native.WmApp + 2;

        /// <summary>等处理器收工的时长。超过就不等了——它可能正卡在别人的渲染代码里。</summary>
        private const int JoinTimeoutMs = 1500;

        private readonly PreviewHandlerHost _owner;
        private readonly string _path;
        private readonly Guid _clsid;
        private readonly IntPtr _container;
        private readonly Thread _thread;

        private IntPtr _surface;
        private IPreviewHandler? _handler;
        private object? _com;

        private HandlerSession(PreviewHandlerHost owner, string path, Guid clsid, IntPtr container)
        {
            _owner = owner;
            _path = path;
            _clsid = clsid;
            _container = container;

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Lingpane.PreviewHandler",
            };

            // Apartment：处理器几乎都是 Apartment 模型，MTA 线程上 CoCreateInstance 会给
            // RPC_E_CHANGED_MODE；而且 STA 才允许它内部创建自己的窗口
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public static HandlerSession Start(PreviewHandlerHost owner, string path, Guid clsid, IntPtr container)
        {
            var session = new HandlerSession(owner, path, clsid, container);
            session._thread.Start();
            return session;
        }

        /// <summary>线程是否还活着。卡在处理器里的会话算"活着"，避免被误判为已结束而重复启动。</summary>
        public bool IsAlive => _thread.IsAlive;

        public bool Matches(string path, Guid clsid)
            => _clsid == clsid && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);

        public void RequestResize()
        {
            if (_surface == IntPtr.Zero) return;

            // 线程间只发消息，不直接调用：UI 线程上调用 SetRect 会跨线程碰 COM 对象
            Native.PostMessage(_surface, WmResize, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// 让处理器收工。
        ///
        /// <paramref name="wait"/> 默认 false 是有意的：这个方法在每次切换选中项时都会调，
        /// 若在这里等线程退出，UI 就会为每一个"处理器慢一点"的文件卡一下——
        /// 而用户此时正在连着按方向键。不等的代价只是旧窗口多存在几十毫秒，
        /// 它已经先被隐藏了，不会盖住新内容。
        /// </summary>
        public void Dispose(bool wait)
        {
            if (_surface != IntPtr.Zero)
            {
                // 先藏起来：线程可能还要在 DoPreview 里待一会儿，这段时间不能让它挡着新预览
                Native.ShowWindow(_surface, Native.SwHide);
                Native.PostMessage(_surface, WmUnload, IntPtr.Zero, IntPtr.Zero);
            }

            if (!wait) return;

            _thread.Join(JoinTimeoutMs);
        }

        private void Run()
        {
            try
            {
                Native.EnsureWindowClass();

                _surface = Native.CreateWindowEx(
                    0,
                    Native.SurfaceClassName,
                    string.Empty,
                    Native.WsChild | Native.WsVisible | Native.WsClipChildren | Native.WsClipSiblings,
                    0, 0, 1, 1,
                    _container,
                    IntPtr.Zero,
                    Native.ModuleHandle,
                    IntPtr.Zero);

                if (_surface == IntPtr.Zero)
                {
                    _owner.RaiseFailed();
                    return;
                }

                Native.Surfaces[_surface] = this;

                if (!Activate()) return;

                ApplyBounds();

                if (!Succeeded(_handler!.DoPreview()))
                {
                    _owner.RaiseFailed();
                    return;
                }

                _owner.RaiseReady();
                Pump();
            }
            catch (Exception)
            {
                // 处理器 DLL 内部抛什么我们控制不了；这里统一折成"预览失败"
                _owner.RaiseFailed();
            }
            finally
            {
                Teardown();
            }
        }

        private bool Activate()
        {
            Type? type = Type.GetTypeFromCLSID(_clsid, throwOnError: false);
            if (type is null)
            {
                _owner.RaiseFailed();
                return false;
            }

            _com = Activator.CreateInstance(type);
            if (_com is null)
            {
                _owner.RaiseFailed();
                return false;
            }

            _handler = _com as IPreviewHandler;

            // 三个初始化接口里只用 IInitializeWithFile：实测本机三类处理器都支持它，
            // 而 IInitializeWithItem 一律返回 E_NOINTERFACE（要 IShellItem，代价大得多）
            if (_handler is null || _com is not IInitializeWithFile initializer)
            {
                _owner.RaiseFailed();
                return false;
            }

            if (!Succeeded(initializer.Initialize(_path, PreviewHandlerInterop.StgmRead)))
            {
                _owner.RaiseFailed();
                return false;
            }

            // 站点给不上不算致命：多数处理器不依赖它也能画
            IntPtr unknown = Marshal.GetIUnknownForObject(_com);

            try
            {
                PreviewHandlerInterop.SetSite(unknown, new HandlerSite(_surface, _container));
            }
            finally
            {
                Marshal.Release(unknown);
            }

            Native.GetWindowRect(_surface, out NativeRect bounds);
            _handler.SetWindow(_surface, ref bounds);

            // 先给出真实尺寸再 SetRect：SetWindow 时还是 1×1，处理器会按 1×1 排版
            ApplyBounds();

            return true;
        }

        /// <summary>
        /// 把处理器窗口摆成容器客户区大小。
        ///
        /// 这一步不能省：子窗口创建时给的是 1×1 占位，容器再变多大它都不会自己跟。
        /// 漏了这一句的表现是处理器"什么都没画出来"——它确实画了，只是画在 1 像素里，
        /// PrintWindow 拿到一张 1×1 的图。
        /// </summary>
        private void ResizeSurface()
        {
            if (_surface == IntPtr.Zero) return;

            Native.GetClientRect(_container, out NativeRect client);
            if (client.Width <= 0 || client.Height <= 0) return;

            Native.SetWindowPos(
                _surface, IntPtr.Zero, 0, 0, client.Width, client.Height,
                Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
        }

        private void ApplyBounds()
        {
            ResizeSurface();

            if (_handler is null || _surface == IntPtr.Zero) return;

            Native.GetWindowRect(_surface, out NativeRect bounds);
            _handler.SetRect(ref bounds);
        }

        private void Pump()
        {
            while (Native.GetMessage(out NativeMsg message, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }

        /// <summary>窗口过程转进来的消息。只在处理器线程上被调用。</summary>
        public void OnMessage(uint message)
        {
            switch (message)
            {
                case WmResize:
                    ApplyBounds();
                    break;

                case WmUnload:
                    Native.PostQuitMessage(0);
                    break;
            }
        }

        private void Teardown()
        {
            if (_surface != IntPtr.Zero)
            {
                Native.Surfaces.TryRemove(_surface, out _);
            }

            try
            {
                _handler?.Unload();
            }
            catch (Exception)
            {
                // 已经崩过一次的处理器在这里再抛一次也没关系
            }

            if (_handler is not null && Marshal.IsComObject(_handler)) Marshal.ReleaseComObject(_handler);

            _handler = null;
            _com = null;

            if (_surface != IntPtr.Zero)
            {
                Native.DestroyWindow(_surface);
                _surface = IntPtr.Zero;
            }
        }

        private static bool Succeeded(int hr) => hr >= 0;
    }

    /// <summary>
    /// 给处理器用的站点。它只回答两件事：这一块可视区域在哪、哪些键要不要转给宿主。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class HandlerSite : IPreviewHandlerFrameNative, IOleWindowNative
    {
        private readonly IntPtr _surface;
        private readonly IntPtr _container;

        public HandlerSite(IntPtr surface, IntPtr container)
        {
            _surface = surface;
            _container = container;
        }

        public int GetWindowContext(out IntPtr accelerators, out NativeRect view, out NativeRect parent, out IntPtr parentWindow)
        {
            accelerators = IntPtr.Zero;
            parentWindow = _container;

            Native.GetWindowRect(_surface, out view);
            Native.GetWindowRect(_container, out parent);

            return 0;
        }

        /// <summary>恒 <c>S_FALSE</c>：键不转交，留在本程序处理。</summary>
        public int TranslateAccelerator(ref NativeMsg msg) => 1;

        public int GetWindow(out IntPtr hwnd)
        {
            hwnd = _surface;
            return 0;
        }

        public int ContextSensitiveHelp(int enterMode) => unchecked((int)0x80004001);
    }

    // ── Win32 ───────────────────────────────────────────────────────────────

    private static class Native
    {
        public const int WmApp = 0x8000;

        public const uint WsChild = 0x4000_0000;
        public const uint WsVisible = 0x1000_0000;
        public const uint WsClipChildren = 0x0200_0000;
        public const uint WsClipSiblings = 0x0400_0000;

        public const int SwHide = 0;

        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpNoOwnerZOrder = 0x0200;

        public const string SurfaceClassName = "LingpanePreviewSurface";

        /// <summary>窗口句柄 → 会话。窗口过程是静态的，只能靠句柄反查。</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, HandlerSession> Surfaces = new();

        private static readonly WndProcDelegate WndProc = WindowProc;
        private static IntPtr _classAtom;

        public static IntPtr ModuleHandle => GetModuleHandle(null);

        public static void EnsureWindowClass()
        {
            if (_classAtom != IntPtr.Zero) return;

            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Style = 0,
                WindowProc = Marshal.GetFunctionPointerForDelegate(WndProc),
                Instance = ModuleHandle,
                ClassName = SurfaceClassName,
            };

            // 注册失败多半是"已经注册过"（同一进程内第二次），当成成功继续走
            _classAtom = RegisterClassEx(ref windowClass);
        }

        private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmApp + 1 || message == WmApp + 2)
            {
                if (Surfaces.TryGetValue(hwnd, out HandlerSession? session)) session.OnMessage(message);

                return IntPtr.Zero;
            }

            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WindowClass
        {
            public uint Size;
            public uint Style;
            public IntPtr WindowProc;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string? MenuName;
            public string ClassName;
            public IntPtr SmallIcon;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        public static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        public static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
        public static extern IntPtr RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(
            IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMessageW")]
        public static extern int GetMessage(out NativeMsg message, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref NativeMsg message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        public static extern IntPtr DispatchMessage(ref NativeMsg message);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        public static extern IntPtr GetModuleHandle(string? module);
    }
}
