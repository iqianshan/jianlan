"""预览窗格里连翻"处理器型文件"（PDF/Office）会不会卡（临时诊断）。

浮层那条路走 Shell 缩略图，窗格那条路会拉起系统预览处理器（HwndHost + prevhost）。
后者每切一次都要建宿主、激活处理器，连按时是否吃得消要实测：这里按住 Alt+P 开窗格，
以 80ms 间隔连发 25 次 ↓，全程用 WM_NULL 探 UI 线程响应。

用法：
    set JIANLAN_EXE_DIR=...\\bin\\Release\\net9.0-windows10.0.17763.0
    python preview_pane_probe.py ["<目录>"]
"""

from __future__ import annotations

import atexit
import ctypes
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import rename_smoke as rs
import alt_probe as ap
import quicklook_smoke as ql

WM_NULL = 0x0000
SMTO_ABORTIFHUNG = 0x0002
VK_ALT = 0x12
VK_P = 0x50
VK_DOWN = 0x28

TARGET = sys.argv[1] if len(sys.argv) > 1 else \
    r"D:\产品工作\02-软件类\01全流程规范\11-安全管理"

PROC: subprocess.Popen | None = None


def _cleanup() -> None:
    if PROC is not None and PROC.poll() is None:
        PROC.kill()


atexit.register(_cleanup)


def ui_busy_ms(hwnd: int, timeout: int = 200) -> int | None:
    result = ctypes.c_size_t()
    started = time.perf_counter()
    ok = rs.user32.SendMessageTimeoutW(
        hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, timeout, ctypes.byref(result)
    )
    elapsed = int((time.perf_counter() - started) * 1000)
    return elapsed if ok else None


def main() -> int:
    global PROC

    rs.stage_app()
    env = dict(os.environ)
    env["JIANLAN_INSTANCE_TAG"] = "paneprobe"

    print(f"启动 {rs.EXE}\n目录 {TARGET}")
    PROC = subprocess.Popen([rs.EXE, TARGET], env=env)
    pid = PROC.pid
    hwnd = int(ap.wait_window(pid, 40)["hwnd"])

    rs._TARGET_PID = pid
    rs._TARGET_HWND = hwnd
    rs._MAIN_HWND = hwnd

    ap.ensure_foreground(pid, hwnd)
    time.sleep(1.5)

    rs.press_combo(VK_ALT, VK_P)
    time.sleep(1.5)

    rows = rs.probe(pid)["rows"]
    print(f"行数 {len(rows)}  首行={rows[0]['name'] if rows else '-'}")
    rs.click(*rs.name_point(rows[1]))
    time.sleep(1.2)

    worst = 0
    timeouts = 0

    def sample(duration: float) -> None:
        nonlocal worst, timeouts
        end = time.time() + duration
        while time.time() < end:
            ms = ui_busy_ms(hwnd)
            if ms is None:
                timeouts += 1
            else:
                worst = max(worst, ms)
            time.sleep(0.06)

    print("\n== 逐次 ↓（每次 0.8s）==")
    for _ in range(6):
        rs.press("down")
        sample(0.8)

    print("== 连发 ↓ 25 次（80ms）==")
    sample(0.5)
    for _ in range(25):
        rs.press("down")
        time.sleep(0.08)
    sample(3.0)

    print(f"\nUI 线程最长响应 {worst}ms，超时样本 {timeouts} 次")

    rs.user32.PostMessageW(hwnd, 0x0010, 0, 0)
    for _ in range(20):
        time.sleep(0.3)
        if PROC.poll() is not None:
            break
    if PROC.poll() is None:
        PROC.kill()

    return 0 if timeouts == 0 and worst < 200 else 1


if __name__ == "__main__":
    raise SystemExit(main())
