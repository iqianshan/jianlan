"""连按方向键时画面是否跟着换（临时诊断）。

用户报的是"看着看着就看不到预览"——不是某一次慢，而是**连按过程中画面整个冻住**。
所以这里不问"单次出图多久"，只问"每按一次，画面有没有变"。

做法：按住空格开着浮层，以 180ms 节奏按 6 次 ↓，每次按完 100ms 抓一张浮层内容区，
与前一张逐像素比对。全部为 0 就是冻住（改造前 300ms 防抖的表现）。

用法：
    set JIANLAN_EXE_DIR=...\\bin\\Release\\net9.0-windows10.0.17763.0
    python preview_rapid_probe.py ["<目录>"]
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
import quicklook_smoke as ql
from PIL import Image, ImageChops

VK_SPACE = 0x20
TMP = os.path.join(os.environ.get("TEMP", "."), "ql_rapid.png")

TARGET = sys.argv[1] if len(sys.argv) > 1 else \
    r"D:\产品工作\集团外项目\青岛高速项目\项目管理\UI设计稿\设计稿图\H5端\应急响应"
STEPS = 6
GAP = 0.18

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


def grab(rect) -> Image.Image:
    fullshot.shot(TMP, rect)
    im = Image.open(TMP)
    im.load()
    return im.convert("RGB")


def changed(a: Image.Image, b: Image.Image) -> int:
    if a.size != b.size:
        return -1
    d = ImageChops.difference(a, b).convert("L")
    return sum(d.histogram()[12:])


def main() -> int:
    global PROC

    rs.stage_app()
    env = dict(os.environ)
    env["JIANLAN_INSTANCE_TAG"] = "rapid"

    print(f"启动 {rs.EXE}\n目录 {TARGET}")
    PROC = subprocess.Popen([rs.EXE, TARGET], env=env)
    pid = PROC.pid
    hwnd = int(ap.wait_window(pid, 40)["hwnd"])

    rs._TARGET_PID = pid
    rs._TARGET_HWND = hwnd
    rs._MAIN_HWND = hwnd

    ap.ensure_foreground(pid, hwnd)
    time.sleep(1.5)

    rows = rs.probe(pid)["rows"]
    rs.click(*rs.name_point(rows[1]))
    time.sleep(0.6)

    space()
    time.sleep(1.5)
    found = ql.find_overlay(pid, hwnd)
    if found is None:
        print("浮层开不起来")
        return 1

    _overlay, (ox, oy, ow, oh) = found
    rect = (ox + int(ow * 0.22), oy + int(oh * 0.42), int(ow * 0.56), int(oh * 0.34))
    print(f"浮层 {ow}x{oh}  节奏 {int(GAP * 1000)}ms x {STEPS}")

    frames = [grab(rect)]
    hits = 0
    for i in range(STEPS):
        rs.press("down")
        time.sleep(GAP)
        frames.append(grab(rect))
        delta = changed(frames[-2], frames[-1])
        ok = delta > 200
        hits += 1 if ok else 0
        print(f"  #{i + 1}  差异像素 {delta:<7} {'换图' if ok else '没变'}")

    # 松手后再给一个窗口，看最后一张是否落地
    time.sleep(0.6)
    tail = grab(rect)
    print(f"  松手后 差异像素 {changed(frames[-1], tail)}")

    print(f"\n连按期间换图 {hits}/{STEPS}")

    space()
    time.sleep(0.4)
    rs.user32.PostMessageW(hwnd, 0x0010, 0, 0)
    for _ in range(20):
        time.sleep(0.3)
        if PROC.poll() is not None:
            break
    if PROC.poll() is None:
        PROC.kill()

    return 0 if hits >= STEPS - 1 else 1


if __name__ == "__main__":
    raise SystemExit(main())
