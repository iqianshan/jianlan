r"""定位"动作为什么不再落盘"。

冒烟跑到第 2 个采样点之后，沙箱目录的 mtime 就不动了——动作在发，键没生效。
这个探针把动作拆开单独打，每一步都数一遍文件数，看是哪一步把键盘焦点弄丢的。

用法：python tools/ac03/probe_actions.py
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


def step(label: str, sandbox: Path) -> int:
    n = count(sandbox)
    print(f"  {label:<28} 文件数 {n}")
    return n


def main() -> int:
    work = Path(r"D:\ac03")
    exe = ROOT / "publish" / "Jianlan.exe"
    target = ac03.prepare_work(work, exe)
    sandbox = work / "sandbox"

    proc = subprocess.Popen([str(target)], cwd=str(target.parent))
    try:
        hwnd = 0
        for _ in range(60):
            time.sleep(0.5)
            hwnd = ac03.main_window(proc.pid)
            if hwnd:
                break
        if not hwnd:
            print("窗口没起来")
            return 2
        if not ac03.focus(hwnd):
            print("抢前台失败")
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
        ac03.focus(hwnd)
        print(f"标题 {ac03.window_title(hwnd)}")

        base = count(sandbox)
        print(f"起始文件数 {base}")

        print("A. 连续新建 3 次（Ctrl+Shift+M）")
        for i in range(3):
            ac03.focus(hwnd)
            ac03.chord("ctrl", "shift", "m")
            time.sleep(0.5)
            ac03.type_text(f"probe{i}")
            ac03.key("enter")
            time.sleep(0.8)
            step(f"第 {i+1} 次新建后", sandbox)

        print("B. Ctrl+A 之后再新建 1 次")
        ac03.focus(hwnd)
        ac03.chord("ctrl", "a")
        time.sleep(0.3)
        ac03.chord("ctrl", "shift", "m")
        time.sleep(0.5)
        ac03.type_text("probeB")
        ac03.key("enter")
        time.sleep(0.8)
        step("新建后", sandbox)

        print("C. 搜索（Ctrl+F → item → Esc）之后再新建 1 次")
        ac03.focus(hwnd)
        ac03.chord("ctrl", "f")
        ac03.type_text("item")
        time.sleep(0.4)
        ac03.key("esc")
        time.sleep(0.5)
        ac03.focus(hwnd)
        ac03.chord("ctrl", "shift", "m")
        time.sleep(0.5)
        ac03.type_text("probeC")
        ac03.key("enter")
        time.sleep(0.8)
        step("新建后", sandbox)

        print("D. 删除（Delete）+ 撤销（Ctrl+Z）")
        ac03.focus(hwnd)
        ac03.key("down")
        ac03.key("delete")
        time.sleep(0.8)
        step("删除后", sandbox)
        ac03.chord("ctrl", "z")
        time.sleep(0.8)
        step("撤销后", sandbox)

        print("E. 地址栏跳转（Ctrl+L → 路径 → Enter）之后再新建 1 次")
        ac03.focus(hwnd)
        ac03.chord("ctrl", "l")
        time.sleep(0.3)
        ac03.type_text(str(sandbox))
        ac03.key("enter")
        time.sleep(1.2)
        ac03.focus(hwnd)
        ac03.chord("ctrl", "shift", "m")
        time.sleep(0.5)
        ac03.type_text("probeE")
        ac03.key("enter")
        time.sleep(0.8)
        step("新建后", sandbox)

        print("F. 视图切换（Ctrl+Shift+2/1）之后再新建 1 次")
        ac03.focus(hwnd)
        ac03.chord("ctrl", "shift", "2")
        time.sleep(0.5)
        ac03.chord("ctrl", "shift", "1")
        time.sleep(0.5)
        ac03.focus(hwnd)
        ac03.chord("ctrl", "shift", "m")
        time.sleep(0.5)
        ac03.type_text("probeF")
        ac03.key("enter")
        time.sleep(0.8)
        step("新建后", sandbox)

        extras = []
        ac03.user32.EnumWindows(
            ac03.WNDENUMPROC(lambda h, _: extras.append((ac03.window_title(h),)) or True), 0)
        modals = [t[0] for t in extras if t[0] and "sandbox" not in t[0]][:10]
        print(f"当前标题 {ac03.window_title(hwnd)}")
        print(f"其它窗口 {modals}")
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
