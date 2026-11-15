r"""搜索框退出后，键盘焦点怎么取回来。

前一个探针定位到：Ctrl+F → 输入 → Esc 之后，Delete / Ctrl+Shift+M 全部失效，
文件列表丢了键盘焦点。这个探针比几种收尾方式，找最省的那一种，压测脚本照此收尾。

用法：python tools/ac03/probe_focus.py
"""

from __future__ import annotations

import importlib.util
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("ac03", Path(__file__).with_name("run_ac03.py"))
ac03 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ac03)


def count(sandbox: Path) -> int:
    return sum(1 for p in sandbox.iterdir() if p.is_file())


def main() -> int:
    work = Path(r"D:\ac03")
    target = ac03.prepare_work(work, ROOT / "publish" / "Jianlan.exe")
    sandbox = work / "sandbox"
    proc = subprocess.Popen([str(target)], cwd=str(target.parent))
    try:
        hwnd = 0
        for _ in range(60):
            time.sleep(0.5)
            hwnd = ac03.main_window(proc.pid)
            if hwnd:
                break
        if not hwnd or not ac03.focus(hwnd):
            print("启动或抢前台失败")
            return 2
        time.sleep(1.0)
        ac03.chord("ctrl", "t")
        time.sleep(2.0)
        if sandbox.name.lower() not in ac03.window_title(hwnd).lower():
            ac03.chord("ctrl", "l")
            ac03.type_text(str(sandbox))
            ac03.key("enter")
            time.sleep(1.5)
        ac03.chord("ctrl", "1")
        time.sleep(0.4)
        ac03.chord("ctrl", "w")
        time.sleep(1.2)

        def search_then(tail: str, keys) -> None:
            ac03.focus(hwnd)
            ac03.chord("ctrl", "f")
            ac03.type_text("item")
            time.sleep(0.4)
            ac03.key("esc")
            time.sleep(0.4)
            keys()
            time.sleep(0.5)
            ac03.focus(hwnd)
            before = count(sandbox)
            ac03.chord("ctrl", "shift", "m")
            time.sleep(0.5)
            ac03.type_text("foc")
            ac03.key("enter")
            time.sleep(0.8)
            after = count(sandbox)
            print(f"  {tail:<22} 新建 {'生效' if after > before else '失效'}（{before}→{after}）")

        print("搜索后追加不同收尾，再看新建能不能落地：")
        search_then("Esc 一次（现状）", lambda: None)
        search_then("Esc 两次", lambda: (ac03.key("esc"), time.sleep(0.3)))
        search_then("Esc + 方向键下", lambda: (ac03.key("down"),))
        search_then("Esc + Tab", lambda: (ac03.key("tab"),))
        search_then("Ctrl+L + Enter", lambda: (
            ac03.chord("ctrl", "l"), time.sleep(0.3), ac03.key("enter"), time.sleep(1.0)))
        search_then("Esc + F5", lambda: (ac03.key("esc"), time.sleep(0.2), ac03.key("f5"), time.sleep(0.6)))
        search_then("F3（双窗格）", lambda: (ac03.key("f3"), time.sleep(0.5)))
        return 0
    finally:
        hwnd2 = ac03.main_window(proc.pid)
        if hwnd2:
            ac03.user32.PostMessageW(hwnd2, ac03.WM_CLOSE, 0, 0)
        time.sleep(1.0)
        if proc.poll() is None:
            proc.kill()


if __name__ == "__main__":
    sys.exit(main())
