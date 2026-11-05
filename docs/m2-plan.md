# M2 实现方案：0.1.0 收尾与 0.2.0 功能完整版

> 对应 `docs/requirements.md` v1.1 §7 的阶段划分。AC-06 视觉走查已闭合（`docs/ac06-visual-acceptance.md`），
> 本文盘点**还没实现的部分**，并给出可执行的实现路径与顺序。
>
> 每项含：现状差距、方案要点、涉及文件、风险与降级、工作量。工作量按"一人、熟悉本仓库、含自测"估算。

## 总览

| 阶段 | 内容 | 工作量 | 前置 |
|---|---|---|---|
| **P0 收尾** | FR-6.1 地址栏补全、FR-6.16 文件夹大小、FR-6.9 系统 Shell 菜单、发版合规（AC-09/AC-10） | 5 天 | 无 |
| **P1 地基** | 单窗格 → 「窗口壳 + 窗格」拆分，行为零变化 | 3.5 天 | P0 |
| **P2 容器** | FR-2 多标签、FR-2.11 会话恢复、FR-1.1 主页 | 5 天 | P1 |
| **P3 双窗格与组织** | FR-3 双窗格、FR-7 收藏、FR-8 工作区 | 5 天 | P2 |
| **P4 标记与设置** | FR-4 彩色标签（引入 SQLite）、FR-5 设置项补齐 | 5 天 | P3 |
| **P5 预览与尾声** | FR-9 预览窗格、FR-10.8 关于页、FR-10.7 可访问性 | 6 天 | P4 |
| **P6 验收** | AC-03 压力、AC-04 三版本、AC-07 断网、AC-09 便携、AC-10 依赖清单 | 3 天 | P5 |

合计约 **6.5 周**。P1 是全局瓶颈：不做拆分，后面每项功能都要在 962 行的 `MainWindow.xaml.cs` 里再长一遍。

---

## 一、进度快照

| 模块 | 状态 | 缺口 |
|---|---|---|
| FR-1.1 主页 | 未开始 | 整个模块（0.2.0） |
| FR-1.2 快捷导航 | 部分 | 缺 FR-1.2.3 网络/回收站节点、FR-1.2.4 宽度可拖拽（现在是内容自适应但不可拖）、FR-1.2.6 中键新标签；1.2.1 的"主页"节点随 1.1 |
| FR-2 多标签 | 未开始 | 整个模块 |
| FR-3 双窗格 | 未开始 | 整个模块 |
| FR-4 彩色标签 | 未开始 | 整个模块（技术风险最高） |
| FR-5 设置 | **外观基本齐** | 缺 FR-5.1.3 强调色；行为缺 5.2.4 系统文件、5.2.5 单击打开、5.2.6 新标签默认目录、5.2.7 关闭最后标签、5.2.8 确认分项、5.2.9 会话/工作区互斥、5.2.10 预览默认；隐私缺 5.3.2 清空历史、5.3.4 条数上限 UI、5.3.5 缩略图缓存、5.3.6 标签库、5.3.8 恢复默认 |
| FR-6 基础浏览 | **缺 3 项 P0** | FR-6.1 自动补全、FR-6.16 文件夹大小、FR-6.9 系统 Shell 菜单；P1 的 6.4 列配置、6.6 缩略图、6.13 搜索也未做 |
| FR-7 收藏 | 未开始 | 整个模块（0.2.0） |
| FR-8 工作区 | 未开始 | 整个模块 |
| FR-9 预览窗格 | 未开始 | 整个模块 |
| FR-10 基础能力 | 大部分已做 | 10.3 便携标记口径与需求不符、10.6 异常退出提示、10.7 可访问性、10.8 关于页、10.9 目录入口未做 |

`src/` 实测核对：`Core` 19 个文件、`App` 32 个文件，**没有** `PathCompletion`、`FolderSizeCalculator`、
`ShellContextMenu`、`PreviewHandler`、`Tags`、`Bookmarks`、`Workspaces` 任何一类文件。

---

## 二、P0：0.1.0 收尾（5 天）

前两项方案已在 `docs/m1-fr6-plan.md` §1 / §3 写定，这里只补本轮走查后的增量结论，不重复正文。

### 2.1 FR-6.1 地址栏自动补全（1.5 天）

按 `m1-fr6-plan.md` §1 执行，四点补充：

1. **候选第一来源改用 `Entries` 快照**。当前目录已枚举的条目零 IO、命中率最高；`Directory.EnumerateDirectories` 只作为"用户输入了别的目录"时的补充，上限 200、超时 150ms。
2. **候选行要能显示"目录/文件"区分**，否则 `D:\work` 下同名的 `work` 目录与 `work.txt` 排在一起无法分辨。行模板复用 `Text.Row` + 右侧灰色 `Text.Secondary` 类型。
3. **空候选不是空弹窗**：没有候选时 `Popup` 不打开，不要显示"无匹配"——地址栏边打字边弹空框比不弹更烦。
4. **i18n**：候选区无新增界面文案（只有路径与类型名），`check-keys.py` 第 3 项可直接过。

风险不变：网络盘靠 150ms 超时降级为"只用缓存候选"。

### 2.2 FR-6.16 文件夹大小按需计算（1 天）

按 `m1-fr6-plan.md` §3 执行。两处与当前代码的衔接要提前确认：

- 大小覆盖字典放 `MainViewModel`，但 **P1 拆分后它必须跟着成为 `PaneViewModel` 的成员**（目录切换即清空）。现在的写法会让 M2 搬一次，不如一次到位：直接放 `PaneViewModel` 形态的字段，命名按窗格。
- 行模板的大小列现在是 `Text.Number` 绑 `SizeText`。加"计算中…"状态时不要塞进 `FileEntry`（Core 的不可变记录），用值转换器按 `(path, state)` 查字典。

### 2.3 FR-6.9 系统 Shell 右键菜单（2 天）

按 `m1-fr6-plan.md` §5 执行，这是 M1 剩余风险最高的一项。**分层策略不变**：右键走自建菜单，
「更多选项…」与 `Shift+右键` 才走系统菜单。

落地时容易踩的三点，先记在这里：

| 点 | 说明 |
|---|---|
| 消息转发 | 只 Hook `WM_INITMENUPOPUP` 会得到"一级菜单正常、二级菜单空白"。`WM_DRAWITEM` / `WM_MEASUREITEM` 三条缺一不可，先给 `IContextMenu3.HandleMenuMsg2`，未处理再给 `IContextMenu2` |
| 坐标 | `TrackPopupMenuEx` 要物理像素。本机 125% DPI，`PointToScreen` 已经返回物理像素，**不要再乘缩放比**——这是最容易多算一次的地方 |
| 多选 | 必须传 PIDL 数组（`cidl > 1`）。只传首项时菜单能弹出来，但"发送到""压缩"只对第一个文件生效，且不报错 |

降级：任何 HRESULT 失败或 COM 异常 → 关掉系统菜单、回落到自建菜单、写 `app.log`，不弹窗。

### 2.4 发版合规（0.5 天）——当前是硬缺口

| 项 | 现状 | 要做的 |
|---|---|---|
| AC-10 依赖清单 | **`THIRD-PARTY-NOTICES.md` 不存在**，`gen-third-party.ps1` 也不存在 | 项目**目前零 NuGet 依赖**，只有 .NET 运行时与 Windows SDK 引用。清单可以手写：.NET 9（MIT）、Windows SDK 互操作程序集（MIT），再加上 WPF 本身。脚本可以后补，文件必须先有 |
| AC-09 便携包 | `AppPaths` 认 `portable.marker`，需求 FR-10.3 写的是 `portable.flag` 或 `data` 目录 | **改代码而不是改需求**：两个标记名都认，`data` 目录存在也认。已发布的说明文档不必跟着改 |
| 便携 zip | 无打包脚本 | `publish.ps1`：`dotnet publish` → 拷 LICENSE/NOTICE/THIRD-PARTY-NOTICES.md → 打 zip。顺带解决"发布产物手工拼"的隐患 |
| 版本号 | `AssemblyInformationalVersion` 未与 CHANGELOG 对齐 | 发版前统一到 `0.1.0+<短 commit>`（关于页要用，见 §4.8） |

### 2.5 可延后的 P1（不进 0.1.0）

| 项 | 排期 | 理由 |
|---|---|---|
| FR-6.6 缩略图 | 随 FR-9 一起做 | 缩略图与预览共用 `IShellItemImageFactory` 与缓存层，单独做一次要拆两遍 |
| FR-6.13 当前目录搜索 | P4 之后 | 纯前端过滤，与容器改造无关；但搜索框要放进标签栏/工具栏，等外壳定形 |
| FR-6.4 列显示配置 | P3 之后 | 列定义要变成数据驱动，与双窗格列表容器重构同批 |

---

## 三、P1：地基——从「窗口即浏览器」拆成「窗口壳 + 窗格」

### 3.1 为什么这是瓶颈

| 现状 | 数字 |
|---|---|
| `MainWindow.xaml.cs` | 962 行 |
| `MainWindow.FileOps.cs` / `.DragDrop.cs` / `.Watcher.cs` / `.Icons.cs` / `.PerfProbe.cs` | 299 / 168 / 129 / 54 / 434 行 |
| `MainViewModel.cs` | 514 行 |

这些文件全部建立在同一个前提上：**一个窗口 = 一个浏览区**。列表控件叫 `FileList`、状态栏只有一个、`FileSystemWatcher` 挂在窗口上、命令处理器直接操作窗口字段。

多标签、双窗格、收藏拖入、主页卡片点击全都要问同一个问题："这个操作作用于哪个浏览区？"——没有"浏览区"这个实体，每一项都要在窗口里再写一份 if。

### 3.2 目标结构

```
MainWindow（壳：只管布局、命令路由、窗口状态）
├── TabStrip                  ← FR-2，顶部，ItemsControl + 横向滚动
├── PaneHost                   ← 1 个或 2 个 PaneView
│    └── PaneView (UserControl)   ← 从 MainWindow 拆出的浏览区
│         ├── 面包屑 / 地址栏
│         ├── FileList（虚拟化三件套）
│         └── 状态栏（每窗格一份）
├── NavPane（窗口级，作用于 FocusedPane）
└── ContextMenu 宿主
```

对应 ViewModel：

| 类型 | 职责 |
|---|---|
| `PaneViewModel` | 现 `MainViewModel` 改名，保留导航/枚举/排序/选中/状态栏/文件大小字典。**去掉**窗口级成员（打开设置、关窗、窗口尺寸） |
| `TabItemViewModel` | `{ PaneViewModel Pane, string Title, IconKey, bool Pinned }`，标题随 pane 路径变化 |
| `ShellViewModel`（或直接留在 MainWindow） | `ObservableCollection<TabItemViewModel> Tabs`、`ActivePane`、`FocusedPane`、双窗格开关 |

### 3.3 四步迁移（每步都要求行为零变化）

| 步 | 动作 | 验收 | 工作量 |
|---|---|---|---|
| S1 | **机械抽取** `PaneView`：把 `MainWindow.xaml` 里 `Grid.Row=1` 的浏览区与状态栏整块剪到 `PaneView.xaml`，`DataContext` 仍是原来的 `MainViewModel`；`MainWindow` 用一个 `<local:PaneView/>` 托管 | 外观与交互零差异；119 单测通过；AC-05 边界场景重跑 | 1 天 |
| S2 | **改名与焦点**：`MainViewModel` → `PaneViewModel`；`MainWindow` 持有 `Panes` + `ActivePane`；所有命令从 `_viewModel.X` 改为 `ActivePane.X` | 仍只有一个 pane，行为不变 | 1 天 |
| S3 | **标签栏与视图复用**：`TabStrip` 绑 `Tabs`；`ContentControl Content="{Binding ActivePane}"` + `DataTemplate` → `PaneView` | 多标签可切换、可关闭；切回时滚动位置与选中还原 | 1 天 |
| S4 | **双窗格**：`PaneHost` 换成两列（或两行）`Grid` + `GridSplitter`；点击决定 `FocusedPane`，焦点窗格画强调色描边 | F3 开关；两侧路径/历史互不影响；跨窗格拖放 | 0.5 天 |

S1 是纯搬运，**不要在这一步顺手改逻辑**。搬运 + 改名分开做，出问题时能立刻判断是"搬错了"还是"改名改错了"。

### 3.4 视图复用与内存

需求 FR-2 要求"按需加载 + 视图复用"，且单标签壳 ≤3MB、双窗格增量 ≤30MB。策略：

- **ViewModel 全保活，View 只保活动那一份。** 非活动标签保留目录、排序、选中项路径、滚动偏移；切走时 `ContentControl.Content` 换掉，`PaneView` 被回收。
- 如果实测切换耗时 >50ms（FR-2.1 的指标），再加"最近 1 个视图"的 LRU 缓存；不要一上来就保活 20 个视图——每个 `PaneView` 带一个虚拟化 `ListBox` + 图标缓存引用，20 份稳超预算。
- 切回时的还原顺序：先设目录 → 枚举 → 恢复排序 → 按路径恢复选中 → 恢复滚动偏移。前两步已有（`NavigateAsync`），后三步是把现有 `RefreshAndSelectAsync` 的收尾逻辑参数化。

### 3.5 监听器与焦点

`FileSystemWatcher` 现在是每窗口一个。多标签后：

- **只有 `ActivePane` 的 watcher 在跑**，切走即停。需求 FR-2 的"非活动标签仅在激活时校验目录是否变更"就是这个意思。
- 切回时用一次轻量枚举比对（mtime + 条目数）判断是否需要重建，避免"切回来发现是旧内容"。
- 双窗格时两个 pane 各自一个，但**非焦点窗格的 watcher 降为 2 秒节流**（需求 FR-3 要求非焦点窗格降优先级）。

### 3.6 本轮走查暴露的两个隐性债，必须在 P1 一并处理

| 债 | 说明 |
|---|---|
| `FocusVisualStyle="{x:Null}"` | `Row.Item`、`Button.Toolbar`、`Nav.Item` 等样式里被显式置空。键盘走查时看不到焦点在哪，直接卡住 FR-10.7 / AC-08。改法是给列表行与按钮各做一条焦点态（描边或底色），而不是把 `Null` 删掉走 WPF 默认虚线框——深色主题下默认框几乎不可见 |
| 列表行模板元素数 ≤3 的约束 | 彩色标签要在文件名前加圆点，会变成 4 个元素。要么把图标与圆点合成一个 `Grid`（仍算 1 个），要么在 `ui-guidelines.md` §14 记录一次例外。**先试合成**，它同时解决图标位置微调 |

---

## 四、P2–P5：逐功能方案

### 4.1 FR-2 多标签（3 天，含会话恢复）

**方案要点**

- `TabStrip` 用 `ItemsControl` + 外层 `ScrollViewer`（`HorizontalScrollBarVisibility=Auto`，`CanContentScroll=False`），单标签 `MinWidth=100`、`MaxWidth=200`；20 个标签不换行、不挤压。
- 标签模板 = 图标 + 目录名（`TextTrimming`），同名目录的 `ToolTip` 显示完整路径。
- 快捷键：`Ctrl+T` 新建、`Ctrl+W`/中键关闭、`Ctrl+Tab`/`Ctrl+Shift+Tab` 循环、`Ctrl+1~9` 跳转。
- 拖拽排序用 `DragDrop.DoDragDrop` + 插入位置竖线。**不要**复用列头那个 `Thumb` 方案——列头是连续调整宽度，标签是离散重排，语义不同。
- FR-2.11 会话恢复：`settings.json` 的 `session` 段加 `tabs: string[]` + `activeTab: int`。启动时**先开第一个标签并显示**，其余在 `ApplicationIdle` 里逐个开、逐个校验目录存在性（不存在的丢弃并记日志）。这是"必须先显示主页/首标签"的硬要求。
- FR-2.8 关闭最后一个标签：默认"回到主页"（依赖 FR-1.1 先落地），先实现为"新建一个默认目录标签"，主页就位后切换。

**风险**

| 风险 | 降级 |
|---|---|
| 20 个标签时标签栏挤压变形 | `MinWidth` + 横向滚动，禁止换行（换行会让标题栏高度跳变） |
| 会话恢复把不存在的路径灌进来 | 恢复前逐个 `Directory.Exists`，失败丢弃；日志记一条 |
| 标签切换丢滚动位置 | 见 §3.4 的还原顺序 |

### 4.2 FR-3 双窗格（1.5 天，依赖 P1）

- `F3` / `Ctrl+Shift+E` 开关第二窗格；关闭后焦点回主窗格（FR-3.3）。
- 分隔条 `GridSplitter`，比例存 `layout.splitRatio`，双击回 50:50。
- 焦点视觉：`FocusedPane` 的外框画 1px 强调色（复用 `Brush.Accent.Default`），非焦点窗格不画。
- 跨窗格拖放：复用现有 `MainWindow.DragDrop.cs`，把"目标目录"从"当前目录"改为"落点所在窗格的目录"。同盘移动/跨盘复制、`Ctrl`/`Shift` 强制的判定逻辑已经是现成的。
- `Tab` 在两窗格间切换焦点（FR-3.5）。注意与 `Tab` 在列表内移动选中项的默认行为冲突——列表内用方向键，`Tab` 让给窗格切换。

**内存**：NF-05 双窗格档位是 185MB（单窗格 150MB）。空窗格先渲染骨架，首次交互再加载内容。

### 4.3 FR-7 收藏（2 天）

- 存储：`settings.json` 新增 `bookmarks` 数组（上限 500，FR-7.9）。字段：`{ path, name, kind(folder|file), group, icon, color, addedAt }`。按需求决策不新建数据库。
- 入口：导航栏收藏区（`NavPane` 里 `ItemsControl` 分区）+ `Ctrl+D` + 从列表拖入（FR-7.4）。拖入时的插入指示复用标签排序那条竖线。
- 失效检测（FR-7.7）：导航栏渲染后异步逐个 `Directory.Exists`/`File.Exists`，失效项灰色；批量清理走右键菜单。**不要在渲染前同步检查**——UNC 路径会让导航栏首帧卡住。
- 与 FR-1.2.5 / FR-1.1.5 同源：主页的收藏区直接读同一个集合。

### 4.4 FR-8 工作区（1.5 天）

- 独立 `%LOCALAPPDATA%\Lingpane\data\workspaces.json`（需求明确"不参与冷启动默认路径"，放在 settings.json 里会让每次启动都解析它）。
- 结构：`{ id, name, savedAt, panes: [{ tabs: [{ path, scrollOffset }], activeTab }] }`。上限 50 个工作区、单工作区 50 标签。
- 恢复：先建壳、再逐个开标签（同 FR-2.11 的异步策略）。
- FR-8.4 与 FR-5.2.9 互斥：设置里二选一，另一个置灰并说明原因。

### 4.5 FR-4 彩色标签（3 天 + 一个依赖决策）

技术方案需求里已经定死（File ID + 路径冗余、SQLite、延迟打开、批量查询），这里只列落地要点与**必须先拍板的一件事**。

**决策点：引入 SQLite 是项目第一个运行时第三方依赖。**

现状是这个仓库 **零 NuGet 依赖**（`csproj` 里没有任何 `PackageReference`），全靠 BCL + P/Invoke。加 `Microsoft.Data.Sqlite` 会带进 `SQLitePCLRaw`（原生 `e_sqlite3.dll`，约 1.4MB）：
- 包体：NF-07 是 ≤30MB（框架依赖），当前产物已接近，需要重新量；
- 合规：AC-10 的依赖清单从"手写两个框架条目"变成"要列出 SQLite 的版权与许可证"，`gen-third-party.ps1` 从可选手写变成必需；
- 便携性：原生 DLL 必须随包分发，`portable` 包结构要跟着调整。

替代方案是自己写一个按 `(VolumeSerial, FileIndex)` 排序的追加日志文件（约 400 行，无依赖）。**不建议**：并发写、损坏恢复、批量查询都要自己实现，需求 FR-4.2.8 的"损坏自动备份并重建"尤其容易写漏。

建议：按需求走 SQLite，在 P4 开工前先把包体量一次，若超 NF-07 再回头优化裁剪。

**落地要点**

| 点 | 做法 |
|---|---|
| File ID | `CreateFileW(FILE_READ_ATTRIBUTES)` + `GetFileInformationByHandle` 取 `dwVolumeSerialNumber` / `nFileIndexHigh/Low`。**只读属性打开**，不要请求读写权限，否则只读文件与系统文件会失败 |
| 延迟打开 | 首次"设置标签"或"显示带标签目录"时才 `CREATE TABLE`；启动路径不碰数据库（否则冷启动 +几十 ms） |
| 批量查询 | 一次 `SELECT ... WHERE (volume_serial, file_index) IN (@v1,@i1, @v2,@i2, ...)`，按可视区域 ≤200 项一批。禁止逐项查询（FR-4.2.4） |
| 渲染 | 圆点走 §3.6 的"图标+圆点合成一个 Grid"，不增加行模板元素数 |
| 跨卷失效 | 明确告知用户"跨盘移动后标记丢失"（需求已接受此限制），不做模糊重绑（那是 P2 需求） |

### 4.6 FR-9 预览窗格（4 天，风险最高）

- COM：`IPreviewHandler` 必须在**独立 STA 线程**创建与调用（FR-9.2.2）。项目里已有先例——`SHFileOperationW` 就在独立 STA 线程，直接照抄那套线程封装。
- 三条防线：2s 超时（`Task.WhenAny` + 释放）、`CancellationToken` 取消上一次、异常一律降级为"文件图标 + 基本信息"。
- 降级渲染：文本 ≤1MB 直接读；图片用 `BitmapImage` + `DecodePixelWidth`（**不要**解全图，50MB 的图会吃掉整档内存预算）；PDF/音视频只走 Preview Handler，失败就显示基本信息。
- 预算：预览窗格内存档位是 230MB，超预算自动降级为"仅文本 + 基本信息"（FR-9.2.7）。降级判断放在每次预览完成后，不是启动时。
- UNC 与可移动设备默认不自动预览（FR-9.2.8），要点击一次才触发。
- 关闭窗格立即释放 Handler 实例与文件句柄（FR-9.2.6）——这条最容易漏，因为窗格关闭时没人会想到还有 COM 持有文件。

### 4.7 FR-1.1 主页（2 天）

- 主页是**一种标签页**（`TabKind.Home`），不是独立窗口。`PaneHost` 里按 `TabKind` 切换模板。
- 常用目录卡片：`KnownFolders` 已有缓存（`SHGetKnownFolderPath` 结果写设置），直接复用；不存在的目录隐藏。
- 驱动器卡片：`DriveInfo` 容量**异步**取，失败显示"—"（FR-1.1.3）。
- 最近访问：历史记录已有（`historyEnabled`），取前 12 条。
- 收藏区：与 FR-7 同源，最多 8 项 + "查看全部"。

### 4.8 FR-10.8 关于页（1 天）

按 `docs/about-page.md` 执行：独立 `AboutWindow`（480×600）、许可证走 `EmbeddedResource` 不进 XAML、版本号取 `AssemblyInformationalVersion`、目录入口复用 `AppPaths`。

依赖：`THIRD-PARTY-NOTICES.md` 与 `LICENSE`/`NOTICE` 必须在 P0 阶段就位（§2.4），否则关于页的"查看依赖清单"按钮没有可显示的内容。

### 4.9 FR-5 设置项补齐（2 天）

按 §一 的缺口清单逐项加。两条实现约束：

- 新增设置项一律走 `AppSettings` 的强类型属性 + `SettingsJsonContext`，不要塞 `Extra` 字典——`Extra` 是给"未知字段保留"用的，写进去等于放弃 schema 校验。
- 每个新增项都要有"即时生效"路径。像 5.2.5 单击打开这种会影响列表交互的项，要能在一处改完立即生效，而不是重启。

### 4.10 FR-10.7 可访问性（1 天）

- 先做 §3.6 的焦点可视化，否则后面全是空谈。
- 主要控件补 `AutomationProperties.Name`（列表、导航栏、标签栏、地址栏、状态栏）。
- `Tab` 顺序手工走一遍：地址栏 → 列表 → 导航栏 → 标签栏，`Tab` 不陷入死循环（列表内的 `Tab` 让给窗格切换）。
- 屏幕阅读器只要求"能读出主要控件"，不做完整 UIA 实现。

---

## 五、顺序与依赖

```
P0 收尾（5d）──┬─ FR-6.1 补全
               ├─ FR-6.16 大小
               ├─ FR-6.9 系统菜单
               └─ 合规（AC-09/AC-10）
                        │
P1 地基（3.5d）─────────┴─ S1 抽 PaneView → S2 改名/焦点 → S3 标签+复用 → S4 双窗格骨架
                        │
P2 容器（5d）───────────┼─ FR-2 标签（含会话恢复）→ FR-1.1 主页
                        │
P3 组织（5d）───────────┼─ FR-3 双窗格收尾 → FR-7 收藏 → FR-8 工作区
                        │
P4 标记（5d）───────────┼─ FR-4 彩标（先定 SQLite）→ FR-5 设置补齐
                        │
P5 预览（6d）───────────┼─ FR-9 预览 → FR-10.8 关于页 → FR-10.7 可访问性
                        │
P6 验收（3d）───────────┴─ AC-03 / AC-04 / AC-07 / AC-09 / AC-10
```

**为什么标签在前、双窗格在后**：双窗格的每个窗格都要有独立标签组（FR-3.2），先有标签模型才有"窗格持有标签组"这层结构。反过来做要返工一次。

**为什么彩标排在预览前**：彩标是唯一需要外部依赖与数据库的模块，越晚做，越可能因为包体/合规问题被迫改方案；早做早暴露。

---

## 六、验收缺口

| AC | 状态 | 还差什么 |
|---|---|---|
| AC-01 功能完整性 | 未达标 | 0.2.0 全部模块 |
| AC-02 性能 | **达标但余量极薄** | NF-05R 最新实测 43.8 MB，阈值 45 MB（文档 §9.7 还写着 29.7，待更新）。彩标数据库常驻连接、预览 COM 都会吃这 1.2 MB。**P4 开工前必须先处理**：要么把基线降下来，要么明确 NF-05R 的重新测量口径 |
| AC-03 稳定性 8h | 未做 | 无脚本化的 8 小时压测 + 20 条高频路径 |
| AC-04 三版本 | 未做 | 需要 Win10 1809 / Win10 22H2 / Win11 23H2 三套环境 |
| AC-05 边界 | 达标 | — |
| AC-06 视觉 | 达标 | 本轮闭合，见 `docs/ac06-visual-acceptance.md` |
| AC-07 断网 | 未做 | 断网下全流程可用（含 UNC 超时提示） |
| AC-08 可访问性 | 未做 | 见 §4.10，焦点可视化是前置障碍 |
| AC-09 便携 | 未做 | 标记口径不一致 + 无 zip 打包验证 |
| AC-10 依赖清单 | 未做 | `THIRD-PARTY-NOTICES.md` 不存在 |
| AC-11 多语言 | 达标 | — |

---

## 七、不建议顺带做的

`docs/requirements.md` §8.3 的黑名单仍然有效，M2 期间最容易"顺手做一下"的三个：

| 项 | 为什么别做 |
|---|---|
| 图标/磁贴视图（C14） | 二维布局无法用 `VirtualizingStackPanel` 虚拟化，万项内存失控。缩略图需求已由预览窗格承担 |
| 多窗口（B12） | 改动窗口级状态（watcher、单实例、内存口径）的成本远高于收益，1.1 再议 |
| 全盘搜索（B9） | 需要索引服务，与"不常驻、不后台索引"的 P4 原则直接冲突 |
