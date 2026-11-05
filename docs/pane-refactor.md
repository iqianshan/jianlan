# 浏览区重构（PaneView）

- 状态：**S1 / S2 / S3 / S4 已完成**（2026-11-05）
- 关联：FR-2 多标签、FR-3 双窗格、ADR-0008（内存重新标定）、ADR-0009（标签 = 一对窗格）

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
| **S2** | 命令表搬进 `PaneView`；快捷键两段路由 | 无 | ✅ 完成 |
| **S3** | 标签栏（VM 保活、View 只留活动那份） | 新增标签栏 | ✅ 完成 |
| **S4** | 双窗格（两列 PaneHost、焦点描边、跨窗格拖放） | 新增分屏 | ✅ 完成 |

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

## 5. S2 做了什么

命令按"状态属于谁"分家，而不是按"UI 放在哪"：

| 表 | 位置 | 命令 |
|---|---|---|
| 窗格表（20 条） | `PaneView.Commands.cs` | 上/后退/前进/刷新、视图切换、地址栏聚焦、打开/新建/重命名/删除/复制/剪切/粘贴/复制路径/属性/右键菜单/全选、设置（转发） |
| 窗口表（2 条） | `MainWindow.xaml.cs` | 主题循环、打开设置 |

判据是"CanExecute 读的是哪份状态"：窗格的路径与选中项各有一份，所以那 20 条必须在窗格里；
主题与应用级设置只有一份，留在窗口。

**设置按钮**在窗格工具栏上，但窗口是应用级的。窗格注册一条 `OpenSettings` 命令，
Execute 只发 `SettingsRequested` 事件，由窗口订阅并 `Show()`——窗格不碰别的窗口。

**快捷键两段路由**（`MainWindow.Dispatch`）：

```
按键 → 窗格表有这条命令吗？
        有 → 窗格执行（它自己知道当前目录与选中项）
        没有 → 窗口表执行
```

判据刻意用"**注册在哪张表**"而不是"此刻能不能执行"。用可执行性判断会让 F5 在加载中
被误判给窗口表（那里根本没有 F5），也会让"删除"在没有选中时落到别处。

`CommandRegistry` 为此补了 `TryGet`——原来的 `Get` 走不到"没注册"和"不可执行"的区分。

**还没做的**：`IsActive` 只有置位逻辑，没有视觉。单窗格时没意义，
S4 做双窗格时一起补 FR-3.5 的 1px `Brush.Border.Strong` 包边（`ui-guidelines.md` 规则 10.2）。

## 6. S3 做了什么

### 6.1 一份视图壳，N 份视图模型

标签切换**不重建控件树**：窗口里始终只有一个 `PaneView`，切标签时调 `Attach(vm)`
把同一个视图壳绑到另一份 `PaneViewModel` 上。

```
MainWindow
├── TabStrip            ← 只画标签条，不持有内容
├── NavPane             ← 窗口级共享
└── PaneView ×1         ← 单份！DataContext 随活动标签切换
    └── PaneViewModel ×N（每个标签一份，保活）
```

预算算得过来：一个空视图壳约 3 MB（20 个标签若各建一套工具栏 + 列表，光控件树就吃掉 60 MB，
而 ADR-0008 定的是私有提交 65 MB）。保活的是**状态**（路径、历史栈、条目、选中、滚动位置），
不是**控件**。

`Attach` 必须可重复调用——它会先把上一份视图状态存回旧 VM（`SaveViewState`），
再挂事件、还原新 VM 的视图状态（`RestoreViewState`）。

### 6.2 切换标签只搬两样视图状态

| 状态 | 归属 | 搬家方式 |
|---|---|---|
| 滚动位置 | 标签 | `CaptureScrollAnchor` / `RestoreScrollAnchor`，存**项目索引 + 偏移**，不存像素（切回来时行高可能因字号变了） |
| 选中项 | 标签 | 记 `FullPath` 集合，新 VM 加载完后按路径重绑 |
| 视图模式 / 排序 / 列宽 | 应用 | 来自设置，不随标签变，不搬 |

滚动还原要 `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)`——立刻还原时
ScrollViewer 的 extent 还是旧的，偏移会被夹到 0。

### 6.3 首帧优先的懒加载

会话恢复**只给路径，不枚举目录**：`RestoreTabs()` 把 `session.tabs` 逐条 `AddPending(path)`
（只设 `CurrentPath`，不读盘），窗口 `Loaded` 之后才 `NavigateAsync` 活动标签。

`HasPendingContent` 用来区分"路径设了但还没读盘"，窗口的 `_started` 标志区分
"启动期还没开始"与"运行期用户真的切过去了"——运行期切过去要立刻按需加载。

单实例转交也要认这个状态：首屏没起来时第二次启动的目录参数直接改进当前标签的待办路径
（`if (!_started && Vm.HasPendingContent) { Vm.PreparePendingPath(path); return; }`），
否则会和 `OnLoaded` 的导航抢同一个标签。

### 6.4 命令分家：6 条进窗口表

判据和 S2 一致——**`CanExecute` 读的是哪份状态**：

| 表 | 命令 | 为什么 |
|---|---|---|
| 窗口表 | `tab.new` / `tab.close` / `tab.reopen` / `tab.next` / `tab.previous` / `tab.last` + `tab.goto1…8` | 标签集合与"当前是第几个"只有一份，属于窗口 |
| 窗格表 | S2 那 20 条 | 路径与选中项每个标签各一份 |

`Ctrl+1…8` 用 `CommandIds.GotoTab(n)` 生成 Id 后循环注册，不写 8 个常量。

### 6.5 关掉最后一个标签

`behavior.closeLastTabToHome` 默认 `true`：不销毁标签、不关窗口，只把最后一个标签
送回默认目录，历史栈保留（还能后退回去）。设成 `false` 才是"关最后一个 = 关窗口"。
关掉的路径进最近关闭列表（上限 10 条），`Ctrl+Shift+T` 取最近一条重开。

### 6.6 溢出与 12 个上限

`session.tabs` 上限 `SessionSettings.MaxTabs = 12`。理由不是性能，是**可用性**：
12 个标签在 1000px 宽窗口里已经缩到最小宽度（100 DIP）、标题只剩两三个字，
再多就只能靠滚轮找标签，不如让用户先关几个。

溢出处理：`ScrollViewer`（`HorizontalScrollBarVisibility="Hidden"`）+ 滚轮转横向滚动，
活动标签变化时 `BringIntoView()` 把它滚进视野。**没有做**两端的渐隐/箭头提示，
翻页唯一入口是滚轮——这是已知的可用性欠账，记在 §8。

### 6.7 宽度策略：`MaxWidth` 必须落到 `TextBlock`

横向 `StackPanel` 给子项的是**无限宽**，`TextTrimming` 永远不会触发，
容器上的 `MaxWidth` 只会把内容硬裁（不是省略号，是切一刀）。

因此标签宽度是两段式：

| Token | 落在哪 | 作用 |
|---|---|---|
| `Size.Tab.MinWidth` | 标签 Border | 不缩到这个宽度以下 |
| `Size.Tab.MaxWidth` | 标签 Border | 上限，超出后不再变宽 |
| `Size.Tab.Title.MaxWidth` | 标题 **TextBlock** | 真正让省略号生效的那个 |

三者都随字号缩放（`FontMetrics`），因为只放大高度不放大宽度时，24pt 下每个标签都成"文…"。

## 7. 验收记录

| 项 | 结果 |
|---|---|
| 编译 | 0 警告 0 错误 |
| 单测 | 119 通过 |
| i18n 检查 | 键一致性 / 生成物同步 / 残留硬编码 三项 OK |
| 冒烟（`D:\ac05\smoke_refactor.py`） | Ctrl+Shift+N 建出目录、F5 刷新、Ctrl+, 打开设置、右键菜单 11 项与键位提示全部正确 |
| 焦点环 | Tab 三次后刷新按钮周围出现 2px 蓝色圆角框（`focus3-3.png`） |
| 性能探针 | `--perf-probe` 正常输出：10000 项、73 fps、私有提交 113.3 MB |
| S2 冒烟（`D://ac05//smoke_s2.py`） | Ctrl+Shift+N 走窗格表建出目录；F5、Ctrl+Shift+1/2 正常；Ctrl+Shift+D 走窗口表切换主题；Ctrl+, 经 `SettingsRequested` 打开设置；右键菜单 11 项与键位提示完好 |

S3 走查（2026-11-05，截图在 `D:\ac05\ac06\`）：

| 项 | 结果 | 图 |
|---|---|---|
| 新建 / 切换 / 独立路径 | `Ctrl+T` 连开三个标签，各自导航，标签名与内容互不影响；`Ctrl+1` 切回时下划线跟随 | `y-1-three-named.bmp` |
| 标签右键菜单 | 4 项文案正确：关闭 / 关闭其他标签页 / 关闭右侧标签页 / 复制路径 | `y-2-menu-only.bmp` |
| 拖动排序 | 最左标签拖到第 3 位之后，顺序变为 临时 / 归档资料 2025 / visual，被拖标签保持活动、内容不变 | `y-3-dragged.bmp` |
| 满额 12 标签 + 溢出 | 1000px 宽窗口只露 5–6 个标签，左侧半截裁切、右侧接「+」；`Ctrl+9` 把第 12 个滚进视野 | `w2-1-tail.bmp`、`w2-3-ctrl9.bmp` |
| 溢出的滚轮 | 光标在标签条上滚动，可见区从 t01–t07 变为 t04–t09；悬停标签显示完整路径 tooltip | `w2-4-wheel.bmp` |
| 上限行为 | 已有 12 个标签时 `Ctrl+T` 不再新增（静默，见 §8） | `z-2-over-cap.bmp` |
| 会话恢复 | 三个标签 + `activeTabIndex=1` 落盘；重启后标签与活动项原样回来，窗口尺寸也还原（1100×760 物理 / 880×608 DIP） | `r-2-restored.bmp` |
| 中键关闭 / `Ctrl+Shift+T` 重开 | 中键点标签 2 关闭，重开回到原目录 | `t-6-midclose.bmp`、`t-7-reopen.bmp` |

S4 走查（2026-11-05，截图在 `D:\ac05\ac06\`）：

| 项 | 结果 | 图 |
|---|---|---|
| `F3` 开分屏 | 左右两栏各有完整工具栏/列头/列表/状态栏，比例 0.5；左窗格文件名可见 | `p4-1-split.bmp`（锁屏态）、`u4-1-baseline.bmp` |
| 右窗格独立导航 | 焦点在右，单实例转交 `D:\ac05\visual` → 只有右栏变，标题跟着变成 `visual` | `q4-3-right-navigated.bmp` |
| 关分屏 | `F3` 关：`Pane2.Release()`，焦点回主窗格，路径留着（再开还是 `visual`） | `q4-5-split-off.bmp` |
| 分隔条拖拽 → 比例落盘 | 0.5 拖到 0.28：包边位置 703 → 496，`settings.json` 的 `splitRatio` = 0.2807 | `u4-2-dragged.bmp` |
| 比例持久化 + 还原 | 重启后仍是 0.28（包边位置 496 一致） | `u4-3-restored.bmp` |
| 双击复位 50:50 | 包边 496 → 703，`splitRatio` 回到 0.5 | `u4-4-after-dclick.bmp` |
| 上下分屏（右键菜单第 2 项） | 菜单 210×112 三项，切后 `splitVertical=true`，上下各半 | `u5-5-split-v.bmp` |
| 上下分屏拖拽 / 双击复位 | 分隔条屏幕 y 610 → 490（拖 120px 生效），双击回 610 | `u6-1-vdragged.bmp`、`u6-2-vreset.bmp` |
| 切回左右分屏（菜单第 1 项） | 菜单点在分隔条上弹出（262×138），选第 1 项后 `splitVertical=false` | `u5-6-back-h.bmp` |
| 焦点包边 | 像素扫描：主窗格活动时 `#FF3A3A42` 出现在 x=186/682，右窗格活动时只在 x=689 | `s4-1/2-primary/secondary-focus.bmp` |
| `F6` / `Tab` 换焦点 | 标题 `t01` → `visual` → `t07` → `t01`；Tab 同样来回（需列表有焦点） | `s4-1…4`、`u7-4-tab-back.bmp` |
| 跨窗格拖放：同盘移动 | 左 `D:\ac05\dnd\src` 拖到右 `dst`：`a.txt` 从 src 消失、出现在 dst | `u7-2-after-drag.bmp` |
| 跨窗格拖放：跨盘复制 | 左 `D:` 拖到右 `C:\…\Temp\ac05-dnd`：`a.txt` 两边都在 | `u9-1-cross-volume.bmp` |
| 双窗格视图模式各自独立 | `F6` 到右窗格按 `Ctrl+Shift+2` → 右栏变列表（无列头），左栏仍是详细 | `u8-2-right-list.bmp`、`u8-3-both-list.bmp` |
| 会话恢复带回第二路径 | `tabSecondPaths: ["D:\\ac05\\visual"]` 落盘，重启后右窗格仍是该目录 | `u10-1-startup-pane.bmp` |
| 编译 / 单测 / i18n | 0 错误；125 单测通过；i18n 三项 OK | — |

**未验通**：`Ctrl` / `Shift` 强制改效果（拖拽全程要按住修饰键，`window_drive` 只能在拖之前按键，
中途按住无效）。逻辑在 `PaneView.DragDrop.ResolveExternalDropEffect`，与"从资源管理器拖入"共用同一条分支，
后者在 0.1.0 已验过。

## 8. 已知欠账

| 项 | 现状 | 建议 |
|---|---|---|
| 溢出没有可视提示 | 标签条左右两端无渐隐、无箭头，滚轮是唯一翻页入口；活动标签滚出视野后看不到蓝线 | S4 之后补两端 16px 渐隐 + 首尾各一个 chevron |
| 「+」与最后一个可见标签贴死 | 标签右滑时紧挨「+」，无留白 | 给 ScrollViewer 加等于按钮宽度的右侧 padding |
| 满额后 `Ctrl+T` 静默 | 到 12 个上限后按键无反馈 | 状态栏提示「最多 12 个标签」，或复用 toast |
| 走查工具链 | `screenshot`（PrintWindow 只画目标窗口）抓不到右键菜单 | 已补 `capture`（BitBlt 屏幕矩形）与 `front`（抢前台），见 `tools/window-drive/window_drive.py` |
| 快捷键的主入口其实是"兜底"那条 | `--keydebug` 里只有 `preview key=…`、从来没有 `vk=…`：WM_KEYDOWN 到不了 `HwndSource` 钩子（同一只钩子收得到 `Ctrl+,` 的 `WM_HOTKEY`，说明钩子本身是活的）。所以**只写在钩子里的键位不会生效**，Tab 换窗格踩过这个坑（见 §9.4） | 新键位一律进 `KeyBindings`（两条路径都认）；必须在钩子里做的要同时在 `OnPreviewKeyDown` 补一份 |
| 第二窗格只在被激活时创建 | 开分屏后切到一个从没分屏过的标签，右窗格要先建 VM 再枚举，有一帧空白 | 若被报为体验问题，改成"开分屏时给前 N 个标签预建" |
| `Ctrl`/`Shift` 强制改拖放效果未验 | 见 §7 末 | 手动按住修饰键拖一次即可，或给 `window_drive` 加 `keysdown`/`keysup` |

## 9. S4 做了什么

### 9.1 标签长出一对窗格

`TabViewModel` 是 S4 唯一新增的类型，把"一个标签"从"一个浏览区"提升为"一对浏览区"：

| 成员 | 语义 |
|---|---|
| `Primary` | 必有。关分屏后剩下的那个 |
| `Secondary` | 分屏时才有，懒创建（开分屏只为当前标签建） |
| `ActivePane` | 命令目标。焦点换到哪个窗格就是哪个 |
| `DisplayName` | **跟随 `ActivePane`**，不是跟随 `Primary` |
| `EnsureSecondary(fallback)` | 取路径优先级：上次留下的 `_secondaryPath` → 调用方给的兜底 → 主窗格当前目录 |
| `DropSecondary()` | 只丢视图、只留路径。12 个标签各留一个空壳窗格等于把内存预算花在看不见的地方 |
| `SnapshotPaths()` | 一次取回 `(主, 副)`，两个字符串必须成对拿 |

`TabHostViewModel.Tabs` 的元素类型随之从 `PaneViewModel` 改成 `TabViewModel`，
窗口里所有 `_tabs.Active` 的用法都多了一层 `.ActivePane`。取舍理由见 ADR-0009。

### 9.2 3×3 网格复用一个 XAML

`PaneHost` 固定 3 列 3 行：左右分屏用列 0/1/2，上下分屏用行 0/1/2，不用的一维宽高归零。
`ApplySplitLayout` 只改 `Grid.SetColumn/SetRow`、`ColumnSpan/RowSpan` 与三行列宽，
不为两种形态各写一套分隔条与子元素摆放。

省下的不只是 XAML：拖拽、双击复位、右键改方向、命中测试全部只有一份实现。

踩到的坑（`ApplySplitLayout` 里已写注释）：**上下分屏必须显式 `Grid.SetColumn(x, 0)`**。
从左右分屏切过来时窗格还挂在列 2 上，而列 2 此刻宽度是 0，`ColumnSpan=3` 又从列 2 起数，
结果是"有高度、没宽度"的一片空白。

### 9.3 焦点包边只在分屏时画

`PaneView.SetActive(active, outline)` 两个参数分开：`active` 表示"我是命令目标"，
`outline` 表示"要不要画框"。宿主只在分屏时把 `outline` 打开——
单窗格时全窗口就一个浏览区，包个框只是多一条线。

包边是 1px `Brush.Border.Strong`（深色主题 `#FF3A3A42`）。走查时肉眼几乎看不出差别，
改用像素扫描确认：主窗格活动时该颜色出现在 x=186 / 682，右窗格活动时只出现在 x=689。

### 9.4 Tab 换焦点：钩子收不到键，改走预览路径

`--keydebug` 日志里只有 `preview key=Tab`，没有 `vk=0x09`。真实按键与 `PostMessage`
投递的 `WM_KEYDOWN` 都到不了 `HwndSource` 钩子——WPF 的键盘输入链路在钩子之前处理掉了它。
同一只钩子收得到 `Ctrl+,` 的 `WM_HOTKEY`（走 `RegisterHotKey`），所以钩子本身是活的。

其他快捷键没受影响，因为 `KeyBindings` 同时能按 `Key` 匹配，`OnPreviewKeyDown` 兜住了；
而 Tab 只写在钩子里（用 VK 判），于是分屏后按 Tab 毫无反应。

修法：把 Tab 的判断搬进 `OnPreviewKeyDown`。**不进 `KeyBindings`**——
Tab 只在分屏时有意义，登记成全局键位会让单窗格下的 Tab 也被吞掉（列表里没法用 Tab 移出焦点）。

### 9.5 响应式列先缩后折

窗格宽度不够时，原来的逻辑是直接按 大小 → 类型 → 日期 折叠。双窗格把这个问题的门槛从
"窗口很窄"变成"一直"：任何一次分屏都只有半窗宽。

改成两段：先把**超过默认值**的列逐个缩回默认（保住这一列），仍放不下才折叠。
折叠只是把 `Column.*.Width` 令牌置 0，用户拖过的宽度留在 `settings.json` 里不动，
窄窗格里折掉的列，窗口一拉宽就回来了。

顺带修了一个真 bug：首次布局时 `ActualWidth` 是 `NaN`，`paneWidth <= 0` 的判断漏掉了它，
三列被一次性折光、文件名整列消失（日志 `columns pane=NaN … shown=False/False/False`）。

### 9.6 分屏设置当场落盘

`Layout.SplitEnabled / SplitRatio / SplitVertical` 都走 `App.SaveSettings()` 立即写盘，
不等退出时那条"窗口尺寸变了才存"的兜底路径。

原因：兜底路径的判据是"尺寸、最后目录、标签有没有变"。用户只拖了一下分隔条就关窗口，
这三样都没变，`PersistWindowState` 直接返回，比例就丢了（走查时实测：重启后仍是 0.5）。
一次拖拽一次写，和列宽（`OnColumnResizeDone`）同一套规矩。

### 9.7 跨窗格拖放没有专门代码

两个 `PaneView` 各有一套拖放处理，落点按**接收方自己的 `CurrentPath`** 解析。
所以"从左窗格拖到右窗格"和"从资源管理器拖进来"走的是同一条路，
同盘移动、跨盘复制、拖到文件夹行、拒绝拖进自身子目录全部自动成立。

验证：同盘 `D:\ac05\dnd\src → dst` 是移动（源消失），跨盘 `D:\… → C:\…\Temp` 是复制（两边都在）。


