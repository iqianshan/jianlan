# 启动性能门禁

> 对应 NF-01 / NF-08。本文件是"启动够不够快"的唯一判定依据，任何与之冲突的口头结论均无效。

## 1. 指标定义

启动包含三个不同的时间点，不能用同一个数字混称：

| 指标 | 定义 | 埋点 | 当前实测 |
|---|---|---|---|
| **T-render** | 进程启动 → 首帧绘制完成 | `render` | ~380 ms |
| **T-interactive** | 进程启动 → 窗口可响应输入（Loaded 完成） | `loaded` | ~560 ms |
| **T-content** | 进程启动 → 文件列表首次填充 | `firstcontent` | ~600 ms |

**门禁采用 T-interactive（`loaded`）。** 理由：窗口画出来了但点了没反应，用户判定为"卡"，
不能算启动完成。T-render 只用于定位问题，不作为验收依据。

## 2. 阈值

| 版本 | P50 | P95 | 判定 |
|---|---|---|---|
| 目标 | ≤ 700 ms | ≤ 900 ms | 通过 |
| 警戒 | 700–800 ms | — | 通过但必须提交优化说明 |
| 阻塞 | > 800 ms | — | 禁止合并 |

> 原目标为 400 ms，M0 实测后调整为 700 ms。依据见 §5。

## 3. 埋点实现

`src/Lingpane.Core/Diagnostics/StartupTrace.cs`。各阶段含义：

| 标记 | 位置 | 已包含 |
|---|---|---|
| `begin` | `App` 构造函数 | CLR 启动、程序集加载之后的进程内起点 |
| `resources` | `OnStartup` 开头 | App.xaml 资源字典合并（DesignTokens / 主题 / 样式） |
| `fontscale` | `FontScaleService.Apply` 之后 | 字号与字体度量下发 |
| `window` | `new MainWindow()` 之后 | 窗口构造（模板、样式系统首次初始化） |
| `render` | `CompositionTarget.Rendering` 首次回调 | 首次排版与绘制 |
| `loaded` | `Loaded` 事件 | 激活流程（含 IME 上下文） |
| `show` | `Show()` 返回 | — |
| `firstcontent` | 首次目录枚举完成 | 磁盘枚举 + 列表绑定 |

日志落盘位置：`%LOCALAPPDATA%\Lingpane\logs\startup.log`，一行一次启动。

**约束**：`Mark` 路径上不得有磁盘 IO。`Flush` 只在首帧之后调用一次。

## 4. 测量方法

```powershell
dotnet publish src/Lingpane.App -c Release -r win-x64 --self-contained false -o publish
pwsh tools/startup-bench/Measure-Startup.ps1 -Exe publish/Lingpane.exe -Runs 20
```

脚本采集两个值：

- **outer**：`Process.Start` → 主窗口句柄出现。受 10 ms 轮询粒度影响，仅参考。
- **inner**：`startup.log` 的 `total`。这是门禁依据。

注意事项：

1. **必须用 Release + ReadyToRun 测**。Debug 无优化，数字无意义。
2. 脚本不清系统文件缓存，测的是"已运行过"的稳定值。首次冷盘单独记录，不参与判定。
3. 测量期间不要跑其他重负载程序。P95 对系统负载极敏感。

### 4.1 测量有效性前置条件（脚本强制）

WPF 的启动成本主要是 CLR 与框架程序集加载。内存紧张时这些页被 Memory Compression
压缩或换出，每次启动都要解压或调页，耗时可以从 578 ms 涨到 800 ms 以上——
**这段差异与代码无关，但会误判为性能回归**。

因此脚本在执行前先校验环境，不合格直接 `exit 5`，不输出判定：

| 条件 | 阈值 | 不合格时的行为 |
|---|---|---|
| 可用内存占比 | ≥ 35% | 跳过，退出码 5 |
| CPU 负载 | ≤ 50% | 跳过，退出码 5 |

退出码约定：

| 码 | 含义 |
|---|---|
| 0 | 通过 |
| 2 | 未通过（代码或基线变化） |
| 3 | P50 通过、P95 抖动超标 |
| 4 | 未采集到埋点（GUI 环境不支持） |
| 5 | **环境不合格，测量无效** |

需要强制测量时加 `-IgnoreEnvironment`。

## 5. 基线数据（2026-11-03）

同机对照，Release + R2R，各 3–5 次：

| 方案 | 到 Loaded | 说明 |
|---|---|---|
| WinForms 最小空窗口 | 214–330 ms | 同环境下限参考 |
| WPF 最小空窗口（默认） | 1030–1120 ms | **框架基线** |
| WPF 最小空窗口（禁 IME） | 541–665 ms | 差异来自输入法上下文 |
| WPF HwndSource 直建（绕过 Application/Window） | 978–1051 ms | 证明慢在渲染管线初始化，不在窗口类本身 |
| **Lingpane M0（禁 IME）** | **574–649 ms（20 次：P50 578 / P95 639，min 542 / max 727）** | 业务代码增量约 0–60 ms |

结论：

1. **1.1 秒是 WPF 在本机的框架基线**，不是业务代码写坏了。
2. 单项最大成本是 **中文输入法上下文创建（约 500 ms）**，且同时吃掉约 43 MB 工作集。
3. 绕开 `Application` / `Window` 类没有收益，慢的是首次渲染管线初始化。
4. 业务代码自身开销很小（资源字典 100–150 ms 是唯一可压缩项）。

**同日 15:20 的复测教训（重要）：** 连续跑完 5 轮 10000 项测量后，系统可用内存降到
3.8 / 14.8 GB（26%）、CPU 70%，同一份代码实测 P50 变为 789 ms，P95 达 1601 ms。
用 `git worktree` 检出初始 commit 的版本在同批次测得 P50 1028 ms——
**两者同样变慢，证明差异来自环境而非代码**。

结论：跨时段的启动耗时数字不可比。这也是 §4.1 强制环境校验的由来。

## 6. 已落实的优化

| 优化 | 收益 | 位置 |
|---|---|---|
| 启动期关闭 IME | **约 −500 ms，−43 MB** | `App.OnStartup` |
| ReadyToRun 发布 | 减少 JIT，首次运行稳定 | `Lingpane.App.csproj` |
| `SatelliteResourceLanguages=en` | 减少附属程序集探测 | `Directory.Build.props` |
| 图标按扩展名而非按文件查询 | 滚动零磁盘 IO | `ShellIconProvider` |
| 列表用 `IReadOnlyList` 整体替换 | 避免 N 次 `CollectionChanged` | `MainViewModel` |

## 7. 待验证的优化（按收益排序）

| 候选 | 预估收益 | 风险 |
|---|---|---|
| 拆分资源字典，首屏只加载必需部分 | −60~100 ms | 中：需保证首帧样式完整 |
| `TieredCompilation=false` + 全量 R2R | −30~80 ms | 低：需实测确认 |
| 延迟初始化控件样式（`Controls.*.xaml` 按需） | −30~50 ms | 中：首次打开列表可能闪一下 |
| 窗口构造后再预热字体 | 未知 | 低 |

**任何优化若不能通过一次 20 次测量的 P50/P95 对比，不予采纳。**

## 8. 门禁：双层阈值

共享 runner 的绝对耗时不可信，人工检查又不足以防渐进退化。因此门禁分两层，
**两层都用 `baseline.json` 做相对比较，不做绝对耗时断言**。

### 8.1 基准文件

仓库根目录 `baseline.json`，记录"当前认定的正常水平"：

```json
{
  "schemaVersion": 1,
  "capturedAt": "2026-11-03",
  "machine": "Ryzen 7 PRO 5850U / 32GB / Win11 26100",
  "metric": "inner-loaded-ms",
  "runs": 20,
  "p50": 578,
  "p95": 639,
  "ciToleranceFactor": 2.5,
  "localToleranceFactor": 1.2
}
```

`metric` 取值 `inner-loaded-ms`，即 `startup.log` 中进程内 `loaded` 的耗时。
**不含进程创建开销**，因此对 runner 机型差异的敏感度低于外部计时。

### 8.2 CI：拦灾难性退化

`perf-gate` job 在每次 PR 执行：

1. 跑 3 次，取中位数
2. 与 `baseline.p50 × ciToleranceFactor（2.5）` 比较
3. 超出即 job 失败

宽松阈值是刻意的：CI 只负责拦住"引入同步 IO、加载大资源、加了启动期初始化"
这类数量级退化（600ms → 2000ms）。**渐进式退化（每次 +5%）由本地门禁负责。**
若 GUI 环境不支持（日志未生成），job 输出 warning 并跳过，不阻塞合并。

### 8.3 本地：拦渐进退化

发布前由负责人执行：

```powershell
pwsh tools/startup-bench/Measure-Startup.ps1 -Exe publish/Lingpane.exe -Runs 20 `
     -BaselinePath baseline.json
```

判定规则：`P50 ≤ baseline.p50 × localToleranceFactor（1.2）` 即 694 ms 以内通过。
超出时必须给出原因并更新 baseline，**不允许直接改阈值绕过**。

### 8.4 更新基准的规则

- 只有在**功能合并、性能确认可接受**之后才更新 `baseline.json`
- 每次更新必须同时改 `capturedAt` 与 `p50/p95`，并在 commit message 写明变化原因
- 阈值因子（2.5 / 1.2）非经 ADR 不得调整

这条规则的目的不是"卡人"，而是防止出现"没人知道现在多慢"的状态。

---

## 9. 列表性能与内存（NF-03 / NF-04 / NF-05）

启动快只解决"第一印象"，大目录卡顿才是每天都会遇到的问题。本节是 M0.5 的实测结论。

### 9.1 测量方法

```powershell
# 1. 生成 10000 项测试目录（约 20 秒）
pwsh tools/perf-fixture/New-TestDirectory.ps1 -Path D:\perf-10k -Count 10000 -Force

# 2. 测量（5 次取中位）
pwsh tools/perf-fixture/Measure-List.ps1 -Exe publish/Lingpane.exe -Path D:\perf-10k -Runs 5
```

实现方式：应用带 `--perf-probe <目录>` 参数启动后，加载目标目录、等待首屏、
逐帧驱动滚动并采样 `CompositionTarget.Rendering` 的帧间隔，最后写出 JSON 并退出。
测的是真实 UI 路径，不是模拟环境。

滚动步长为**每帧 3 行**（按项实际高度计算），模拟滚轮或拖动内容。
不使用"拖动滚动条跳转"——那会在一帧内把数百项拉进视口，测到的是极端场景，
不能代表日常滚动的帧率。

### 9.2 实测数据（2026-11-03，5 次中位，Release + R2R）

| 场景 | 条目 | 枚举 | 首屏 | 帧率 | P95 帧 | 私有提交 | 工作集 |
|---|---|---|---|---|---|---|---|
| 空目录 | 0 | — | 99 ms | — | — | 93.0 MB | 134.5 MB |
| 100 项 | 100 | — | 236 ms | — | — | 99.9 MB | 143.6 MB |
| 10000 项（只加载） | 10000 | 64 ms | 218 ms | — | — | 102.6 MB | 141.8 MB |
| **10000 项（滚动 240 帧）** | **10000** | **66 ms** | **233 ms** | **125.8 fps** | **15.7 ms** | **114.6 MB** | 166.6 MB |

判定：

| 指标 | 实测 | 阈值 | 结论 |
|---|---|---|---|
| NF-03 首屏 | 233 ms | ≤ 800 ms | ✅ 余量 3.4 倍 |
| NF-04 帧率 | 125.8 fps | ≥ 55 fps | ✅ 余量 2.3 倍 |
| NF-05 私有提交 | 114.6 MB | ≤ 120 MB | ✅ **余量仅 5.4 MB** |
| 句柄 | 951 | < 10% 增长 | ✅ 滚动前后无增长（951 → 951） |
| GDI 对象 | 56 | < 2000 | ✅ |

### 9.3 内存口径（必须固定，否则数字无法比较）

| 口径 | 值 | 说明 |
|---|---|---|
| **私有提交**（NF-05 判定用） | 114.6 MB | 对应任务管理器"提交大小"列，是本项目采用的判定基准 |
| 工作集（含共享） | 166.6 MB | 含 `PresentationFramework` 等与其他 WPF 进程共享的页，虚高 50 MB，仅参考 |

用含共享页的工作集会得出"超标 46 MB"的错误结论——同一台机器上再开一个 WPF 程序，
这 50 MB 不会翻倍，因为它本来就是共享的。

### 9.4 三条结论

**1. 10000 项只增加 9.6 MB（93.0 → 102.6）。** 说明虚拟化与 `FileEntry` 的轻量设计是有效的：
每个条目约 1 KB，且只创建可视区域内的项容器。这项风险可以关闭。

**2. 基础占用 93 MB 才是大头，也是 M1 前唯一值得优化的项。** 空目录就要 93 MB，
来自 WPF 框架、资源字典与运行时。它决定了内存上限的天花板：
按当前数值，10000 项目录已经吃掉 120 MB 预算的 95%。

**3. 首屏 233 ms 里枚举只占 66 ms。** 剩余约 167 ms 是 WPF 布局与 `ItemsControl` 首次填充，
属于框架成本。想再压只能减少行模板复杂度，收益有限。

### 9.5 遗留风险

- **内存余量只有 5.4 MB。** M1 增加标签页、地址栏、右键菜单后必然突破 120 MB。
  开工前需要二选一：降基础占用（延迟加载主题资源、精简资源字典），或按 ADR 修订 NF-05 分档。
- **首屏有单次长帧**（max 92.9 ms），出现在滚动跨越到新区域时，来自项容器批量创建。
  平均帧率不受影响，M1 若加入缩略图需重新评估。

