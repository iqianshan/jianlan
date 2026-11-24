"""验证「简览里 Ctrl+C / Ctrl+X，能否在资源管理器里 Ctrl+V 出来」。

只看代码不够：真正决定成败的是系统剪贴板里有没有 CF_HDROP，以及资源管理器
按 Ctrl+V 时到底拿没拿到东西。所以这里分两段验证：

  1. 用独立进程（这个脚本）去读剪贴板——这正是资源管理器的读取路径；
  2. 真的开一个资源管理器窗口，把焦点抢过来按 Ctrl+V，看文件落没落地。

第 2 段发的是真键盘输入，所以每次发送前都确认前台窗口就是目标窗口，
不是就跳过，绝不让按键落到用户正在用的窗口上。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import os
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rename_smoke as R  # noqa: E402

SRC_BIN = r"D:\workspace\FileExplorer\Jianlan\src\Jianlan.App\bin\Release\net9.0-windows10.0.17763.0"
WORK = os.path.join(os.environ["TEMP"], "jianlan-clip-test")
APP = os.path.join(WORK, "app")
FILES = os.path.join(WORK, "src")
# 目录名带进程号：资源管理器窗口标题就是目录名，收尾时按这个名字关窗口，
# 不会误关用户自己开着的同名文件夹
TARGET = os.path.join(WORK, f"dst-{os.getpid()}")

user32 = R.user32
shell32 = ctypes.WinDLL("shell32")
kernel32 = ctypes.WinDLL("kernel32")

# 句柄必须声明成 64 位：默认 restype 是 c_int，HDROP 会被截断成坏指针，
# DragQueryFileW 于是回报 0 条路径，看着像"没复制成功"。
user32.GetClipboardData.restype = ctypes.c_void_p
user32.GetClipboardData.argtypes = [wt.UINT]
user32.RegisterClipboardFormatW.restype = wt.UINT
user32.RegisterClipboardFormatW.argtypes = [wt.LPCWSTR]
shell32.DragQueryFileW.restype = wt.UINT
shell32.DragQueryFileW.argtypes = [ctypes.c_void_p, wt.UINT, ctypes.c_wchar_p, wt.UINT]
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]

CF_HDROP = 15
DROPEFFECT_COPY, DROPEFFECT_MOVE = 1, 2

VK_CONTROL = 0x11
VK_C, VK_V, VK_X = 0x43, 0x56, 0x58

failures: list[str] = []


def check(cond: bool, label: str, detail: str = "") -> None:
    print(("  OK   " if cond else "  FAIL ") + label + (f"  [{detail}]" if detail else ""))
    if not cond:
        failures.append(label)


# ── 剪贴板读取（与资源管理器同路径：OpenClipboard + CF_HDROP） ──────────────
def clipboard_formats() -> list:
    """列出剪贴板上现有的全部格式，用来分辨"空"和"有东西但没文件"。"""
    if not user32.OpenClipboard(None):
        return ["<打不开>"]
    try:
        out, fmt = [], 0
        while True:
            fmt = user32.EnumClipboardFormats(fmt)
            if not fmt:
                return out
            buf = ctypes.create_unicode_buffer(256)
            user32.GetClipboardFormatNameW(fmt, buf, 256)
            out.append(buf.value or f"#{fmt}")
    finally:
        user32.CloseClipboard()


def read_clipboard() -> dict:
    for _ in range(20):
        if user32.OpenClipboard(None):
            break
        time.sleep(0.1)
    else:
        raise RuntimeError("拿不到剪贴板（一直被别人占着）")

    try:
        out: dict = {"has_hdrop": bool(user32.IsClipboardFormatAvailable(CF_HDROP)), "paths": [], "effect": None}

        if out["has_hdrop"]:
            handle = user32.GetClipboardData(CF_HDROP)
            count = shell32.DragQueryFileW(ctypes.c_void_p(handle), 0xFFFFFFFF, None, 0)
            for i in range(count):
                length = shell32.DragQueryFileW(ctypes.c_void_p(handle), i, None, 0)
                buf = ctypes.create_unicode_buffer(length + 1)
                shell32.DragQueryFileW(ctypes.c_void_p(handle), i, buf, length + 1)
                out["paths"].append(buf.value)

        fmt = user32.RegisterClipboardFormatW("Preferred DropEffect")
        if fmt and user32.IsClipboardFormatAvailable(fmt):
            handle = user32.GetClipboardData(fmt)
            if handle:
                ptr = kernel32.GlobalLock(ctypes.c_void_p(handle))
                if ptr:
                    out["effect"] = ctypes.cast(ptr, ctypes.POINTER(ctypes.c_uint32))[0]
                    kernel32.GlobalUnlock(ctypes.c_void_p(handle))

        # 顺带读一下文本，失败时能分辨"复制的是文件"还是"复制的是某个输入框里的字"
        if user32.IsClipboardFormatAvailable(13):
            handle = user32.GetClipboardData(13)
            if handle:
                ptr = kernel32.GlobalLock(ctypes.c_void_p(handle))
                if ptr:
                    out["text"] = ctypes.wstring_at(ptr)
                    kernel32.GlobalUnlock(ctypes.c_void_p(handle))

        return out
    finally:
        user32.CloseClipboard()


# ── 资源管理器窗口 ────────────────────────────────────────────────────────
def find_explorer_window(title: str) -> int | None:
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        if cls.value != "CabinetWClass":
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        text = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, text, n + 1)
        if text.value == title or text.value.startswith(title + " - "):
            found.append(int(hwnd))
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else None


def focus_hwnd(hwnd: int) -> bool:
    """抢前台。先敲一下 SHIFT 让本进程成为"最后产生输入事件的进程"，否则必被拒。"""
    user32.ShowWindow(hwnd, 9)
    user32.keybd_event(R.VK_SHIFT, 0, 0, 0)
    user32.keybd_event(R.VK_SHIFT, 0, R.KEYEVENTF_KEYUP, 0)
    time.sleep(0.15)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.6)
    return user32.GetForegroundWindow() == hwnd


def point_of(pid: int, name: str) -> tuple[int, int]:
    """按名字重新量一次行坐标。

    列表内容会变（粘贴后多一行、剪切后少一行），行会整体上移或下移，
    拿开场那一份 rows 去点，点到的是隔壁。
    """
    for row in R.probe(pid).get("rows") or []:
        if row["name"] == name:
            return R.name_point(row)
    raise RuntimeError(f"{name} 不在列表里")


def close_explorer_windows(title: str) -> None:
    """关掉本次打开的窗口。只认自己那个目录名，绝不碰用户开着的窗口。"""
    for _ in range(3):
        hwnd = find_explorer_window(title)
        if not hwnd:
            return
        user32.PostMessageW(hwnd, 0x0010, 0, 0)   # WM_CLOSE
        time.sleep(0.6)


def double_click(x: int, y: int) -> None:
    R.click(x, y)
    time.sleep(0.08)
    R.click(x, y)


def focus_retry(pid: int, tries: int = 6) -> int:
    """抢前台偶发被拒：用户正在别的窗口里打字时，系统的防抢焦点规则会拦住。

    多试几次而不是一次就报错——真抢不到会抛，后面的 SendInput 就不会发出去。
    """
    last: Exception | None = None
    for _ in range(tries):
        try:
            return R.focus(pid)
        except RuntimeError as ex:
            last = ex
            time.sleep(1.0)
    raise last if last else RuntimeError("拿不到前台")


def pid_of(hwnd: int) -> int:
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def explorer_window_for(folder: str) -> int:
    title = os.path.basename(folder.rstrip("\\"))
    subprocess.Popen(["explorer.exe", folder])
    for _ in range(40):
        time.sleep(0.5)
        hwnd = find_explorer_window(title)
        if hwnd:
            return hwnd
    raise RuntimeError(f"没等到标题为 {title} 的资源管理器窗口")


# ── 主流程 ────────────────────────────────────────────────────────────────
def main() -> int:
    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(FILES, exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    shutil.copytree(SRC_BIN, APP, ignore=shutil.ignore_patterns("data"))
    open(os.path.join(APP, "portable.flag"), "w").close()

    for name in ("a.txt", "b.txt"):
        with open(os.path.join(FILES, name), "w", encoding="utf-8") as fh:
            fh.write(name)
    os.makedirs(os.path.join(FILES, "sub"), exist_ok=True)

    env = dict(os.environ, JIANLAN_INSTANCE_TAG=".clip")
    exe = os.path.join(APP, "Jianlan.exe")
    proc = subprocess.Popen([exe, FILES], cwd=APP, env=env)
    pid = proc.pid
    R._TARGET_PID = pid

    try:
        state: dict = {}
        for _ in range(40):
            state = R.probe(pid)
            if state.get("hwnd") and len(state.get("rows") or []) >= 2:
                break
            time.sleep(0.5)

        rows = state.get("rows") or []
        check(len(rows) >= 2, "程序启动并列出文件", f"{len(rows)} 行")
        if len(rows) < 2:
            return 1

        hwnd = focus_retry(pid)
        R._TARGET_HWND = hwnd
        R._MAIN_HWND = hwnd

        def row_of(name: str) -> dict:
            return next(r for r in rows if r["name"] == name)

        # ── 1. Ctrl+C 之后系统剪贴板里有什么 ─────────────────────────────
        print("\n[1] 简览里 Ctrl+C")
        R.click(*R.name_point(row_of("a.txt")))
        time.sleep(0.4)
        R.press_combo(VK_CONTROL, VK_C)
        time.sleep(0.8)

        clip = read_clipboard()
        check(clip["has_hdrop"], "系统剪贴板有 CF_HDROP（资源管理器认这个）")
        check(len(clip["paths"]) == 1, "剪贴板里是 1 个路径", str(clip["paths"]))
        check(
            bool(clip["paths"]) and os.path.normcase(clip["paths"][0]) == os.path.normcase(os.path.join(FILES, "a.txt")),
            "路径指向被复制的那个文件",
            clip["paths"][0] if clip["paths"] else "-",
        )
        check(clip["effect"] == DROPEFFECT_COPY, "Preferred DropEffect = Copy(1)", str(clip["effect"]))

        # ── 2. 到资源管理器里 Ctrl+V ─────────────────────────────────────
        print("\n[2] 资源管理器里 Ctrl+V（复制）")
        exp = explorer_window_for(TARGET)
        if not focus_hwnd(exp):
            check(False, "抢到资源管理器前台", "前台被别的窗口占着，跳过粘贴")
        else:
            R._TARGET_PID = pid_of(exp)
            R.press_combo(VK_CONTROL, VK_V)
            landed = os.path.join(TARGET, "a.txt")
            for _ in range(20):
                time.sleep(0.3)
                if os.path.isfile(landed):
                    break
            check(os.path.isfile(landed), "a.txt 粘到了资源管理器目录里", TARGET)
            check(os.path.isfile(os.path.join(FILES, "a.txt")), "复制后源文件还在")

        # ── 4. Ctrl+X 应当写成 Move ──────────────────────────────────────
        print("\n[3] 简览里 Ctrl+X")
        R._TARGET_PID = pid
        focus_retry(pid)
        R.click(*point_of(pid, "b.txt"))
        time.sleep(0.4)
        R.press_combo(VK_CONTROL, VK_X)
        time.sleep(0.8)

        clip = read_clipboard()
        check(bool(clip["paths"]), "剪切也写进了系统剪贴板", str(clip["paths"]))
        check(clip["effect"] == DROPEFFECT_MOVE, "Preferred DropEffect = Move(2)", str(clip["effect"]))

        # ── 5. 资源管理器里粘贴剪切的内容 ────────────────────────────────
        print("\n[4] 资源管理器里 Ctrl+V（剪切）")
        exp = find_explorer_window(os.path.basename(TARGET)) or explorer_window_for(TARGET)
        if not focus_hwnd(exp):
            check(False, "抢到资源管理器前台", "跳过")
        else:
            R._TARGET_PID = pid_of(exp)
            R.press_combo(VK_CONTROL, VK_V)
            landed = os.path.join(TARGET, "b.txt")
            for _ in range(20):
                time.sleep(0.3)
                if os.path.isfile(landed):
                    break
            check(os.path.isfile(landed), "b.txt 粘到了资源管理器目录里")
            check(not os.path.isfile(os.path.join(FILES, "b.txt")), "剪切后源文件已移走")

            # 刚粘好的文件在资源管理器里是选中态，顺势复制一份，验证反方向
            R.press_combo(VK_CONTROL, VK_C)
            time.sleep(0.8)

        # ── 5. 资源管理器复制 → 简览粘贴 ────────────────────────────────
        print("\n[5] 资源管理器 Ctrl+C → 简览 Ctrl+V")
        clip = read_clipboard()
        check(
            bool(clip["paths"]) and os.path.normcase(clip["paths"][0]) == os.path.normcase(os.path.join(TARGET, "b.txt")),
            "资源管理器把 b.txt 写进了剪贴板",
            str(clip["paths"]),
        )

        R._TARGET_PID = pid
        focus_retry(pid)
        R.press_combo(VK_CONTROL, VK_V)
        back = os.path.join(FILES, "b.txt")
        for _ in range(20):
            time.sleep(0.3)
            if os.path.isfile(back):
                break
        check(os.path.isfile(back), "从资源管理器复制的内容能粘进简览", back)

        # ── 6. 简览内部粘贴仍然正常（复制 a.txt，进子目录粘贴） ──────────
        print("\n[6] 简览内部复制粘贴未受影响")
        R._TARGET_PID = pid
        focus_retry(pid)
        R.click(*point_of(pid, "a.txt"))
        time.sleep(0.4)
        R.press_combo(VK_CONTROL, VK_C)
        time.sleep(0.6)

        double_click(*point_of(pid, "sub"))
        time.sleep(1.6)
        R.press_combo(VK_CONTROL, VK_V)
        time.sleep(3.0)

        landed = os.path.join(FILES, "sub", "a.txt")
        check(os.path.isfile(landed), "粘进了子目录", landed)
        check(os.path.isfile(os.path.join(FILES, "a.txt")), "原文件仍在（是复制不是移动）")

        # ── 7. 关掉简览之后剪贴板还在不在 ────────────────────────────────
        # 写剪贴板时开了 copy: true，内容当场落成系统里的实体数据；不开的话，
        # 对方粘贴时是在向简览要数据，程序一退剪贴板就空了。
        print("\n[7] 关闭程序后剪贴板仍可用")
        # 上一步粘贴完，那份文件是选中态。此时再点它的名字会进入就地改名
        # （按下即候选），Ctrl+C 就变成了复制输入框里的半个文件名。所以这里
        # 不再点，先 Esc 收掉可能存在的编辑态，直接对选中项按 Ctrl+C。
        R._TARGET_PID = pid
        focus_retry(pid)
        R.press("esc")
        time.sleep(0.3)
        R.press_combo(VK_CONTROL, VK_C)
        time.sleep(0.8)

        # 先确认这次复制确实发生了，否则"退出后没了"会被误判成 flush 失效
        clip = read_clipboard()
        check(
            bool(clip["paths"]) and clip["paths"][0].endswith(os.path.join("sub", "a.txt")),
            "退出前剪贴板里有那个文件",
            str(clip["paths"]),
        )

        # 走正常关闭（发 WM_CLOSE），而不是直接结束进程
        user32.PostMessageW(wt.HWND(hwnd), 0x0010, 0, 0)
        for _ in range(30):
            time.sleep(0.5)
            if proc.poll() is not None:
                break
        time.sleep(1.0)

        clip = read_clipboard()
        check(
            bool(clip["paths"]) and clip["paths"][0].endswith(os.path.join("sub", "a.txt")),
            "关闭后剪贴板里仍有那个文件",
            str(clip["paths"]),
        )

    finally:
        close_explorer_windows(os.path.basename(TARGET))
        try:
            if proc.poll() is None:
                proc.terminate()
                proc.wait(timeout=10)
        except Exception:
            pass

    print()
    if failures:
        print(f"失败 {len(failures)} 项：")
        for f in failures:
            print("  - " + f)
        return 1

    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
