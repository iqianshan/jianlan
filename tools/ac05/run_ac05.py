"""AC-05 边界场景验收：六类极端环境下的"不崩溃"判定。

为什么用 --perf-probe 而不是手工点：
    边界场景的失败往往是"偶发异常弹窗"或"界面空白但进程还在"，靠肉眼点一遍既不可
    重复也留不下证据。探针模式走的是真实 UI 导航路径（NavigateAsync + 首屏渲染），
    但会自动退出并写出 perf-probe.json，可脚本判定。

判定口径（四条同时成立才算 PASS）：
    1. 进程在超时前自行退出（不是被 kill）；
    2. 退出码 0；
    3. crash.log 没有新增记录（AppDomain / Dispatcher 未处理异常）；
    4. perf-probe.json 生成且 error 为空（探针自身没抛异常）。

    另有一列"预期失败"：无权限目录、断网路径这类场景必须给出 navigateError。
    若 items=0 且 navigateError 为空，说明界面会显示成"空目录"，用户无法区分，
    记 WARN（不算崩溃，但属于误导）。

fixture 不在此脚本内生成（10k 项要 20 秒以上，且 ACL 需管理员/所有者操作），
只用前先按 README 准备。脚本会检查缺失并直接报错。

用法：
    python tools/ac05/run_ac05.py                 # 全部六类
    python tools/ac05/run_ac05.py --only S1,S3    # 只跑指定场景
    python tools/ac05/run_ac05.py --keep-subst    # 失败后保留 subst 盘符便于排查
"""

import argparse
import json
import os
import pathlib
import subprocess
import sys
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
EXE = ROOT / "publish" / "Lingpane.exe"
LOGS = pathlib.Path(os.environ["LOCALAPPDATA"]) / "Lingpane" / "logs"
PROBE_JSON = LOGS / "perf-probe.json"
CRASH_LOG = LOGS / "crash.log"
BASE = pathlib.Path(r"D:\ac05")
REMOVABLE_DRIVE = "X:"

results = []


def crash_lines():
    if not CRASH_LOG.exists():
        return 0
    with open(CRASH_LOG, encoding="utf-8", errors="ignore") as f:
        return len(f.read().splitlines())


def run_probe(path, frames=0, timeout=180, during=None):
    """启动一次探针导航。during 是在进程运行期间执行的动作（模拟热拔出）。"""
    before = crash_lines()
    if PROBE_JSON.exists():
        PROBE_JSON.unlink()

    started = time.time()
    proc = subprocess.Popen(
        [str(EXE), "--perf-probe", path, "--perf-frames", str(frames)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    timer = None
    if during is not None:
        timer = threading.Timer(during[0], during[1])
        timer.start()

    timed_out = False
    try:
        code = proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=10)
        code, timed_out = None, True
    finally:
        if timer is not None:
            timer.cancel()

    elapsed = time.time() - started
    probe = None
    if PROBE_JSON.exists():
        try:
            with open(PROBE_JSON, encoding="utf-8") as f:
                probe = json.load(f)
        except (OSError, json.JSONDecodeError):
            probe = None

    return {
        "exit": code,
        "timedOut": timed_out,
        "elapsedMs": round(elapsed * 1000),
        "crashDelta": crash_lines() - before,
        "probe": probe,
    }


def check(name, run, expect_failure, note=""):
    """把一次 run_probe 结果折算成 PASS / WARN / FAIL。"""
    probe = run["probe"]
    problems = []

    if run["timedOut"]:
        problems.append("超时未退出（疑似挂起）")
    elif run["exit"] != 0:
        problems.append(f"退出码 {run['exit']}")
    if run["crashDelta"] > 0:
        problems.append(f"crash.log 新增 {run['crashDelta']} 条")
    if probe is None:
        problems.append("未生成 perf-probe.json")
    elif probe.get("error"):
        problems.append("探针异常：" + str(probe["error"]).splitlines()[0][:120])

    nav_error = (probe or {}).get("navigateError")
    items = (probe or {}).get("itemCount")
    status_text = (probe or {}).get("statusText")

    warn = ""
    if not problems and expect_failure and not nav_error:
        warn = "预期打不开，但界面会显示为空目录（navigateError 为空）"

    status = "FAIL" if problems else ("WARN" if warn else "PASS")
    row = {
        "id": name,
        "status": status,
        "problems": problems,
        "warn": warn,
        "items": items,
        "navigateError": nav_error,
        "statusText": status_text,
        "enumerateMs": (probe or {}).get("enumerateMs"),
        "firstScreenMs": (probe or {}).get("firstScreenMs"),
        "workingSetMb": (probe or {}).get("workingSetMb"),
        "elapsedMs": run["elapsedMs"],
        "note": note,
    }
    results.append(row)

    detail = "; ".join(problems) or warn or "ok"
    print(
        f"  {name:<6} {status:<4} items={items!s:<6} enum={row['enumerateMs']!s:<5}ms "
        f"first={row['firstScreenMs']!s:<7}ms {detail}"
    )
    return row


# ---------------- 场景 ----------------

def s1_big_directory():
    """10000 项：验证批量枚举 + 虚拟化滚动。帧数用默认 240，滚动路径也一并压到。"""
    r = run_probe(str(BASE / "10k"), frames=240, timeout=300)
    row = check("S1", r, expect_failure=False, note="10000 项目录（含 240 帧滚动）")
    if row["items"] is not None and row["items"] < 10000:
        row["status"] = "FAIL"
        row["problems"].append(f"条目数 {row['items']} < 10000")
        print(f"  S1     追加判定失败：只枚举到 {row['items']} 项")


def s2_long_path():
    """>260 字符路径：本项目靠 manifest longPathAware 生效，系统长路径开关已开。"""
    p = BASE / "longpath"
    for i in range(10):
        p = p / f"L0123456789abcdefghijklmn_{i:02d}"
    target = p / "长路径下的测试文件_2026_Q3.txt"
    r = run_probe(str(p), frames=0, timeout=120)
    row = check("S2", r, expect_failure=False, note=f"目录 {len(str(p))} 字符 / 文件 {len(str(target))} 字符")
    if row["items"] == 0 and not row["navigateError"]:
        row["status"] = "FAIL"
        print("  S2     追加判定失败：路径能打开却枚举到 0 项")


def s3_no_permission():
    """无权限目录：枚举必须抛 UnauthorizedAccessException，被 TryRead 吞掉并给出原因。"""
    r = run_probe(str(BASE / "noperm"), frames=0, timeout=120)
    check("S3", r, expect_failure=True, note="ACL 拒绝 Everyone")


def s4_network_down():
    """网络断开：用 TEST-NET-1 保留地址，等价于断网后访问一个已失效的共享。

    不用拔网线的方式：会连带影响本机其他测试与日志采集，且无法重复。
    """
    r = run_probe(r"\\192.0.2.1\share", frames=0, timeout=240)
    check("S4", r, expect_failure=True, note="不可达 UNC 路径")


def s5_usb_removed():
    """U 盘热拔出：无实体 U 盘，用 subst 盘符模拟卷消失。

    两段：
      5a 盘符已消失后再导航（用户切回旧标签页）；
      5b 使用中拔出——导航已经开始、列表已加载，随后卷消失。
         时延按本机实测取值：0.75s 前撤盘会落在导航开始前，1.3s 落在枚举完成后，
         两种都是合法时序，脚本只判定"不崩溃"，命中哪种由结果列体现。
    """
    def drop():
        subprocess.run(["subst", REMOVABLE_DRIVE, "/D"], check=False)

    subprocess.run(["subst", REMOVABLE_DRIVE, str(BASE / "removable")], check=False)
    drop()
    r = run_probe(REMOVABLE_DRIVE + "\\", frames=0, timeout=120)
    check("S5a", r, expect_failure=True, note="盘符消失后导航")

    # 卷上放 10000 项：枚举耗时足够长，撤盘才有机会落在"使用中"
    subprocess.run(["subst", REMOVABLE_DRIVE, str(BASE / "10k")], check=False)
    r = run_probe(REMOVABLE_DRIVE + "\\", frames=0, timeout=120, during=(1.3, drop))
    check("S5b", r, expect_failure=False, note="使用中撤盘（时延 1.3s）")
    drop()


def s6_readonly_media():
    """只读介质：本机无光驱且非管理员，无法挂载只读 VHD，用"可读不可写"的 ACL 目录模拟。

    覆盖的是同一类失败：目录能枚举，但任何写操作都会失败。枚举阶段不应崩溃。
    """
    r = run_probe(str(BASE / "readonly"), frames=0, timeout=120)
    row = check("S6", r, expect_failure=False, note="ACL 只读目录（可读不可写）")
    if row["items"] == 0 and not row["navigateError"]:
        row["status"] = "FAIL"
        print("  S6     追加判定失败：只读目录应能正常枚举")


SCENARIOS = {
    "S1": s1_big_directory,
    "S2": s2_long_path,
    "S3": s3_no_permission,
    "S4": s4_network_down,
    "S5": s5_usb_removed,
    "S6": s6_readonly_media,
}


def preflight():
    if not EXE.exists():
        print(f"找不到可执行文件：{EXE}\n先 dotnet publish 到 publish/。")
        return False

    missing = []
    if not (BASE / "10k").exists():
        missing.append(r"D:\ac05\10k（10000 项）")
    if not (BASE / "longpath").exists():
        missing.append(r"D:\ac05\longpath")
    if not (BASE / "noperm").exists():
        missing.append(r"D:\ac05\noperm（ACL 拒绝）")
    if not (BASE / "readonly").exists():
        missing.append(r"D:\ac05\readonly（ACL 只读）")
    if not (BASE / "removable").exists():
        missing.append(r"D:\ac05\removable")

    if missing:
        print("fixture 缺失，按 tools/ac05/README.md 准备：")
        for m in missing:
            print("  - " + m)
        return False
    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", help="逗号分隔的场景号，如 S1,S3")
    parser.add_argument("--exe", help="被测可执行文件，默认 publish/Lingpane.exe")
    parser.add_argument("--out", default=str(ROOT / "tools" / "ac05" / "results"))
    args = parser.parse_args()

    if args.exe:
        global EXE
        EXE = pathlib.Path(args.exe)

    if not preflight():
        return 2

    wanted = [k.strip().upper() for k in args.only.split(",")] if args.only else list(SCENARIOS)
    print(f"可执行文件：{EXE}")
    print(f"开始时间：{time.strftime('%Y-%m-%d %H:%M:%S')}\n")

    for key in wanted:
        fn = SCENARIOS.get(key)
        if fn is None:
            print(f"未知场景：{key}")
            return 2
        fn()

    print("\n================ 汇总 ================")
    passed = sum(1 for r in results if r["status"] == "PASS")
    warn = sum(1 for r in results if r["status"] == "WARN")
    failed = sum(1 for r in results if r["status"] == "FAIL")
    print(f"PASS {passed} / WARN {warn} / FAIL {failed}（共 {len(results)} 项）")

    out_dir = pathlib.Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    target = out_dir / f"ac05-{stamp}.json"
    with open(target, "w", encoding="utf-8") as f:
        json.dump(
            {
                "exe": str(EXE),
                "exeMtime": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(EXE.stat().st_mtime)),
                "startedAt": stamp,
                "results": results,
            },
            f,
            ensure_ascii=False,
            indent=2,
        )
    print(f"明细：{target}")

    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
