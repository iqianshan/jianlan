"""打包产物的冷启动冒烟：双击能起、能收、数据边界没坏。

发布包最容易坏在两处，且都不在代码里：
  · 解压后的目录里混进了 `data\\` 或漏了 `portable.flag`——前者会把上次会话带出来，
    后者让程序把设置写到 `%LOCALAPPDATA%`，用户以为"设置丢了"；
  · 二进制与仓库不同步——`publish\\` 与 `dist\\` 各编一次，版本串、dll 都能对不上。

这个脚本只做四件事：起、看窗口、正常关、确认 data 里的退出标记被清掉。

用法：
    python package_smoke.py                       # 默认查 dist\\Jianlan-0.2.1-portable-win-x64
    python package_smoke.py --dir <解压目录>
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

WM_CLOSE = 0x0010


def find_dir() -> str:
    if "--dir" in sys.argv:
        return sys.argv[sys.argv.index("--dir") + 1]
    dist = os.path.join(ROOT, "dist")
    for name in sorted(os.listdir(dist)):
        full = os.path.join(dist, name)
        if os.path.isdir(full) and os.path.isfile(os.path.join(full, "Jianlan.exe")):
            return full
    raise SystemExit(f"{dist} 下找不到解压好的程序")


def main() -> int:
    app = find_dir()
    exe = os.path.join(app, "Jianlan.exe")
    data = os.path.join(app, "data")
    print(f"被测目录：{app}")

    fails: list[str] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
        if not ok:
            fails.append(name)

    check("包里有 portable.flag", os.path.isfile(os.path.join(app, "portable.flag")))

    env = dict(os.environ)
    env["JIANLAN_INSTANCE_TAG"] = "pkgsmoke"
    proc = subprocess.Popen([exe, app], env=env)
    pid = proc.pid

    hwnd = 0
    deadline = time.time() + 30
    started_at = time.time()

    def visible_top_level(h: int, pid: int) -> bool:
        p = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        return p.value == pid and bool(user32.IsWindowVisible(h))

    while time.time() < deadline:
        found: list[int] = []
        user32.EnumWindows(
            ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)(
                lambda h, l: (found.append(h), True)[1]), 0)
        hwnd = next((h for h in found if visible_top_level(h, pid)), 0)
        if hwnd:
            break
        time.sleep(0.4)

    check("窗口起来了", bool(hwnd), f"pid={pid} hwnd={hwnd}")
    if not hwnd:
        proc.kill()
        return 1

    time.sleep(1.5)

    started = os.path.join(data, "logs", "startup.log")
    check("startup 日志被本次启动追加",
          os.path.isfile(started) and os.path.getmtime(started) >= started_at - 2,
          f"mtime={time.strftime('%H:%M:%S', time.localtime(os.path.getmtime(started)))}"
          if os.path.isfile(started) else "文件不存在")
    if os.path.isfile(started):
        with open(started, encoding="utf-8", errors="replace") as f:
            print("    末行：", f.read().strip().splitlines()[-1][:150])

    # 正常关闭：走 WM_CLOSE，与用户点窗口的 × 等价。
    # 强杀不算——"上次未干净退出"那个标记只有正常退出才会被删。
    user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
    try:
        code = proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        proc.kill()
        check("WM_CLOSE 后进程退出", False, "30s 未退出")
        return 1

    check("退出码为 0", code == 0, f"code={code}")
    check("干净退出后 session.json 已删", not os.path.isfile(os.path.join(data, "session.json")))
    check("data\\settings.json 存在（便携模式写在自己目录里）",
          os.path.isfile(os.path.join(data, "settings.json")))

    print()
    if fails:
        print(f"{len(fails)} 项失败：{fails}")
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
