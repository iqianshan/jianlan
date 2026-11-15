r"""把压测从当前 shell 里脱手启动。

直接在该 shell 的后台跑，会话一结束任务会被一起收走；8 小时的压测必须活过这一轮对话。
用 DETACHED_PROCESS + CREATE_BREAKAWAY_FROM_JOB 另起一个独立进程组，父进程退出后
它不受影响，日志仍然由 run_ac03.py 自己写到 tools/ac03/results/。

用法：
    python tools/ac03/detach_start.py --hours 8 --cycle 45 --sample 60
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = Path(__file__).with_name("run_ac03.py")

DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000


def main() -> int:
    args = sys.argv[1:] or ["--hours", "8"]
    cmd = [sys.executable, str(SCRIPT), *args]
    out_path = ROOT / "tools" / "ac03" / "results" / "detach-stdout.log"
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out = open(out_path, "ab", buffering=0)
    proc = subprocess.Popen(
        cmd,
        cwd=str(ROOT),
        stdout=out,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        creationflags=DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP,
        close_fds=True,
    )
    print(f"已启动 pid={proc.pid}  {Path(sys.executable).name} {' '.join(args)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
