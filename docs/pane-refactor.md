# 浏览区重构（PaneView）

- 状态：**S1 已完成**（2026-11-05），S2–S4 待做
- 关联：FR-2 多标签、FR-3 双窗格、ADR-0008（内存重新标定）

---

## 1. 为什么必须先重构

0.1.0 的代码建立在"**一个窗口 = 一个浏览区**"这个假设上：

| 证据 | 形态 |
|---|---|
| `MainWindow.xaml.cs` | 962 行，构造函数 222 行 |
| `MainWindow.FileOps/FileDragDrop/Watcher/Icons/PerfProbe` | 5 个 partial 共 1084 行 |
| `MainViewModel` | 514 行，`_all` / `_entries` / 导航栈 / 选中项全是单份 |
| XAML | 列表叫 `FileList`、状态栏只有一份、`Watcher` 挂在窗口上 |

而 M2 的每一项功能都要问"**这作用于哪个浏览区**"：

- 多标签：每个标签有自己的路径、历史、滚动位置、选中项
- 双窗格：同上，而且两份同时可见
- 收藏拖入：要判断拖到哪个窗格的导航栏
- 标签点右键：菜单要作用于"我点的那一项"，而不是"当前窗口选中的那一项"

没有"浏览区"这个实体，每加一项功能就得在窗口里再写一份 `if (哪个窗格)`。
所以先重构，再上功能。

## 2. 目标形态

```
MainWindow（窗口壳）
├── TabStrip                    ← S3，
├── Grid
│   ├── NavPane（窗口级，共享）
│   └── PaneHost
│       ├── PaneView ×1         ← 现在
│       └── PaneView ×2         ← S4，双窗格
```

```
PaneView（UserControl，浏览区）
├── Toolbar   后退/前进/上/刷新 + 地址栏 + 视图切换 + 设置
├── Header    列头（可拖宽）
├── FileList  虚拟化列表 + 右键菜单 + 空态
└── StatusBar 项数/耗时 · 提示 · 选中统计
```

**状态栏为什么进窗格**：双窗格时两个目录的项数不同，共用一条状态栏要么只能显示活动窗格、
要么得挤成"左 8 项 / 右 1024 项"两段。各自一条最省事，也是 Q-Dir / Total Commander 的做法。

## 3. 四步计划

| 步 | 内容 | 行为变化 | 状态 |
|---|---|---|---|
| **S1** | 机械抽出 `PaneView`；`MainViewModel` → `PaneViewModel` | 仅布局（见 §4） | ✅ 完成 |
| **S2** | 命令表搬进 `PaneView`；引入"活动窗格"与焦点路由 | 无 | 待做 |
| **S3** | 标签栏（VM 保活、View 只留活动那份） | 新增标签栏 | 待做 |
| **S4** | 双窗格（两列 PaneHost、焦点描边、跨窗格拖放） | 新增分屏 | 待做 |

每步都要求：编译 0 警告、119 单测通过、i18n 三项检查 OK、截图对比。

## 4. S1 实际改了什么

### 4.1 文件搬迁

| 原 | 现 | 行数 |
|---|---|---|
| `Views/MainWindow.xaml`（438 行） | 拆成 `MainWindow.xaml`（113）+ `PaneView.xaml`（368） | — |
| `Views/MainWindow.xaml.cs`（962 行） | `MainWindow.xaml.cs`（520）+ `PaneView.xaml.cs`（480） | — |
| `Views/MainWindow.FileOps.cs` | `Views/PaneView.FileOps.cs` | 299 |
| `Views/MainWindow.DragDrop.cs` | `Views/PaneView.DragDrop.cs` | 168 |
| `Views/MainWindow.Watcher.cs` | `Views/PaneView.Watcher.cs` | 129 |
| `Views/MainWindow.Icons.cs` | `Views/PaneView.Icons.cs` | 54 |
| `ViewModels/MainViewModel.cs` | `ViewModels/PaneViewModel.cs` | 514 |
| `Views/MainWindow.PerfProbe.cs` | **留在窗口**（探测整窗），改引用 `Pane.FileList` | 434 |

`PerfProbe` 留下是因为它测的是"整个进程的启动与渲染"，不是"某个窗格的浏览性能"。
它通过 `Pane.FileList` 驱动滚动，接口没变。

### 4.2 边界划分

| 层 | 负责 |
|---|---|
| `MainWindow` | 窗口标题与尺寸、导航栏、快捷键钩子、Ctrl+, 系统热键、设置窗口、启动计时、会话保存 |
| `PaneView` | 工具栏、地址栏、列头、列表、状态栏、文件操作、拖放、目录监听、图标解析、列宽 |

视图模型由宿主创建并注入（`Pane.Attach(vm)`），不由窗格自己 `new`——
双窗格要建几份、什么时候建，是宿主的决定。

### 4.3 两处可见的布局变化

**① 导航栏现在贯穿到窗口顶部**

改前：工具栏横跨整窗，导航栏从工具栏**下方**开始，左侧形成一个台阶。
改后：导航栏顶到客户区顶部，工具栏只在列表这一列。

这是双窗格的必然结果——两个窗格各自需要地址栏，工具栏不可能再横跨整窗。
改后的形态与资源管理器一致。截图对比见 `D:\ac05\png\dark-14-main.png`（改前）与
`ref2-14-main.png`（改后）。

**② 状态栏只覆盖列表区，导航栏底部补了一条等高占位**

改前：状态栏横跨整窗（包含导航栏下方）。
改后：状态栏在窗格内，导航栏底部加一条 `Height={Size.StatusBar.Height}` 的空 Border，
带同样的上边框，让分隔线贯通。状态栏隐藏时占位条同步隐藏（`ApplySettingsToUi`）。

### 4.4 顺带修的隐性债

**键盘焦点完全不可见（FR-10.7 / AC-08 的直接阻塞项）。**
`Controls.Button.xaml` 三个样式与 `Controls.List.xaml` 两个样式都写了
`FocusVisualStyle="{x:Null}"`，把系统虚线框关掉后再没有补任何替代。
纯键盘操作时 Tab 走一遍工具栏、方向键在列表里移动，屏幕上没有任何反馈。

修法：新增 `Design/Styles/FocusVisual.xaml`，两个变体——`FocusVisual.Ring`（按钮，外扩 2px）
与 `FocusVisual.Inset`（列表项，内缩 1px）。颜色取 `Brush.Border.Focus`，
三个主题都有定义，高对比度自动切到 `SystemColors.HighlightColor`。
用 `FocusVisualStyle` 而不是模板内的 Border，因为前者画在 Adorner 层、不参与布局，
焦点进出时控件尺寸不会跳动。

`docs/ui-guidelines.md` 第 234 / 321 / 388 行本来就规定了"2px `Brush.Border.Focus`"和
"禁止使用系统默认虚线框"——**文档是对的，实现是空的**。这次补上的是实现。

## 5. S2 要做什么（下一步）

现状：命令表在 `MainWindow` 上，`Execute` 已经全部指向 `Pane`，
所以语义上已经是"作用于当前窗格的命令"，但表本身还是窗口级。

S2 的三件事：

1. **命令表搬进 `PaneView`**，每个窗格一份。命令天然作用于自己，不需要任何 `if`。
2. **窗口级命令单独留一份**：设置、主题、新建标签、切换窗格、关闭标签。
   快捷键派发时先查活动窗格的表，找不到再查窗口表。
3. **活动窗格**：`PaneView.IsActive` 已经有了（`GotKeyboardFocus` 时置位），
   需要补两件事——宿主维护"同一时刻只有一个活动"，以及 FR-3.5 要求的
   1px `Brush.Border.Strong` 包边（`ui-guidelines.md` 规则 10.2）。

## 6. 验收记录

| 项 | 结果 |
|---|---|
| 编译 | 0 警告 0 错误 |
| 单测 | 119 通过 |
| i18n 检查 | 键一致性 / 生成物同步 / 残留硬编码 三项 OK |
| 冒烟（`D:\ac05\smoke_refactor.py`） | Ctrl+Shift+N 建出目录、F5 刷新、Ctrl+, 打开设置、右键菜单 11 项与键位提示全部正确 |
| 焦点环 | Tab 三次后刷新按钮周围出现 2px 蓝色圆角框（`focus3-3.png`） |
| 性能探针 | `--perf-probe` 正常输出：10000 项、73 fps、私有提交 113.3 MB |
