using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Lingpane.App.Diagnostics;

namespace Lingpane.App.Shell;

/// <summary>
/// 系统 Shell 上下文菜单（FR-6.9）。只从两个显式入口调用：右键菜单里的「更多选项…」
/// 与 <c>Shift+右键</c>——日常右键仍走自建精简菜单。
///
/// 为什么不默认用系统菜单：<c>QueryContextMenu</c> 会把所有注册过的 Shell 扩展都实例化一遍。
/// 实机上 200–400ms 的首响还算好的，坏情况是某个扩展（压缩、网盘、杀软）在 UI 线程上挂住，
/// 于是右键之后整个窗口卡几秒。这个调用无法在我们代码里超时兜底——COM 调用在 UI 线程上不可中断。
/// 用户主动要"打开方式/发送到/七压解压"时，等这一下是可接受的；日常复制粘贴不该陪着等。
///
/// 落地时最容易做错的五条，都是实测踩出来的：
///   1) 只 Hook <c>WM_INITMENUPOPUP</c> 会得到"一级菜单正常、二级菜单一片空白"。
///      <c>WM_DRAWITEM</c> / <c>WM_MEASUREITEM</c> 三条缺一不可；
///   2) 坐标为屏幕**物理像素**。<c>PointToScreen</c> 返回的已经是物理像素，不要再乘缩放比；
///   3) 多选必须传 PIDL **数组**。只传首项时菜单能弹出来，但"发送到""压缩"只对第一个文件生效，
///      而且不报错——这是最难发现的一种错；
///   4) 不能在 WPF 自建菜单的点击回调里同步弹系统菜单。那个时刻自建菜单还没关闭，
///      它关窗时收回鼠标捕获，会把刚弹出来的系统菜单一起收掉。必须推迟
///      （<c>PaneView.QueueShellContextMenu</c>）；
///   5) 取 PIDL 末级元素不能用 <c>shlwapi!ILFindLastID</c>——它在 SDK 头文件里是内联函数，
///      动态库里没有同名导出，P/Invoke 会抛 <c>EntryPointNotFoundException</c>。
///      改走 <see cref="ShellInterop.IlFindLastId"/> 手动遍历（PIDL 每项前两字节是自身长度）。
/// </summary>
internal static class ShellContextMenu
{
    private const int SW_SHOWNORMAL = 1;

    // 菜单打开期间 Shell 会在 owner 窗口的嵌套消息循环里发这三条消息，
    // 由钩子转给扩展自己处理。字段是静态的：钩子签名拿不到闭包状态。
    private static ShellInterop.IContextMenu2? _handler2;
    private static ShellInterop.IContextMenu3? _handler3;

    /// <summary>
    /// 弹出系统菜单并执行用户选中的项。返回 false 表示"没能弹出来"，
    /// 调用方应降级（自建菜单或状态栏提示），不要静默吞掉。
    /// </summary>
    /// <param name="owner">主窗口句柄。既是菜单的 owner，也是消息钩子的挂载点。</param>
    /// <param name="paths">同一目录下的若干项；跨目录的项由 Shell 各自处理会错乱，调用方保证同父。</param>
    /// <param name="screenX">屏幕物理像素。</param>
    /// <param name="screenY">屏幕物理像素。</param>
    /// <param name="extendedVerbs">true 时用 <c>CMF_EXTENDEDVERBS</c>（Shift+右键的"显示更多选项"语义）。</param>
    public static bool TryShow(
        IntPtr owner, IReadOnlyList<string> paths, int screenX, int screenY, bool extendedVerbs)
    {
        if (owner == IntPtr.Zero || paths.Count == 0) return false;

        var pidls = new List<IntPtr>(paths.Count);
        IntPtr parentPtr = IntPtr.Zero;
        IntPtr menu = IntPtr.Zero;
        object? parentFolder = null;
        object? contextMenuObject = null;
        HwndSource? source = null;

        try
        {
            // ── 1. 路径 → 绝对 PIDL ────────────────────────────────────────
            foreach (string path in paths)
            {
                if (ShellInterop.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) < 0
                    || pidl == IntPtr.Zero)
                {
                    AppLog.Warn("shell.menu", "E7003", $"SHParseDisplayName 失败：{path}");  // i18n-ignore：只进 app.log
                    return false;
                }

                pidls.Add(pidl);
            }

            // ── 2. 父目录的 IShellFolder ──────────────────────────────────
            Guid folderIid = ShellInterop.IID_IShellFolder;
            int hr = ShellInterop.SHBindToParent(pidls[0], ref folderIid, out parentPtr, out _);
            if (hr < 0 || parentPtr == IntPtr.Zero)
            {
                AppLog.Warn("shell.menu", "E7003", $"SHBindToParent 失败 hr=0x{hr:X8}（{paths[0]}）");  // i18n-ignore
                return false;
            }

            var parent = (ShellInterop.IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
            parentFolder = parent;
            Marshal.Release(parentPtr);
            parentPtr = IntPtr.Zero;

            // ── 3. 取 IContextMenu（多选用 PIDL 数组）─────────────────────
            // 相对 PIDL 由绝对 PIDL 取末级元素得到；这里不能自己拼子项路径，
            // Shell 认的是 PIDL 而不是字符串。
            var relative = new IntPtr[pidls.Count];
            for (int i = 0; i < pidls.Count; i++) relative[i] = ShellInterop.IlFindLastId(pidls[i]);

            Guid menuIid = ShellInterop.IID_IContextMenu;
            hr = parent.GetUIObjectOf(owner, (uint)relative.Length, relative, ref menuIid,
                IntPtr.Zero, out IntPtr menuPtr);

            if (hr < 0 || menuPtr == IntPtr.Zero)
            {
                AppLog.Warn("shell.menu", "E7003", $"GetUIObjectOf 失败 hr=0x{hr:X8}");  // i18n-ignore
                return false;
            }

            var contextMenu = (ShellInterop.IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            contextMenuObject = contextMenu;
            Marshal.Release(menuPtr);

            _handler2 = contextMenu as ShellInterop.IContextMenu2;
            _handler3 = contextMenu as ShellInterop.IContextMenu3;

            // ── 4. 让扩展往菜单里填项 ────────────────────────────────────
            menu = ShellInterop.CreatePopupMenu();
            if (menu == IntPtr.Zero) return false;

            uint flags = extendedVerbs
                ? ShellInterop.CMF_NORMAL | ShellInterop.CMF_EXTENDEDVERBS
                : ShellInterop.CMF_NORMAL;

            hr = contextMenu.QueryContextMenu(
                menu, 0, ShellInterop.IDCMD_FIRST, ShellInterop.IDCMD_LAST, flags);

            if (hr < 0)
            {
                AppLog.Warn("shell.menu", "E7003", $"QueryContextMenu 失败 hr=0x{hr:X8}");  // i18n-ignore
                return false;
            }

            // ── 5. 弹菜单（阻塞）+ 消息转发 ──────────────────────────────
            source = HwndSource.FromHwnd(owner);
            source?.AddHook(MenuMessageHook);

            // 菜单必须由前台窗口弹出，否则它虽然画出来了却收不到键盘和点击，
            // 第一次输入就被关掉——用户看到的是"菜单闪了一下"。调用方已把这一步
            // 推迟到自建菜单关闭之后（见 PaneView.QueueShellContextMenu），
            // 此刻前台窗口可能正好是空的那一下，这里再兜一次。
            if (ShellInterop.GetForegroundWindow() != owner)
            {
                ShellInterop.SetForegroundWindow(owner);
            }

            int command;
            try
            {
                command = ShellInterop.TrackPopupMenuEx(
                    menu,
                    ShellInterop.TPM_RETURNCMD | ShellInterop.TPM_LEFTALIGN |
                    ShellInterop.TPM_TOPALIGN | ShellInterop.TPM_RIGHTBUTTON,
                    screenX, screenY, owner, IntPtr.Zero);
            }
            finally
            {
                source?.RemoveHook(MenuMessageHook);
            }

            // 0 = 用户点了别处 / 按了 Esc，不是错误
            if (command <= 0) return true;

            // ── 6. 执行选中的项 ──────────────────────────────────────────
            var info = new ShellInterop.CMINVOKECOMMANDINFO
            {
                cbSize = Marshal.SizeOf<ShellInterop.CMINVOKECOMMANDINFO>(),
                fMask = 0,
                hwnd = owner,
                // MAKEINTRESOURCE 语义：低位直接放命令偏移，不是字符串指针
                lpVerb = command - ShellInterop.IDCMD_FIRST,
                nShow = SW_SHOWNORMAL,
            };

            hr = contextMenu.InvokeCommand(ref info);
            if (hr < 0)
            {
                AppLog.Warn("shell.menu", "E7004", $"InvokeCommand 失败 hr=0x{hr:X8} id={command}");  // i18n-ignore
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // 系统菜单不允许成为"右键没反应"的原因：任何异常都退回自建菜单。
            // 这里刻意不按异常类型过滤——第一版只接 COM / IO 那几种，结果
            // IlFindLastId 抛出的 EntryPointNotFoundException 直接漏出去，
            // 菜单不弹、不降级、日志里也没有一行，排查时只能靠猜。
            // Shell 的调用面太杂（扩展是第三方代码），宁可宽一点。
            AppLog.Warn("shell.menu", "E7001", $"{ex.GetType().Name}: {ex.Message}");  // i18n-ignore
            return false;
        }
        finally
        {
            _handler2 = null;
            _handler3 = null;

            if (menu != IntPtr.Zero) ShellInterop.DestroyMenu(menu);
            if (source is not null) source.RemoveHook(MenuMessageHook);

            if (contextMenuObject is not null) ReleaseComObject(contextMenuObject);
            if (parentFolder is not null) ReleaseComObject(parentFolder);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);

            foreach (IntPtr pidl in pidls)
            {
                if (pidl != IntPtr.Zero) ShellInterop.CoTaskMemFree(pidl);
            }
        }
    }

    /// <summary>
    /// 把菜单消息转给扩展。
    ///
    /// 顺序是 IContextMenu3 → IContextMenu2 → 交给默认处理：
    /// 3 的 <c>HandleMenuMsg2</c> 能回填 result，2 的 <c>HandleMenuMsg</c> 只能表达"处理了/没处理"。
    /// 扩展返回 S_OK 才 <c>handled = true</c>，否则 WPF 还要继续画自己的菜单背景。
    /// </summary>
    private static IntPtr MenuMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (ShellInterop.WM_INITMENUPOPUP or ShellInterop.WM_DRAWITEM or ShellInterop.WM_MEASUREITEM))
        {
            return IntPtr.Zero;
        }

        try
        {
            if (_handler3 is not null)
            {
                int hr = _handler3.HandleMenuMsg2((uint)msg, wParam, lParam, out IntPtr result);
                if (ShellInterop.Succeeded(hr))
                {
                    handled = true;
                    return result;
                }
            }

            if (_handler2 is not null)
            {
                int hr = _handler2.HandleMenuMsg((uint)msg, wParam, lParam);
                if (ShellInterop.Succeeded(hr))
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            AppLog.Warn("shell.menu", "E7002", $"HandleMenuMsg(0x{msg:X4}) 异常：{ex.Message}");  // i18n-ignore
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 释放 COM 对象。用 <c>ReleaseComObject</c> 而不是 <c>FinalReleaseComObject</c>：
    /// 后者会把别的持有者的引用一起掐掉，而这些接口可能被 Shell 自己在别处持有。
    /// 失败一律吞掉——清理阶段抛异常会掩盖真正的错误。
    /// </summary>
    private static void ReleaseComObject(object target)
    {
        try
        {
            if (Marshal.IsComObject(target)) Marshal.ReleaseComObject(target);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // 已经释放过或不是 RCW，忽略
        }
    }
}
