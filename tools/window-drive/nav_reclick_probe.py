"""复现"点左上角「下载」右侧列表没反应"。

现象：在「下载」的子目录里，侧栏的「下载」已经是高亮项；这时点它，右侧列表不动。

怀疑的链路：
  · 侧栏高亮用的是 ListBox.SelectedItem，而导航（双击进入子目录、地址栏、
    后退）之后没有任何地方重新同步高亮，于是高亮停在上一次点过的那一项上；
  · 点一个**已经选中**的 ListBox 项不会触发 SelectionChanged，
    而导航动作只挂在 SelectionChanged 上 —— 于是点击被完全吞掉。

这个脚本用真窗口、真鼠标走一遍：
  1. 点「下载」→ 应在 Downloads 根
  2. 双击进入子目录 → 侧栏高亮是否还跟着（这里只记录，不作判据）
  3. 再点「下载」→ **应回到 Downloads 根**（这就是回归判据）

样本目录建在 Downloads 下（`_jianlan_nav_probe/inner`），跑完删掉自己建的那一份。

用法：
    python nav_reclick_probe.py
    set JIANLAN_EXE_DIR=...\\bin\\Release\\net9.0-windows10.0.17763.0 && python nav_reclick_probe.py
"""

from __future__ import annotations

import atexit
import json
import os
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import rename_smoke as rs

HERE = os.path.dirname(os.path.abspath(__file__))
SNAP = os.path.join(HERE, "ui_snapshot_probe.ps1")

SAMPLE = "_jianlan_nav_probe"
PROC: subprocess.Popen | None = None
FAILS: list[str] = []


def _cleanup() -> None:
    if PROC is not None and PROC.poll() is None:
        PROC.kill()
    root = os.path.join(downloads_root(), SAMPLE)
    shutil.rmtree(root, ignore_errors=True)


atexit.register(_cleanup)


def downloads_root() -> str:
    return os.path.join(os.path.expanduser("~"), "Downloads")


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILS.append(name)


def snapshot(pid: int, list_id: str = "") -> dict:
    args = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", SNAP, "-TargetPid", str(pid)]
    if list_id:
        args += ["-ListId", list_id]
    out = subprocess.run(args, capture_output=True, text=True, errors="replace",
                         encoding="utf-8").stdout
    out = (out or "").strip().splitlines()
    if not out:
        raise RuntimeError("快照没有输出")
    return json.loads(out[-1])


def last_segment(snap: dict) -> str:
    """面包屑末段的 FullPath = 当前目录。

    面包屑是个 ItemsControl，UIA 把它暴露成 List，每段的 Name 是
    `BreadcrumbSegment { Label = X, FullPath = Y }`（records 的 ToString）。
    """
    crumbs = snap.get("crumbs") or []
    if not crumbs:
        bc = list_of(snap, "BreadcrumbHost")
        crumbs = [r["name"] for r in (bc.get("rows") or [])]
    for c in reversed(crumbs):
        if "FullPath" in c:
            return c.split("FullPath = ")[-1].rstrip(" }")
    return ""


def list_of(snap: dict, list_id: str) -> dict:
    for l in snap.get("lists") or []:
        if l.get("id") == list_id:
            return l
    return {}


def click_row(row: dict) -> None:
    """点行的中间偏左（名字之外的空白也行，导航项整行可点）。"""
    x = row["x"] + min(60, max(10, row["w"] // 4))
    y = row["y"] + row["h"] // 2
    rs.click(x, y)


def double_click(x: int, y: int) -> None:
    """两次真实单击，间隔远小于系统双击时间（默认 500ms）。"""
    for _ in range(2):
        rs.click(x, y)
        time.sleep(0.06)


def wait_window(pid: int, timeout: float = 30.0) -> int:
    deadline = time.time() + timeout
    while time.time() < deadline:
        st = rs.probe(pid)
        if st.get("hwnd"):
            return int(st["hwnd"])
        time.sleep(0.4)
    raise RuntimeError("窗口没起来")


def stabilize() -> None:
    """这台机器上有 explorerX 之类的窗口会周期性抢前台。"""
    for _ in range(10):
        try:
            rs._assert_foreground()
            return
        except RuntimeError:
            time.sleep(0.3)


def refocus(pid: int) -> int:
    for _ in range(6):
        try:
            return rs.focus(pid)
        except RuntimeError:
            time.sleep(0.4)
    raise RuntimeError("抢不回前台")


def main() -> int:
    # 用户桌上可能正开着正式副本；互斥体是全局的，不加后缀就只会激活它，
    # 探针拿不到自己的窗口（表现为"窗口没起来"）。
    os.environ.setdefault("JIANLAN_INSTANCE_TAG", "navprobe")

    root = os.path.join(downloads_root(), SAMPLE)
    inner = os.path.join(root, "inner")
    shutil.rmtree(root, ignore_errors=True)
    os.makedirs(inner, exist_ok=True)
    with open(os.path.join(inner, "note.txt"), "w", encoding="utf-8") as f:
        f.write("probe\n")
    print(f"样本 {inner}")

    rs.stage_app()
    global PROC
    PROC = subprocess.Popen([rs.EXE, rs.DIR])
    pid = PROC.pid
    hwnd = wait_window(pid)
    rs._TARGET_PID, rs._TARGET_HWND, rs._MAIN_HWND = pid, hwnd, hwnd
    refocus(pid)
    stabilize()
    print(f"pid={pid} hwnd={hwnd}")

    # ── 1. 点「下载」 ────────────────────────────────────────────────
    snap = snapshot(pid, "QuickAccessList")
    nav = list_of(snap, "QuickAccessList")
    rows = nav.get("rows") or []
    dl = next((r for r in rows if r["name"] in ("下载", "Downloads")), None)
    if dl is None:
        print("  侧栏里没有「下载」：", [r["name"] for r in rows])
        return 2
    print(f"  「下载」行 y={dl['y']} sel={dl['sel']}")

    click_row(dl)
    time.sleep(1.0)
    snap = snapshot(pid)
    stage1 = last_segment(snap)
    check("点「下载」进入 Downloads 根", stage1.rstrip("\\").endswith("Downloads"), stage1)

    # ── 2. 双击进入自己的样本子目录 ───────────────────────────────────
    fl = list_of(snap, "FileList")
    target = next((r for r in (fl.get("rows") or []) if r["name"] == SAMPLE), None)
    if target is None:
        print("  列表里没有样本目录：", [r["name"] for r in (fl.get("rows") or [])][:10])
        return 2

    double_click(target["x"] + 80, target["y"] + target["h"] // 2)
    time.sleep(1.2)
    snap = snapshot(pid)
    stage2 = last_segment(snap)

    # 双击的第一下若有"再点一次改名"的挂起，会在这里留下编辑框——记录一下
    edit = "编辑态未知"
    st = rs.probe(pid)
    edit = f"editing={st.get('editing')}"
    print(f"  {edit}")

    check("双击进入子目录", stage2.rstrip("\\").endswith(SAMPLE), stage2)

    nav = list_of(snap, "QuickAccessList")
    still = [r["name"] for r in (nav.get("rows") or []) if r["sel"]]
    print(f"  进入子目录后侧栏高亮：{still}（资源管理器会保留「下载」高亮）")

    # ── 3. 再点一次「下载」：这里就是用户报的那一下 ────────────────────
    dl_row = next((r for r in (nav.get("rows") or []) if r["name"] in ("下载", "Downloads")), None)
    if dl_row is None:
        print("  侧栏里找不到「下载」了")
        return 2

    click_row(dl_row)
    time.sleep(1.2)
    snap = snapshot(pid)
    stage3 = last_segment(snap)
    back = stage3.rstrip("\\").endswith("Downloads")

    print(f"\n  路径：开始={stage1}\n        进子目录={stage2}\n        再点下载={stage3}")
    check("再点已高亮的「下载」能回到 Downloads 根", back, stage3)

    # ── 3.5 已经在根上时再点一次：什么也不该发生（不能报错、也不能往回跳） ──
    click_row(dl_row)
    time.sleep(0.7)
    stage35 = last_segment(snapshot(pid))
    check("在根上再点「下载」位置不变", stage35 == stage3, stage35)

    # ── 4. 地址栏跳到与侧栏无关的目录：高亮必须跟着走 ──
    # 这一条是针对另一半修复（导航完成后同步高亮）。没有它，侧栏会一直停在
    # 上一次点过的项上，于是"我明明在 D 盘，侧栏却亮着「下载」"。
    rs.press_combo(0x11, 0x4C)          # Ctrl+L 聚焦地址栏（会全选）
    time.sleep(0.35)
    elsewhere = r"D:\workspace"
    rs.type_text(elsewhere)
    rs.press("enter")
    time.sleep(1.3)

    snap = snapshot(pid)
    stage4 = last_segment(snap)
    nav = list_of(snap, "QuickAccessList")
    lit = [r["name"] for r in (nav.get("rows") or []) if r["sel"]]
    print(f"\n  地址栏到 {stage4}，侧栏高亮：{lit}")
    check("地址栏跳走后高亮不再指向「下载」",
          not any(n in ("下载", "Downloads") for n in lit), str(lit))
    check("地址栏确实跳到了目标目录", stage4.rstrip("\\") == elsewhere, stage4)

    print()
    if FAILS:
        print(f"{len(FAILS)} 项失败：{FAILS}")
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
