"""焦点环探针：验证"单按 Alt 会不会让当前行补画焦点环"。

起因（2026-11-24 走查）：详情/列表视图里选中一行后按一下 Alt，行上冒出一个 1px 蓝框。
那是 WPF 的 `ShowKeyboardCues` 被 Alt 打开后补画的 `FocusVisualStyle`，不是布局出错。
修法是把单按 Alt 在 `PreviewKeyDown` 里标成已处理（见 MainWindow.OnPreviewKeyDown 的注释）。

这个脚本就是那次的验收工具：干净的临时副本里启动程序，按三种焦点状态各截一对图
（Alt 前 / Alt 后）逐像素比对，再按一次 ↓ 确认"键盘导航仍然点亮焦点环"。

判读：
  · 场景 A 差异为空 → 修复生效（改之前这里会框住整行，是一圈蓝边）。
  · 场景 A 有差异、且差异框等于一行的高度 → 焦点环又回来了。
  · ↓ 之后蓝色像素数明显上涨 → 键盘提示没被一起关掉（无障碍要求，AC-08）。

用法：python alt_probe.py
产物：%TEMP%\\jianlan-rename-smoke\\probe\\*.png
"""

import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import fullshot
import rename_smoke as rs

OUT = os.path.join(rs.WORK, "probe")
VK_ALT, VK_DOWN = 0x12, 0x28
VK_CONTROL, VK_SHIFT = 0x11, 0x10
DWMWA_EXTENDED_FRAME_BOUNDS = 9


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    """窗口的屏幕矩形。取 DWM 的"扩展边框"，比 GetWindowRect 少一圈不可见阴影。"""
    r = wt.RECT()
    dwmapi = ctypes.WinDLL("dwmapi")
    hr = dwmapi.DwmGetWindowAttribute(wt.HWND(hwnd), DWMWA_EXTENDED_FRAME_BOUNDS,
                                      ctypes.byref(r), ctypes.sizeof(r))
    if hr != 0:
        rs.user32.GetWindowRect(wt.HWND(hwnd), ctypes.byref(r))
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def wait_window(pid: int, timeout: float = 25.0) -> dict:
    """等主窗起来。冷启动（自包含包）偶尔超过 rename_smoke 里那 4 秒的固定等待。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        state = rs.probe(pid)
        if state.get("hwnd"):
            return state
        time.sleep(0.5)
    raise SystemExit("窗口没起来")


def tap_alt() -> None:
    rs._assert_foreground()
    rs.user32.keybd_event(VK_ALT, 0, 0, 0)
    time.sleep(0.12)
    rs.user32.keybd_event(VK_ALT, 0, rs.KEYEVENTF_KEYUP, 0)
    time.sleep(0.7)


def focus_blue(path: str) -> int:
    """数一下 Brush.Border.Focus（品牌蓝）的像素。焦点环就靠它显形。"""
    from PIL import Image

    im = Image.open(path).convert("RGB")
    px = im.load()
    return sum(1 for y in range(im.height) for x in range(im.width)
               if px[x, y][2] > 200 and px[x, y][2] - px[x, y][0] > 80 and px[x, y][1] > 140)


def diff(tag: str, a: str, b: str) -> None:
    from PIL import Image, ImageChops

    ia, ib = Image.open(a).convert("RGB"), Image.open(b).convert("RGB")
    box = ImageChops.difference(ia, ib).getbbox()
    if not box:
        print(f"[{tag}] 两张图完全一致（没有多画任何东西）")
        return

    left, top, right, bottom = box
    print(f"[{tag}] 差异 {right - left}x{bottom - top} @ x={left} y={top}")
    for name, img in (("before", ia), ("after", ib)):
        crop = img.crop(box)
        zoom = 4 if (right - left) * 4 <= 1800 else 1
        crop.resize(((right - left) * zoom, (bottom - top) * zoom)).save(
            os.path.join(OUT, f"diff_{tag}_{name}.png"))


def ensure_foreground(pid: int, hwnd: int) -> None:
    """每次合成输入前重新抢一次前台。

    只靠启动时那一次不够：程序刚起来时窗口还在建（缩略图、图标缓存都在跑），
    前台可能在几秒内被别人拿走，然后 SendInput 就打到别人身上了。
    先敲一下 SHIFT 再 SetForegroundWindow —— 见 rename_smoke.focus 的说明。
    """
    for _ in range(6):
        if rs.user32.GetForegroundWindow() == hwnd:
            return
        rs.user32.keybd_event(0x10, 0, 0, 0)
        rs.user32.keybd_event(0x10, 0, rs.KEYEVENTF_KEYUP, 0)
        time.sleep(0.12)
        rs.user32.SetForegroundWindow(wt.HWND(hwnd))
        time.sleep(0.4)
    raise SystemExit("拿不到前台窗口，中止")


def main() -> int:
    os.makedirs(OUT, exist_ok=True)
    rs.stage_app()
    rs.seed()

    proc = rs.launch()
    pid = proc.pid
    state = wait_window(pid)
    hwnd = int(state["hwnd"])
    rect = window_rect(hwnd)
    print("窗口矩形：", rect)

    def shot(name: str) -> str:
        path = os.path.join(OUT, f"{name}.png")
        fullshot.shot(path, rect)
        return path

    # 场景 A：列表有焦点（点一行文件名）后单按 Alt —— 修复后应当毫无变化
    ensure_foreground(pid, hwnd)
    rs.press_combo(VK_CONTROL, VK_SHIFT, 0x32)          # Ctrl+Shift+2 → 列表视图
    time.sleep(1.5)
    ensure_foreground(pid, hwnd)
    rs.click(*rs.name_point(rs.probe(pid)["rows"][1]))
    time.sleep(0.6)
    a1 = shot("a_before")
    tap_alt()
    a2 = shot("a_after")
    diff("a_alt", a1, a2)

    # 场景 B：↓ 导航 —— 焦点环必须还在（AC-08）
    ensure_foreground(pid, hwnd)
    rs.press("down")
    time.sleep(0.6)
    b2 = shot("b_arrow")
    print(f"[b_arrow] 蓝像素：Alt 前 {focus_blue(a1)} → ↓ 后 {focus_blue(b2)}")

    # 场景 C：Alt+← 后退 —— 组合键不能被误吞
    ensure_foreground(pid, hwnd)
    rs.click(*rs.name_point(rs.probe(pid)["rows"][0]))
    rs.press("enter")
    time.sleep(1.2)
    rs.press_combo(VK_ALT, 0x25)
    time.sleep(1.2)
    rows = rs.probe(pid)["rows"]
    print(f"[c_alt_left] 后退后首行：{rows[0]['name'] if rows else '（空）'}")

    rs.user32.PostMessageW(wt.HWND(hwnd), 0x0010, 0, 0)
    time.sleep(1.0)
    if proc.poll() is None:
        proc.kill()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
