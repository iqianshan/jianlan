"""空格预览浮层（FR-9.1.7）真窗口验收。

为什么必须开真窗口：这一版的功能点全在"窗口行为"上——
浮层不抢焦点、鼠标穿透、空格/Esc 开关、方向键翻页。UIA 与单测都看不到这些，
只有把 SendInput 打进真窗口、再逐像素比对浮层区域，才能证明"翻页确实换了内容"。

用例：
  1. 空格弹出浮层，且前台仍是主窗（不抢焦点）
  2. 方向键翻页：浮层内容变化
  3. 再按空格：收起
  4. 重开 + Esc：收起
  5. 重开 + 点主窗别处：收起
  6. 地址栏里按空格：不弹浮层（空格归输入框）
  7. 浮层矩形不越出主窗

用法：
    python quicklook_smoke.py
    set JIANLAN_EXE_DIR=...\\bin\\Release\\net9.0-windows10.0.17763.0 && python quicklook_smoke.py
"""

from __future__ import annotations

import atexit
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import fullshot
import rename_smoke as rs
import alt_probe as ap
from PIL import Image, ImageChops

VK_SPACE = 0x20
VK_ESCAPE = 0x1B

FAILS: list[str] = []


# 本脚本自己拉起来的进程。异常退出时也要收掉：这款程序是全局单实例，
# 留一个活着的副本会让下一次"启动"直接转交参数并退出，表现为"窗口没起来"。
PROC: subprocess.Popen | None = None


def _cleanup() -> None:
    if PROC is not None and PROC.poll() is None:
        PROC.kill()


atexit.register(_cleanup)


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILS.append(name)


def space() -> None:
    """单按空格。press() 的键表里没有它，这里单独发。"""
    rs._assert_foreground()
    rs.user32.keybd_event(VK_SPACE, 0, 0, 0)
    time.sleep(0.05)
    rs.user32.keybd_event(VK_SPACE, 0, rs.KEYEVENTF_KEYUP, 0)


def find_overlay(pid: int, main_hwnd: int):
    """浮层 = 同进程、可见、非主窗、尺寸像一块预览板的顶层窗口。"""
    best = None
    for hwnd, _title in rs.visible_windows(pid):
        if hwnd == main_hwnd:
            continue
        rect = ap.window_rect(hwnd)
        if rect[2] >= 300 and rect[3] >= 200:
            if best is None or rect[2] * rect[3] > best[1][2] * best[1][3]:
                best = (hwnd, rect)
    return best


def wait_overlay(pid: int, main_hwnd: int, present: bool, timeout: float = 3.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        found = find_overlay(pid, main_hwnd)
        if (found is not None) == present:
            return found
        time.sleep(0.1)
    return find_overlay(pid, main_hwnd)


def stabilize(pid: int, hwnd: int, tries: int = 6) -> bool:
    """把前台抢回来并确认稳定。这台机器上有第三方程序（explorerX）会周期性抢前台，
    一轮 SetForegroundWindow 之后可能立刻被顶掉——不确认就发键，键会打进别人家。"""
    for _ in range(tries):
        if rs.user32.GetForegroundWindow() == hwnd:
            time.sleep(0.25)
            if rs.user32.GetForegroundWindow() == hwnd:
                return True

        # 先制造一次本进程的输入事件，再抢前台（与 rename_smoke.focus 同一个理由）
        rs.user32.keybd_event(0x10, 0, 0, 0)
        rs.user32.keybd_event(0x10, 0, rs.KEYEVENTF_KEYUP, 0)
        time.sleep(0.1)
        rs.user32.SetForegroundWindow(hwnd)
        time.sleep(0.35)

    return rs.user32.GetForegroundWindow() == hwnd


def shot(path: str, rect) -> None:
    fullshot.shot(path, rect)


def region_diff(a_path: str, b_path: str, box) -> int:
    a = Image.open(a_path).convert("RGB").crop(box)
    b = Image.open(b_path).convert("RGB").crop(box)
    diff = ImageChops.difference(a, b)
    return sum(1 for pixel in diff.getdata() if sum(pixel) > 40)


def main() -> int:
    rs.stage_app()
    rs.seed()

    proc = subprocess.Popen([rs.EXE, rs.DIR])
    global PROC
    PROC = proc
    pid = proc.pid
    state = ap.wait_window(pid, timeout=30.0)
    main_hwnd = int(state["hwnd"])

    # rename_smoke 的 _assert_foreground 认这两个模块级变量；自己启动时得补上，
    # 否则每一次合成输入都会被判成"前台被抢走"而中止（launch() 那 4 秒固定等待
    # 在冷启动时不够，probe 还没看到窗口就先抛了，所以这里不用 launch）。
    rs._TARGET_PID = pid
    rs._TARGET_HWND = main_hwnd
    rs._MAIN_HWND = main_hwnd

    main_rect = ap.window_rect(main_hwnd)
    print("主窗", main_hwnd, main_rect)

    ap.ensure_foreground(pid, main_hwnd)
    rs.press_combo(0x11, 0x10, 0x31)          # Ctrl+Shift+1 → 详情视图
    time.sleep(1.5)

    rows = rs.probe(pid)["rows"]
    if len(rows) < 3:
        print("样本行不足，无法选文件")
        return 1

    # 选第 2 行：第 1 行常是文件夹（没有内容可预览），第 2 行是文件
    rs.click(*rs.name_point(rows[1]))
    time.sleep(0.6)
    print("已选中", rows[1]["name"])

    # ── 1. 空格弹出，且不抢焦点 ────────────────────────────────────────────
    space()
    found = wait_overlay(pid, main_hwnd, present=True)
    check("空格弹出浮层", found is not None)

    if found is None:
        rs.user32.PostMessageW(main_hwnd, 0x0010, 0, 0)
        time.sleep(1)
        if proc.poll() is None:
            proc.kill()
        return 1

    overlay_hwnd, overlay_rect = found
    foreground = rs.user32.GetForegroundWindow()
    check("浮层不抢焦点（前台仍是主窗）", foreground == main_hwnd,
          f"foreground={foreground}")

    mx, my, mw, mh = main_rect
    ox, oy, ow, oh = overlay_rect
    check("浮层在主窗范围内",
          ox >= mx and oy >= my and ox + ow <= mx + mw + 1 and oy + oh <= my + mh + 1,
          f"{overlay_rect} vs {main_rect}")

    box = (ox - mx, oy - my, ox - mx + ow, oy - my + oh)
    before = os.path.join(ap.OUT, "ql_open.png")
    shot(before, main_rect)

    # ── 2. 方向键翻页：浮层内容跟着换 ──────────────────────────────────────
    ap.ensure_foreground(pid, main_hwnd)
    rs.press("down")
    time.sleep(1.2)
    after = os.path.join(ap.OUT, "ql_next.png")
    shot(after, main_rect)

    changed = region_diff(before, after, box)
    check("↓ 后浮层内容变化", changed > 200, f"差异像素 {changed}")

    # 浮层仍在、前台仍是主窗：翻页没有把浮层关掉或激活
    check("翻页后浮层仍在", find_overlay(pid, main_hwnd) is not None)
    check("翻页后前台仍是主窗", rs.user32.GetForegroundWindow() == main_hwnd)

    # ── 3. 再按空格收起 ───────────────────────────────────────────────────
    space()
    check("再按空格收起", wait_overlay(pid, main_hwnd, present=False) is None)

    # ── 4. Esc 收起 ──────────────────────────────────────────────────────
    ap.ensure_foreground(pid, main_hwnd)
    space()
    check("重开浮层", wait_overlay(pid, main_hwnd, present=True) is not None)
    rs.press("esc")
    check("Esc 收起", wait_overlay(pid, main_hwnd, present=False) is None)

    # ── 5. 点主窗别处收起（浮层鼠标穿透，点击落到主窗）──────────────────
    ap.ensure_foreground(pid, main_hwnd)
    space()
    check("重开浮层", wait_overlay(pid, main_hwnd, present=True) is not None)

    # 点侧栏空白：不在浮层正中，避免"点在浮层上"与"点在主窗"混淆
    rs.click(mx + 40, my + mh // 2)
    check("点别处收起", wait_overlay(pid, main_hwnd, present=False) is None)

    # ── 6. 焦点不在列表时，空格不该弹浮层 ─────────────────────────────────
    # 落点选地址栏（列表上方那一行）：不在列表里，按空格要么进输入框、要么按按钮，
    # 都不该弹预览。不用 Ctrl+L 聚焦——这台机器上有第三方程序注册了同类全局热键，
    # 会把前台抢走（实测一次之后前台变成 explorerX），那样测的是别人的程序。
    focus_ok = stabilize(pid, main_hwnd)
    if focus_ok:
        rs.click(mx + 400, my + 80)           # 地址栏
        time.sleep(0.4)

    if focus_ok and rs.user32.GetForegroundWindow() == main_hwnd:
        space()
        time.sleep(0.4)
        check("焦点不在列表时空格不弹浮层", find_overlay(pid, main_hwnd) is None)
    else:
        print("  SKIP  焦点不在列表时空格不弹浮层（前台被别的程序占用）")

    rs.user32.PostMessageW(main_hwnd, 0x0010, 0, 0)
    for _ in range(20):
        time.sleep(0.3)
        if proc.poll() is not None:
            break
    if proc.poll() is None:
        proc.kill()

    print()
    if FAILS:
        print(f"FAILED: {len(FAILS)} 项 → {FAILS}")
        return 1

    print("全部通过")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
