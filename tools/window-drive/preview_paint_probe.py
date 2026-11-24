"""端到端测"按键 → 画面真正变化"。

日志里的 `load done` 只说明位图对象到手，WPF 还要把它缩放到浮层尺寸、合成、上屏。
125% 缩放下这个放大是 1024x1600 → 约 1190 宽，用 HighQuality(Fant) 重采样，
软件渲染路径下可能吃掉几百毫秒到数秒——用户看到的"空白 4-5 秒"就在这一段。

做法：按 ↓ 之前先截浮层内容区一帧，之后每 35ms 截一帧与它比较，
记下"第一次明显变化"与"稳定下来"的时刻。

用法：
    python preview_paint_probe.py [--attach] ["<目录>"]
    --attach  不启动新进程，直接驱动已经在跑的那个实例（用户的真实环境）
"""

from __future__ import annotations

import atexit
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import ctypes
import fullshot
import rename_smoke as rs
import alt_probe as ap
import quicklook_smoke as ql
from PIL import Image, ImageChops

VK_SPACE = 0x20
TMP = os.path.join(os.environ.get("TEMP", "."), "ql_frame.png")

ATTACH = "--attach" in sys.argv
ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
TARGET = ARGS[0] if ARGS else r"D:\产品工作\集团外项目\青岛高速项目\项目管理\UI设计稿\设计稿图\H5端\应急响应"

PROC: subprocess.Popen | None = None


def _cleanup() -> None:
    if PROC is not None and PROC.poll() is None:
        PROC.kill()


atexit.register(_cleanup)


def space() -> None:
    rs.user32.keybd_event(VK_SPACE, 0, 0, 0)
    time.sleep(0.05)
    rs.user32.keybd_event(VK_SPACE, 0, rs.KEYEVENTF_KEYUP, 0)


def grab(rect) -> Image.Image:
    fullshot.shot(TMP, rect)
    im = Image.open(TMP)
    im.load()
    return im.convert("RGB")


def diff_ratio(a: Image.Image, b: Image.Image) -> float:
    if a.size != b.size:
        return 1.0
    d = ImageChops.difference(a, b).convert("L")
    hist = d.histogram()
    changed = sum(hist[12:])
    total = a.size[0] * a.size[1]
    return changed / total


def measure_step(hwnd: int, overlay_rect, label: str) -> None:
    ox, oy, ow, oh = overlay_rect
    # 内容区中段：避开标题栏（上 1/3）和底部提示
    rect = (ox + int(ow * 0.22), oy + int(oh * 0.42), int(ow * 0.56), int(oh * 0.34))

    before = grab(rect)
    rs.user32.SetForegroundWindow(hwnd)
    t0 = time.perf_counter()
    rs.press("down")

    first_change = None
    stable_at = None
    prev = before
    frames = 0

    while True:
        now = time.perf_counter() - t0
        if now > 6.0:
            break
        cur = grab(rect)
        frames += 1
        ratio_prev = diff_ratio(prev, cur)
        ratio_base = diff_ratio(before, cur)

        if first_change is None and ratio_base > 0.02:
            first_change = now

        if first_change is not None and ratio_prev < 0.002:
            # 连续两帧一样 = 画面稳定了
            check = grab(rect)
            frames += 1
            if diff_ratio(cur, check) < 0.002:
                stable_at = time.perf_counter() - t0
                break
        prev = cur
        time.sleep(0.02)

    print(f"  {label}: 首次变化 {('%.0fms' % (first_change * 1000)) if first_change else '未变化'}   "
          f"稳定 {('%.0fms' % (stable_at * 1000)) if stable_at else '>6000ms'}   帧数 {frames}")


def main() -> int:
    global PROC

    if ATTACH:
        # 驱动用户正在跑的实例。只找窗口，不启动、不关闭。
        pid = int(os.environ.get("JIANLAN_PID", "0")) or _find_pid()
        print(f"attach pid={pid}")
    else:
        rs.stage_app()
        env = dict(os.environ)
        env["JIANLAN_INSTANCE_TAG"] = "probe"
        PROC = subprocess.Popen([rs.EXE, TARGET], env=env)
        pid = PROC.pid

    state = ap.wait_window(pid, 40)
    hwnd = int(state["hwnd"])
    print(f"主窗 {hex(hwnd)} rect={ap.window_rect(hwnd)}")

    # rename_smoke 的 press/click 会校验前台，attach 模式下要手工告诉它目标是谁
    rs._TARGET_PID = pid
    rs._TARGET_HWND = hwnd
    rs._MAIN_HWND = hwnd

    ap.ensure_foreground(pid, hwnd)
    time.sleep(1.2)

    if ATTACH:
        # 不动用户的选中项：浮层已开就直接测，没开就打开
        if ql.find_overlay(pid, hwnd) is None:
            space()
            time.sleep(2.0)
    else:
        rows = rs.probe(pid)["rows"]
        print(f"行数 {len(rows)}")
        rs.click(*rs.name_point(rows[1]))
        time.sleep(0.8)
        space()
        time.sleep(2.0)

    found = ql.find_overlay(pid, hwnd)
    if found is None:
        print("浮层没弹出来")
        return 1
    overlay_hwnd, overlay_rect = found
    print(f"浮层 {hex(overlay_hwnd)} rect={overlay_rect}")

    for i in range(6):
        measure_step(hwnd, overlay_rect, f"↓#{i + 1}")

    space()
    time.sleep(0.4)
    if PROC is not None:
        rs.user32.PostMessageW(hwnd, 0x0010, 0, 0)
        for _ in range(20):
            time.sleep(0.3)
            if PROC.poll() is not None:
                break
        if PROC.poll() is None:
            PROC.kill()
    return 0


def _find_pid() -> int:
    out = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Jianlan.exe", "/FO", "CSV", "/NH"],
        capture_output=True, text=True, encoding="gbk", errors="replace").stdout
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[0].lower() == "jianlan.exe":
            return int(parts[1])
    raise SystemExit("找不到运行中的简览进程")


if __name__ == "__main__":
    raise SystemExit(main())
