# 参与开发

## 先读这三份

| 文档 | 什么时候必须读 |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | 任何改动之前。分层依赖不可逆 |
| [`docs/ui-guidelines.md`](docs/ui-guidelines.md) | 碰任何 XAML。本文件是强制规范，不是建议 |
| [`docs/performance-gate.md`](docs/performance-gate.md) | 改启动路径、列表、图标、枚举时 |

## 硬性要求

1. **不引入启动期 IO**。首帧之前不得读磁盘、开数据库、调 Shell。新代码要先回答"能不能挪到 `Loaded` 之后"
2. **XAML 无颜色字面量、无魔法数字**。全部走 token，见界面规则 §2
3. **`Jianlan.Core` 不引用 WPF**。它必须能脱离 UI 单测
4. **三份主题字典键集保持一致**（当前 83 键）。加颜色必须三份同时加
5. **提交前跑启动测量**并在 PR 描述里贴 P50/P95

## 提交信息

```
<类型>: <一句话说明>

类型：feat | fix | perf | docs | refactor | test | chore
```

例：

```
perf: 图标缓存改为按扩展名索引

滚动 10000 项时不再触发 SHGetFileInfo，实测帧率从 48 提升到 61。
```

## PR 自检

复制 [`docs/ui-guidelines.md`](docs/ui-guidelines.md) §14 的清单，逐条勾选。

## 不接受

- 为"看起来更现代"引入模糊、阴影、过渡动画
- 未经性能预算评审就添加常驻后台任务
- 修改 `docs/` 里的指标数字而不附测量数据
- 一次 PR 混入多个不相关改动

## 开发环境

需要 .NET 9 SDK。不需要 Visual Studio（VS Code + C# DevKit 足够），
也不需要单独安装 Windows SDK——相关引用包由 NuGet 还原。

## 构建、打包与测量

```powershell
# 编译
dotnet build

# 本地调试用发布（框架依赖，体积小，需本机已装 .NET 9 桌面运行时）
dotnet publish src/Jianlan.App -c Release -r win-x64 --self-contained false -o publish

# 出正式发布的便携包（AC-09）：自包含运行时 + portable.flag + zip + SHA256
pwsh tools/release/publish.ps1

# 测启动
pwsh tools/startup-bench/Measure-Startup.ps1 -Exe publish/Jianlan.exe -Runs 20

# 测列表：先生成 10000 项测试目录，再测量
pwsh tools/perf-fixture/New-TestDirectory.ps1 -Path D:\perf-10k -Count 10000
pwsh tools/perf-fixture/Measure-List.ps1 -Exe publish/Jianlan.exe -Path D:\perf-10k
```

`tools/release/publish.ps1` 的产物在 `dist/`：`Jianlan-<版本>-portable-win-x64.zip`
与配套 `.sha256`。包内的 `portable.flag` 让程序进入便携模式，设置与日志落在解压目录的 `data\` 里。

### 诊断开关

排查界面类问题时用得到，正常启动都不带：

| 变量 | 作用 |
|---|---|
| `JIANLAN_PREVIEWDEBUG=1` | 预览链路（选中 → 合并窗口 → 读盘 → 解码）的分段时间戳写进 `data/logs/app.log`，前缀 `preview.dbg` |
| `JIANLAN_INSTANCE_TAG=<串>` | 单实例互斥体与管道加一个后缀。用户正开着正式副本时，靠它在一边起一份隔离实例做真窗口实测，否则第二份会立刻转交参数并退出——表现为"脚本说窗口没起来" |
| `JIANLAN_EXE_DIR=<目录>` | 给 `tools/window-drive` 下的冒烟脚本指定被测 exe 所在目录（默认取 `publish/`） |

真窗口验收脚本在 `tools/window-drive/`：`quicklook_smoke.py`（空格预览 12 项）、
`rename_smoke.py`（就地改名）、`clipboard_smoke.py`（剪贴板）、`alt_probe.py`（单按 Alt 不冒焦点环）、
`preview_rapid_probe.py`（连按方向键时画面是否跟着换）。另有 `preview_lag_probe.py` /
`preview_paint_probe.py` / `preview_long_probe.py` / `preview_pane_probe.py` 四个纯诊断探针，
只在查"预览慢/卡"时用（最后一个测的是预览窗格里连翻 PDF 时 UI 线程的响应）。
它们都用 `SendInput` 打真实按键，跑之前请勿操作鼠标键盘。

> **脚本副作用**：打包结束会删掉 `dist/` 下同名的已解压目录，里面改过的 `data\settings.json`
> 会一起消失。要保留先在跑之前拷走。
>
> **便携版图标**：任务栏图标从 exe 自身的 `#32512` 资源按 DPI 取图，并同时写 `WM_SETICON`
> 与窗口类图标。改图标前先读 [docs/app-icon.md](docs/app-icon.md) —— 那里记着三次返工的教训。
