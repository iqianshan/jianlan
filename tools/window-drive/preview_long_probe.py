"""长时间反复翻页，抓偶发离群值（临时）。

单次测得 350ms 正常，但用户反映"经常"要等 4-5 秒——偶发问题只能靠次数堆。
每步记录：端到端出图耗时、浮层是否还在、内容区是否处于"空白"（既非图也非说明）。

用法：
    python preview_long_probe.py [次数]
"""

from __future__ import annotations

import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import fullshot
import rename_smoke as rs
import alt_probe as ap
import quicklook_smoke as ql
from PIL import Image, ImageChops

TMP = os.path.join(os.environ.get("TEMP", "."), "ql_long.png")
N = int(sys.argv[1]) if len(sys.argv) > 1 else 40
PID = int(os.environ.get("JIANLAN_PID", "0"))


def grab(rect) -> Image.Image:
    fullshot.shot(TMP, rect)
    im = Image.open(TMP)
    im.load()
    return im.convert("RGB")


def diff_ratio(a, b) -> float:
    if a.size != b.size:
        return 1.0
    d = ImageChops.difference(a, b).convert("L")
    return sum(d.histogram()[12:]) / (a.size[0] * a.size[1])


def main() -> int:
    hwnd = int(ap.wait_window(PID, 10)["hwnd"])
    rs._TARGET_PID = PID
    rs._TARGET_HWND = hwnd
    rs._MAIN_HWND = hwnd
    ql.stabilize(PID, hwnd)

    found = ql.find_overlay(PID, hwnd)
    if found is None:
        rows = rs.probe(PID)["rows"]
        rs.click(*rs.name_point(rows[1]))
        time.sleep(0.5)
        ql.space()
        time.sleep(2.0)
        found = ql.find_overlay(PID, hwnd)
    if found is None:
        print("浮层开不起来")
        return 1

    overlay, (ox, oy, ow, oh) = found
    rect = (ox + int(ow * 0.22), oy + int(oh * 0.42), int(ow * 0.56), int(oh * 0.34))
    print(f"浮层 {hex(overlay)} {ow}x{oh}  次数={N}")

    results: list[float | None] = []
    gone = 0

    for i in range(N):
        key = "down" if i % 3 != 2 else "up"
        before = grab(rect)
        t0 = time.perf_counter()
        rs.press(key)
        first = None
        while time.perf_counter() - t0 < 6.0:
            cur = grab(rect)
            if diff_ratio(before, cur) > 0.02:
                first = time.perf_counter() - t0
                break
        results.append(first)
        if ql.find_overlay(PID, hwnd) is None:
            gone += 1
            print(f"  #{i + 1:<2} 浮层消失！")
            found = ql.find_overlay(PID, hwnd)
            if found is None:
                ql.space()
                time.sleep(1.5)
                found = ql.find_overlay(PID, hwnd)
                if found is None:
                    break
                overlay, (ox, oy, ow, oh) = found
                rect = (ox + int(ow * 0.22), oy + int(oh * 0.42), int(ow * 0.56), int(oh * 0.34))
        tag = f"{first * 1000:.0f}ms" if first is not None else ">6000ms"
        print(f"  #{i + 1:<2} {key:<4} {tag}")
        time.sleep(0.35)

    valid = [r for r in results if r is not None]
    missing = len(results) - len(valid)
    print(f"\n样本 {len(results)}  缺失 {missing}  浮层消失 {gone}")
    if valid:
        valid.sort()
        print(f"  最快 {valid[0] * 1000:.0f}ms  中位 {valid[len(valid) // 2] * 1000:.0f}ms  "
              f"最慢 {valid[-1] * 1000:.0f}ms")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
