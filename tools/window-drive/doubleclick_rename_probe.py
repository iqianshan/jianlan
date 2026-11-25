"""快速双击打开文件时，列表是否同时进了就地改名。

用户报的现象：双击某个文件，默认程序已经起来了，列表里那一行却停在编辑态
（绿色边框、名字可编辑）。这两个动作本应互斥——打开文件不该顺带把行变成输入框。

脚本按两种真实手势各打一遍：
  A. 空白处清掉选中，直接双击未选中的行（最常见的"双击打开"）
  B. 先点一下选中它（等过双击时间），再双击同一个名字（"上次点过它"的手感）

判据只有一条：双击之后列表里有没有 Edit 控件（UIA 的 editing 字段）。
同时记录双击前后的 notepad 进程数，确认"文件确实被打开了"——
只有两个动作同时成立才是要修的那个 bug。

用法：python doubleclick_rename_probe.py
换程序用 JIANLAN_EXE_DIR=<含 Jianlan.exe 的目录>。
"""

from __future__ import annotations

import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import rename_smoke as rs

GAP = 0.06          # 双击内两下的间隔（真实快速双击在 80-150ms 之间）
SETTLE = 1.2        # 双击后等默认程序起来 / 编辑框出现


def dbl_click(x: int, y: int) -> None:
    """真两下左键。不用 click() 连调：那样中间会多做一次 SetCursorPos 与 50ms 等待，
    间隔被拉到 130ms 以上，慢速双击的临界情况就测不到了。"""
    rs._assert_foreground()
    rs.user32.SetCursorPos(int(x), int(y))
    time.sleep(0.05)

    for i in range(2):
        rs._send(rs.INPUT(type=rs.INPUT_MOUSE,
                          u=rs._U(mi=rs.MOUSEINPUT(0, 0, 0, rs.MOUSEEVENTF_LEFTDOWN, 0, None))))
        time.sleep(0.03)
        rs._send(rs.INPUT(type=rs.INPUT_MOUSE,
                          u=rs._U(mi=rs.MOUSEINPUT(0, 0, 0, rs.MOUSEEVENTF_LEFTUP, 0, None))))
        if i == 0:
            time.sleep(max(0.0, GAP - 0.03))


def blank_point(state: dict) -> tuple[int, int]:
    """列表底部的空白：样本只有两个文件，下面全是空的。"""
    box = state["list"]
    return box["x"] + box["w"] // 2, box["y"] + box["h"] - 16


def notepad_pids() -> set[int]:
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq notepad.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True, errors="replace")
    pids = set()
    for line in out.stdout.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[0].lower() == "notepad.exe":
            try:
                pids.add(int(parts[1]))
            except ValueError:
                pass
    return pids


def kill_new_notepads(before: set[int]) -> int:
    new = notepad_pids() - before
    for pid in new:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
    return len(new)


def refocus(pid: int) -> int:
    """记事本被杀掉的那一瞬间前台还指着它，SetForegroundWindow 会被系统拒绝。
    等它真的消失再抢，成功为止。"""
    for _ in range(6):
        try:
            return rs.focus(pid)
        except RuntimeError:
            time.sleep(0.4)
    raise RuntimeError("抢不回前台")


def find_row(state: dict, name: str) -> dict:
    for row in state["rows"]:
        if row.get("name") == name:
            return row
    raise SystemExit(f"没找到行 {name}（探针看到 {[r.get('name') for r in state['rows']]}）")


def main() -> int:
    rs.stage_app()
    rs.seed()

    before_notepad = notepad_pids()
    proc = rs.launch()
    pid = proc.pid
    failures: list[str] = []

    def check(label: str, ok: bool, detail: str = "") -> None:
        print(("  ✓ " if ok else "  ✗ ") + label + (f"（{detail}）" if detail else ""), flush=True)
        if not ok:
            failures.append(label + (f"（{detail}）" if detail else ""))

    def state() -> dict:
        return rs.probe(pid)

    try:
        # ── A：未选中状态直接双击 ────────────────────────────────────────────
        refocus(pid)
        st = state()
        bx, by = blank_point(st)
        rs.click(bx, by)
        time.sleep(0.3)

        row = find_row(state(), "b.txt")
        x, y = rs.name_point(row)
        dbl_click(x, y)
        time.sleep(SETTLE)

        # 先收掉记事本再探：它一起来就盖在主窗上，UIA 有时会连主窗都枚举不到
        opened_a = kill_new_notepads(before_notepad)
        refocus(pid)

        st = state()
        check("A 未选中双击：列表没有进入编辑态",
              st.get("ok") is True and st.get("editing") is False,
              f"ok={st.get('ok')} editing={st.get('editing')} text={st.get('text')!r}")
        check("A 双击打开了文件", opened_a > 0, f"新进程 {opened_a} 个")

        # 关掉可能被打开的记事本，避免它抢前台影响 B
        time.sleep(0.4)
        kill_new_notepads(before_notepad)
        refocus(pid)
        rs.press("esc")
        time.sleep(0.2)

        # ── A2：已选中项只点一下，等过双击时间应进入编辑 ────────────────────
        #
        # "再点一次改名"是设计行为，但必须等过一个双击时间再落地：
        # 修复前它在抬起的瞬间就进编辑，一次双击的第一下同样满足条件，
        # 于是打开文件与编辑框同时出现，第二下还常常落进编辑框。
        st = state()
        row = find_row(st, "b.txt")
        x, y = rs.name_point(row)
        rs.click(x, y)          # 先选中
        time.sleep(0.9)         # 越过双击时间，下一次点击是新的序列

        row = find_row(state(), "b.txt")
        check("A2 前置：b.txt 已被选中", bool(row.get("selected")))

        x, y = rs.name_point(row)
        rs.click(x, y)          # 对已选中项再点一下
        time.sleep(0.9)         # 等过双击窗口

        st = state()
        check("A2 单击已选中项：等过双击时间后进入编辑",
              st.get("editing") is True, f"editing={st.get('editing')} text={st.get('text')!r}")

        refocus(pid)
        rs.press("esc")
        time.sleep(0.3)

        # ── B：先选中、等过双击时间、再双击 ─────────────────────────────────
        st = state()
        row = find_row(st, "a.txt")
        x, y = rs.name_point(row)
        rs.click(x, y)
        time.sleep(0.9)          # 超过系统双击时间，保证下一次点击是新的序列

        st = state()
        row = find_row(st, "a.txt")
        check("B 前置：a.txt 已被选中", bool(row.get("selected")))

        x, y = rs.name_point(row)
        dbl_click(x, y)
        time.sleep(SETTLE)

        st = state()
        check("B 已选中项双击：列表没有进入编辑态", st.get("editing") is not True,
              f"editing={st.get('editing')} text={st.get('text')!r}")

        time.sleep(0.4)
        opened = kill_new_notepads(before_notepad)
        check("B 双击确实打开了文件（见过 notepad 新进程）", opened > 0, f"新进程 {opened} 个")

        return 0 if not failures else 1
    finally:
        try:
            user32 = rs.user32
            user32.PostMessageW(rs._MAIN_HWND, 0x0010, 0, 0)   # WM_CLOSE
        except Exception:
            pass
        try:
            proc.wait(timeout=10)
        except Exception:
            proc.kill()
        kill_new_notepads(before_notepad)


if __name__ == "__main__":
    raise SystemExit(main())
