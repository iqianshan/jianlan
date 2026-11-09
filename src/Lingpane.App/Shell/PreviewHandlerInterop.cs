using System.Runtime.InteropServices;

namespace Lingpane.App.Shell;

/// <summary>
/// 预览处理器的 COM 契约（FR-9.1.3）。
///
/// 这一层只放定义，不放逻辑。接口的 vtable 顺序必须与 Windows SDK 头文件
/// （<c>shobjidl_core.h</c> / <c>msap.h</c>）严格一致——顺序错了不会编译报错，
/// 只会让调用落到隔壁方法上，表现为"什么都没发生"或直接访问冲突。
/// </summary>
internal static class PreviewHandlerInterop
{
    /// <summary>扩展名 → 预览处理器 CLSID 的约定键（<c>{8895b1c6-b41f-4c1c-a562-0d564250836f}</c>）。</summary>
    public const string HandlerKeyName = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    public static readonly Guid IidPreviewHandler = new("8895b1c6-b41f-4c1c-a562-0d564250836f");
    public static readonly Guid IidInitializeWithFile = new("b7d14566-0509-4cce-a71f-0a554233bd9b");
    public static readonly Guid IidObjectWithSite = new("fc4801a3-2ba9-11cf-a229-00aa003d7352");
    public static readonly Guid IidPreviewHandlerFrame = new("fec87aaf-35f9-447a-adb7-20234491401a");

    public const uint StgmRead = 0x0000_0000;

    /// <summary>
    /// 按 vtable 槽位调用 <see cref="IidObjectWithSite"/> 的 <c>SetSite</c>。
    ///
    /// 不声明 <c>[ComImport]</c> 的 IObjectWithSite 是有原因的：本程序集里还要实现
    /// 这个接口的**回调**版本（<see cref="IPreviewHandlerFrameNative"/> 所在的类型），
    /// 同一个程序集里出现两个同 GUID 的接口，CLR 在建立 CCW 时会按 GUID 缓存而混淆。
    /// 因此调用侧走裸 vtable，实现侧才用带 GUID 的托管接口。
    /// </summary>
    public static int SetSite(IntPtr handlerUnknown, object site)
    {
        Guid iid = IidObjectWithSite;
        int hr = Marshal.QueryInterface(handlerUnknown, ref iid, out IntPtr siteTarget);
        if (hr != 0) return hr;

        IntPtr siteUnknown = IntPtr.Zero;

        try
        {
            // IObjectWithSite 的槽位：0 QueryInterface / 1 AddRef / 2 Release / 3 SetSite
            IntPtr vtable = Marshal.ReadIntPtr(siteTarget);
            IntPtr entry = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);

            var setSite = Marshal.GetDelegateForFunctionPointer<SetSiteDelegate>(entry);

            siteUnknown = Marshal.GetIUnknownForObject(site);
            return setSite(siteTarget, siteUnknown);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // 拿不到代理（跨进程场景下确实可能）：不设站点也能预览，降级即可
            return unchecked((int)0x80004005);
        }
        finally
        {
            if (siteUnknown != IntPtr.Zero) Marshal.Release(siteUnknown);
            Marshal.Release(siteTarget);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetSiteDelegate(IntPtr self, IntPtr site);
}

/// <summary>
/// Win32 的 <c>RECT</c>。用自定义结构而不是 <c>System.Windows.Rect</c>：
/// 后者是浮点的，且字段顺序与 Win32 不一致。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>Win32 的 <c>MSG</c>。<c>lPrivate</c> 是 Win8 之后加的，漏掉会让结构小 4 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeMsg
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int X;
    public int Y;
    public uint Private;
}

/// <summary>
/// <c>IPreviewHandler</c>。所有方法都 <c>PreserveSig</c>：处理器大量用
/// <c>S_FALSE</c> / <c>E_PENDING</c> 表达"可以，但不是现在"，用异常式签名会把它们全抹平。
/// </summary>
[ComImport]
[Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    [PreserveSig]
    int SetWindow(IntPtr hwnd, ref NativeRect rect);

    [PreserveSig]
    int SetRect(ref NativeRect rect);

    [PreserveSig]
    int DoPreview();

    [PreserveSig]
    int Unload();

    [PreserveSig]
    int SetFocus();

    [PreserveSig]
    int QueryFocus(out IntPtr phwnd);

    /// <summary>返回 <c>S_FALSE</c> 表示"这个键我不要"，交回宿主处理。</summary>
    [PreserveSig]
    int TranslateAccelerator(ref NativeMsg msg);
}

/// <summary><c>IInitializeWithFile</c>。三个初始化接口里兼容性最好的一个。</summary>
[ComImport]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    [PreserveSig]
    int Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint mode);
}

/// <summary>
/// 宿主站点。处理器通过它问"你的窗口在哪、这一块的可视区域多大、哪些键要转给你"。
///
/// 用托管接口实现而不是裸 vtable：CLR 生成的 CCW 会自动处理 QueryInterface，
/// 而这三个接口在 Windows 里都有 proxy/stub 注册，跨进程（32 位处理器跑在
/// prevhost.exe 里）也能正常往返。
/// </summary>
[ComVisible(true)]
[Guid("fec87aaf-35f9-447a-adb7-20234491401a")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerFrameNative
{
    /// <summary>
    /// <paramref name="accelerators"/> 为 <see cref="IntPtr.Zero"/> 表示宿主不拦截任何键。
    /// 不返回加速键表是保守选择：处理器自己抢键会让我们窗口里的方向键、Ctrl+C 失灵。
    /// </summary>
    [PreserveSig]
    int GetWindowContext(
        out IntPtr accelerators,
        out NativeRect view,
        out NativeRect parent,
        out IntPtr parentWindow);

    /// <summary>恒返回 <c>S_FALSE</c>：键留在本程序，预览窗格不参与键盘导航。</summary>
    [PreserveSig]
    int TranslateAccelerator(ref NativeMsg msg);
}

/// <summary><c>IOleWindow</c>。部分处理器在 <c>SetSite</c> 之后会 QI 它来定位父窗口。</summary>
[ComVisible(true)]
[Guid("00000114-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleWindowNative
{
    [PreserveSig]
    int GetWindow(out IntPtr hwnd);

    [PreserveSig]
    int ContextSensitiveHelp(int enterMode);
}
