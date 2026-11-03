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

## 8. CI 门禁

GitHub Actions 无法提供可信的绝对耗时（共享 runner 噪声大）。因此：

- PR 流水线只做**构建 + 单测 + 启动冒烟**（进程能起来、3 秒内不退出）
- 耗时判定在**本地发布前**由负责人执行脚本并附数据
- 每个发布 tag 的 README 性能表必须附当次实测的 P50/P95 与机器型号

这条规则的目的不是"卡人"，而是防止出现"没人知道现在多慢"的状态。
