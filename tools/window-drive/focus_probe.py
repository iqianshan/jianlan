"""探一探：能不能把一个后台窗口真正提到前台。

为什么单独做这一件事：SendInput 造出来的点击只会落到**当前前台**窗口上。
之前整条冒烟验证卡在"点了没反应"，查下去发现点击落到了旁边一个记事本上——
我们的窗口根本没拿到前台，而 SetForegroundWindow 对后台进程会被系统拒绝。

这里逐个试几种抢前台的手段，打印每一种之后的实际结果（GetForegroundWindow
是不是目标），好用哪种就在 rename_smoke.py 里固定用哪种。

用法：python focus_probe.py
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)

try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except AttributeError:
    user32.SetProcessDPIAware()

EXE = r"D:\ac05\smoke-app\Jianlan.exe"
DIR = r"D:\ac05\rename-smoke"

VK_MENU = 0x12
KEYEVENTF_KEYUP = 0x0002
SW_RESTORE = 9
SWP_NOMOVE_NOSIZE = 0x0003
HWND_TOPMOST, HWND_NOTOPMOST = -1, -2


def fg_info() -> tuple[int, str, int]:
    hwnd = user32.GetForegroundWindow()
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return hwnd, buf.value, pid.value


def find_main_window(pid: int) -> int:
    """按进程找出可见的顶层窗口。"""
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def enum(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    user32.EnumWindows(enum, 0)
    return found[0] if found else 0


def try_set(hwnd: int, label: str) -> bool:
    print(f"  [{label}] SetForegroundWindow ->", user32.SetForegroundWindow(hwnd), end="  ")
    time.sleep(0.4)
    fg, cls, pid = fg_info()
    ok = fg == hwnd
    print(f"foreground={fg} (want {hwnd}) class={cls} ok={ok}")
    return ok


def main() -> int:
    proc = subprocess.Popen([EXE, DIR])
    time.sleep(4.0)
    try:
        hwnd = find_main_window(proc.pid)
        print("目标窗口:", hwnd, "自身 pid:", proc.pid)
        if not hwnd:
            print("没找到窗口")
            return 2

        fg, cls, fgpid = fg_info()
        print(f"当前前台: hwnd={fg} class={cls} pid={fgpid}")

        # 1) 直接调，通常会被系统拒绝（只有前台进程才被允许抢前台）
        user32.ShowWindow(hwnd, SW_RESTORE)
        if try_set(hwnd, "直接"):
            return 0

        # 2) 置顶再取消置顶：老办法，Win10 之后多半已经失效
        user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE_NOSIZE)
        ok = try_set(hwnd, "置顶")
        user32.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE_NOSIZE)
        if ok:
            return 0

        # 3) 敲一下修饰键：让本进程成为"最后产生输入事件的进程"，
        #    系统对前台权限的限制随之解除。这是 SendInput 之前的老套路。
        #    用 SHIFT 而不是 ALT：单独一下 ALT 抬起会被 WPF 的菜单条当成
        #    "进入菜单模式"，焦点从列表跑到菜单上，后面的点击就白点了。
        for vk, name in ((0x10, "SHIFT"), (0x12, "ALT")):
            user32.keybd_event(vk, 0, 0, 0)
            user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
            time.sleep(0.15)
            if try_set(hwnd, name):
                return 0

        # 4) 线程输入附加：把本线程的输入队列接到前台窗口所在线程上，
        #    这样"我们是后台进程"这个前提就不成立了。
        fg, _, _ = fg_info()
        cur = user32.GetCurrentThreadId()
        other = user32.GetWindowThreadProcessId(fg, None)
        attached = user32.AttachThreadInput(cur, other, True)
        print(f"  [附加] AttachThreadInput({cur},{other}) -> {attached}")
        ok = try_set(hwnd, "附加")
        user32.AttachThreadInput(cur, other, False)
        return 0 if ok else 1
    finally:
        user32.PostMessageW(hwnd, 0x0010, 0, 0) if hwnd else None
        time.sleep(1.5)
        if proc.poll() is None:
            proc.kill()


if __name__ == "__main__":
    sys.exit(main())
