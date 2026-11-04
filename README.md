# Lingpane · 棂窗

轻量、原生、启动即用的 Windows 文件管理器。双窗格、多标签、彩色标签，基于 .NET 9 + WPF。

> 棂（窗棂，中式窗格）＋ pane（英文窗格）——两个语言里指的是同一个东西。

**当前状态：M1 收尾阶段，尚未发布。** 已覆盖日常浏览与基本的文件管理操作；
多标签、双窗格、彩色标签在 M2。

---

## 实测性能

测量环境：AMD Ryzen 7 PRO 5850U / 16GB / Windows 11 / Release + ReadyToRun。

### 启动（20 次取值，系统空闲）

| 方案 | 冷启动 P50 | 备注 |
|---|---|---|
| **Lingpane（当前）** | **694–769 ms（用户感知）** | P95 776–883 ms；含进程创建与运行时初始化 |
| Lingpane 应用内 | 511–569 ms | P95 540–619 ms；`App` 构造 → 窗口可交互，用于回归比对 |
| WPF 最小空窗口 | ~1100 ms | 同环境对照，说明 1.1s 是框架基线而非业务代码 |
| WinForms 最小空窗口 | ~250 ms | 同环境对照 |
| Files（社区版） | 1.5–3 s | 官方 issue 与实测反馈 |

上表第一行是**用户实际等待**（双击图标 → 窗口出现并响应），第二行是应用内埋点，
不含进程创建与 CLR 加载，两者相差约 200 ms。对外引用一律用第一行。

启动耗时的最大单项来自**中文输入法上下文的创建**（约 500 ms）。文件浏览不需要输入，
因此启动阶段关闭 IME，首次进入文本输入场景时再启用。

> 早期目标为 P50 ≤ 400 ms。M0 实测表明该指标在 WPF 上不可达（空窗口即 1.1 s），
> 现按实测调整为 **用户感知 P50 ≤ 900 ms / 埋点 P50 ≤ 700 ms**，并保留门禁防止回退。
> 区间是两个测量轮次的范围：同一台机器、同一份二进制，CPU 背景负载 29% 与 39%
> 就能差出 58 ms，所以对外给区间而不是单点数字。
>
> 启动耗时受系统内存压力影响极大：可用内存降到 26% 时同一份代码会测到 789 ms。
> 测量脚本会先校验环境（可用内存 ≥ 50%、CPU ≤ 80%），不达标时仍出数据，
> 但标记为参考值、不作出门禁判定，避免把系统负载当成代码回归。

### 列表（10000 项目录，5 次中位）

| 指标 | 实测 | 目标 |
|---|---|---|
| 目录枚举 | 66 ms | — |
| 首屏（枚举+渲染） | 233 ms | ≤ 800 ms |
| 滚动帧率 | 125.8 fps | ≥ 55 fps |
| 私有提交内存 | 114.6 MB（优化后 110.7 MB） | ≤ 150 MB（ADR-0002） |
| 相对增量（减空窗口） | 29.7 MB | ≤ 45 MB（NF-05R） |

10000 项相比空目录只增加 9.6 MB，虚拟化按预期工作。
详细方法与口径见 [`docs/performance-gate.md`](docs/performance-gate.md) §9。

## 已完成（M0 / M0.5）

- 目录枚举（`FileSystemEnumerable`，零多余分配）
- 系统图标异步提取（按扩展名，不访问磁盘）
- 列表虚拟化（Recycling + Pixel 滚动），10000 项实测通过
- 启动耗时埋点与测量脚本（`tools/startup-bench`）
- 列表性能探针与测试目录生成（`tools/perf-fixture`）
- 设计 Token 体系（四级表面 / 三主题 / 8–24pt 字号缩放）

## 已完成（M1）

**浏览**

- 前进/后退/上级、面包屑点击跳转、地址栏输入路径（`Ctrl+L` 聚焦）
- 详细信息 / 列表两种视图、四列排序、文件夹优先且不随降序反转
- 列宽可拖拽（日期/类型/大小三列的右边缘），宽度记忆进设置
- 目录真实图标：Downloads、桌面这类已知文件夹与带 `desktop.ini` 的目录显示专属图标
  （后台解析，不占首帧；ADR-0006）
- 左侧常用目录、隐藏文件开关、扩展名显示开关
- 单实例：再次启动转交参数到已有窗口，不新开进程
- 支持 `Lingpane.exe <目录>` 直接打开指定位置

**文件操作**

- 新建文件夹、重命名、复制、剪切、粘贴、删除到回收站、永久删除
- 名称校验（非法字符、系统保留名、结尾点）、重名自动追加 `(2)`
- 右键菜单（键位提示与快捷键同源）、属性、复制完整路径
- 拖放：拖入复制/移动（同卷移动、跨卷复制，`Ctrl`/`Shift` 强制）、
  拖到文件夹行放进该文件夹、拖出到其他程序
- 当前目录变更监听（`FileSystemWatcher` + 400ms 去抖），外部改动自动刷新并保留选中

**设置**

- 三主题、界面字体与字号（8–24pt）、界面密度
- 默认视图与默认排序、显示隐藏文件、显示扩展名、删除前确认
- 导航栏与状态栏开关、记住上次目录、清空历史

**快捷键**：见 [`docs/shortcuts.md`](docs/shortcuts.md)，键位表集中在
`src/Lingpane.App/Input/KeyBindings.cs`，窗口消息（物理键码）为主入口、`PreviewKeyDown` 兜底。

## 规划中

多标签页 · 双窗格 · 彩色标签 · 收藏书签 · 工作区 · 预览窗格 · 设置中心

完整需求见 [`docs/requirements.md`](docs/requirements.md)，界面规范见 [`docs/ui-guidelines.md`](docs/ui-guidelines.md)。

## 构建

```powershell
# 需要 .NET 9 SDK
dotnet build

# 发布（启用 ReadyToRun）
dotnet publish src/Lingpane.App -c Release -r win-x64 --self-contained false -o publish

# 测启动
pwsh tools/startup-bench/Measure-Startup.ps1 -Exe publish/Lingpane.exe -Runs 20

# 测列表：先生成 10000 项测试目录，再测量
pwsh tools/perf-fixture/New-TestDirectory.ps1 -Path D:\perf-10k -Count 10000
pwsh tools/perf-fixture/Measure-List.ps1 -Exe publish/Lingpane.exe -Path D:\perf-10k
```

## 设计原则

1. **首帧优先**：窗口先出现，内容后填充
2. **一切延迟**：启动路径上只做必须做的事
3. **失败不崩溃**：任何非关键路径异常都不得终止进程
4. **零常驻**：不装服务、不开机自启、不后台驻留

详见 [`docs/ui-guidelines.md`](docs/ui-guidelines.md) 与 [`docs/adr/`](docs/adr/)。

## 许可证

[Apache-2.0](LICENSE)。Copyright 2026 谦山。
