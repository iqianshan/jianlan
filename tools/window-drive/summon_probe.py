"""全局唤起键的真窗口探针（FR-10.13）。

验三件事，缺一条这个功能就不算能用：

  1. 窗口在最前时按唤起键 → 收起（最小化）；
  2. 窗口被别的程序压住时按唤起键 → 回到前台；
  3. 再按一次 → 收起。

为什么不用 session.py 的 `keys`：那条路会先把目标窗口拎到前台再发键，
"从后台唤起"这一步在它手里根本构造不出来（发键之前前台已经是自己了）。
这里用 SendInput 直接发组合键，全程不动前台；RegisterHotKey 注册的组合键
由系统在输入线程里分发，与谁持有前台无关。

用法：
    python summon_probe.py <exe> <组合键>
    组合键写法：ctrl+alt+shift+f9（大小写不敏感，键名见 KEYS 表）

输出每步一行 `[step] ok/fail 详情`，末尾一行 JSON 汇总；任一步失败返回 1。
"""

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)

# 句柄一律显式声明：默认 restype 是 c_int，64 位下会把 HWND 截成负数/半截，
# 表现为"找到了窗口但比较永远不相等"（剪贴板那次踩的就是这个坑）。
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
user32.GetWindow.restype = wintypes.HWND
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsIconic.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]

SW_RESTORE = 9
WM_CLOSE = 0x0010

WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

# 回调类型要写成 WNDENUMPROC 而不是 c_void_p：声明成 c_void_p 时传函数指针进去
# 会被拒（EnumWindows 一个窗口都不回调，表现得像"程序没窗口"）。
user32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]

KEYS = {
    "f1": 0x70, "f2": 0x71, "f3": 0x72, "f4": 0x73, "f5": 0x74,
    "f6": 0x75, "f7": 0x76, "f8": 0x77, "f9": 0x78, "f10": 0x79,
    "f11": 0x7A, "f12": 0x7B,
}
# 单个字母/数字按虚拟键码规律直接算：'0'-'9' = 0x30+，'A'-'Z' = 0x41+
KEYS.update({str(d): 0x30 + d for d in range(10)})
KEYS.update({c.lower(): 0x41 + i for i, c in enumerate("ABCDEFGHIJKLMNOPQRSTUVWXYZ")})
MODS = {"ctrl": 0x11, "alt": 0x12, "shift": 0xA0, "win": 0x5B}


def find_window(pid, timeout=30.0):
    """按 pid 找第一个可见顶层窗口。返回 (hwnd, 标题) 或 (0, "")。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        hits = []

        def cb(hwnd, _):
            wpid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
            if wpid.value != pid or not user32.IsWindowVisible(hwnd):
                return True
            # 只认顶层：有 owner 的（菜单、工具提示）不算。
            # 注意用真值判断，不能写 `!= 0`：HWND 的 restype 下 NULL 回的是 None，
            # `None != 0` 为真，会把主窗口一起筛掉。
            if user32.GetWindow(hwnd, 4):
                return True
            hits.append(hwnd)
            return False

        user32.EnumWindows(WNDENUMPROC(cb), 0)
        if hits:
            hwnd = hits[0]
            n = user32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            return hwnd, buf.value
        time.sleep(0.25)
    return 0, ""


def foreground():
    return user32.GetForegroundWindow()


def is_iconic(hwnd):
    return bool(user32.IsIconic(hwnd))


def press(combo):
    """按一遍组合键并松开。全程不改前台。"""
    parts = [p.strip().lower() for p in combo.split("+") if p.strip()]
    mods, key = [], 0
    for p in parts:
        if p in MODS:
            mods.append(MODS[p])
        elif p in KEYS:
            key = KEYS[p]
    if key == 0:
        raise SystemExit(f"组合键里没有可识别的实键：{combo}")

    for vk in mods:
        user32.keybd_event(vk, 0, 0, 0)
        time.sleep(0.02)
    user32.keybd_event(key, 0, 0, 0)
    time.sleep(0.03)
    user32.keybd_event(key, 0, 2, 0)
    for vk in reversed(mods):
        time.sleep(0.02)
        user32.keybd_event(vk, 0, 2, 0)
    time.sleep(0.35)


def wait_for(predicate, timeout=3.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(0.05)
    return False


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    exe, combo = sys.argv[1], sys.argv[2]
    app = subprocess.Popen([exe], cwd=os.path.dirname(os.path.abspath(exe)))
    decoy = None
    hwnd = 0
    results = {}

    try:
        hwnd, title = find_window(app.pid)
        if hwnd == 0:
            print(f"[step] fail 没有等到主窗口 pid={app.pid}")
            return 1
        print(f"[step] ok 主窗口 hwnd={hwnd} title={title!r}")

        # 启动后主窗口通常已在前台；没在前台也不要紧，先点一下标题栏把它带上来
        time.sleep(1.0)
        if foreground() != hwnd:
            user32.SetForegroundWindow(hwnd)
            time.sleep(0.5)

        # ── 1. 前台时按 → 收起
        press(combo)
        results["minimize_when_front"] = wait_for(lambda: is_iconic(hwnd), 3.0)
        print(f"[step] {'ok' if results['minimize_when_front'] else 'fail'} 前台按一次 → 收起")

        # ── 2. 后台时按 → 回来
        # 诱饵用一个自带控制台的 cmd：它一定会开出自己的可见窗口，进程 pid 也不会像
        # Windows 11 的记事本那样转交给打包版（转交之后就按 pid 找不到了）。
        decoy = subprocess.Popen(
            ["cmd.exe", "/k", "title summon-probe-decoy"],
            creationflags=getattr(subprocess, "CREATE_NEW_CONSOLE", 0))
        decoy_hwnd, _ = find_window(decoy.pid, 10.0)
        if decoy_hwnd == 0:
            print("[step] fail 诱饵窗口没起来，第 2、3 步无法验证")
            return 1
        user32.SetForegroundWindow(decoy_hwnd)
        time.sleep(0.6)
        print(f"[step] ok 诱饵窗口到前台 hwnd={decoy_hwnd} 前台={foreground()}")

        press(combo)
        results["restore_from_background"] = wait_for(
            lambda: foreground() == hwnd and not is_iconic(hwnd), 3.0)
        print(f"[step] {'ok' if results['restore_from_background'] else 'fail'} "
              f"后台按一次 → 回到前台（前台={foreground()} iconic={is_iconic(hwnd)}）")

        # ── 3. 又在前台了，再按 → 再收起
        press(combo)
        results["minimize_again"] = wait_for(lambda: is_iconic(hwnd), 3.0)
        print(f"[step] {'ok' if results['minimize_again'] else 'fail'} 再按一次 → 收起")

        ok = all(results.values())
        print(json.dumps({"ok": ok, "steps": results}, ensure_ascii=False))
        return 0 if ok else 1
    finally:
        if decoy is not None and decoy.poll() is None:
            decoy.kill()
        if app.poll() is None:
            # 走正常关闭：直接 terminate 会让 session.json 留下"上次没干净退出"，
            # 下次启动弹恢复提示，后续截图全被那条横幅干扰。
            if hwnd:
                user32.ShowWindow(hwnd, SW_RESTORE)
                time.sleep(0.3)
                user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
            try:
                app.wait(timeout=8)
            except subprocess.TimeoutExpired:
                app.kill()


if __name__ == "__main__":
    raise SystemExit(main())
