using System.Runtime.InteropServices;

namespace Jianlan.App.Shell;

/// <summary>
/// <c>IContextMenu</c> / <c>IContextMenu2</c> / <c>IContextMenu3</c> 与 <c>IShellFolder</c> 的最小互操作声明。
///
/// 全部手写 P/Invoke 与 COM 声明，不引第三方库（与 <see cref="ShellIconProvider"/> 一致）：
/// 这几个接口的 vtable 顺序是 Windows SDK 定的稳定契约，声明成本比引一个互操作包低，
/// 也避免"升级依赖导致右键菜单静默坏掉"这种最难查的问题。
///
/// **方法顺序就是 vtable 顺序，一行都不能调整。** 缺方法时要补占位而不是删——
/// 少一个 <c>SetNameOf</c>，<c>GetUIObjectOf</c> 就会调到别处，返回一个莫名 HRESULT。
/// </summary>
internal static class ShellInterop
{
    public const uint CMF_NORMAL = 0x0000_0000;
    public const uint CMF_EXTENDEDVERBS = 0x0000_0100;

    public const int IDCMD_FIRST = 1;
    public const int IDCMD_LAST = 0x7FFF;

    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_TOPALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    public const int WM_INITMENUPOPUP = 0x0117;
    public const int WM_DRAWITEM = 0x002B;
    public const int WM_MEASUREITEM = 0x002C;

    /// <summary>Shell 的"调用成功"判据是 <c>hr &gt;= 0</c>；用 <c>== 0</c> 会把 S_FALSE 当失败。</summary>
    public static bool Succeeded(int hr) => hr >= 0;

    public static Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    public struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid,
            IntPtr rgfReserved, out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);

        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFO pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved,
            [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder pszName, uint cchMax);
    }

    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu2
    {
        // 继承 IContextMenu 的三个方法，必须按 vtable 顺序完整重声明
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFO pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved,
            [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFO pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved,
            [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHBindToParent(
        IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);

    /// <summary>
    /// 取绝对 PIDL 的末级元素（等价于 C++ 的 <c>ILFindLastID</c>）。
    ///
    /// 不能 P/Invoke <c>shlwapi!ILFindLastID</c>：它在 SDK 头文件里是内联函数，
    /// 动态库里没有同名导出，调用会抛 <see cref="EntryPointNotFoundException"/>。
    /// PIDL 的结构很好走：每个 SHITEMID 前两个字节是自身长度（含这两个字节），
    /// 以长度 0 的项结尾，所以顺着长度跳到 0 就停在最后一项上。
    /// </summary>
    public static IntPtr IlFindLastId(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero) return IntPtr.Zero;

        IntPtr last = pidl;
        IntPtr current = pidl;
        while (true)
        {
            int cb = Marshal.ReadInt16(current);
            if (cb == 0) return last;

            last = current;
            current = (IntPtr)((long)current + cb);
        }
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(
        IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
}
