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


def _bring_to_front(user32, hwnd):
    """把目标窗口抢到前台。

    SetForegroundWindow 对"非子进程"会被前台锁直接拒绝，按键/鼠标事件就静默
    发给了别的窗口。必须先把输入队列挂到目标线程（以及当前前台窗口的线程）上
    再抢，失败再退回 SwitchToThisWindow。
    """
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    target_thread = user32.GetWindowThreadProcessId(hwnd, None)
    current_thread = kernel32.GetCurrentThreadId()
    fg = user32.GetForegroundWindow()
    fg_thread = user32.GetWindowThreadProcessId(fg, None) if fg else 0

    attaches = [t for t in {fg_thread, target_thread} if t]
    for t in attaches:
        user32.AttachThreadInput(current_thread, t, True)
    try:
        user32.ShowWindow(hwnd, 9)  # SW_RESTORE
        user32.BringWindowToTop(hwnd)
        user32.SetForegroundWindow(hwnd)
        user32.SetFocus(hwnd)
        time.sleep(0.2)
        if user32.GetForegroundWindow() != hwnd:
            user32.SwitchToThisWindow(hwnd, True)
            time.sleep(0.3)
    finally:
        for t in attaches:
            user32.AttachThreadInput(current_thread, t, False)


class _BitmapInfoHeader(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


# ── 文本输入：SendInput + KEYEVENTF_UNICODE ─────────────────────────────────
# 地址栏补全（FR-6.1）要往 TextBox 里真的敲字。keybd_event 只认虚拟键码，
# 反斜杠、冒号、中文都得自己算 Shift 组合；直接送 Unicode 扫描码最省事，
# 且与真实键盘一样走输入队列，WPF 的 TextChanged 会正常触发。
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004
INPUT_KEYBOARD = 1


class _KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", wt.WORD),
        ("wScan", wt.WORD),
        ("dwFlags", wt.DWORD),
        ("time", wt.DWORD),
        ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class _MOUSEINPUT(ctypes.Structure):
    """只为把 INPUT 的 union 撑到 32 字节。

    SendInput 按 cbSize 校验结构体大小，对不上就直接返回 0（错误码 87 参数错误），
    一个字符也不会送进去。x64 上 MOUSEINPUT 是 32 字节、KEYBDINPUT 只有 24，
    union 里只放 ki 会让 INPUT 少 8 字节——这个坑的表现是"命令成功但界面毫无反应"。
    """

    _fields_ = [
        ("dx", wt.LONG),
        ("dy", wt.LONG),
        ("mouseData", wt.DWORD),
        ("dwFlags", wt.DWORD),
        ("time", wt.DWORD),
        ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class _INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", _MOUSEINPUT), ("ki", _KEYBDINPUT)]

    _anonymous_ = ("u",)
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def _type_text(user32, text: str) -> None:
    """逐字符发送 Unicode 按键。每个字符一次按下、一次抬起，中间留一点间隔。"""
    for ch in text:
        for extra in (0, KEYEVENTF_KEYUP):
            inp = _INPUT(type=INPUT_KEYBOARD)
            inp.ki = _KEYBDINPUT(0, ord(ch), KEYEVENTF_UNICODE | extra, 0, None)
            sent = user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(_INPUT))
            if sent != 1:
                # 静默失败最难查：表现出来只是"文本没输进去"
                raise OSError(f"SendInput 失败（字符 {ch!r}），错误码 {ctypes.get_last_error()}")
            time.sleep(0.008)


def _dib_bytes(gdi32, mem, bmp, width: int, height: int) -> bytes:
    """从内存位图取像素，返回 bottom-up 的 BGRA 原始数据。"""
    header = _BitmapInfoHeader()
    header.biSize = ctypes.sizeof(_BitmapInfoHeader)
    header.biWidth = width
    header.biHeight = height
    header.biPlanes = 1
    header.biBitCount = 32
    header.biCompression = 0  # BI_RGB

    buf = ctypes.create_string_buffer(width * 4 * height)
    got = gdi32.GetDIBits(mem, bmp, 0, height, buf, ctypes.byref(header), 0)
    return buf.raw if got else b""


def _write_bmp(out: str, width: int, height: int, raw: bytes) -> None:
    import struct
    stride = width * 4
    info = struct.pack("<IiiHHIIiiII", 40, width, height, 1, 32, 0, stride * height, 0, 0, 0, 0)
    file_header = struct.pack("<2sIHHI", b"BM", 14 + 40 + len(raw), 0, 0, 54)
    with open(out, "wb") as f:
        f.write(file_header + info + raw)


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

    if action == "move":
        # 搬到指定位置并定尺寸。多显示器下截图必须先把窗口挪回主屏：
        # capture 走 GetDC(0)（主屏 DC），窗口在第二屏时 BitBlt 按坐标抓到的
        # 是主屏同一块区域——图里出现的是别的程序，而不是目标窗口。
        x, y, width, height = (int(v) for v in sys.argv[3:7])
        if not user32.MoveWindow(hwnd, x, y, width, height, True):
            print(f"MoveWindow 失败：{ctypes.get_last_error()}")
            return 1
        time.sleep(0.5)
        print(f"已移动：{get_rect(hwnd)}")
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

    if action == "rect":
        # 取任意 hwnd 的屏幕矩形。用途：右键菜单是独立 Popup HWND，
        # 菜单项的点击位置只能按这个矩形算（按主窗口坐标猜一定会错，
        # 菜单贴边时 WPF 会把它翻到光标的另一侧）。
        # 参数可以是 hwnd 十进制，也可以是 "small"——取该进程最小的那个可见窗口。
        who = sys.argv[3] if len(sys.argv) > 3 else "small"
        if who == "small":
            found = []

            @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
            def enum_small(h, _):
                owner = wt.DWORD()
                user32.GetWindowThreadProcessId(h, ctypes.byref(owner))
                if owner.value == pid and user32.IsWindowVisible(h):
                    _, _, width, height = get_rect(h)
                    if width > 40 and height > 20 and h != hwnd:
                        found.append((width * height, h))
                return True

            user32.EnumWindows(enum_small, 0)
            if not found:
                print("没有更小的可见窗口")
                return 1
            found.sort()
            target = found[0][1]
        else:
            target = int(who, 0)

        left, top, width, height = get_rect(target)
        print(f"hwnd={target} left={left} top={top} width={width} height={height}")
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

    if action == "lclick":
        # 真实左键单击。click 走 PostMessage，只能命中目标窗口自己的控件；
        # 右键菜单、下拉框是独立 Popup HWND，必须用真实鼠标事件——坐标是屏幕像素。
        # 参数用客户区坐标，内部换算（和 click/rclick 统一，脚本里不必记两套基准）。
        x, y = int(sys.argv[3]), int(sys.argv[4])
        vis_left, vis_top, _, _ = get_visible_rect(hwnd)
        _bring_to_front(user32, hwnd)
        user32.SetCursorPos(vis_left + x, vis_top + y)
        time.sleep(0.25)
        user32.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
        time.sleep(0.08)
        user32.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP
        time.sleep(0.9)
        print(f"已真实单击客户区 ({x},{y})")
        return 0

    if action == "dclick":
        # 双击。**不能用 PostMessage 合成**：WPF 的 MouseDoubleClick 走的是
        # WM_LBUTTONDBLCLK，而这条消息只有窗口类带 CS_DBLCLKS 且由系统根据真实
        # 输入判定"两次点击落在双击时间内"才会生成——投递两条 WM_LBUTTONDOWN
        # 只会得到两次单击。所以这里和 drag 一样用真实鼠标事件。
        # 坐标是客户区像素（和 click 一致），内部换算成屏幕坐标。
        x, y = int(sys.argv[3]), int(sys.argv[4])
        vis_left, vis_top, _, _ = get_visible_rect(hwnd)
        _bring_to_front(user32, hwnd)
        user32.SetCursorPos(vis_left + x, vis_top + y)
        time.sleep(0.25)
        for _ in range(2):
            user32.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
            time.sleep(0.04)
            user32.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP
            time.sleep(0.06)                          # 远小于 GetDoubleClickTime()
        time.sleep(0.9)
        print(f"已双击客户区 ({x},{y})")
        return 0

    if action == "ctrlclick":
        # Ctrl+左键：FR-2.10 的第二个入口。修饰键必须用**真实键盘事件**按下，
        # 因为应用读的是 Keyboard.Modifiers（物理状态）；投递 WM_KEYDOWN 只会在
        # 消息队列里排一条按键，修饰键状态仍是"没按"。
        x, y = int(sys.argv[3]), int(sys.argv[4])
        vis_left, vis_top, _, _ = get_visible_rect(hwnd)
        _bring_to_front(user32, hwnd)
        user32.SetCursorPos(vis_left + x, vis_top + y)
        time.sleep(0.25)
        user32.keybd_event(0x11, 0, 0, 0)          # VK_CONTROL down
        time.sleep(0.1)
        user32.mouse_event(0x0002, 0, 0, 0, 0)     # LEFTDOWN
        time.sleep(0.08)
        user32.mouse_event(0x0004, 0, 0, 0, 0)     # LEFTUP
        time.sleep(0.1)
        user32.keybd_event(0x11, 0, 2, 0)          # VK_CONTROL up（KEYEVENTF_KEYUP）
        time.sleep(0.9)
        print(f"已 Ctrl+单击客户区 ({x},{y})")
        return 0

    if action == "rclick":
        # 右键菜单：ContextMenu 只响应 WM_RBUTTONUP，且 WPF 会等一个 200ms 左右
        # 的"双击判定窗口"再弹，所以这里的人工延时不能省。
        # 用真实鼠标事件而不是 PostMessage：WPF 的 ContextMenu 打开前会检查
        # 鼠标按键状态（GetKeyState），投递的消息没有真实按键状态，菜单不弹。
        x, y = int(sys.argv[3]), int(sys.argv[4])
        # 必须用**可见区域**原点，不能用 GetWindowRect：后者含 DWM 的隐形边框，
        # 左边比可见区域多 8px（实测 rect=360 而 visible=368）。差这 8px，
        # 点分隔条会落进左边列表——菜单弹出来的是"打开/复制/删除"，不是分隔条那三项。
        left, top, _, _ = get_visible_rect(hwnd)
        # 真实鼠标事件只会送到前台窗口，抢前台不能只靠 SetForegroundWindow（见 helper）
        _bring_to_front(user32, hwnd)
        user32.SetCursorPos(left + x, top + y)
        time.sleep(0.25)
        user32.mouse_event(0x0008, 0, 0, 0, 0)   # RIGHTDOWN
        time.sleep(0.12)
        user32.mouse_event(0x0010, 0, 0, 0, 0)   # RIGHTUP
        time.sleep(1.0)
        print(f"已右键客户区 ({x},{y})")
        return 0

    if action == "shift_rclick":
        # Shift+右键：FR-6.9 的第二个入口（CMF_EXTENDEDVERBS）。
        # 修饰键必须用真实键盘事件按下——应用读的是 Keyboard.Modifiers（物理状态），
        # 投递消息只会在队列里排一条按键，修饰键状态仍是"没按"。
        # 坐标与 rclick 一致：客户区像素，内部换算到可见区域原点。
        x, y = int(sys.argv[3]), int(sys.argv[4])
        left, top, _, _ = get_visible_rect(hwnd)
        _bring_to_front(user32, hwnd)
        user32.SetCursorPos(left + x, top + y)
        time.sleep(0.25)
        user32.keybd_event(0x10, 0, 0, 0)        # VK_SHIFT down
        time.sleep(0.1)
        user32.mouse_event(0x0008, 0, 0, 0, 0)   # RIGHTDOWN
        time.sleep(0.12)
        user32.mouse_event(0x0010, 0, 0, 0, 0)   # RIGHTUP
        time.sleep(0.1)
        user32.keybd_event(0x10, 0, 2, 0)        # VK_SHIFT up（KEYEVENTF_KEYUP）
        time.sleep(1.2)
        print(f"已 Shift+右键客户区 ({x},{y})")
        return 0

    if action == "mclick":
        # 中键点击：标签页关闭（FR-2.3）等场景要用。同 click，投递消息不抢前台。
        x, y = int(sys.argv[3]), int(sys.argv[4])
        lparam = (y << 16) | (x & 0xFFFF)
        user32.PostMessageW(hwnd, 0x0200, 0, lparam)        # WM_MOUSEMOVE
        time.sleep(0.1)
        user32.PostMessageW(hwnd, 0x0207, 0x0010, lparam)   # WM_MBUTTONDOWN, MK_MBUTTON
        time.sleep(0.08)
        user32.PostMessageW(hwnd, 0x0208, 0, lparam)        # WM_MBUTTONUP
        time.sleep(0.8)
        print(f"已中键点击客户区 ({x},{y})")
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

    if action == "type":
        # 文本输入：python window_drive.py <pid> type D:\ac05\s7
        # 参数用空格拼回来，路径里本来就不该带空格时的写法更省事；
        # 真要输空格就多写几个参数，不在这里做引号解析。
        text = " ".join(sys.argv[3:])
        _bring_to_front(user32, hwnd)
        time.sleep(0.25)
        _type_text(user32, text)
        time.sleep(0.5)
        print(f"已输入：{text}")
        return 0

    if action == "keys":
        # 只支持验证用的少数组合，够驱动快捷键即可
        combos = {
            "ctrl+comma": (0x11, 0xBC),
            "ctrl+l": (0x11, 0x4C),
            # 快捷键帮助（FR-10.13）：F1 打开设置的「快捷键」页
            "f1": (0x70,),
            # 全局唤起键的验证组合。刻意选一个不会被系统或其它程序占用的组合。
            "ctrl+alt+shift+f9": (0x11, 0x12, 0xA0, 0x70),
            "ctrl+shift+1": (0x11, 0xA0, 0x31),
            "ctrl+shift+2": (0x11, 0xA0, 0x32),
            # 设置页「快捷键」录制用的空闲组合（九号键位没有绑命令）
            "ctrl+shift+9": (0x11, 0xA0, 0x39),
            "ctrl+shift+8": (0x11, 0xA0, 0x38),
            # 选择（Ctrl+A / Ctrl+Shift+A 反选）
            "ctrl+a": (0x11, 0x41),
            "ctrl+shift+a": (0x11, 0xA0, 0x41),
            # 退出与应用级快捷键
            "ctrl+q": (0x11, 0x51),
            "ctrl+alt+s": (0x11, 0x12, 0x53),
            "ctrl+alt+o": (0x11, 0x12, 0x4F),
            # 菜单键（VK_APPS）：键盘打开列表右键菜单，验证菜单项用
            "apps": (0x5D,),
            # Shift + 不产生文字的键：策略放行这一类（Shift+Delete 本来就是内置默认）
            "shift+home": (0xA0, 0x24),
            # 唤起键的 Win 组合。VK_LWIN = 0x5B；Win 必须显式发，SendInput 不会替我们加
            "win+shift+f9": (0x5B, 0xA0, 0x70),
            # 标签页（FR-2）
            "ctrl+t": (0x11, 0x54),
            "ctrl+w": (0x11, 0x57),
            "ctrl+shift+t": (0x11, 0xA0, 0x54),
            "ctrl+tab": (0x11, 0x09),
            "ctrl+shift+tab": (0x11, 0xA0, 0x09),
            "ctrl+1": (0x11, 0x31),
            "ctrl+2": (0x11, 0x32),
            "ctrl+3": (0x11, 0x33),
            "ctrl+9": (0x11, 0x39),
            "ctrl+shift+n": (0x11, 0xA0, 0x4E),
            "ctrl+shift+m": (0x11, 0xA0, 0x4D),
            "ctrl+shift+d": (0x11, 0xA0, 0x44),
            # 双窗格（FR-3）：F3 与 Ctrl+Shift+E 同效
            "f3": (0x72,),
            "ctrl+shift+e": (0x11, 0xA0, 0x45),
            "alt+up": (0x12, 0x26),
            "alt+left": (0x12, 0x25),
            "alt+right": (0x12, 0x27),
            # 预览窗格（FR-9.1.1）。VK_P = 0x50，与 Alt+↑ 同一套修饰键顺序。
            "alt+p": (0x12, 0x50),
            "f2": (0x71,),
            "down": (0x28,),
            "up": (0x26,),
            "alt+enter": (0x12, 0x0D),
            "f5": (0x74,),
            "delete": (0x2E,),
            "enter": (0x0D,),
            "ctrl+a": (0x11, 0x41),
            # 视图开关（FR-10.12 补齐）：侧栏、隐藏文件、历史分区
            "ctrl+b": (0x11, 0x42),
            "ctrl+h": (0x11, 0x48),
            "ctrl+shift+h": (0x11, 0xA0, 0x48),
            "ctrl+d": (0x11, 0x44),
            # 复制完整路径（FR-6.14）。VK_C = 0x43，Shift 用 VK_LSHIFT(0xA0)
            # 而不是 VK_SHIFT(0x10)——与上面 ctrl+shift+n/m/d 保持同一写法。
            "ctrl+shift+c": (0x11, 0xA0, 0x43),
            # 设置入口（FR-5.1.10）。VK_OEM_COMMA = 0xBC，逗号必须走 OEM 码，
            # 没有对应的 ASCII/字母虚拟键。
            "ctrl+,": (0x11, 0xBC),
            # 文本框内复制/粘贴：走查时用来把地址栏里的当前路径读回剪贴板，
            # 比截图认字可靠（斜杠方向、全角半角都能原样拿到）。
            "ctrl+c": (0x11, 0x43),
            "ctrl+v": (0x11, 0x56),
            "esc": (0x1B,),
            "left": (0x25,),
            "right": (0x27,),
            "tab": (0x09,),
            "shift+tab": (0x10, 0x09),
            "app": (0x5D,),
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

        # 每个键带上扫描码：VK_LSHIFT(0xA0) / VK_RSHIFT(0xA1) 这类"左右分身的修饰键"
        # 在 scan=0 时按下可能不被系统登记，WPF 收到的 KeyDown 里也就没有 Shift。
        user32.MapVirtualKeyW.argtypes = [wt.UINT, wt.UINT]
        user32.MapVirtualKeyW.restype = wt.UINT

        for vk in keys:
            user32.keybd_event(vk, user32.MapVirtualKeyW(vk, 0), 0, 0)
            time.sleep(0.06)
        for vk in reversed(keys):
            user32.keybd_event(vk, user32.MapVirtualKeyW(vk, 0), 2, 0)
            time.sleep(0.06)

        time.sleep(0.6)
        print(f"已发送 {combo}")
        return 0

    if action == "wheel":
        # 面板滚动：AC-06/AC-11 走查要看设置页底部，Click 只能点到可见区域内的控件。
        # delta 正数向上、负数向下，120 为一格（Windows 标准滚轮步长）。
        # 第 5、6 个参数可选：<x> <y> 指定滚动位置（客户区 y 要自己加标题栏高度），
        # 不传就落在窗口中心。滚轮落在哪个控件上决定滚谁——例如标签栏横向滚动
        # 必须把光标放到标签条上，否则滚的是文件列表。
        delta = int(sys.argv[3])
        times = int(sys.argv[4]) if len(sys.argv) > 4 else 1
        left, top, width, height = get_rect(hwnd)
        if len(sys.argv) >= 7:
            user32.SetCursorPos(left + int(sys.argv[5]), top + int(sys.argv[6]))
        else:
            user32.SetCursorPos(left + width // 2, top + height // 2)
        time.sleep(0.15)
        for _ in range(max(1, times)):
            user32.mouse_event(0x0800, 0, 0, ctypes.c_ulong(delta & 0xFFFFFFFF), 0)
            time.sleep(0.06)
        time.sleep(0.6)
        print(f"已滚动 {delta} x{times}")
        return 0

    if action == "sendkey":
        # 直接投递 WM_KEYDOWN/WM_KEYUP，不经过真实键盘。
        #
        # 用途：锁屏、远程会话断开、窗口抢不到前台时，keys（真实按键）会把事件送错窗口，
        # 而窗口自己的 WM_KEYDOWN 钩子照样能收到投递的消息。
        # 限制：**只对无修饰键有效**——程序里的修饰键读的是 Keyboard.Modifiers（真实键盘状态），
        # 投递的消息不会改变它，所以 Ctrl+X 这类组合仍要用 keys。
        vk = int(sys.argv[3], 0)
        scan = user32.MapVirtualKeyW(vk, 0)
        down = 1 | (scan << 16)
        up = down | (1 << 30) | (1 << 31)
        user32.PostMessageW(hwnd, 0x0100, vk, down)   # WM_KEYDOWN
        time.sleep(0.05)
        user32.PostMessageW(hwnd, 0x0101, vk, up)     # WM_KEYUP
        time.sleep(0.6)
        print(f"已投递按键 vk=0x{vk:02X}")
        return 0

    if action == "navkey":
        # 真实按键，但**不抢前台**。给弹出层（右键菜单、下拉框）里的键盘导航用：
        # keys 每次都会 SetForegroundWindow(主窗口) + SetFocus(主窗口)，
        # 菜单一打开就把焦点拽回主窗口，随后的 ↓ 全被列表吃掉——
        # 表现是"菜单开着，高亮项却没动，列表选中项反而在动"。
        # 弹出层自己会抢前台，所以这里什么都不用做，直接发键即可。
        vk = int(sys.argv[3], 0)
        user32.keybd_event(vk, 0, 0, 0)
        time.sleep(0.05)
        user32.keybd_event(vk, 0, 2, 0)          # KEYEVENTF_KEYUP
        time.sleep(0.25)
        print(f"已发送按键 vk=0x{vk:02X}（不抢前台）")
        return 0

    if action == "sclick":
        # 屏幕坐标真实单击，且**不抢前台**。给弹出层（右键菜单、下拉框）里的条目用：
        # lclick/rclick 会先 SetForegroundWindow(主窗口)，浮动菜单在 owner 被激活时就关了，
        # 点下去落在"菜单刚消失的位置"，表现为点了个寂寞。
        # 坐标直接给屏幕像素，调用方从截图里读，不做换算。
        x, y = int(sys.argv[3]), int(sys.argv[4])
        user32.SetCursorPos(x, y)
        time.sleep(0.25)
        user32.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
        time.sleep(0.08)
        user32.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP
        time.sleep(0.9)
        print(f"已屏幕单击 ({x},{y})（不抢前台）")
        return 0

    if action == "front":
        # 抢到前台。capture 是 BitBlt 屏幕矩形，被别的窗口压着就会把别人拍进来；
        # 拍之前先把它拎到最前。
        _bring_to_front(user32, hwnd)
        print(f"前台={user32.GetForegroundWindow()}（目标 {hwnd}）")
        return 0

    if action == "capture":
        # 抓"屏幕上真正看到的那块矩形"，含右键菜单/下拉/Tooltip 等独立 HWND 的浮层。
        # PrintWindow 只画目标窗口自身，WPF 的 ContextMenu 是另一个 Popup HWND，
        # 用 screenshot 抓主窗口永远看不到菜单。
        out = sys.argv[3] if len(sys.argv) > 3 else "capture.bmp"
        left, top, width, height = get_visible_rect(hwnd)
        gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

        hdc = user32.GetDC(0)
        mem = gdi32.CreateCompatibleDC(hdc)
        bmp = gdi32.CreateCompatibleBitmap(hdc, width, height)
        old = gdi32.SelectObject(mem, bmp)
        gdi32.BitBlt(mem, 0, 0, width, height, hdc, left, top, 0x00CC0020)
        raw = _dib_bytes(gdi32, mem, bmp, width, height)

        gdi32.SelectObject(mem, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem)
        user32.ReleaseDC(0, hdc)

        if not raw:
            print("GetDIBits 失败")
            return 1
        _write_bmp(out, width, height, raw)
        print(f"已保存 {out}（{width}x{height}，屏幕区域 {left},{top}）")
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

        buf_raw = _dib_bytes(gdi32, mem, bmp, width, height)

        gdi32.SelectObject(mem, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem)
        user32.ReleaseDC(hwnd, hdc)

        if not buf_raw:
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
        raw = buf_raw
        stride = width * 4
        if crop_w != width or crop_h != height or x0 or y0:
            # GetDIBits 给的是 bottom-up：文件里第一行是最底行
            top_row = height - y0 - crop_h
            if top_row < 0:
                print(f"裁剪起点越界：top_row={top_row}")
                return 1
            rows = [raw[(top_row + k) * stride + x0 * 4: (top_row + k) * stride + (x0 + crop_w) * 4]
                    for k in range(crop_h)]
            raw = b"".join(rows)
            width, height = crop_w, crop_h

        _write_bmp(out, width, height, raw)
        print(f"已保存 {out}（{width}x{height}）")
        return 0

    print(f"未知动作：{action}")
    return 2


if __name__ == "__main__":
    sys.exit(main())
