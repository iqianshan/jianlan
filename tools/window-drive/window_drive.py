"""窗口驱动工具：按进程 ID 找到顶层窗口，调整尺寸或关闭。

用途：在无法使用 Add-Type / COM 的环境里做界面自动化验证
（例如验证"窗口尺寸记忆"是否真的写盘）。只用 ctypes 调 user32，无外部依赖。

用法：
    python window_drive.py <pid> size 1000 640    # 调整到 1000x640
    python window_drive.py <pid> close            # 发送 WM_CLOSE（等同点右上角）
    python window_drive.py <pid> info             # 打印当前矩形
"""

import ctypes
import ctypes.wintypes as wt
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)

WM_CLOSE = 0x0010


def find_window(pid: int) -> int:
    """返回该进程面积最大的可见顶层窗口句柄（主窗口），找不到返回 0。

    不能取"第一个"：WPF 进程除主窗口外还会有小的辅助窗口（如输入法候选、
    隐藏的 tooltip 宿主），先遇到的往往只有几十像素。
    """
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def enum_proc(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            _, _, width, height = get_rect(hwnd)
            if width >= 200 and height >= 200:
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


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    pid = int(sys.argv[1])
    action = sys.argv[2]

    hwnd = find_window(pid)
    if not hwnd:
        print(f"未找到 pid={pid} 的可见窗口")
        return 1

    if action == "info":
        print(f"hwnd={hwnd} rect={get_rect(hwnd)}")
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
                titles.append(buf.value)
            return True

        user32.EnumWindows(enum_proc, 0)
        print(" | ".join(t for t in titles if t))
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
            "alt+up": (0x12, 0x26),
            "alt+left": (0x12, 0x25),
            "alt+right": (0x12, 0x27),
            "f5": (0x74,),
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

        attached = user32.AttachThreadInput(current_thread, target_thread, True)
        try:
            user32.ShowWindow(hwnd, 9)  # SW_RESTORE
            ok = user32.SetForegroundWindow(hwnd)
            user32.SetFocus(hwnd)
            time.sleep(0.3)
        finally:
            user32.AttachThreadInput(current_thread, target_thread, False)

        if not ok:
            print("前台切换失败，快捷键可能未送达")

        for vk in keys:
            user32.keybd_event(vk, 0, 0, 0)
            time.sleep(0.03)
        for vk in reversed(keys):
            user32.keybd_event(vk, 0, 2, 0)
            time.sleep(0.03)

        time.sleep(0.6)
        print(f"已发送 {combo}")
        return 0

    print(f"未知动作：{action}")
    return 2


if __name__ == "__main__":
    sys.exit(main())
