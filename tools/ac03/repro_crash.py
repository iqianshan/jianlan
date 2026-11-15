"""定点复现 AC-03 的 0xC0000005 崩溃（DUI70.dll）。

8 小时压测在 15.4 分钟处挂掉，WER 记录：Jianlan.exe 0xC0000005，故障模块 DUI70.dll，
同时进程内加载了 msctfui.dll / DUI70.dll / DUser.dll 与搜狗输入法（SogouTSF.ime）。
本脚本按"动作类型"分桶狂刷，命中哪一桶就说明崩溃由哪条路径触发。

用法：
    python tools/ac03/repro_crash.py --mode type  --iters 80
    python tools/ac03/repro_crash.py --mode props --iters 60
    python tools/ac03/repro_crash.py --mode tabs  --iters 60
"""
from __future__ import annotations

import argparse
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_ac03 as R   # noqa: E402

MODES = {
    "type": [("地址栏跳转", R.act_addr_jump), ("搜索", R.act_search),
             ("重命名", R.act_rename), ("新建文件夹", R.act_new_folder),
             ("新建文本", R.act_new_text)],
    "props": [("属性对话框", R.act_props)],
    "tabs": [("标签开关", R.act_tabs)],
    "panes": [("双窗格", R.act_split), ("预览窗格", R.act_preview)],
    "mix": list(R.ACTIONS),
    # 崩溃那一批的原顺序：重命名 → 前进 → 全选 → 属性 → 地址栏 → 搜索 → 重命名
    # 前四桶都没炸，怀疑是"模态框 + 连续打字"的组合，这一桶按原顺序循环。
    "batch18": [("重命名", R.act_rename), ("前进", R.act_nav_fwd), ("全选", R.act_select_all),
                ("属性对话框", R.act_props), ("地址栏跳转", R.act_addr_jump),
                ("搜索", R.act_search), ("重命名", R.act_rename)],
}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--mode", default="type", choices=sorted(MODES))
    ap.add_argument("--iters", type=int, default=60)
    ap.add_argument("--exe", default=str(R.ROOT / "publish" / "Jianlan.exe"))
    ap.add_argument("--work", default=r"D:\ac03")
    args = ap.parse_args()

    exe = Path(args.exe).resolve()
    work = Path(args.work)
    target = R.prepare_work(work, exe)
    sandbox = work / "sandbox"
    actions = MODES[args.mode]

    proc = subprocess.Popen([str(target)], cwd=str(target.parent))
    hwnd = 0
    for _ in range(60):
        time.sleep(0.5)
        hwnd = R.main_window(proc.pid)
        if hwnd:
            break
    if not hwnd:
        print("窗口没起来")
        return 2
    if not R.focus(hwnd):
        print("抢前台失败")
        return 2
    time.sleep(1.0)
    # 落到沙箱（首启恒开主页，走 Ctrl+T → Ctrl+1 → Ctrl+W）
    if sandbox.name.lower() not in R.window_title(hwnd).lower():
        R.chord("ctrl", "t")
        time.sleep(2.0)
        if sandbox.name.lower() not in R.window_title(hwnd).lower():
            R.chord("ctrl", "l")
            R.type_text(str(sandbox))
            R.key("enter")
            time.sleep(1.5)
        R.chord("ctrl", "1")
        time.sleep(0.4)
        R.chord("ctrl", "w")
        time.sleep(1.2)
    R.chord("ctrl", "t")      # 备用标签，防止 Ctrl+W 关掉最后一个标签
    time.sleep(1.5)

    ctx = {"sandbox": sandbox, "view": True, "split": False, "preview": False}
    print(f"[{datetime.now():%H:%M:%S}] 桶 {args.mode} 起跑，{args.iters} 次；标题 {R.window_title(hwnd)}", flush=True)

    last = ""
    for i in range(1, args.iters + 1):
        name, fn = actions[(i - 1) % len(actions)]
        last = name
        print(f"[{datetime.now():%H:%M:%S}] #{i:>3} {name}", flush=True)
        try:
            fn(hwnd, ctx)
        except Exception as exc:                       # 动作自身异常不该中断复现
            print(f"  动作异常：{exc}", flush=True)
        time.sleep(0.2)

        code = proc.poll()
        if code is not None:
            print(f"\n!!! 进程在「{last}」之后退出，exit={code}（0x{code & 0xFFFFFFFF:08X}）", flush=True)
            return 3
        if not R.responsive(hwnd):
            print(f"\n!!! 窗口无响应，发生在「{last}」之后", flush=True)
            return 3
        # 动作可能把窗口带出沙箱，拉回来，否则后续动作落在别处
        if sandbox.name.lower() not in R.window_title(hwnd).lower():
            R.focus(hwnd)
            R.chord("ctrl", "l")
            R.type_text(str(sandbox))
            R.key("enter")
            time.sleep(0.8)
            R.key("esc")

    print(f"\n完成 {args.iters} 次，未崩溃（exit={proc.poll()}）", flush=True)
    return 0


if __name__ == "__main__":
    code = main()
    sys.exit(code)
