"""窗口驱动工具：按进程 ID 找到顶层窗口，调整尺寸或关闭。

用途：在无法使用 Add-Type / COM 的环境里做界面自动化验证
（例如验证"窗口尺寸记忆"是否真的写盘）。只用 ctypes 调 user32，无外部依赖。

用法：
    python window_drive.py <pid> size 1000 640    # 调整到 1000x640
    python window_drive.py <pid> close            # 发送 WM_CLOSE（等同点右上角）
    python window_drive.py <pid> info             # 打印当前矩形
"""

import ctypes
import os
import ctypes.wintypes as wt
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)
dwmapi = ctypes.WinDLL("dwmapi", use_last_error=True)

WM_CLOSE = 0x0010
DWMWA_EXTENDED_FRAME_BOUNDS = 9


def _enable_dpi_awareness() -> None:
    """声明本进程 DPI 感知。

    不声明就会被系统按 96 DPI 逻辑坐标欺骗：GetWindowRect 返回缩小后的矩形，
    而 PrintWindow 按物理像素绘制，于是截图右下角被裁掉一块（窗口越高缺得越多）。
    """
    try:
        if user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):  # PER_MONITOR_AWARE_V2
            return
    except AttributeError:
        pass
    try:
        user32.SetProcessDPIAware()
    except AttributeError:
        pass


_enable_dpi_awareness()


def find_window(pid: int, title: str | None = None, min_size: int = 200) -> int:
    """返回该进程面积最大的可见顶层窗口句柄，找不到返回 0。

    不能取"第一个"：WPF 进程除主窗口外还会有小的辅助窗口（如输入法候选、
    隐藏的 tooltip 宿主），先遇到的往往只有几十像素。

    title 非空时只考虑标题包含该子串的窗口——设置页、对话框都比主窗口小，
    按面积取永远拿不到它们。走查靠 `WD_TITLE` 环境变量指定（见 main）。
    """
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def enum_proc(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            if title:
                buf = ctypes.create_unicode_buffer(512)
                user32.GetWindowTextW(hwnd, buf, 512)
                if title.lower() not in buf.value.lower():
                    return True
            _, _, width, height = get_rect(hwnd)
            if width >= min_size and height >= min_size // 2:
                found.append((width * height, hwnd))
        return True

    user32.EnumWindows(enum_proc, 0)
    if not found:
        return 0
    found.sort(reverse=True)
    return found[0][1]


def get_rect(hwnd: int):
    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top


def get_visible_rect(hwnd: int):
    """窗口的可见边界（不含 DWM 阴影）。取不到就退回 GetWindowRect。"""
    rect = wt.RECT()
    try:
        hr = dwmapi.DwmGetWindowAttribute(
            wt.HWND(hwnd), DWMWA_EXTENDED_FRAME_BOUNDS, ctypes.byref(rect), ctypes.sizeof(rect)
        )
        if hr == 0 and rect.right > rect.left and rect.bottom > rect.top:
            return rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top
    except (AttributeError, OSError):
        pass
    return get_rect(hwnd)


def resolve_hwnd(pid: int) -> int:
    """按优先级取窗口句柄：WD_HWND 直指 > WD_TITLE 标题子串 > 面积最大的主窗口。

    WD_HWND 是为了截图弹出层：右键菜单/下拉菜单是独立 HWND 且比主窗口小，
    既拿不到标题（通常是空的），也会被尺寸门槛过滤掉。
    """
    forced = os.environ.get("WD_HWND")
    if forced:
        return int(forced, 0)
    minimum = int(os.environ.get("WD_MIN", "200"))
    return find_window(pid, os.environ.get("WD_TITLE"), minimum)


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    pid = int(sys.argv[1])
    action = sys.argv[2]

    hwnd = resolve_hwnd(pid)
    if not hwnd:
        print(f"未找到 pid={pid} 的可见窗口")
        return 1

    if action == "info":
        # 两个矩形都输出：裁剪参数是相对"可见区域"（去掉 DWM 阴影）算的，
        # 只给外框会引起越界——最大化窗口的外框上下各多 9px 阴影。
        print(f"hwnd={hwnd} rect={get_rect(hwnd)} visible={get_visible_rect(hwnd)}")
        return 0

    if action == "size":
        width, height = int(sys.argv[3]), int(sys.argv[4])
        left, top, _, _ = get_rect(hwnd)
        if not user32.MoveWindow(hwnd, left, top, width, height, True):
            print(f"MoveWindow 失败：{ctypes.get_last_error()}")
            return 1
        time.sleep(0.4)
        print(f"已调整：{get_rect(hwnd)}")
        return 0

    if action == "close":
        user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
        print("已发送 WM_CLOSE")
        return 0

    if action == "max":
        # SW_MAXIMIZE = 3
        user32.ShowWindow(hwnd, 3)
        time.sleep(0.5)
        print(f"已最大化：{get_rect(hwnd)}")
        return 0

    if action == "title":
        buffer = ctypes.create_unicode_buffer(512)
        user32.GetWindowTextW(hwnd, buffer, 512)
        print(buffer.value)
        return 0

    if action == "windows":
        # 列出该进程全部可见顶层窗口的标题，用于确认弹出了哪些窗口
        titles = []

        @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
        def enum_proc(h, _):
            owner = wt.DWORD()
            user32.GetWindowThreadProcessId(h, ctypes.byref(owner))
            if owner.value == pid and user32.IsWindowVisible(h):
                buf = ctypes.create_unicode_buffer(256)
                user32.GetWindowTextW(h, buf, 256)
                _, _, width, height = get_rect(h)
                titles.append(f"{h} {width}x{height} {buf.value}")
            return True

        user32.EnumWindows(enum_proc, 0)
        print(" | ".join(titles))
        return 0

    if action == "click":
        # 直接向窗口投递鼠标消息：不需要抢前台，坐标是客户区像素
        x, y = int(sys.argv[3]), int(sys.argv[4])
        lparam = (y << 16) | (x & 0xFFFF)
        user32.PostMessageW(hwnd, 0x0200, 0, lparam)      # WM_MOUSEMOVE
        time.sleep(0.1)
        user32.PostMessageW(hwnd, 0x0201, 0x0001, lparam)  # WM_LBUTTONDOWN, MK_LBUTTON
        time.sleep(0.08)
        user32.PostMessageW(hwnd, 0x0202, 0, lparam)       # WM_LBUTTONUP
        time.sleep(0.8)
        print(f"已点击客户区 ({x},{y})")
        return 0

    if action == "rclick":
        # 右键菜单：ContextMenu 只响应 WM_RBUTTONUP，且 WPF 会等一个 200ms 左右
        # 的"双击判定窗口"再弹，所以这里的人工延时不能省。
        # 用真实鼠标事件而不是 PostMessage：WPF 的 ContextMenu 打开前会检查
        # 鼠标按键状态（GetKeyState），投递的消息没有真实按键状态，菜单不弹。
        x, y = int(sys.argv[3]), int(sys.argv[4])
        left, top, _, _ = get_rect(hwnd)
        user32.BringWindowToTop(hwnd)
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.2)
        user32.SetCursorPos(left + x, top + y)
        time.sleep(0.25)
        user32.mouse_event(0x0008, 0, 0, 0, 0)   # RIGHTDOWN
        time.sleep(0.12)
        user32.mouse_event(0x0010, 0, 0, 0, 0)   # RIGHTUP
        time.sleep(1.0)
        print(f"已右键客户区 ({x},{y})")
        return 0

    if action == "drag":
        # 真实输入拖拽（不是 PostMessage 合成）：Thumb 依赖 GetKeyState(VK_LBUTTON)，
        # 投递的消息过不了这一步，只有 SetCursorPos + mouse_event 才会被认作拖拽。
        # 坐标是屏幕物理像素。
        x1, y1, x2, y2 = (int(v) for v in sys.argv[3:7])

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        target_thread = user32.GetWindowThreadProcessId(hwnd, None)
        current_thread = kernel32.GetCurrentThreadId()

        attached = user32.AttachThreadInput(current_thread, target_thread, True)
        try:
            # 不能用 SW_RESTORE：它会把最大化窗口还原成普通尺寸并挪位置，
            # 后续按"最大化时的屏幕坐标"算出来的点就全错位了。
            user32.BringWindowToTop(hwnd)
            user32.SetForegroundWindow(hwnd)
            user32.SetActiveWindow(hwnd)
            user32.SetFocus(hwnd)
            time.sleep(0.35)
        finally:
            user32.AttachThreadInput(current_thread, target_thread, False)

        fg = user32.GetForegroundWindow()
        fg_pid = wt.DWORD()
        user32.GetWindowThreadProcessId(fg, ctypes.byref(fg_pid))
        print(f"前台窗口 pid={fg_pid.value}（目标 pid={int(sys.argv[1])}），hwnd={hwnd} fg={fg}")

        user32.SetCursorPos(x1, y1)
        time.sleep(0.25)
        user32.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
        time.sleep(0.12)

        steps = 14
        for i in range(1, steps + 1):
            user32.SetCursorPos(x1 + (x2 - x1) * i // steps, y1 + (y2 - y1) * i // steps)
            time.sleep(0.03)

        time.sleep(0.15)
        user32.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP
        time.sleep(0.5)
        print(f"已拖拽 ({x1},{y1}) -> ({x2},{y2})")
        return 0

    if action == "keys":
        # 只支持验证用的少数组合，够驱动快捷键即可
        combos = {
            "ctrl+comma": (0x11, 0xBC),
            "ctrl+l": (0x11, 0x4C),
            "ctrl+shift+1": (0x11, 0xA0, 0x31),
            "ctrl+shift+2": (0x11, 0xA0, 0x32),
            "ctrl+shift+n": (0x11, 0xA0, 0x4E),
            "ctrl+shift+m": (0x11, 0xA0, 0x4D),
            "ctrl+shift+d": (0x11, 0xA0, 0x44),
            "alt+up": (0x12, 0x26),
            "alt+left": (0x12, 0x25),
            "alt+right": (0x12, 0x27),
            "f2": (0x71,),
            "down": (0x28,),
            "up": (0x26,),
            "alt+enter": (0x12, 0x0D),
            "f5": (0x74,),
            "delete": (0x2E,),
            "enter": (0x0D,),
            "ctrl+a": (0x11, 0x41),
            "esc": (0x1B,),
        }

        combo = " ".join(sys.argv[3:]).lower()
        if combo not in combos:
            print(f"未定义组合：{combo}，可用：{', '.join(combos)}")
            return 2

        keys = combos[combo]

        # 仅 SetForegroundWindow 常常无效（本进程不是前台进程），
        # 先用 AttachThreadInput 把输入队列挂到目标窗口线程上再抢前台。
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        target_thread = user32.GetWindowThreadProcessId(hwnd, None)
        current_thread = kernel32.GetCurrentThreadId()

        # 前台锁（SPI_GETFOREGROUNDLOCKTIMEOUT）：目标进程不是调用者的子进程时
        # SetForegroundWindow 会被直接拒绝，按键就静默发给了别人。
        # 三步依次加强，任一步成功即可：
        #   1) AttachThreadInput 把当前线程挂到目标线程，再抢前台；
        #   2) 同时挂到"当前前台窗口"的线程——前台锁就是被它占着；
        #   3) SwitchToThisWindow，老 API 不受前台锁限制。
        fg = user32.GetForegroundWindow()
        fg_thread = user32.GetWindowThreadProcessId(fg, None) if fg else 0
        attaches = [t for t in {fg_thread, target_thread} if t]
        for t in attaches:
            user32.AttachThreadInput(current_thread, t, True)
        try:
            user32.ShowWindow(hwnd, 9)  # SW_RESTORE
            user32.BringWindowToTop(hwnd)
            ok = user32.SetForegroundWindow(hwnd)
            user32.SetFocus(hwnd)
            time.sleep(0.2)
            if user32.GetForegroundWindow() != hwnd:
                user32.SwitchToThisWindow(hwnd, True)
                time.sleep(0.3)
        finally:
            for t in attaches:
                user32.AttachThreadInput(current_thread, t, False)

        actual = user32.GetForegroundWindow()
        if actual != hwnd:
            print(f"前台切换失败（当前前台 {actual}，目标 {hwnd}），快捷键可能未送达")

        for vk in keys:
            user32.keybd_event(vk, 0, 0, 0)
            time.sleep(0.03)
        for vk in reversed(keys):
            user32.keybd_event(vk, 0, 2, 0)
            time.sleep(0.03)

        time.sleep(0.6)
        print(f"已发送 {combo}")
        return 0

    if action == "wheel":
        # 面板滚动：AC-06/AC-11 走查要看设置页底部，Click 只能点到可见区域内的控件。
        # delta 正数向上、负数向下，120 为一格（Windows 标准滚轮步长）。
        delta = int(sys.argv[3])
        times = int(sys.argv[4]) if len(sys.argv) > 4 else 1
        left, top, width, height = get_rect(hwnd)
        user32.SetCursorPos(left + width // 2, top + height // 2)
        time.sleep(0.15)
        for _ in range(max(1, times)):
            user32.mouse_event(0x0800, 0, 0, ctypes.c_ulong(delta & 0xFFFFFFFF), 0)
            time.sleep(0.06)
        time.sleep(0.6)
        print(f"已滚动 {delta} x{times}")
        return 0

    if action == "screenshot":
        # 抓窗口位图存 BMP（不依赖 PIL）。用 PrintWindow + PW_RENDERFULLCONTENT，
        # 否则 WPF 的硬件加速内容会抓成一片空白。
        # 第 4 个参数起可选：<x> <y> <w> <h> 按可见区域裁剪，用于只看某一条（如状态栏）。
        out = sys.argv[3] if len(sys.argv) > 3 else "window.bmp"
        win_left, win_top, win_w, win_h = get_rect(hwnd)
        vis_left, vis_top, vis_w, vis_h = get_visible_rect(hwnd)
        width, height = win_w, win_h
        gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

        hdc = user32.GetWindowDC(hwnd)
        mem = gdi32.CreateCompatibleDC(hdc)
        bmp = gdi32.CreateCompatibleBitmap(hdc, width, height)
        old = gdi32.SelectObject(mem, bmp)
        ok = user32.PrintWindow(hwnd, mem, 2)
        if not ok:
            print("PrintWindow 失败，退回 BitBlt")
            gdi32.BitBlt(mem, 0, 0, width, height, hdc, 0, 0, 0x00CC0020)

        class BITMAPINFOHEADER(ctypes.Structure):
            _fields_ = [
                ("biSize", wt.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
            ]

        header = BITMAPINFOHEADER()
        header.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        header.biWidth = width
        header.biHeight = height
        header.biPlanes = 1
        header.biBitCount = 32
        header.biCompression = 0  # BI_RGB

        stride = width * 4
        buf = ctypes.create_string_buffer(stride * height)
        got = gdi32.GetDIBits(mem, bmp, 0, height, buf, ctypes.byref(header), 0)

        gdi32.SelectObject(mem, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem)
        user32.ReleaseDC(hwnd, hdc)

        if got == 0:
            print("GetDIBits 失败")
            return 1

        # 先裁掉 DWM 阴影，得到"眼睛能看到的窗口"；再按请求的矩形二次裁剪。
        dx, dy = vis_left - win_left, vis_top - win_top
        crop_w, crop_h, off_x, off_y = vis_w, vis_h, 0, 0
        if len(sys.argv) >= 7:
            off_x, off_y = int(sys.argv[4]), int(sys.argv[5])
            crop_w, crop_h = int(sys.argv[6]), int(sys.argv[7])
            if off_x < 0 or off_y < 0 or off_x + crop_w > vis_w or off_y + crop_h > vis_h:
                print(f"裁剪区域超出可见范围 {vis_w}x{vis_h}")
                return 1

        x0, y0 = dx + off_x, dy + off_y
        raw = buf.raw
        if crop_w != width or crop_h != height or x0 or y0:
            # GetDIBits 给的是 bottom-up：文件里第一行是最底行
            top_row = height - y0 - crop_h
            rows = [raw[(top_row + k) * stride + x0 * 4: (top_row + k) * stride + (x0 + crop_w) * 4]
                    for k in range(crop_h)]
            raw = b"".join(rows) if top_row >= 0 else raw
            if top_row < 0:
                print(f"裁剪起点越界：top_row={top_row}")
                return 1
            width, height = crop_w, crop_h
            stride = width * 4

        import struct
        info = struct.pack("<IiiHHIIiiII", 40, width, height, 1, 32, 0, stride * height, 0, 0, 0, 0)
        file_header = struct.pack("<2sIHHI", b"BM", 14 + 40 + len(raw), 0, 0, 54)
        with open(out, "wb") as f:
            f.write(file_header + info + raw)
        print(f"已保存 {out}（{width}x{height}）")
        return 0

    print(f"未知动作：{action}")
    return 2


if __name__ == "__main__":
    sys.exit(main())
