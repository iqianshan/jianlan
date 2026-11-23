"""就地改名的冒烟验证：真的点两下、真的敲字、真的落盘。

为什么要有这个脚本：这一版改的是鼠标时序与焦点转移，光看代码断不出来
"第二次点击到底进没进编辑态"。判定规则本身有单测（RenameGesturePolicyTests），
但"点下去 → 编辑框出现 → 回车 → 磁盘上换了名字"这条链只能在真窗口上跑。

用法：python rename_smoke.py
前提：交互式桌面（SendInput 需要），且没有别的窗口抢焦点。脚本会把窗口抢到前台，
      跑的过程中会真的移动鼠标——别在它跑的时候干别的活。

默认从仓库根的 publish\\ 取程序，拷一份到 %TEMP% 里跑（见 stage_app）。
换程序用环境变量：JIANLAN_EXE_DIR=<含 Jianlan.exe 的目录>。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)

# 必须先声明 DPI 感知：UIA 给的矩形是物理像素，而没声明时 SetCursorPos 收的是
# 逻辑像素，125% 缩放下会被再放大一次，点击落到右下 25% 的位置——
# 表现就是"点到了列表外面"，一次都选不中。
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))   # PER_MONITOR_AWARE_V2
except AttributeError:
    user32.SetProcessDPIAware()

TOOLS = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(TOOLS))
UIA = os.path.join(TOOLS, "uia_rename_probe.ps1")

SRC_APP = os.environ.get("JIANLAN_EXE_DIR") or os.path.join(ROOT, "publish")
WORK = os.path.join(tempfile.gettempdir(), "jianlan-rename-smoke")
EXE = os.path.join(WORK, "app", "Jianlan.exe")
DIR = os.path.join(WORK, "files")

# ── SendInput ───────────────────────────────────────────────────────────────
INPUT_MOUSE, INPUT_KEYBOARD = 0, 1
KEYEVENTF_KEYUP, KEYEVENTF_UNICODE = 0x0002, 0x0004
MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP = 0x0002, 0x0004
VK_SHIFT = 0x10

# 点击必须落在自己的窗口上：一旦前台被别人抢走，SendInput 会点进别人家的界面，
# 那就是在用户的桌面上乱点。每个 click/press 之前都核对一次，宁可中途中止。
_TARGET_HWND = 0


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_void_p)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_void_p)]


class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def _send(*items: INPUT) -> None:
    arr = (INPUT * len(items))(*items)
    user32.SendInput(len(items), arr, ctypes.sizeof(INPUT))


def _assert_foreground() -> None:
    """确认前台还是我们的窗口，否则后面的合成输入会打到别人身上。"""
    fg = user32.GetForegroundWindow()
    if _TARGET_HWND and fg != _TARGET_HWND:
        raise RuntimeError(f"前台窗口被抢走（{fg} != {_TARGET_HWND}），中止以免误点别的程序")


def click(x: int, y: int) -> None:
    _assert_foreground()
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.05)
    _send(INPUT(type=INPUT_MOUSE, u=_U(mi=MOUSEINPUT(0, 0, 0, MOUSEEVENTF_LEFTDOWN, 0, None))))
    time.sleep(0.03)
    _send(INPUT(type=INPUT_MOUSE, u=_U(mi=MOUSEINPUT(0, 0, 0, MOUSEEVENTF_LEFTUP, 0, None))))


VK = {"enter": 0x0D, "esc": 0x1B, "f2": 0x71, "f5": 0x74}


def press(name: str) -> None:
    _assert_foreground()
    vk = VK[name]
    _send(
        INPUT(type=INPUT_KEYBOARD, u=_U(ki=KEYBDINPUT(vk, 0, 0, 0, None))),
        INPUT(type=INPUT_KEYBOARD, u=_U(ki=KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, None))),
    )


def type_text(text: str) -> None:
    _assert_foreground()
    items = []
    for ch in text:
        items.append(INPUT(type=INPUT_KEYBOARD, u=_U(ki=KEYBDINPUT(0, ord(ch), KEYEVENTF_UNICODE, 0, None))))
        items.append(INPUT(type=INPUT_KEYBOARD, u=_U(ki=KEYBDINPUT(0, ord(ch), KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, 0, None))))
    _send(*items)


VK_CONTROL, VK_SHIFT = 0x11, 0x10


def press_combo(*keys: int) -> None:
    """按组合键（修饰键先按下、再逆序松开）。"""
    _assert_foreground()
    items = [INPUT(type=INPUT_KEYBOARD, u=_U(ki=KEYBDINPUT(vk, 0, 0, 0, None))) for vk in keys]
    items += [INPUT(type=INPUT_KEYBOARD, u=_U(ki=KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, None)))
              for vk in reversed(keys)]
    _send(*items)


# ── 进程与窗口 ──────────────────────────────────────────────────────────────
def launch() -> subprocess.Popen:
    global _TARGET_HWND
    proc = subprocess.Popen([EXE, DIR])
    time.sleep(4.0)
    _TARGET_HWND = focus(proc.pid)
    return proc


def focus(pid: int) -> int:
    """把目标窗口提到前台，返回它的 hwnd。

    必须显式做：脚本是从别的窗口（IDE、终端）里跑起来的，新进程的窗口可能开在后面，
    SendInput 的点击与按键只会落到当时前台的那个窗口上——表现是"点了半天没反应"，
    实际是点在了别人身上。

    直接 SetForegroundWindow 会被系统拒绝（只有已经持有前台的进程才被允许抢前台），
    实测点了半天其实落在旁边一个记事本上。先敲一下 SHIFT 再抢：
    这样本进程就成了"最后产生输入事件的进程"，前台权限的限制随之解除。
    这条路 Windows 10/11 上仍然有效，置顶再取消置顶的老办法已经不行了。

    用 SHIFT 而不是更常见的 ALT：单独一下 ALT 抬起会被 WPF 的菜单条当成
    "进入菜单模式"，焦点从列表跑到菜单上，后面的点击就全白点了。
    """
    state = probe(pid)
    hwnd = state.get("hwnd")
    if not hwnd:
        raise RuntimeError("没找到窗口")

    hwnd = int(hwnd)
    user32.ShowWindow(hwnd, 9)          # SW_RESTORE

    user32.keybd_event(VK_SHIFT, 0, 0, 0)
    user32.keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.15)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.5)

    if user32.GetForegroundWindow() != hwnd:
        raise RuntimeError(f"拿不到前台窗口（前台仍是 {user32.GetForegroundWindow()}）")

    return hwnd


def probe(pid: int) -> dict:
    out = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", UIA, "-TargetPid", str(pid)],
        capture_output=True,
    )
    # 5.1 宿主按控制台代码页输出，中文机器上是 GBK：先按 utf-8 试，失败再退回 gbk，
    # 两种都失败时只取 ASCII 部分——探针的 JSON 结构是纯 ASCII，坏字符只可能出现在 stderr。
    raw = out.stdout or b""
    for enc in ("utf-8", "gbk", "latin-1"):
        try:
            text = raw.decode(enc).strip().splitlines()
            break
        except UnicodeDecodeError:
            continue
    else:
        return {"ok": False, "error": "decode"}

    if not text:
        return {"ok": False, "error": "no-output", "stderr": str(out.stderr[:200])}
    return json.loads(text[-1])


def name_point(row: dict) -> tuple[int, int]:
    """点击点取"文件名文字"的中心。

    资源管理器只在点中文件名文字本身时才开始改名，点图标、点名称列右侧的空白
    都只是选中。之前按"整行左边 + 70"去猜，实际落到了行容器的 Border 上，
    手势一次都没成立——所以这里直接用探针量出来的文字矩形。短名字（a.txt）
    只有二十几个像素宽，偏一点就落到外面了。
    """
    if row.get("nw"):
        return row["nx"] + row["nw"] // 2, row["ny"] + row["nh"] // 2
    return row["x"] + 70, row["y"] + row["h"] // 2


def stage_app() -> None:
    """把一份发布产物拷到临时目录里再跑，每次都是干净的一份。

    必须在副本里跑：脚本会改名、新建、删文件，而且便携模式把设置与日志写进
    exe\\data\\。直接在 publish\\ 或者用户解压出来的目录里跑，会把这些东西一起搅了。

    拷的时候跳过 `data`：从用户解压出来的目录取程序时，那份目录里已经有他的会话，
    启动会把上次打开的目录恢复出来，而不是我们传进去的样本目录。
    然后再写 portable.flag——publish\\ 里没有它（那边的程序跑起来会读 %LOCALAPPDATA%
    里的真实配置），而临时副本必须是便携的。
    """
    if not os.path.isfile(os.path.join(SRC_APP, "Jianlan.exe")):
        raise SystemExit(f"找不到程序：{SRC_APP}\\Jianlan.exe（先跑 tools/release/publish.ps1，"
                         f"或用 JIANLAN_EXE_DIR 指定目录）")

    app = os.path.dirname(EXE)
    shutil.rmtree(app, ignore_errors=True)
    shutil.copytree(SRC_APP, app, ignore=shutil.ignore_patterns("data"))
    with open(os.path.join(app, "portable.flag"), "w", encoding="utf-8"):
        pass


def seed() -> None:
    """铺一份固定样本。每次跑都会把一个文件改名、删掉，所以不能指望上一次留下的目录还齐全。"""
    os.makedirs(DIR, exist_ok=True)
    os.makedirs(os.path.join(DIR, "材料"), exist_ok=True)
    for name in ("a.txt", "b.txt"):
        path = os.path.join(DIR, name)
        if not os.path.exists(path):
            with open(path, "w", encoding="utf-8") as handle:
                handle.write("")


def main() -> int:
    stage_app()
    seed()

    proc = launch()
    pid = proc.pid
    failures = []

    def check(label: str, ok: bool, detail: str = "") -> bool:
        """逐步打印结果。失败不能攒到最后才说——中途崩溃时前面几步的结论就丢了。"""
        print(("  ✓ " if ok else "  ✗ ") + label + (f"（{detail}）" if detail else ""), flush=True)
        if not ok:
            failures.append(label + (f"（{detail}）" if detail else ""))
        return ok

    def ensure_focus() -> None:
        """每一步之前重新确认前台。合成的点击只会落在前台窗口上，跑长一点就会被别的程序抢走。"""
        global _TARGET_HWND
        _TARGET_HWND = focus(pid)

    try:
        state = probe(pid)
        if not state.get("ok"):
            print("探针失败:", state)
            return 3

        # 冷启动时窗口先出来、列表后填，4 秒不一定够：目录枚举完才画行
        rows = state["rows"]
        for _ in range(20):
            if rows:
                break
            time.sleep(0.5)
            state = probe(pid)
            rows = state.get("rows", [])
        print("条目:", [r["name"] for r in rows])
        if not rows:
            print("列表是空的，无法继续：", DIR)
            return 4

        target = next(r for r in rows if r["name"] == "a.txt")
        cx, cy = name_point(target)

        # 1) 第一次点击：只选中
        ensure_focus()
        click(cx, cy)
        time.sleep(0.9)
        check("第一次点击只选中、不进编辑态", not probe(pid).get("editing"))

        # 2) 隔一会儿再点名称：进编辑
        ensure_focus()
        click(cx, cy)
        time.sleep(0.6)
        after = probe(pid)
        if check("再点一次名称进入编辑态", bool(after.get("editing")), f"文本={after.get('text')}"):
            check("编辑框里是完整原名", after.get("text") == "a.txt", repr(after.get("text")))

        # 3) 只敲词干、不敲扩展名，回车。预选的就是词干（"a"），扩展名留在后面，
        #    所以结果应该是 renamed.txt 而不是 renamed.txt.txt——这条同时验证了
        #    "进编辑时只选中扩展名之前那一段"的规则。
        ensure_focus()
        type_text("renamed")
        time.sleep(0.25)
        press("enter")
        time.sleep(1.8)

        if check("回车后磁盘上出现 renamed.txt", os.path.exists(os.path.join(DIR, "renamed.txt"))):
            check("原文件 a.txt 已不存在", not os.path.exists(os.path.join(DIR, "a.txt")))

        # 提交后不跳转：被改名的那一项仍是选中项（用户要求"回车后手不离开这一行"）
        rows_now = probe(pid).get("rows", [])
        kept = next((r for r in rows_now if r["name"] == "renamed.txt"), None)
        check("提交后仍选中被改名的那一项", bool(kept and kept.get("selected")),
              "选中=" + str([r["name"] for r in rows_now if r.get("selected")]))

        # 4) F2 再进一次，按 Esc 放弃
        row = kept
        if check("刷新后列表里是改名后的行", row is not None):
            rx, ry = name_point(row)
            ensure_focus()
            click(rx, ry)
            time.sleep(0.4)
            press("f2")
            time.sleep(0.6)
            if check("F2 进入编辑态", bool(probe(pid).get("editing"))):
                ensure_focus()
                type_text("should-not-apply")
                time.sleep(0.25)
                press("esc")
                time.sleep(1.4)
                check("Esc 放弃：原名还在", os.path.exists(os.path.join(DIR, "renamed.txt")))
                check("Esc 放弃：磁盘未改", not os.path.exists(os.path.join(DIR, "should-not-apply")))

        # 5) 快速双击仍是打开，不该进编辑
        row = next((r for r in probe(pid).get("rows", []) if r["name"] == "b.txt"), None)
        if row:
            bx, by = name_point(row)
            ensure_focus()
            click(bx, by)
            time.sleep(0.08)
            click(bx, by)
            time.sleep(0.8)
            check("快速双击不进编辑态", not probe(pid).get("editing"))

        # 6) Ctrl+Shift+N 新建文件夹 → 直接进编辑态（与资源管理器一致）
        ensure_focus()
        press_combo(VK_CONTROL, VK_SHIFT, 0x4E)
        time.sleep(1.0)
        fresh = probe(pid)
        if check("新建文件夹后直接进入编辑态", bool(fresh.get("editing")), f"文本={fresh.get('text')}"):
            ensure_focus()
            press("esc")
            time.sleep(1.2)
            names = {r["name"] for r in probe(pid).get("rows", [])}
            created = names - {"材料", "b.txt", "renamed.txt"}
            if check("Esc 后新建的文件夹保留默认名", len(created) == 1, str(created)):
                os.rmdir(os.path.join(DIR, next(iter(created))))

        # 7) 改成已存在的名字 → 留在编辑态并提示，磁盘不动
        row = next((r for r in probe(pid).get("rows", []) if r["name"] == "renamed.txt"), None)
        if row:
            rx, ry = name_point(row)
            ensure_focus()
            click(rx, ry)
            time.sleep(0.4)
            press("f2")
            time.sleep(0.5)
            ensure_focus()
            type_text("b")            # 词干被选中，结果就是 b.txt，与已有文件同名
            time.sleep(0.2)
            press("enter")
            time.sleep(0.6)
            check("重名被拒后仍留在编辑态", bool(probe(pid).get("editing")))
            check("重名被拒后磁盘未变", os.path.exists(os.path.join(DIR, "renamed.txt")))

            # 8) 点另一行 → 按提交处理，并让被点的那一行成为选中项
            #    校验失败时编辑框内容被全选（红框 + 全选），所以这里敲的是带扩展名的全名
            ensure_focus()
            type_text("zzz.txt")
            time.sleep(0.2)
            other = next((r for r in probe(pid).get("rows", []) if r["name"] == "b.txt"), None)
            if check("找得到 b.txt 这一行", other is not None):
                ox, oy = name_point(other)
                ensure_focus()
                click(ox, oy)
                time.sleep(1.6)
                after = probe(pid)
                check("点别处提交：磁盘上出现 zzz.txt",
                      os.path.exists(os.path.join(DIR, "zzz.txt")))
                check("点别处提交：编辑态已结束", not after.get("editing"))
                check("点别处之后选中的是被点的那一行",
                      any(r["name"] == "b.txt" and r.get("selected") for r in after.get("rows", [])),
                      "选中=" + str([r["name"] for r in after.get("rows", []) if r.get("selected")]))
                os.remove(os.path.join(DIR, "zzz.txt"))

    except RuntimeError as error:
        print("  ! 中止:", error, flush=True)
        failures.append(str(error))

    finally:
        # 关窗口：优先 WM_CLOSE，留时间保存会话
        hwnd = probe(pid).get("hwnd")
        if hwnd:
            user32.PostMessageW(int(hwnd), 0x0010, 0, 0)
        time.sleep(1.5)
        if proc.poll() is None:
            proc.kill()

    print()
    if failures:
        for f in failures:
            print("✗", f)
        return 1

    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
