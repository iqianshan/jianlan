# AC-05 边界场景验收

对应 `docs/requirements.md` §12 AC-05：10000 项目录、>260 字符长路径、无权限目录、
网络断开、U 盘热拔出、只读介质六类场景不崩溃。

## 1. 为什么要脚本化

边界场景的失败形态是"异常被吞掉后界面空白"或"偶发崩溃对话框"，手点一遍既不可重复，
也留不下证据。`run_ac05.py` 用 `--perf-probe` 驱动真实导航路径（`NavigateAsync` +
首屏渲染），进程自行退出后解析 `perf-probe.json` 与 `crash.log`，每条结论都有落盘依据。

## 2. 准备 fixture

10k 项目录生成约 20 秒，ACL 需要所有者权限，因此不在脚本里生成，先手动准备：

```powershell
# 10000 项（9950 文件 + 50 目录）
.\tools\perf-fixture\New-TestDirectory.ps1 -Path D:\ac05\10k -Count 10000 -Force

# >260 字符长路径（10 层，目录 306 字符 / 文件 328 字符）
# 需要系统 LongPathsEnabled=1，否则创建阶段就会失败
python - <<'EOF'
import os
p = r"D:\ac05\longpath"
for i in range(10):
    p = os.path.join(p, f"L0123456789abcdefghijklmn_{i:02d}")
os.makedirs(p, exist_ok=True)
open(os.path.join(p, "长路径下的测试文件_2026_Q3.txt"), "w").close()
EOF

# 无权限目录：拒绝 Everyone，任何访问都抛 UnauthorizedAccessException
New-Item -ItemType Directory D:\ac05\noperm -Force | Out-Null
icacls D:\ac05\noperm /inheritance:r /deny '*S-1-1-0:(OI)(CI)(F)'

# 只读介质模拟：可读不可写
New-Item -ItemType Directory D:\ac05\readonly -Force | Out-Null
'x' | Set-Content D:\ac05\readonly\a.txt
'x' | Set-Content D:\ac05\readonly\b.txt
icacls D:\ac05\readonly /inheritance:r /grant '*S-1-1-0:(OI)(CI)(RX)' /deny '*S-1-1-0:(OI)(CI)(WD,AD)'

# 可移除卷模拟内容
New-Item -ItemType Directory D:\ac05\removable -Force | Out-Null
1..2000 | ForEach-Object { New-Item -ItemType File ("D:\ac05\removable\usb_{0:D5}.dat" -f $_) -Force | Out-Null }
```

## 3. 运行

```powershell
publish 后执行：
python tools/ac05/run_ac05.py                # 六类全跑，约 1~2 分钟（网络场景可能等 20s）
python tools/ac05/run_ac05.py --only S1,S3   # 只跑指定场景
```

结果写到 `tools/ac05/results/ac05-<时间>.json`。

## 4. 判定口径

PASS 需同时满足：

1. 进程在超时前自行退出（不是被 kill）；
2. 退出码 0；
3. `crash.log` 无新增；
4. `perf-probe.json` 生成且 `error` 为空。

另外三列属于"预期失败"：无权限目录、断网路径、盘符消失后再导航必须给出 `navigateError`。
若 `itemCount=0` 且 `navigateError` 为空，界面会显示成空目录、用户无从判断，记 WARN。

## 5. 已知局限（报告须原样引用）

| 场景 | 模拟方式 | 没覆盖到什么 |
|---|---|---|
| 网络断开 | `\\192.0.2.1\share`（TEST-NET-1 保留地址，不可路由） | 已建立连接后链路中断（TCP 半开、SMB 重连）；真实断网时其他功能的表现归 AC-07 |
| U 盘热拔出 | `subst X:` 盘符挂载/撤销 | USB 驱动栈的延迟 I/O 与"设备未就绪"错误码；真实 U 盘拔出后句柄失效的时序 |
| 只读介质 | ACL 只读目录（可读不可写） | 卷级只读（`ERROR_WRITE_PROTECT`）路径；本机无光驱，非管理员无法挂载只读 VHD |

以上三项需在具备实体介质的环境补测，脚本本身可直接复用。
