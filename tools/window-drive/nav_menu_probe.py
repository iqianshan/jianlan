"""量侧栏「⋯」菜单到底弹在哪一边。

用户报的现象：点「位置」「收藏」「工作区」里的「⋯」，菜单看起来压在左侧栏上，
而不是出现在右边（文件列表那一侧）。

侧栏这几处菜单走的是 `AnchorMenuBesideNav`（`PlacementMode.Custom`，坐标自己算），
和窗口级 `MenuPlacement.KeepRightOfCursor`（只管 `MousePoint`）不是同一条路。
所以先取证再改：把菜单的屏幕矩形与主窗口、侧栏宽度放一起比。

判据（改完之后）：
    popup.x - window.x  ≈  侧栏宽度 + 4
改坏的样子（旧行为）：
    popup.x - window.x  ≈  34（右缘贴侧栏右界、向左铺）

坐标一律从同一次 UIA/GetWindowRect 里取，避免 DPI 虚拟化造成的两套数字打架。

用法：
    python nav_menu_probe.py            # 三处入口各量一次
    python nav_menu_probe.py --keep     # 跑完留着进程
产物：_shots/menu-*.png（整屏截图裁剪到窗口左下角）
"""

from __future__ import annotations

import atexit
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import fullshot
import rename_smoke as rs

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS = os.path.join(HERE, "_shots")
UIA_CLICK = os.path.join(HERE, "uia_click.ps1")
UIA_RECT = os.path.join(HERE, "uia_rect.ps1")
SNAP = os.path.join(HERE, "ui_snapshot_probe.ps1")

ROW_MENU_NAME = "更多操作"          # A11y.RowMenu 的 zh-Hans 文案
GAP = 4                            # AnchorMenuBesideNav 里菜单与侧栏之间的缝

PROC: subprocess.Popen | None = None
KEEP = "--keep" in sys.argv
FAILS: list[str] = []


def _cleanup() -> None:
    if PROC is not None and PROC.poll() is None and not KEEP:
        PROC.kill()


atexit.register(_cleanup)


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILS.append(name)


def ps_json(script: str, *args: str) -> dict:
    proc = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, *args],
        capture_output=True, text=True, errors="replace", encoding="utf-8")
    lines = (proc.stdout or "").strip().splitlines()
    if not lines:
        return {"ok": False, "error": f"no-output:{proc.stderr.strip()[:160]}"}
    os.makedirs(SHOTS, exist_ok=True)
    with open(os.path.join(SHOTS, "ps_raw.txt"), "w", encoding="utf-8") as f:
        f.write(proc.stdout or "")
    try:
        return json.loads(lines[-1])
    except json.JSONDecodeError:
        raise


def wait_window(pid: int, timeout: float = 30.0) -> int:
    deadline = time.time() + timeout
    while time.time() < deadline:
        st = rs.probe(pid)
        if st.get("hwnd"):
            return int(st["hwnd"])
        time.sleep(0.4)
    raise RuntimeError("窗口没起来")


def refocus(pid: int) -> int:
    for _ in range(6):
        try:
            return rs.focus(pid)
        except RuntimeError:
            time.sleep(0.4)
    raise RuntimeError("抢不回前台")


def shot(name: str, rect=None) -> str:
    os.makedirs(SHOTS, exist_ok=True)
    path = os.path.join(SHOTS, name)
    fullshot.shot(path, rect)
    return path


def measure(pid: int) -> dict:
    """一次取全：主窗口、popup、按钮列表。同一脚本同一次调用，坐标同源。"""
    m = ps_json(UIA_RECT, "-TargetPid", str(pid), "-Buttons")
    if not m.get("ok"):
        raise RuntimeError(f"量不到窗口：{m}")

    wins = sorted(m.get("windows") or [], key=lambda w: w["w"] * w["h"], reverse=True)
    main = wins[0] if wins else {}
    popups = [w for w in wins[1:]] if len(wins) > 1 else []
    m["main"] = main
    m["popup"] = popups[0] if popups else None
    return m


def nav_geometry(pid: int) -> dict:
    """侧栏 ListBox 与文件列表的矩形（都是 UIA 源，与菜单元素同源）。

    分界线不能直接用侧栏 ListBox 的 x+w：ListBox 的矩形把右边那不显示的滚动条
    与内边距也算进去了，比肉眼看到的那条线靠右约 19px。文件列表的左缘才是
    "分界线右边一点"，用它当基准，判据才不会因为内边距而误判。
    """
    snap = ps_json(SNAP, "-TargetPid", str(pid))
    out: dict = {}
    for l in snap.get("lists") or []:
        if l.get("id") == "QuickAccessList":
            out["nav"] = l
        elif l.get("id") == "FileList":
            out["file"] = l
    return out


def open_and_measure(pid: int, rect, name: str) -> dict:
    time.sleep(0.6)
    return measure(pid)


def main() -> int:
    os.environ.setdefault("JIANLAN_INSTANCE_TAG", "navmenu")

    rs.stage_app()
    global PROC
    PROC = subprocess.Popen([rs.EXE, rs.DIR])
    pid = PROC.pid
    hwnd = wait_window(pid)
    rs._TARGET_PID, rs._TARGET_HWND, rs._MAIN_HWND = pid, hwnd, hwnd
    refocus(pid)
    time.sleep(1.2)
    print(f"pid={pid} hwnd={hwnd}")

    # 窗口挪到主屏：这台机器上文件管理器原来跑在第二屏，整屏截图抓不到
    fullshot.user32.SetWindowPos(hwnd, 0, 80, 60, 1100, 720, 0x0004)
    time.sleep(0.8)
    rect = (60, 40, 1180, 800)

    geo = nav_geometry(pid)
    nav = geo.get("nav") or {}
    filelist = geo.get("file") or {}
    nav_w = nav.get("w", 0)
    print(f"\n侧栏 ListBox：x={nav.get('x')} w={nav_w}")
    print(f"文件列表：x={filelist.get('x')}")

    base = measure(pid)
    win = base["main"]
    print(f"主窗口：x={win['x']} y={win['y']} {win['w']}x{win['h']}")

    # 分界线右缘的基准：文件列表左缘。菜单左缘应该落在它附近（±10）而不是压在侧栏上。
    nav_right = filelist.get("x", nav.get("x", 0) + nav_w)
    print(shot("before.png", rect))

    results = []

    def report(label: str, op: dict) -> None:
        m = measure(pid)
        p = m.get("popup")
        # 判据必须同源：侧栏 ListBox 的矩形来自 UIA，菜单也取 UIA 暴露的 Menu 元素，
        # 不能拿 GetWindowRect 的 popup 去比——两套坐标在 125% 缩放下差 1.25 倍。
        menus = m.get("menus") or []
        if not p or not menus:
            check(f"{label}：菜单弹出来了", False, str(m)[:200])
            return
        mm = menus[0]
        print(f"    popup(hwnd) x={p['x']} w={p['w']}   menu(uia) x={mm['x']} w={mm['w']}")
        print(f"    文件列表左缘={nav_right}（uia，菜单左缘应贴着它）")
        results.append((label, mm["x"], mm["w"], mm["h"]))
        check(f"{label}：菜单在侧栏右侧、不压侧栏",
              nav_right - 12 <= mm["x"] <= nav_right + 16,
              f"menuLeft={mm['x']} fileLeft={nav_right}")
        print("   ", shot(f"menu-{label}.png", rect))
        rs.press("esc")
        time.sleep(0.5)

    # ── 1. 收藏 / 工作区行尾的「⋯」（数据模板里，没有 AutomationId） ─────
    # 干净的临时副本里收藏是空的，行尾按钮一个都不存在。先 Ctrl+D 钉一个当前目录，
    # 收藏这一行就有了「⋯」——它和「工作区」行用的是同一个模板、同一个处理器。
    rs.press_combo(0x11, 0x44)          # Ctrl+D
    time.sleep(0.9)
    base = measure(pid)
    btns = [b for b in (base.get("buttons") or []) if b["name"] == ROW_MENU_NAME]
    print(f"\n找到 {len(btns)} 个行尾「⋯」按钮")
    if not btns:
        print("  按钮列表：", [(b["name"], b["id"]) for b in (base.get("buttons") or [])])
    else:
        b = btns[0]
        print(f"  点第一个：x={b['x']} y={b['y']} {b['w']}x{b['h']}")
        rs.click(b["x"] + b["w"] // 2, b["y"] + b["h"] // 2)
        report("row", {"left": 0})

    # ── 2. 「位置」标题行尾的「⋯」（x:Name=PlacesConfigButton） ─────────
    r = ps_json(UIA_CLICK, "-TargetPid", str(pid), "-AutomationId", "PlacesConfigButton")
    print(f"\n点位置标题的「⋯」：{r}")
    if r.get("ok"):
        report("places", {"left": 0})

    print("\n汇总（菜单左缘距窗口左 / 菜单尺寸）：")
    for label, offset, w, h in results:
        print(f"  {label:8s} offset={offset:4d}  {w}x{h}")
    print(f"  侧栏宽度 {nav_w}")

    if KEEP:
        print(f"\n--keep：进程留着（pid={pid}）")

    print()
    if FAILS:
        print(f"{len(FAILS)} 项失败：{FAILS}")
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
