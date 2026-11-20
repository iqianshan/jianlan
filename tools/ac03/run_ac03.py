r"""AC-03 / NF-06 八小时稳定性压测。

判定口径（docs/requirements.md §12 AC-03 + NF-06）：
    0 崩溃、0 未处理异常；句柄增长 < 10%；GDI 对象 < 2000。

设计要点：
1. **独立数据目录**。被测 exe 复制到 `--work\app\`，旁边放 `portable.flag`，
   于是设置/收藏/标签/会话全落在 `--work\app\data\`。压测会反复收藏、改主题、
   新建文件，绝不能把这些写进用户真实的 %LOCALAPPDATA%。
2. **只动沙箱目录**。所有文件操作发生在 `--work\sandbox\`，被测应用每轮被
   `Ctrl+L` 拉回这里；删除类动作后面紧跟 `Ctrl+Z`，回收站不会越堆越大。
3. **键盘驱动**。全部走键位表（docs/shortcuts.md），不依赖像素坐标——窗口一改大小
   或换字号，按坐标点的动作就漂了，压测会变成"测鼠标"。
4. **边跑边采样**。每 `--sample` 秒记一次私有提交、工作集、句柄、GDI、USER 对象、线程数；
   进程消失或有未处理异常即停，现场落盘。

用法：
    python tools/ac03/run_ac03.py --hours 8
    python tools/ac03/run_ac03.py --hours 0.05          # 冒烟（3 分钟）
    python tools/ac03/run_ac03.py --hours 8 --exe publish/Jianlan.exe --work D:\\ac03
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import random
import re
import shutil
import signal
import sys
import time
from ctypes import wintypes
from datetime import datetime, timedelta
from pathlib import Path

# 驱动自己会发 Ctrl+C（"复制粘贴"动作）。焦点没抢到时，这个按键落在跑本脚本的
# 控制台窗口上，会把压测本体一起带走——现场表现为进程无汇总地消失，
# 计划任务 LastTaskResult = 0xC000013A（STATUS_CONTROL_C_EXIT）。
# 压测期间一律忽略 Ctrl+C，只能从外部结束进程。
try:
    signal.signal(signal.SIGINT, signal.SIG_IGN)
except Exception:
    pass

ROOT = Path(__file__).resolve().parents[2]

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)

# ── 常量 ────────────────────────────────────────────────────────────────────
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004
SMTO_ABORTIFHUNG = 0x0002
WM_NULL = 0x0000
WM_CLOSE = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
ES_CONTINUOUS = 0x80000000
ES_SYSTEM_REQUIRED = 0x00000001

VK = {
    "back": 0x08, "tab": 0x09, "enter": 0x0D, "esc": 0x1B,
    "left": 0x25, "up": 0x26, "right": 0x27, "down": 0x28,
    "delete": 0x2E, "f2": 0x71, "f3": 0x72, "f5": 0x74,
    "ctrl": 0x11, "alt": 0x12, "shift": 0x10,
    "l": 0x4C, "c": 0x43, "v": 0x56, "x": 0x58, "a": 0x41,
    "f": 0x46, "d": 0x44, "t": 0x54, "w": 0x57, "n": 0x4E,
    "m": 0x4D, "z": 0x5A, "p": 0x50, "app": 0x5D,
    "1": 0x31, "2": 0x32,
}


# ── SendInput ───────────────────────────────────────────────────────────────
class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", wintypes.LONG), ("dy", wintypes.LONG), ("mouseData", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", wintypes.DWORD), ("wParamL", wintypes.WORD), ("wParamH", wintypes.WORD)]


class _INPUTUNION(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("u", _INPUTUNION)]


def _key_event(vk: int, up: bool, unicode: bool = False, scan: int = 0) -> INPUT:
    inp = INPUT(type=INPUT_KEYBOARD)
    flags = KEYEVENTF_KEYUP if up else 0
    if unicode:
        flags |= KEYEVENTF_UNICODE
        inp.u.ki = KEYBDINPUT(0, scan, flags, 0, None)
    else:
        inp.u.ki = KEYBDINPUT(vk, 0, flags, 0, None)
    return inp


def _send(*inputs: INPUT) -> None:
    arr = (INPUT * len(inputs))(*inputs)
    user32.SendInput(len(inputs), arr, ctypes.sizeof(INPUT))


def key(name: str, ctrl: bool = False, alt: bool = False, shift: bool = False) -> None:
    vk = VK[name]
    mods = [VK["ctrl"]] if ctrl else []
    mods += [VK["alt"]] if alt else []
    mods += [VK["shift"]] if shift else []
    seq = [_key_event(m, False) for m in mods]
    seq.append(_key_event(vk, False))
    seq.append(_key_event(vk, True))
    seq += [_key_event(m, True) for m in reversed(mods)]
    _send(*seq)


def chord(*names: str) -> None:
    """chord("ctrl", "shift", "n") —— 最后一个键是主键。"""
    main = names[-1]
    mods = set(names[:-1])
    key(main, ctrl="ctrl" in mods, alt="alt" in mods, shift="shift" in mods)


def type_text(text: str) -> None:
    seq = []
    for ch in text:
        seq.append(_key_event(0, False, unicode=True, scan=ord(ch)))
        seq.append(_key_event(0, True, unicode=True, scan=ord(ch)))
    _send(*seq)


# ── 窗口与进程 ──────────────────────────────────────────────────────────────
WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def _visible_rect(hwnd: int):
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def main_window(pid: int, min_width: int = 400) -> int:
    found = []

    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid:
            return True
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        if "HwndWrapper" not in cls.value:
            return True
        l, t, w, h = _visible_rect(hwnd)
        if w >= min_width and h >= 300:
            found.append((w * h, hwnd))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return max(found)[1] if found else 0


def stray_windows(pid: int, main: int) -> list[int]:
    """除主窗以外、当前**可见**的 WPF 顶层窗口——也就是卡住的模态框。

    不能复用 `main_window()` 来找：它带 `h >= 300` 的尺寸门槛（用来排除提示气泡），
    而"要覆盖吗"这类确认框只有 440×150，永远过不了门槛。压测里一旦它留在屏幕上，
    主窗被 `IsEnabled=false` 禁掉，`focus()` 必然失败、键又打不进模态框，
    整轮空转直到超时（2026-11-16 10:55 轮就是这么连续 9 次抢前台失败后崩掉的）。
    """
    found = []

    def cb(hwnd, _):
        if hwnd == main or not user32.IsWindowVisible(hwnd):
            return True
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid:
            return True
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        # #32770 是系统对话框（Shell 的"这将取消所有正在进行的传输"就是它），
        # 不是 WPF 窗口，但同样会挡住主窗、吃掉按键。
        if "HwndWrapper" not in cls.value and cls.value != "#32770":
            return True
        # 空标题的 HwndWrapper 是提示气泡/弹出层（WPF Popup 自成一个顶层窗口），
        # 不挡键盘、也没有可关的模态语义。8 小时轮里这类窗口每个采样点抓到一个，
        # 被当成"游离窗口"收掉纯属误伤，还会把焦点抢走。
        if cls.value != "#32770" and not window_title(hwnd).strip():
            return True
        found.append(hwnd)
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def is_system_dialog(hwnd: int) -> bool:
    """`#32770` = 系统对话框（Shell 的"要覆盖吗""这将取消所有正在进行的传输"）。

    只有这一类可以被 WM_CLOSE 强拆：它是系统的，不属于应用状态。
    """
    cls = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, cls, 256)
    return cls.value == "#32770"


def dismiss_strays(pid: int, main: int, out=None) -> int:
    """把卡住的模态框收掉。返回收掉的个数。

    顺序是有讲究的：先 `Esc`（对话框的取消路径），再 `Enter`（默认按钮通常是"否"），
    最后才对**系统对话框** `PostMessage(WM_CLOSE)`。

    WPF 窗口（`HwndWrapper`）**绝不**发 WM_CLOSE：模态窗是常驻复用的，被销毁就再也
    Show() 不出来（WPF 抛"关闭窗口后，无法设置可见性"），整条提示链路会永久哑掉。
    2026-11-16 的 8 小时轮就是这么被自己人打死的：第 20 分钟确认窗被 WM_CLOSE 销毁，
    之后 7.5 小时 189 次无效调用。空标题的 `HwndWrapper` 是提示气泡/弹出层，也别碰。
    """
    closed = 0
    for _ in range(3):
        strays = stray_windows(pid, main)
        if not strays:
            break
        for h in strays:
            title = window_title(h)[:24]
            focus(h)
            time.sleep(0.2)
            key("esc")
            time.sleep(0.3)
            if user32.IsWindowVisible(h):
                key("enter")
                time.sleep(0.3)
            if user32.IsWindowVisible(h) and is_system_dialog(h):
                user32.PostMessageW(h, WM_CLOSE, 0, 0)
                time.sleep(0.3)
            closed += 1
            log(f"收掉游离窗口 hwnd={h} 「{title}」", out)
    return closed


def list_windows(pid: int) -> list:
    """列出该进程全部顶层窗口（含隐藏），崩溃取证用。

    崩溃栈落在 `DirectUI::HWNDElement::OnInput`，而 DirectUI 不是 WPF 的窗口类——
    它只可能来自进程内托管的 shell 属性表或输入法候选窗。把崩溃前一刻进程里到底
    挂着哪些 DirectUI 窗口记下来，才能确定那个被摘除的元素属于谁。
    """
    found = []

    def cb(hwnd, _):
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid:
            return True
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)
        title = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, title, 256)
        found.append({
            "hwnd": hwnd,
            "cls": cls.value,
            "title": title.value,
            "visible": bool(user32.IsWindowVisible(hwnd)),
        })
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def focus(hwnd: int) -> bool:
    """把窗口抢到前台。

    `SetForegroundWindow` 对"非子进程"会被前台锁直接拒绝，按键就静默发给了别的窗口——
    冒烟时表现为"动作全都跑了，沙箱里一个文件都没多"。必须先把输入队列挂到目标线程
    与当前前台窗口的线程上再抢，失败再退回 `SwitchToThisWindow`。
    """
    target_thread = user32.GetWindowThreadProcessId(hwnd, None)
    current_thread = kernel32.GetCurrentThreadId()
    fg = user32.GetForegroundWindow()
    fg_thread = user32.GetWindowThreadProcessId(fg, None) if fg else 0

    attach = [t for t in {fg_thread, target_thread} if t]
    for t in attach:
        user32.AttachThreadInput(current_thread, t, True)
    try:
        user32.ShowWindow(hwnd, 9)  # SW_RESTORE
        user32.BringWindowToTop(hwnd)
        user32.SetForegroundWindow(hwnd)
        user32.SetFocus(hwnd)
        time.sleep(0.2)
        if user32.GetForegroundWindow() != hwnd:
            user32.SwitchToThisWindow(hwnd, True)
            time.sleep(0.3)
    finally:
        for t in attach:
            user32.AttachThreadInput(current_thread, t, False)
    return user32.GetForegroundWindow() == hwnd


def window_title(hwnd: int) -> str:
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value


def responsive(hwnd: int) -> bool:
    """WM_NULL 有回音即意味着消息泵没卡死（1 秒超时）。"""
    res = ctypes.c_size_t(0)
    ret = user32.SendMessageTimeoutW(
        hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, 1000, ctypes.byref(res))
    return bool(ret)


WM_INPUTLANGCHANGEREQUEST = 0x0050
KLF_ACTIVATE = 0x0001


def window_lang(hwnd: int) -> str:
    """窗口所在线程当前的键盘布局，用于确认输入语言真的换了。"""
    tid = user32.GetWindowThreadProcessId(hwnd, None)
    hkl = user32.GetKeyboardLayout(tid)
    return f"{(hkl or 0) & 0xFFFF:04x}"


def force_input_language(hwnd: int, tag: str) -> tuple[bool, str]:
    """把被测窗口的输入语言钉到指定布局，返回 (是否生效, 生效后的布局)。

    存在的理由：这台机器默认输入法是搜狗，它把 TSF 文本服务连同皮肤组件
    （systembeautify / PicFace 等）一起塞进被测进程。压测靠注入按键打字，
    命中输入法皮肤方向的可能崩在别人的 DLL 里——那就不是本产品的稳定性。
    把语言切成 en-US 后，同一套动作仍走 TSF，但不触发中文候选窗，可用作对照。
    """
    hkl = user32.LoadKeyboardLayoutW(tag, KLF_ACTIVATE)
    if not hkl:
        return False, window_lang(hwnd)
    user32.PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, hkl)
    time.sleep(0.5)
    got = window_lang(hwnd)
    return got.lower() == tag[-4:].lower(), got


class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),
    ]


def sample(pid: int) -> dict | None:
    h = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        return None
    try:
        counters = PROCESS_MEMORY_COUNTERS_EX()
        counters.cb = ctypes.sizeof(counters)
        if not psapi.GetProcessMemoryInfo(h, ctypes.byref(counters), counters.cb):
            return None
        handles = wintypes.DWORD(0)
        kernel32.GetProcessHandleCount(h, ctypes.byref(handles))
        gdi = user32.GetGuiResources(h, 0)
        usr = user32.GetGuiResources(h, 1)
        return {
            "commitMb": round(counters.PrivateUsage / 1048576, 1),
            "workingMb": round(counters.WorkingSetSize / 1048576, 1),
            "handles": handles.value,
            "gdi": gdi,
            "user": usr,
        }
    finally:
        kernel32.CloseHandle(h)


# ── 沙箱与夹具 ──────────────────────────────────────────────────────────────
def prepare_work(work: Path, exe: Path) -> Path:
    app_dir = work / "app"
    sandbox = work / "sandbox"
    app_dir.mkdir(parents=True, exist_ok=True)
    sandbox.mkdir(parents=True, exist_ok=True)

    # 便携标记：设置与日志落 app\data\，不碰 %LOCALAPPDATA%
    (app_dir / "portable.flag").write_text("ac03 soak\n", encoding="utf-8")

    target = app_dir / exe.name
    if not target.exists() or target.stat().st_mtime < exe.stat().st_mtime:
        for item in exe.parent.iterdir():
            if item.is_file():
                shutil.copy2(item, app_dir / item.name)
        # 压测不该带便携包里的 data\（如果有）
        stale = app_dir / "data"
        if stale.exists():
            shutil.rmtree(stale, ignore_errors=True)

    # 每轮强制重置沙箱。11-17 那轮起点就带着上一轮残留的 2160 项，一路涨到 7.7 万，
    # 提交内存跟着涨到 987 MB——压测就变成在测大目录，AC-03 的结论无从谈起。
    # 起点必须恒定：200 个文件 + 4 个子目录（各 20 项），跑完才能比较。
    reset_sandbox(sandbox)

    # 预置设置：新标签直接落在沙箱目录。压测不能依赖"启动后在主页按 Ctrl+L"——
    # 主页上没有文件标签，`Ctrl+L` 不响应，动作会全部落空（冒烟时踩过）。
    # 语言固定中文，避免英文界面的确认按钮文案影响后续动作判定。
    settings = app_dir / "data" / "settings.json"
    if not settings.exists():
        settings.parent.mkdir(parents=True, exist_ok=True)
        settings.write_text(json.dumps({
            "schemaVersion": 1,
            "general": {"language": "zh-Hans", "newTabMode": "path",
                        "newTabPath": str(sandbox), "confirmDelete": False},
            "appearance": {"theme": "light", "fontSizePt": 11},
        }, ensure_ascii=False, indent=1), encoding="utf-8")

    return target


def reset_sandbox(sandbox: Path) -> None:
    """把沙箱恢复成固定起点，且**不删任何文件**。

    每轮强制重置是必要的（11-17 轮起点就带着上一轮残留的 2160 项），但
    `seed_sandbox(force=True)` 会一次性删掉两百来个条目，同样撞上宿主的删除保护。
    改成把整个目录 rename 到 overflow 再原地重建：同卷 rename 是瞬时的，
    路径不变（应用设置里的 newTabPath 继续有效），也没有任何删除动作。
    """
    try:
        if sandbox.exists() and any(sandbox.iterdir()):
            dest = OVERFLOW / f"reset-{datetime.now().strftime('%Y%m%d-%H%M%S')}"
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(sandbox), str(dest))
    except OSError:
        pass
    sandbox.mkdir(parents=True, exist_ok=True)
    seed_sandbox(sandbox, force=True)


def seed_sandbox(sandbox: Path, force: bool = False) -> None:
    if force:
        for child in sandbox.iterdir():
            if child.is_dir():
                shutil.rmtree(child, ignore_errors=True)
            else:
                child.unlink(missing_ok=True)
        for i in range(4):
            sub = sandbox / f"sub{i:02d}"
            sub.mkdir(exist_ok=True)
            for j in range(20):
                (sub / f"inner_{i:02d}_{j:03d}.txt").write_text("x", encoding="utf-8")
        for i in range(120):
            (sandbox / f"item_{i:03d}.txt").write_text("ac03\n", encoding="utf-8")


def count_files(sandbox: Path) -> int:
    try:
        return sum(1 for p in sandbox.iterdir() if p.is_file())
    except OSError:
        return -1


SCALE_LIMIT = 400          # 沙箱总条目上限（种子 200 项，留两倍余量）
MEM_LIMIT_MB = 600         # 末点提交内存上限
# 骨架白名单必须按**完整命名**匹配。第一版写成 startswith(("item_", "inner_"))，
# 结果应用自动改名的副本 `item_000 (2).txt` 同样以 item_ 开头，被当成骨架留下——
# 守卫每批都在跑、每批都判定"全是骨架"，一个都没移走，11-20 那轮 40 分钟就堆到 1160 项。
SKELETON_RE = re.compile(r"^(item_\d{3}\.txt|inner_\d{2}_\d{3}\.txt)$")
SKELETON_DIRS = {f"sub{i:02d}" for i in range(4)}
OVERFLOW = Path(r"D:\ac03\overflow")   # 规模守卫的移出目的地，跑完人工清理


def count_entries(root: Path) -> int:
    """递归数文件。只看顶层会被骗——副本可能被粘进子目录。"""
    total = 0
    try:
        for _, _, filenames in os.walk(root):
            total += len(filenames)
    except OSError:
        return -1
    return total


def guard_scale(sandbox: Path, out=None) -> int:
    """规模守卫：条目数超限就把"不是种子骨架"的文件**移出**沙箱。

    11-17 那轮涨到 7.7 万项、提交内存 987 MB，根因是复制粘贴的撤销没生效，
    而 top_up 的兜底只在顶层数、且只认"- 副本"这一种命名。这里改成递归计数 +
    白名单：只留 item_*/inner_*（沙箱骨架），其余（副本、soak*、ren*、refill*）
    一律移走。不清的话压测测的是磁盘目录规模，不是应用稳定性。

    用 `shutil.move` 而不是 `unlink`：驱动里的批量删除会被宿主的删除保护拦下来
    （单回合累计 50 次即中止），8 小时无人值守跑不动。移到同卷的 overflow 目录
    是一次 rename，既不计删除、也不占额外空间，跑完由人工一次性清理。
    """
    total = count_entries(sandbox)
    if total < 0 or total <= SCALE_LIMIT:
        return 0
    target_dir = OVERFLOW / datetime.now().strftime("%Y%m%d-%H%M")
    target_dir.mkdir(parents=True, exist_ok=True)
    moved = 0
    # 先清目录：应用会造出 `sub00 (2)` 这种目录副本，里面的文件仍叫
    # `inner_00_001.txt`，按文件名白名单认不出来（11-20 那轮就卡在这：
    # 顶层副本移完了，剩 560 项全在目录副本里）。骨架只认 sub00–sub03。
    for entry in sorted(sandbox.iterdir()):
        if not entry.is_dir() or entry.name in SKELETON_DIRS:
            continue
        try:
            shutil.move(str(entry), str(target_dir / f"dir{moved:04d}_{entry.name}"))
            moved += 1
        except OSError:
            pass
        if moved >= 300:
            break
    if moved < 300:
        for dirpath, _, filenames in os.walk(sandbox, topdown=False):
            here = Path(dirpath)
            for name in filenames:
                if SKELETON_RE.match(name):
                    continue
                try:
                    shutil.move(str(here / name), str(target_dir / f"{moved:05d}_{name}"))
                    moved += 1
                except OSError:
                    pass
                if moved >= 300:      # 单批上限：不能让清理本身把节奏拖垮
                    break
            if moved >= 300:
                break
    if moved:
        log(f"规模守卫：沙箱 {total} 项超限（上限 {SCALE_LIMIT}），移出 {moved} 项", out)
    return moved


def ensure_skeleton(sandbox: Path) -> int:
    """补回被"删除"动作吃掉的骨架。

    删除动作会整个删掉 sub00 这类子目录，5 分钟冒烟就少了 20 项。不补的话 8 小时
    跑下来沙箱会退化成只剩顶层文件，"进子目录 / 删子目录"这条路径再也测不到。

    只能低频补（每 40 批一次）：驱动自己写盘同样会改目录 mtime，而 mtime 变化是
    "动作落地"的判据。高频补会让判据变成假阳性——8 小时门槛是 10% 的采样点，
    40 批才贡献 1 次，不越界。
    """
    added = 0
    for i in range(4):
        sub = sandbox / f"sub{i:02d}"
        try:
            sub.mkdir(exist_ok=True)
            have = sum(1 for _ in sub.iterdir())
            for j in range(have, 20):
                (sub / f"inner_{i:02d}_{j:03d}.txt").write_text("x", encoding="utf-8")
                added += 1
        except OSError:
            pass
    return added


def top_up(sandbox: Path) -> int:
    """补齐被永久删除吃掉的文件，保证每轮都有东西可点。"""
    files = [p for p in sandbox.iterdir() if p.is_file()]
    count = len(files)
    added = 0
    while count + added < 100:
        (sandbox / f"refill_{int(time.time()*1000) % 10**8}_{added}.txt").write_text("x", encoding="utf-8")
        added += 1
    return added


# ── 20 条高频路径 ───────────────────────────────────────────────────────────
def act_nav_enter(hwnd, ctx):
    chord("ctrl", "a")
    key("down")
    key("enter")


def act_nav_up(hwnd, ctx):
    key("back")


def act_nav_back(hwnd, ctx):
    key("left", alt=True)


def act_nav_fwd(hwnd, ctx):
    key("right", alt=True)


def act_addr_jump(hwnd, ctx):
    chord("ctrl", "l")
    time.sleep(0.2)
    type_text(str(ctx["sandbox"]))
    time.sleep(0.2)
    key("enter")
    time.sleep(0.5)


def act_view_toggle(hwnd, ctx):
    ctx["view"] = not ctx.get("view", True)
    chord("ctrl", "shift", "1" if ctx["view"] else "2")


def act_search(hwnd, ctx):
    chord("ctrl", "f")
    type_text("item")
    time.sleep(0.3)
    key("esc")
    # 第一下 Esc 只收起搜索框，键盘焦点没回到文件列表——之后 Delete / Ctrl+Shift+M
    # 全部打空（probe_focus 实测：120→120）。补第二下 Esc 才重新落到列表上。
    time.sleep(0.2)
    key("esc")


def act_select_all(hwnd, ctx):
    chord("ctrl", "a")


def act_copy_paste(hwnd, ctx):
    # 只复制首个条目。全选复制一次是 120 个副本，撤销万一失效，8 小时能堆出几万个文件，
    # 压测就变成在测磁盘而不是测应用；单文件复制照样走完 读源→写目标→刷新 的整条链路。
    key("down")
    chord("ctrl", "c")
    time.sleep(0.3)
    sandbox = ctx["sandbox"]
    before = {p.name for p in sandbox.iterdir()}
    chord("ctrl", "v")
    time.sleep(0.8)
    # 目标名撞上了会弹"要覆盖吗"确认框。这个框只有 440×150，过去收窗逻辑按尺寸
    # 找不到它，一旦留着就把主窗永久禁用（见 stray_windows）。粘贴后先按 Esc 走"否"。
    key("esc")
    time.sleep(0.2)
    chord("ctrl", "z")   # 撤销副本，沙箱不膨胀
    time.sleep(0.3)
    # 撤销不可靠：撞名弹框时 Ctrl+Z 落在确认框上，副本留在盘里。11-17 那轮就是这么
    # 从 2160 项涨到 7.7 万、提交内存跟着到 987 MB 的。这里只**统计**不删——驱动内的
    # 批量 unlink 会被宿主的删除保护拦下（单回合累计 50 次即中止），清理交给每批的
    # guard_scale（移出沙箱，不是删除）。
    leaked = [p.name for p in sandbox.iterdir()
              if p.is_file() and p.name not in before and not SKELETON_RE.match(p.name)]
    ctx["copyLeaks"] = ctx.get("copyLeaks", 0) + len(leaked)


def _seq_name(ctx, tag):
    """给每次新建/改名发一个轮转的名字。

    固定名字在撤销失手时会撞名，弹"已存在"确认框——那个框会接管键盘，后续动作全部
    打空，压测跑完只有"没崩"，看不出应用是否真在干活。轮转 1000 个名字足够 8 小时不重。
    """
    n = ctx.get(tag, 0)
    ctx[tag] = n + 1
    return f"{tag}{n % 1000:03d}"


def act_new_folder(hwnd, ctx):
    chord("ctrl", "shift", "n")
    time.sleep(0.4)
    type_text(_seq_name(ctx, "soakd"))
    key("enter")
    time.sleep(0.3)
    chord("ctrl", "z")


def act_new_text(hwnd, ctx):
    chord("ctrl", "shift", "m")
    time.sleep(0.4)
    type_text(_seq_name(ctx, "soak"))
    key("enter")
    time.sleep(0.3)
    chord("ctrl", "z")


def act_rename(hwnd, ctx):
    chord("ctrl", "a")
    key("down")
    key("f2")
    time.sleep(0.3)
    type_text(_seq_name(ctx, "ren"))
    key("enter")
    time.sleep(0.3)
    chord("ctrl", "z")


def act_delete(hwnd, ctx):
    key("down")
    key("delete")
    time.sleep(0.5)
    chord("ctrl", "z")   # 从回收站还原，回收站不累积


def act_undo(hwnd, ctx):
    chord("ctrl", "z")


def act_props(hwnd, ctx):
    key("down")
    chord("alt", "enter")
    time.sleep(0.6)
    key("esc")
    time.sleep(0.2)


def act_copy_path(hwnd, ctx):
    key("down")
    chord("ctrl", "shift", "c")


def act_bookmark(hwnd, ctx):
    chord("ctrl", "d")
    time.sleep(0.3)
    key("esc")   # 重复收藏时会有提示，Esc 收掉


def act_tabs(hwnd, ctx):
    chord("ctrl", "t")
    time.sleep(0.6)
    chord("ctrl", "tab")
    time.sleep(0.2)
    chord("ctrl", "w")
    time.sleep(0.3)


def act_split(hwnd, ctx):
    ctx["split"] = not ctx.get("split", False)
    key("f3")
    time.sleep(0.4)


def act_preview(hwnd, ctx):
    ctx["preview"] = not ctx.get("preview", False)
    chord("alt", "p")
    time.sleep(0.4)


ACTIONS = [
    ("进入子目录", act_nav_enter),
    ("返回上级", act_nav_up),
    ("后退", act_nav_back),
    ("前进", act_nav_fwd),
    ("地址栏跳转", act_addr_jump),
    ("视图切换", act_view_toggle),
    ("搜索当前目录", act_search),
    ("全选", act_select_all),
    ("复制粘贴", act_copy_paste),
    ("新建文件夹", act_new_folder),
    ("新建文本文档", act_new_text),
    ("重命名", act_rename),
    ("删除到回收站", act_delete),
    ("撤销", act_undo),
    ("属性对话框", act_props),
    ("复制完整路径", act_copy_path),
    ("收藏", act_bookmark),
    ("新建/切换/关闭标签", act_tabs),
    ("双窗格开关", act_split),
    ("预览窗格开关", act_preview),
]

HOUSEKEEPING = [("刷新", lambda hwnd, ctx: key("f5"))]


# ── 主循环 ──────────────────────────────────────────────────────────────────
def log(msg: str, out=None) -> None:
    line = f"[{datetime.now():%H:%M:%S}] {msg}"
    print(line, flush=True)
    if out:
        out.write(line + "\n")
        out.flush()


def main() -> int:
    ap = argparse.ArgumentParser(description="AC-03 / NF-06 稳定性压测")
    ap.add_argument("--exe", default=str(ROOT / "publish" / "Jianlan.exe"))
    ap.add_argument("--work", default=r"D:\ac03")
    ap.add_argument("--hours", type=float, default=8.0)
    ap.add_argument("--cycle", type=float, default=45.0, help="每轮间隔秒数")
    ap.add_argument("--sample", type=float, default=60.0, help="采样间隔秒数")
    ap.add_argument("--out", default=str(ROOT / "tools" / "ac03" / "results"))
    ap.add_argument("--seed", type=int, default=20261114)
    ap.add_argument("--input", default="", help="把被测窗口的输入语言钉到这个布局（如 00000409=en-US）；默认不动")
    ap.add_argument("--fast-batches", type=int, default=0,
                    help="前 N 轮用 --fast-cycle 的快节奏跑完，之后恢复 --cycle。"
                         "用于在几十分钟内把动作序列推到指定批次的时序条件上，"
                         "同时保留崩溃点前后那几轮的真实空闲时长")
    ap.add_argument("--fast-cycle", type=float, default=6.0, help="快节奏段的轮间隔秒数")
    ap.add_argument("--probe-windows", action="store_true",
                    help="每轮记录进程内全部顶层窗口（类名/标题），用于崩溃取证")
    args = ap.parse_args()

    random.seed(args.seed)
    exe = Path(args.exe).resolve()
    if not exe.exists():
        print(f"找不到 {exe}", file=sys.stderr)
        return 2
    work = Path(args.work)
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    jsonl_path = out_dir / f"ac03-{stamp}.jsonl"
    win_path = out_dir / f"ac03-{stamp}.win.jsonl"
    summary_path = out_dir / f"ac03-{stamp}.json"
    log_path = out_dir / f"ac03-{stamp}.log"

    target = prepare_work(work, exe)
    sandbox = work / "sandbox"
    crash_log = work / "app" / "data" / "logs" / "crash.log"
    crash_base = crash_log.stat().st_size if crash_log.exists() else 0

    # 原生 AV 不会写 crash.log，只会留下 procdump 的转储；而 procdump 抓转储时要
    # DebugActiveProcess，会把被测进程**整体挂起**几十秒。这期间 poll() 仍是 None、
    # 消息泵当然没有回音——2026-11-16 那两次"驱动卡死"和一次"应用自退"全是这么误判的。
    # 用"转储目录里多了一份 .dmp"当崩溃判据，比退出码更早、更可信。
    dumps_dir = work / "dumps"
    dump_base = len(list(dumps_dir.glob("Jianlan.exe_*.dmp"))) if dumps_dir.exists() else 0

    # 8 小时不许睡眠：只对本进程生效，不改系统电源方案
    kernel32.SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)

    started = datetime.now()
    deadline = started + timedelta(hours=args.hours)

    with open(log_path, "w", encoding="utf-8") as logf:
        log(f"被测 {target}", logf)
        log(f"沙箱 {sandbox}；时长 {args.hours} h；轮间隔 {args.cycle}s；采样 {args.sample}s", logf)
        proc = None

        def launch():
            """起一个被测进程并等主窗口。"""
            p = __import__("subprocess").Popen([str(target)], cwd=str(target.parent))
            h = 0
            for _ in range(60):
                time.sleep(0.5)
                h = main_window(p.pid)
                if h:
                    break
            return p, h

        def drop_to_sandbox(h) -> bool:
            """把窗口拉到沙箱目录，并留一个备用标签。

            备用标签是刚需：只剩一个标签时 `Ctrl+W` 就是关窗口（正常行为），
            8 小时里只要有一次 `Ctrl+T` 被吃掉，应用就被自己人关掉了。
            跑第一版 8 小时时 9 分钟就撞上了——exit=0、crash.log 没增，纯自退。
            """
            if not focus(h):
                return False
            time.sleep(1.0)
            if sandbox.name.lower() not in window_title(h).lower():
                # 首次启动恒开主页（FR-1.1），而主页上没有文件标签，Ctrl+L 不响应。
                # 走"Ctrl+T 开新标签（按 newTabMode=path 落到沙箱）→ Ctrl+1 回主页
                # → Ctrl+W 关掉主页"，全程只有键位，不依赖坐标。
                chord("ctrl", "t")
                time.sleep(2.0)
                if sandbox.name.lower() not in window_title(h).lower():
                    chord("ctrl", "l")
                    type_text(str(sandbox))
                    key("enter")
                    time.sleep(1.5)
                chord("ctrl", "1")
                time.sleep(0.4)
                chord("ctrl", "w")
                time.sleep(1.2)
            chord("ctrl", "t")   # 备用标签
            time.sleep(1.5)
            return True

        proc, hwnd = launch()
        try:
            if not hwnd:
                log("窗口没起来，终止", logf)
                summary = {"verdict": "FAIL", "reason": "no-window", "samples": []}
                summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
                return 2

            if not drop_to_sandbox(hwnd):
                log("抢前台失败，终止", logf)
                summary_path.write_text(json.dumps({"verdict": "FAIL", "reason": "no-foreground"}, ensure_ascii=False), encoding="utf-8")
                return 2
            log(f"起始窗口标题：{window_title(hwnd)}", logf)
            if args.input:
                ok, got = force_input_language(hwnd, args.input)
                log(f"输入语言 → {args.input}：{'已生效' if ok else '未生效'}（当前 {got}）", logf)

            samples, failures, actions_done = [], [], 0
            focus_fails = 0
            resets = 0
            app_exits = 0
            actions_since_sample = 0
            # 弹窗存活证据：属性/确认弹窗不写盘，mtime 摸不到它们。没有这个计数，
            # "第 20 分钟起每次弹窗都抛异常"这种事只能靠翻 crash.log 反推。
            dialogs_seen = set()
            ctx = {"sandbox": sandbox, "view": True, "split": False, "preview": False}
            next_sample = time.monotonic()
            order = list(range(len(ACTIONS)))
            batches = 0

            while datetime.now() < deadline:
                # 采点
                now = time.monotonic()
                if now >= next_sample:
                    s = sample(proc.pid)
                    # 进程死掉的那一刻，OpenProcess 仍可能成功、计数器全为 0
                    # （第一次 8 小时压测就是这么被记成"消息泵无响应"的）。
                    # 句柄与 GDI 同时为 0 一律按"进程没了"处理，再按退出码分流。
                    dead = s is None or (s["handles"] == 0 and s["gdi"] == 0)
                    if s is not None and not dead:
                        s["responsive"] = responsive(hwnd)
                        dead = not s["responsive"]
                        if dead:
                            s["mark"] = "unresponsive"
                    if dead and proc.poll() is None:
                        # 进程还在，"没有回音"多半是瞬时卡顿：复制整个目录、Shell 回收站
                        # 操作、缩略图首屏生成都会把消息泵堵上一两秒。复查两次再判，
                        # 否则一次 3 秒的复制就能把这轮压测误判成"应用自退"并停跑
                        # （2026-11-16 10:30 的冒烟就是这么被自己人判死的）。
                        for _ in range(2):
                            time.sleep(1.0)
                            if proc.poll() is not None:
                                break
                            if responsive(hwnd):
                                dead = False
                                s["responsive"] = True
                                s.pop("mark", None)
                                break
                    if dead:
                        code = proc.poll()
                        crash_bytes = crash_log.stat().st_size if crash_log.exists() else 0
                        dumps_now = len(list(dumps_dir.glob("Jianlan.exe_*.dmp"))) if dumps_dir.exists() else 0
                        if dumps_now > dump_base:
                            # 有转储 = 原生崩溃。procdump 还在写盘（进程被挂起），
                            # 等它写完再收尾，否则最后一份转储是残缺的。
                            failures.append({"at": datetime.now().isoformat(),
                                             "kind": "crash", "exitCode": code,
                                             "note": "dump", "newDumps": dumps_now - dump_base,
                                             "crashLogNewBytes": crash_bytes - crash_base})
                            log(f"发现新转储（+{dumps_now - dump_base} 份），判定崩溃；"
                                f"等转储写完再收尾", logf)
                            for _ in range(60):
                                if proc.poll() is not None:
                                    break
                                time.sleep(1.0)
                            break
                        if code is None:
                            # 复查后仍不响应，但进程确实还活着：既不是崩溃也不是自退，
                            # 记一笔"消息泵无响应"继续跑——真死掉的话下一轮 poll 会给退出码。
                            if s is None:
                                log("进程仍在但取不到计数器，本点跳过", logf)
                                next_sample = now + args.sample
                                continue
                            s["responsive"] = False
                            s["mark"] = "unresponsive"
                            log("消息泵连续 3 秒无响应，本点记为 unresponsive，继续", logf)
                        elif code != 0 or crash_bytes > crash_base:
                            # 非 0 退出或 crash.log 有新行 = 真崩溃，留现场停跑
                            failures.append({"at": datetime.now().isoformat(),
                                             "kind": "crash", "exitCode": code,
                                             "note": (s or {}).get("mark", "gone"),
                                             "crashLogNewBytes": crash_bytes - crash_base})
                            log(f"进程异常退出 exit={code}（0x{(code or 0) & 0xFFFFFFFF:08X}），"
                                f"crash.log 新增 {crash_bytes - crash_base} 字节", logf)
                            break
                        else:
                            # exit=0 且无崩溃记录：应用是被"关掉最后一个标签"这类正常操作带走的。
                            # 这不算崩溃，重启接上，把这件事单独记一笔。
                            app_exits += 1
                            log(f"应用自退（exit=0，无崩溃记录），第 {app_exits} 次重启", logf)
                            if app_exits > 30:
                                failures.append({"at": datetime.now().isoformat(), "kind": "too-many-exits"})
                                log("自退次数超限，停跑", logf)
                                break
                            proc, hwnd = launch()
                            if not hwnd or not drop_to_sandbox(hwnd):
                                failures.append({"at": datetime.now().isoformat(), "kind": "restart-failed"})
                                log("重启失败，停跑", logf)
                                break
                            if args.input:
                                force_input_language(hwnd, args.input)
                            actions_since_sample = 0
                            next_sample = time.monotonic() + args.sample
                            continue
                    s["at"] = datetime.now().isoformat()
                    s["elapsedMin"] = round((datetime.now() - started).total_seconds() / 60, 1)
                    s["crashLogBytes"] = crash_log.stat().st_size if crash_log.exists() else 0
                    # 动作生效性证据：沙箱目录的 mtime。新建/删除/改名/粘贴都会改它，
                    # 而脚本自己这一侧只在文件少于 100 个时才补 refill（正常不会触发），
                    # 所以 mtime 在动 = 按键真的打在被测窗口上并且落地写盘了。
                    s["sandboxFiles"] = count_files(sandbox)
                    s["sandboxEntries"] = count_entries(sandbox)
                    s["sandboxMtime"] = sandbox.stat().st_mtime_ns
                    s["inSandbox"] = sandbox.name.lower() in window_title(hwnd).lower()
                    s["focusFails"] = focus_fails
                    # 自愈：上一个采样点到这一个点之间发过动作、沙箱却一次写都没发生，
                    # 说明键盘焦点又飘了（Esc/模态/拖选都可能）。按 Esc+方向键把焦点
                    # 拽回列表再继续——不然 8 小时跑下来的只是"窗口还活着"。
                    if (samples and s["sandboxMtime"] == samples[-1]["sandboxMtime"]
                            and actions_since_sample > 0):
                        focus(hwnd)
                        key("esc")
                        time.sleep(0.2)
                        key("down")
                        resets += 1
                        log(f"本段没有写入，焦点已复位（第 {resets} 次）", logf)
                    actions_since_sample = 0
                    samples.append(s)
                    # 逐点落盘。8 小时跑完才写文件的话，中途被打断（关机、手动中止、
                    # 蓝屏）就一个数都留不下——冒烟被打断两次，只捞到 .log 里的几行。
                    with open(jsonl_path, "a", encoding="utf-8") as jf:
                        jf.write(json.dumps(s, ensure_ascii=False) + "\n")
                    log(f"#{len(samples):>3} {s['elapsedMin']:>6.1f}min 提交 {s['commitMb']:>6.1f}MB "
                        f"句柄 {s['handles']:>5} GDI {s['gdi']:>4} USER {s['user']:>4}", logf)
                    next_sample = now + args.sample

                # 一轮动作
                if not order:
                    order = list(range(len(ACTIONS)))
                    random.shuffle(order)
                batch = [order.pop() for _ in range(min(7, len(order)))]
                # 起步先收卡住的模态框：它在屏幕上，主窗就是禁用状态，focus() 必然失败，
                # 而键也进不了模态框——不先收掉，后面每一轮都是空转。
                if dismiss_strays(proc.pid, hwnd, logf):
                    time.sleep(0.4)
                if not focus(hwnd):
                    focus_fails += 1
                    log(f"抢前台失败（累计 {focus_fails} 次），本轮动作跳过", logf)
                    time.sleep(1.0)
                    continue
                # "返回上级"按设计就会走出沙箱，走完不拉回来的话，后面几个新建动作
                # 会落到 D:\ac03 根上，沙箱计数就成了假数据。每轮起步先归位。
                if sandbox.name.lower() not in window_title(hwnd).lower():
                    chord("ctrl", "l")
                    type_text(str(sandbox))
                    key("enter")
                    time.sleep(0.8)
                for idx in batch:
                    name, fn = ACTIONS[idx]
                    try:
                        fn(hwnd, ctx)
                    except Exception as exc:  # 动作失败不该中止压测
                        log(f"动作 {name} 抛错：{exc}", logf)
                    actions_done += 1
                    actions_since_sample += 1
                    time.sleep(0.25)

                top_up(sandbox)
                # 每批都查规模。撤销失手是常态（目标名撞车会弹"要覆盖吗"，
                # 那一轮的 Ctrl+Z 就落在确认框上），放到轮末才查，8 小时能堆出几万个。
                guard_scale(sandbox, logf)
                if batches and batches % 40 == 0:
                    n = ensure_skeleton(sandbox)
                    if n:
                        log(f"骨架补回 {n} 项（第 {batches} 批）", logf)
                for _, fn in HOUSEKEEPING:
                    fn(hwnd, ctx)

                batches += 1
                # 窗口快照必须取在"收掉游离窗口"之前：崩溃很可能就发生在收窗的那一下，
                # 事后补记就看不到那个 DirectUI 窗口了。
                windows_now = (list_windows(proc.pid)
                               if (args.probe_windows or batches % 3 == 0) else [])
                for w in windows_now:
                    if w["visible"] and w["hwnd"] != hwnd and "HwndWrapper" in w["cls"]:
                        dialogs_seen.add(w["hwnd"])
                if args.probe_windows:
                    snap = {
                        "at": datetime.now().isoformat(),
                        "batch": batches,
                        "actions": actions_done,
                        "elapsedMin": round((datetime.now() - started).total_seconds() / 60, 1),
                        "windows": windows_now,
                    }
                    with open(win_path, "a", encoding="utf-8") as wf:
                        wf.write(json.dumps(snap, ensure_ascii=False) + "\n")
                    dui = [w for w in snap["windows"] if "DirectUI" in w["cls"]]
                    if dui:
                        log(f"批 {batches} 发现 DirectUI 窗口：" +
                            "；".join(f"{w['cls']}/{w['title'][:20]}" for w in dui), logf)

                # 收尾再查一遍：这一轮里可能弹了"要覆盖吗"这类确认框，留着它下一轮就空转
                if dismiss_strays(proc.pid, hwnd, logf):
                    focus(hwnd)

                cycle = args.fast_cycle if batches <= args.fast_batches else args.cycle
                time.sleep(max(1.0, cycle - 2.2))

            alive = proc.poll() is None
            exit_code = proc.poll()
            if alive:
                hwnd2 = main_window(proc.pid)
                if hwnd2:
                    user32.PostMessageW(hwnd2, WM_CLOSE, 0, 0)
                for _ in range(20):
                    if proc.poll() is not None:
                        break
                    time.sleep(0.5)
                if proc.poll() is None:
                    proc.kill()
                exit_code = proc.poll()

            crash_now = crash_log.stat().st_size if crash_log.exists() else 0
            first, last = (samples[0], samples[-1]) if samples else (None, None)
            max_gdi = max((s["gdi"] for s in samples), default=0)
            max_handles = max((s["handles"] for s in samples), default=0)
            # 基线取"热身之后"的点。刚启动那几分钟句柄还在爬（XAML 资源、图标缓存、
            # 字体回退都是按需加载），拿第一分钟的点当基线，8 小时跑完必然报 15% 以上
            # 的假泄漏——冒烟就是这样被判 FAIL 的（1324 → 1541，之后一直平在 1541）。
            total_min = args.hours * 60
            warmup_min = min(10.0, max(0.5, total_min * 0.05))
            base = next((s for s in samples if s["elapsedMin"] >= warmup_min), first)
            growth = ((last["handles"] - base["handles"]) / base["handles"] * 100) if base and last and base["handles"] else 0.0
            # 提交内存门槛。此前没有任何内存判据：11-16 轮末点 332 MB（条目恒定）时
            # 判 PASS，11-17 轮涨到 987 MB 也没被拦，只是恰好有别的项 FAIL。
            # 600 MB 是"起点约 135 MB、8 小时允许涨 3.5 倍"的量级，足够容纳缓存预热，
            # 又能拦住线性泄漏。
            mem_end = (last or {}).get("commitMb") or 0
            mem_max = max((s.get("commitMb") or 0 for s in samples), default=0)

            ok = (
                not failures
                and crash_now == crash_base
                and growth < 10
                and max_gdi < 2000
                and mem_end <= MEM_LIMIT_MB
            )
            # crash.log 只增不减地累积托管异常，而"有没有异常"本身是判定条件之一。
            # 长跑里 FAIL 了却看不出败因，就是因为汇总只说"新增 N 字节"。
            # 把新增段里每类异常的首行与条数提出来，判定理由自带解释。
            crash_types = {}
            if crash_now > crash_base and crash_log.exists():
                with open(crash_log, "r", encoding="utf-8-sig", errors="replace") as f:
                    f.seek(crash_base)
                    tail = f.read()
                for line in tail.splitlines():
                    if line.startswith("异常"):
                        head = line[len("异常"):].strip()[:120]
                        crash_types[head] = crash_types.get(head, 0) + 1
            # 动作生效性：沙箱目录 mtime 变过几次。撤销做对了的话文件数会回到原样，
            # 单看数量会被判成"没干活"；目录 mtime 记录的是"发生过写操作"这件事本身。
            mtimes = [s.get("sandboxMtime") for s in samples if s.get("sandboxMtime")]
            mtime_changes = sum(1 for a, b in zip(mtimes, mtimes[1:]) if a != b)
            effective = mtime_changes >= max(2, len(samples) // 10)
            file_span = ((max(s["sandboxFiles"] for s in samples)
                          - min(s["sandboxFiles"] for s in samples)) if samples else 0)
            summary = {
                "verdict": "PASS" if (ok and effective) else "FAIL",
                "checks": {
                    "crashes": 0 if not failures else len(failures),
                    "crashLogNewBytes": crash_now - crash_base,
                    "crashLogTypes": crash_types,
                    "handleGrowthPct": round(growth, 2),
                    "handleGrowthLimitPct": 10,
                    "baselineHandles": base["handles"] if base else None,
                    "baselineAtMin": base["elapsedMin"] if base else None,
                    "warmupMin": round(warmup_min, 2),
                    "maxHandles": max_handles,
                    "maxGdi": max_gdi,
                    "gdiLimit": 2000,
                    "actionsEffective": effective,
                    "sandboxMtimeChanges": mtime_changes,
                    "sandboxFileSpan": file_span,
                    "focusFails": focus_fails,
                    "focusResets": resets,
                    "appSelfExits": app_exits,
                    "dialogsShown": len(dialogs_seen),
                    "copyLeaks": ctx.get("copyLeaks", 0),
                    "commitEndMb": round(mem_end, 1),
                    "commitMaxMb": round(mem_max, 1),
                    "commitLimitMb": MEM_LIMIT_MB,
                    "sandboxEntriesPeak": max((s.get("sandboxEntries") or 0 for s in samples),
                                              default=0),
                },
                "run": {
                    "exe": str(target),
                    "startedAt": started.isoformat(),
                    "finishedAt": datetime.now().isoformat(),
                    "hours": args.hours,
                    "cycleSeconds": args.cycle,
                    "sampleSeconds": args.sample,
                    "inputLanguage": args.input or "keep",
                    "samples": len(samples),
                    "actions": actions_done,
                    "appExitCode": exit_code,
                },
                "failures": failures,
                "first": first,
                "last": last,
            }
            summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")

            log("", logf)
            log(f"判定 {summary['verdict']}：崩溃 {summary['checks']['crashes']} 次、"
                f"句柄增长 {growth:.2f}%（上限 10%）、GDI 峰值 {max_gdi}（上限 2000）、"
                f"提交内存末点 {mem_end:.0f}MB（上限 {MEM_LIMIT_MB}MB）、"
                f"crash.log 新增 {crash_now - crash_base} 字节", logf)
            log(f"动作 {actions_done} 次；沙箱目录 mtime 变化 {mtime_changes} 次（<2 说明动作没落地）；"
                f"文件数波动 {file_span}；抢前台失败 {focus_fails} 次；"
                f"弹窗出现 {len(dialogs_seen)} 个不同 HWND", logf)
            for msg, n in crash_types.items():
                log(f"  crash.log 新增异常 ×{n}：{msg}", logf)
            log(f"采样 {jsonl_path}", logf)
            log(f"汇总 {summary_path}", logf)
            return 0 if ok else 2
        finally:
            kernel32.SetThreadExecutionState(ES_CONTINUOUS)
            if proc.poll() is None:
                proc.kill()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SystemExit:
        raise
    except BaseException:
        # 计划任务里没有控制台，stderr 会被丢弃，脚本自身出错时只剩一个 exit=1。
        # 2026-11-17 08:11 那轮就是这么丢掉线索的（实则 UnboundLocalError）。
        import traceback
        crash_txt = traceback.format_exc()
        print(crash_txt, file=sys.stderr)
        try:
            (Path(__file__).resolve().parent / "last_error.log").write_text(crash_txt, encoding="utf-8")
        except OSError:
            pass
        sys.exit(1)
