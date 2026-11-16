r"""模态窗复用探针：确认"连续两次弹窗"落在同一个 HWND 上。

背景见 docs/ac03-stability-acceptance.md §6.14：AC-03 的原生崩溃来自 TSF 为每个
拿到焦点的 HWND 建/毁输入上下文。改法是把 ConfirmWindow 与 TextPromptWindow 改成
常驻单例、只 Hide/Show。这个探针不看崩溃（那是长跑的事），只回答一个问题：

    第二次弹出的输入框，还是第一次那个 HWND 吗？

是 → 复用生效，链路上不再有反复的建/毁；否 → 改造没接上，长跑没有意义。

用法：
    python tools/ac03/probe_modal.py
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


def visible_shells(pid: int) -> set[int]:
    """可见的顶层 WPF 窗口（HwndWrapper），模态窗与主窗都在其中。"""
    return {w["hwnd"] for w in drv.list_windows(pid) if w["visible"] and "HwndWrapper" in w["cls"]}


def snapshot(pid: int) -> list[tuple[int, str, str]]:
    return [(w["hwnd"], w["cls"], w["title"][:28]) for w in drv.list_windows(pid) if w["visible"]]


def main() -> int:
    if not EXE.exists():
        print(f"找不到 {EXE}", file=sys.stderr)
        return 2

    drv.prepare_work(WORK, EXE)
    sandbox = WORK / "sandbox"

    proc = subprocess.Popen([str(WORK / "app" / "Jianlan.exe")], cwd=str(WORK / "app"))
    print(f"启动 pid={proc.pid}")

    hwnd = 0
    for _ in range(120):
        hwnd = drv.main_window(proc.pid)
        if hwnd:
            break
        time.sleep(0.5)
    if not hwnd:
        print("窗口没出来", file=sys.stderr)
        proc.kill()
        return 2

    time.sleep(2.0)
    drv.focus(hwnd)
    # 首次启动恒开主页（FR-1.1），主页上没有文件标签，Ctrl+L 不响应——
    # 先 Ctrl+T 开新标签（按 newTabMode=path 落到沙箱），与驱动 drop_to_sandbox 同一走法。
    if sandbox.name.lower() not in drv.window_title(hwnd).lower():
        drv.chord("ctrl", "t")
        time.sleep(2.5)
    if sandbox.name.lower() not in drv.window_title(hwnd).lower():
        drv.chord("ctrl", "l")
        drv.type_text(str(sandbox))
        drv.key("enter")
        time.sleep(2.0)
    print("当前目录：", drv.window_title(hwnd))
    if sandbox.name.lower() not in drv.window_title(hwnd).lower():
        print("没能进到沙箱目录，探针中止", file=sys.stderr)
        proc.kill()
        return 2
    time.sleep(1.0)

    base = visible_shells(proc.pid)
    print(f"基线可见 WPF 窗口：{len(base)} 个")

    rounds = []

    def open_prompt(label: str, opener, single: bool = False) -> int | None:
        drv.focus(hwnd)
        # 列表里得有选中项，否则 F2 / Alt+Enter 找不到操作对象，什么都不弹，
        # 探针会误判成"窗口没复用"。重命名只接受单选（全选后 F2 是禁用的），
        # 所以单选用 Home 落第一行，多选用 Ctrl+A。
        if single:
            drv.key("down")
        else:
            drv.chord("ctrl", "a")
            drv.key("down")
        time.sleep(0.3)
        opener()
        time.sleep(1.0)
        now = visible_shells(proc.pid)
        extra = sorted(now - base)
        print(f"  [{label}] 新增可见窗口：{extra}")
        for h, cls, title in snapshot(proc.pid):
            if h in extra:
                print(f"      hwnd={h} cls={cls} title={title}")
        drv.key("esc")
        time.sleep(0.6)
        after = visible_shells(proc.pid)
        if after != base:
            print(f"  [{label}] Esc 后可见窗口未回到基线：{sorted(after)}")
        return extra[0] if len(extra) == 1 else None

    print("第 1 次：新建文件夹（Ctrl+Shift+N）")
    rounds.append(open_prompt("新建#1", lambda: drv.chord("ctrl", "shift", "n")))
    print("第 2 次：新建文件夹（Ctrl+Shift+N）")
    rounds.append(open_prompt("新建#2", lambda: drv.chord("ctrl", "shift", "n")))
    print("第 3 次：重命名（F2）")
    rounds.append(open_prompt("重命名", lambda: drv.key("f2"), single=True))
    print("第 4 次：属性（Alt+Enter）")
    rounds.append(open_prompt("属性#1", lambda: drv.chord("alt", "enter")))
    print("第 5 次：属性（Alt+Enter）")
    rounds.append(open_prompt("属性#2", lambda: drv.chord("alt", "enter")))

    print()
    print("结果：", rounds)
    prompt_pair = rounds[0:2]
    rename_hwnd = rounds[2]
    confirm_pair = rounds[3:5]

    ok = True
    if None in prompt_pair or prompt_pair[0] != prompt_pair[1]:
        print("[FAIL] 输入框没有复用同一个 HWND")
        ok = False
    else:
        print(f"[PASS] 输入框复用 HWND {prompt_pair[0]}")
    if rename_hwnd is None:
        print("[WARN] F2 没弹出窗口，重命名路径未取到样本")
    elif rename_hwnd != prompt_pair[0]:
        print(f"[FAIL] 重命名用了另一个 HWND {rename_hwnd}（应为 {prompt_pair[0]}）")
        ok = False
    else:
        print("[PASS] 重命名与新建共用同一个输入框 HWND")
    if None in confirm_pair or confirm_pair[0] != confirm_pair[1]:
        print("[FAIL] 确认窗没有复用同一个 HWND")
        ok = False
    else:
        print(f"[PASS] 确认窗复用 HWND {confirm_pair[0]}")
    if prompt_pair[0] is not None and prompt_pair[0] == confirm_pair[0]:
        print("[FAIL] 两个窗口共用了同一个 HWND，探针不可信")
        ok = False

    # 退出检查：上面已经把两扇常驻模态窗都建出来了（现在都隐藏着），此时关主窗，
    # 进程必须在几秒内退出。默认的 ShutdownMode=OnLastWindowClose 会把这两扇看不见的
    # 窗也算作窗口，主窗关掉后进程被吊着——用户点关闭没有任何反应（§6.14 的回归点）。
    print()
    print("退出检查：关闭主窗")
    drv.focus(hwnd)
    drv.user32.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE
    gone = False
    for _ in range(24):
        if proc.poll() is not None:
            gone = True
            break
        time.sleep(0.25)
    if gone:
        print(f"[PASS] 主窗关闭后进程退出，exit={proc.poll()}")
    else:
        print("[FAIL] 主窗关闭 6 秒后进程仍在（常驻隐藏窗把进程吊住了）")
        ok = False
        proc.kill()

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
