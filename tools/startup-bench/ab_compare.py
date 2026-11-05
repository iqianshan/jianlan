"""A/B 启动耗时对照：同一环境下交替启动两个版本，比较进程内埋点。

为什么不用 Measure-Startup.ps1 分开测：
系统负载在两次测量之间会漂移（内存 32% 时同一份代码可以从 684ms 涨到 989ms），
分开测的差值分不清是代码改动还是环境变化。交替测让两个版本经历同样的环境，
差值才可信（依据 docs/performance-gate.md §11.4）。

用法：
    python tools/startup-bench/ab_compare.py <exeA> <exeB> [轮数]
"""

import os
import re
import statistics
import subprocess
import sys
import time

LOG = os.path.join(os.environ["LOCALAPPDATA"], "Lingpane", "logs", "startup.log")


def _line_count():
    if not os.path.exists(LOG):
        return 0
    with open(LOG, encoding="utf-8", errors="ignore") as f:
        return len(f.read().splitlines())


def run_once(exe):
    """启动一次，等 startup.log 出现新行，返回该行文本。"""
    before = _line_count()
    proc = subprocess.Popen([exe])
    line = None
    deadline = time.time() + 15
    while time.time() < deadline:
        time.sleep(0.15)
        if _line_count() > before:
            with open(LOG, encoding="utf-8", errors="ignore") as f:
                line = f.read().splitlines()[-1]
            break
    try:
        proc.kill()
        proc.wait(timeout=5)
    except Exception:
        pass
    time.sleep(0.4)
    return line


def parse(line):
    total = int(re.search(r"total=(\d+)", line).group(1))
    marks = {k: int(v) for k, v in re.findall(r"(\w+)=(\d+)ms", line)}
    return total, marks


def pct(values, p):
    s = sorted(values)
    return s[max(0, min(len(s) - 1, int(len(s) * p + 0.9999) - 1))]


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1

    exe_a, exe_b = sys.argv[1], sys.argv[2]
    rounds = int(sys.argv[3]) if len(sys.argv) > 3 else 10

    for path in (exe_a, exe_b):
        if not os.path.exists(path):
            print(f"找不到：{path}")
            return 1

    res = {"A": [], "B": []}
    segs = {"A": [], "B": []}

    for i in range(1, rounds + 1):
        for tag, exe in (("A", exe_a), ("B", exe_b)):
            line = run_once(exe)
            if not line or "total=" not in line:
                print(f"  第 {i} 轮 {tag}：未采到埋点")
                continue
            total, marks = parse(line)
            res[tag].append(total)
            segs[tag].append(marks)
        print(f"  第 {i:2} 轮  A={res['A'][-1] if res['A'] else '-'}  B={res['B'][-1] if res['B'] else '-'}")

    print("\n================ 结果 ================")
    for tag in ("A", "B"):
        v = res[tag]
        if not v:
            print(f"{tag}：无数据")
            continue
        print(f"{tag}：P50={pct(v, 0.5)}  min={min(v)}  max={max(v)}  n={len(v)}")

    if res["A"] and res["B"]:
        pa, pb = statistics.median(res["A"]), statistics.median(res["B"])
        print(f"\n中位数差值 B-A = {pb - pa:+.0f} ms（{pb / pa - 1:+.1%}）")

        keys = [k for k in segs["A"][0] if k != "total"]
        print("\n分段中位数（ms）")
        print(f"{'阶段':<16}{'A':>8}{'B':>8}{'diff':>8}")
        for k in keys:
            a = statistics.median([s.get(k, 0) for s in segs["A"]])
            b = statistics.median([s.get(k, 0) for s in segs["B"]])
            print(f"{k:<16}{a:>8.0f}{b:>8.0f}{b - a:>+8.0f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
