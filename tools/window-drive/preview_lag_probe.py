"""空格预览翻页延迟诊断（临时）。

用户报：在某个目录里用空格预览，按上下键切换时"看着看着就看不到预览，
要等 4-5 秒才出现对应图片"。

测三件事：
  1. 逐次按 ↓（每次间隔 0.8s）：走完整个目录，记录每次的"选中 → 防抖触发 → 加载完成"；
  2. 按住 ↓ 不放 3s（键盘自动重复，真实用户会这么翻）：记录松手后多久出图；
  3. 全程每 60ms 给 UI 线程发一条 WM_NULL 并等 120ms——超时就说明 UI 线程被占住了，
     记下最长一次。卡顿是"感觉像卡了"最可能的来源。

用法：
    set JIANLAN_EXE_DIR=...\\bin\\Release\\net9.0-windows10.0.17763.0
    python preview_lag_probe.py "<目标目录>"
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

VK_SPACE = 0x20
VK_DOWN = 0x28
WM_NULL = 0x0000
SMTO_ABORTIFHUNG = 0x0002

TARGET = sys.argv[1] if len(sys.argv) > 1 else r"D:\产品工作\集团外项目\青岛高速项目\项目管理\UI设计稿\设计稿图\H5端\应急响应"

PROC: subprocess.Popen | None = None


def _cleanup() -> None:
    if PROC is not None and PROC.poll() is None:
        PROC.kill()


atexit.register(_cleanup)


def space() -> None:
    rs._assert_foreground()
    rs.user32.keybd_event(VK_SPACE, 0, 0, 0)
    time.sleep(0.05)
    rs.user32.keybd_event(VK_SPACE, 0, rs.KEYEVENTF_KEYUP, 0)


def find_overlay(pid: int, main_hwnd: int):
    return ql.find_overlay(pid, main_hwnd)


def ui_busy_ms(hwnd: int, timeout: int = 120) -> int | None:
    """给主窗发 WM_NULL。返回耗时（ms）；超时返回 None（UI 线程被占住）。"""
    result = ctypes.c_size_t()
    started = time.perf_counter()
    ok = rs.user32.SendMessageTimeoutW(
        hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, timeout, ctypes.byref(result)
    )
    elapsed = int((time.perf_counter() - started) * 1000)
    return elapsed if ok else None


def read_log(log_path: str) -> list[str]:
    if not os.path.isfile(log_path):
        return []
    with open(log_path, encoding="utf-8", errors="replace") as f:
        return [ln.rstrip() for ln in f if "preview.dbg" in ln]


def main() -> int:
    global PROC

    rs.stage_app()
    log_path = os.path.join(os.path.dirname(rs.EXE), "data", "logs", "app.log")
    if os.path.isfile(log_path):
        os.remove(log_path)

    env = dict(os.environ)
    env["JIANLAN_PREVIEWDEBUG"] = "1"
    env["JIANLAN_INSTANCE_TAG"] = "probe"

    print(f"启动 {rs.EXE}\n目录 {TARGET}")
    PROC = subprocess.Popen([rs.EXE, TARGET], env=env)
    pid = PROC.pid

    state = ap.wait_window(pid, 40)
    hwnd = int(state["hwnd"])
    print(f"窗口 {hex(hwnd)} rect={ap.window_rect(hwnd)}")

    ap.ensure_foreground(pid, hwnd)
    time.sleep(2.0)

    rows = rs.probe(pid)["rows"]
    print(f"行数 {len(rows)}  首行={rows[0]['name'] if rows else '-'}")
    rs.click(*rs.name_point(rows[1]))
    time.sleep(0.8)

    space()
    time.sleep(1.8)

    worst_busy = 0
    busy_samples = 0

    def sample(duration: float, label: str) -> None:
        nonlocal worst_busy, busy_samples
        end = time.time() + duration
        while time.time() < end:
            ms = ui_busy_ms(hwnd)
            if ms is None:
                busy_samples += 1
                worst_busy = max(worst_busy, 120)
                print(f"    ! {label} UI 线程 >120ms 无响应")
            else:
                worst_busy = max(worst_busy, ms)
            time.sleep(0.06)

    print("\n== 场景 A：逐次 ↓，每次等 0.8s ==")
    for i in range(8):
        rs.press("down")
        sample(0.8, f"A{i + 1}")

    print("\n== 场景 B：快速连发 ↓ 25 次（间隔 80ms，模拟按住键的自动重复）==")
    alive: list[bool] = []
    for i in range(25):
        rs.press("down")
        time.sleep(0.08)
        if i % 5 == 0:
            alive.append(find_overlay(pid, hwnd) is not None)
    print(f"  连发期间浮层存活采样: {alive}")
    print("  松手 at +0.00s")
    sample(3.0, "B松手后")

    print(f"\nUI 线程最长响应 {worst_busy}ms，超时样本 {busy_samples} 次")

    space()
    time.sleep(0.5)
    rs.user32.PostMessageW(hwnd, 0x0010, 0, 0)
    for _ in range(20):
        time.sleep(0.3)
        if PROC.poll() is not None:
            break
    if PROC.poll() is None:
        PROC.kill()

    print("\n=== preview.dbg ===")
    for line in read_log(log_path):
        print("  " + line)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
