r"""常驻模态窗被关掉之后的存活探针。

背景：8 小时长跑（2026-11-16 11:42 轮）0 崩溃，但 crash.log 增了 402 KB、197 条托管异常，
其中 189 条是 `System.InvalidOperationException: 关闭窗口后，无法设置可见性…`，
栈顶恒为 `ModalSession.Run → ConfirmWindow.Notify → PaneView.ShowProperties`。
含义只有一个：ConfirmWindow 那个单例**已经被销毁**，之后每次 Show() 都抛。

这个探针要回答两件事：

1. 隐藏中的常驻窗收到 WM_CLOSE 会不会真被销毁（不经过 ModalSession 的 Closing 拦阻）？
2. 一旦被销毁，后续弹窗是否还能恢复？当前实现答案是"不能"——整条提示链路哑掉。

用法：
    python tools/ac03/probe_singleton.py
"""

from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import run_ac03 as drv  # noqa: E402

EXE = drv.ROOT / "publish" / "Jianlan.exe"
WORK = Path(r"D:\ac03")
CRASH_LOG = WORK / "app" / "data" / "logs" / "crash.log"


def dialogs(pid: int, main: int) -> list[int]:
    return [h for h in drv.stray_windows(pid, main)]


def main() -> int:
    if not EXE.exists():
        print(f"找不到 {EXE}", file=sys.stderr)
        return 2

    drv.prepare_work(WORK, EXE)
    sandbox = WORK / "sandbox"
    crash_base = CRASH_LOG.stat().st_size if CRASH_LOG.exists() else 0

    proc = subprocess.Popen([str(WORK / "app" / "Jianlan.exe")], cwd=str(WORK / "app"))
    print(f"启动 pid={proc.pid}")

    main_hwnd = 0
    for _ in range(120):
        main_hwnd = drv.main_window(proc.pid)
        if main_hwnd:
            break
        time.sleep(0.5)
    if not main_hwnd:
        print("主窗没出来", file=sys.stderr)
        proc.kill()
        return 2

    time.sleep(2.0)
    drv.focus(main_hwnd)
    # 首次启动恒开主页，主页上 Ctrl+L 不响应：先 Ctrl+T 开新标签落到沙箱，
    # 与 probe_modal.py 同一走法。
    if sandbox.name.lower() not in drv.window_title(main_hwnd).lower():
        drv.chord("ctrl", "t")
        time.sleep(2.5)
    if sandbox.name.lower() not in drv.window_title(main_hwnd).lower():
        drv.chord("ctrl", "l")
        drv.type_text(str(sandbox))
        drv.key("enter")
        time.sleep(2.0)
    print("当前目录：", drv.window_title(main_hwnd))
    if sandbox.name.lower() not in drv.window_title(main_hwnd).lower():
        print("没能进到沙箱目录，探针中止", file=sys.stderr)
        proc.kill()
        return 2
    time.sleep(1.0)

    def open_props(label: str) -> int:
        drv.focus(main_hwnd)
        drv.chord("ctrl", "a")
        drv.key("down")
        time.sleep(0.3)
        drv.chord("alt", "enter")
        time.sleep(1.0)
        got = dialogs(proc.pid, main_hwnd)
        print(f"{label}：弹出 {len(got)} 个，hwnd={got}")
        return got[0] if got else 0

    ok = True

    first = open_props("第 1 次属性")
    if not first:
        print("[FAIL] 属性框没弹出，探针无法继续")
        proc.kill()
        return 1

    drv.key("esc")
    time.sleep(0.6)
    print(f"Esc 之后：可见={bool(drv.user32.IsWindowVisible(first))} 存在={bool(drv.user32.IsWindow(first))}")

    # 驱动 dismiss_strays 的最后一步就是这句。窗口此刻隐藏，但 HWND 还在。
    drv.user32.PostMessageW(first, drv.WM_CLOSE, 0, 0)
    time.sleep(0.8)
    alive = bool(drv.user32.IsWindow(first))
    print(f"对隐藏窗口发 WM_CLOSE 之后：存在={alive}")
    if not alive:
        print("[!] 隐藏窗被 WM_CLOSE 销毁——单例就此失效")

    before = CRASH_LOG.stat().st_size if CRASH_LOG.exists() else 0
    second = open_props("第 2 次属性")
    after = CRASH_LOG.stat().st_size if CRASH_LOG.exists() else 0

    if second:
        print(f"[PASS] 第 2 次仍然弹窗（hwnd={second}），单例可自愈")
    else:
        print("[FAIL] 第 2 次没弹窗，crash.log 新增 %d 字节" % (after - before))
        ok = False

    proc.kill()
    print("结论：", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
