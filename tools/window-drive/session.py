"""走查会话驱动：启动应用 → 依次执行动作 → 关闭，全程在同一个进程里。

为什么必须放一个进程里：工具调用结束时子进程会被回收，分成两次调用就会出现
"未找到 pid=N 的可见窗口"。所以启动、操作、截图、关闭要一次做完。

用法：
    python session.py <exe> <步骤...>

步骤语法（每个参数是一步）：

    info                      打印主窗口矩形
    move x y w h              搬到指定位置并定尺寸
    front                     抢到前台
    capture out.bmp           抓窗口矩形（含浮层）
    screen out.png            整屏截图（含独立 Popup HWND）
    lclick x y / sclick x y   真实左键，客户区 / 屏幕坐标
    rclick x y / dclick x y / mclick x y
    keys ctrl+,               真实按键组合（见 window_drive.py 的 combos 表）
    type 文本                 逐字符 Unicode 输入
    sendkey 0x0D              投递 WM_KEYDOWN（不动前台，地址栏回车用这个）
    wheel delta times x y     滚轮
    rect small                量该进程最小的可见窗口（弹出层）
    windows                   列出该进程全部可见顶层窗口
    T:<标题> <动作...>        这一步按标题找窗口（设置窗、对话框）
    set:VAR=VALUE             之后的步骤都带这个环境变量
    uia:<行序号>              按行序号选侧栏（AutomationId=QuickAccessList）
    sleep:<秒>

踩过的坑写在 docs/ac06-visual-acceptance.md 与 docs/ui-guidelines.md 规则 7.16 / 7.17。
"""

import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
WD = os.path.join(HERE, "window_drive.py")
SHOT = os.path.join(HERE, "fullshot.py")
UIA = os.path.join(HERE, "uia_select.ps1")


def call_wd(pid, args, env):
    r = subprocess.run(
        [sys.executable, WD, str(pid), *args],
        capture_output=True, text=True, errors="replace", env=env,
    )
    out = (r.stdout or "").strip()
    err = (r.stderr or "").strip()
    print(f"[wd {' '.join(args)}] {out}{(' ERR ' + err) if err else ''}")
    return out


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    exe = sys.argv[1]
    steps = sys.argv[2:]
    proc = subprocess.Popen([exe], cwd=os.path.dirname(os.path.abspath(exe)))
    env = dict(os.environ)

    # 等主窗口出现（最多 30 秒）
    ready, info = False, ""
    for _ in range(60):
        time.sleep(0.5)
        info = subprocess.run(
            [sys.executable, WD, str(proc.pid), "info"],
            capture_output=True, text=True, errors="replace", env=env,
        ).stdout
        if "hwnd=" in info:
            ready = True
            break
    print(f"pid={proc.pid} ready={ready} {info.strip() if ready else ''}")

    for step in steps:
        if step.startswith("sleep:"):
            time.sleep(float(step[6:]))
            continue

        if step.startswith("set:"):
            key, _, value = step[4:].partition("=")
            env[key] = value
            print(f"[env] {key}={value}")
            continue

        if step.startswith("screen:"):
            r = subprocess.run(
                [sys.executable, SHOT, step[7:]],
                capture_output=True, text=True, errors="replace", env=env,
            )
            print(f"[shot] {(r.stdout or '').strip()} {(r.stderr or '').strip()}")
            continue

        if step.startswith("uia:"):
            parts = step[4:].split()
            args = [
                "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", UIA, "-TargetPid", str(proc.pid), "-Index", parts[0],
            ]
            if len(parts) > 1:
                args += ["-ListId", parts[1]]
            r = subprocess.run(args, capture_output=True, text=True, errors="replace", env=env)
            print(f"[uia {parts[0]}] {(r.stdout or '').strip()} {(r.stderr or '').strip()[:200]}")
            time.sleep(1.2)
            continue

        body = step
        step_env = dict(env)
        if step.startswith("T:"):
            title, _, body = step[2:].partition(" ")
            step_env["WD_TITLE"] = title
            step_env.pop("WD_HWND", None)

        call_wd(proc.pid, body.split(), step_env)

    call_wd(proc.pid, ["close"], env)
    for _ in range(20):
        if proc.poll() is not None:
            break
        time.sleep(0.3)
    print(f"exit={proc.poll()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
