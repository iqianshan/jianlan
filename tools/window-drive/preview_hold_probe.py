"""按住方向键不放时，预览会不会被自己一遍遍地取消（临时诊断）。

`preview_rapid_probe.py` 打的是"180ms 一次的独立按键"，也就是人手点着翻。
但真实的"卡"多半发生在**按住 ↓ 不放**：Windows 的自动重复约 30ms 一次，
而节流窗口是 120ms——每到一个窗口就把 `_pending` 换成最新项重新开一次加载，
`LoadAsync` 入口又会 `CancelInFlight()`。只要单张加载比窗口长，就永远出不来。

注意 `keybd_event` 按住不放**不会**触发系统自动重复（那是键盘驱动层的），
所以这里按 30ms 节奏手工连发，才是"手一直摁着"的真实按键流。

所以这里只问一个问题：1.5 秒里画面换了几张、松手后有没有补上。
再配合日志数 `load start` / `load done` / `merged` / `throttle run`：
start 多、done 少 = 在途被反复作废。

用法：
    set JIANLAN_EXE_DIR=...\\bin\\Release\\net9.0-windows10.0.17763.0
    python preview_hold_probe.py ["<目录>"]
"""

from __future__ import annotations

import atexit
import glob
import os
import re
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
VK_DOWN = 0x28
TMP = os.path.join(os.environ.get("TEMP", "."), "ql_hold.png")
LOG_DIR = os.path.join(os.path.dirname(rs.EXE), "data", "logs")

TARGET = sys.argv[1] if len(sys.argv) > 1 else \
    r"D:\产品工作\集团外项目\青岛高速项目\项目管理\UI设计稿\设计稿图\H5端\应急响应"
HOLD = 1.5
REPEAT = 0.03   # 系统自动重复的间隔
STEPS = 12      # 目录里 15 个文件，够翻到底

PROC: subprocess.Popen | None = None


def _cleanup() -> None:
    if PROC is not None and PROC.poll() is None:
        PROC.kill()


atexit.register(_cleanup)


def tap(vk: int) -> None:
    rs._assert_foreground()
    rs.user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.04)
    rs.user32.keybd_event(vk, 0, rs.KEYEVENTF_KEYUP, 0)


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


def read_log() -> str:
    """整份日志。只取末 N 行会把统计截断，之前就因此把 update 数读成 0。"""
    files = sorted(glob.glob(os.path.join(LOG_DIR, "*.log")), key=os.path.getmtime)
    if not files:
        return ""
    with open(files[-1], "r", encoding="utf-8", errors="replace") as f:
        return f.read()


def main() -> int:
    global PROC

    rs.stage_app()
    env = dict(os.environ)
    env["JIANLAN_INSTANCE_TAG"] = "hold"
    env["JIANLAN_PREVIEWDEBUG"] = "1"

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

    tap(VK_SPACE)
    time.sleep(1.5)
    found = ql.find_overlay(pid, hwnd)
    if found is None:
        print("浮层开不起来")
        return 1

    _overlay, (ox, oy, ow, oh) = found
    rect = (ox + int(ow * 0.22), oy + int(oh * 0.42), int(ow * 0.56), int(oh * 0.34))
    print(f"浮层 {ow}x{oh}  连发 {STEPS} 次 ↓（间隔 {int(REPEAT * 1000)}ms）")

    base = grab(rect)

    # 30ms 一次的按键流：这才是"手一直摁着 ↓"在程序里看到的样子
    rs._assert_foreground()
    presses = 0
    for _ in range(STEPS):
        rs.user32.keybd_event(VK_DOWN, 0, 0, 0)
        rs.user32.keybd_event(VK_DOWN, 0, rs.KEYEVENTF_KEYUP, 0)
        presses += 1
        time.sleep(REPEAT)

    # 抓图本身要几十毫秒，先让最后一张落地再拍（最慢一张冷缓存 100ms 上下），
    # 否则拍到的差异是"最后一张正在解码"，看着像"停手后画面还在追"
    time.sleep(0.3)
    during = grab(rect)          # 按键刚停、画面已稳定
    time.sleep(0.35)
    after = grab(rect)
    time.sleep(0.8)
    settled = grab(rect)

    print(f"  连发 {presses} 次 ↓（间隔 {int(REPEAT * 1000)}ms）")
    print(f"  按键期间 vs 起始 差异 {changed(base, during)}")
    print(f"  停手 350ms 后     差异 {changed(during, after)}")
    print(f"  再 800ms 后       差异 {changed(after, settled)}")

    text = read_log()
    stat = {
        "update": len(re.findall(r"update [\"']", text)),
        "queued": len(re.findall(r"-> queued", text)),
        "pass": len(re.findall(r"preview [\"']", text)),
        "start": len(re.findall(r"load start", text)),
        "done": len(re.findall(r"load done", text)),
    }
    print(f"\n日志统计 {stat}")

    lat = [int(m) for m in re.findall(r"load done .*? (\d+)ms from selection", text)]
    if lat:
        lat.sort()
        print(f"出图耗时（自选中起）{lat}  中位 {lat[len(lat) // 2]}ms")

    # S 后面紧跟另一个 S 说明前一张被作废；正常应是一条 S 一条 D 交替
    seq = re.findall(r"(load start|load done|preview [\"']|-> queued)", text)
    print("事件序列 " + " ".join(
        {"load start": "S", "load done": "D", "preview \"": "P", "preview '": "P", "-> queued": "q"}[s]
        for s in seq))

    tap(VK_SPACE)
    time.sleep(0.4)
    rs.user32.PostMessageW(hwnd, 0x0010, 0, 0)
    for _ in range(20):
        time.sleep(0.3)
        if PROC.poll() is not None:
            break
    if PROC.poll() is None:
        PROC.kill()

    moved = changed(base, during) > 200
    stable = changed(during, settled) == 0
    print(f"\n结论：连发期间画面{'跟着换' if moved else '没换'}，停手后{'已稳定' if stable else '仍在变'}")
    return 0 if (moved and stable) else 1


if __name__ == "__main__":
    raise SystemExit(main())
