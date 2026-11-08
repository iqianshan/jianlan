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
| **P4 标记与设置** | FR-4 彩色标签（JSONL + 内存索引，零依赖）、FR-5 设置项补齐 | 5 天 | P3 |
| **P5 预览与尾声** | FR-9 预览窗格、FR-10.8 关于页、FR-10.7 可访问性 | 6 天 | P4 |
| **P6 验收** | AC-03 压力、AC-04 三版本、AC-07 断网、AC-09 便携、AC-10 依赖清单 | 3 天 | P5 |

合计约 **6.5 周**。P1 是全局瓶颈：不做拆分，后面每项功能都要在 962 行的 `MainWindow.xaml.cs` 里再长一遍。

---

## 一、进度快照

> **0.1.0 已打包（2026-11-08）**：`Lingpane-0.1.0-portable-win-x64.zip`，版本 `0.1.0+05d6b04`，
> 69.7 MB，SHA256 `3b73f1ae…6a6d`。产物与发布说明在 `outputs/release-0.1.0/`。
> A / B 两类全部闭合，C 类六项按计划留白并逐条写进 `CHANGELOG.md`「0.1.0 已知限制」。
> 发版前的机验项（编译、176 单测、i18n 三项、包内容、解压启动、系统目录零写入、中英界面）
> 全部通过；AC-03 / AC-04 / AC-07 / AC-08 仍未执行，不挡内部试用。

| 模块 | 状态 | 缺口 |
|---|---|---|
| FR-1.1 主页 | 未开始 | 整个模块（0.2.0） |
| FR-1.2 快捷导航 | 部分 | 缺 FR-1.2.3 网络/回收站节点、FR-1.2.4 宽度可拖拽（现在是内容自适应但不可拖）、FR-1.2.6 中键新标签；1.2.1 的"主页"节点随 1.1 |
| FR-2 多标签 | **基本齐（S3–S5）** | 已落地 2.1/2.3/2.4/2.5/2.6/2.7/2.8/2.9/2.10/2.11/2.12；缺 2.12 的"在新窗口打开"（P2）、2.9 的键盘入口（只有右键菜单）；2.5「20 个标签」已按 CHANGELOG 的口径改为 12 |
| FR-3 双窗格 | **已落地（S4）** | 3.1–3.5 完成；跨窗格拖放复用同一条路径，无需专门代码 |
| FR-4 彩色标签 | 未开始 | 整个模块（技术风险最高） |
| FR-5 设置 | **外观基本齐** | 缺 FR-5.1.3 强调色；行为缺 5.2.4 系统文件、5.2.5 单击打开、5.2.7 关闭最后标签、5.2.8 确认分项、5.2.9 会话/工作区互斥、5.2.10 预览默认；隐私缺 5.3.2 清空历史、5.3.4 条数上限 UI、5.3.5 缩略图缓存、5.3.6 标签库、5.3.8 恢复默认 |
| FR-6 基础浏览 | **P0 三项齐** | FR-6.1 自动补全、FR-6.16 文件夹大小、FR-6.9 系统 Shell 菜单均已落地；P1 的 6.4 列配置、6.6 缩略图、6.13 搜索仍未做 |
| FR-7 收藏 | 未开始 | 整个模块（0.2.0） |
| FR-8 工作区 | 未开始 | 整个模块 |
| FR-9 预览窗格 | 未开始 | 整个模块 |
| FR-10 基础能力 | 大部分已做 | 10.6 异常退出提示、10.7 可访问性、10.8 关于页、10.9 目录入口未做；10.3 便携口径已对齐（三种标记等价） |

`src/` 实测核对：`Core` 20 个源文件、`App` 38 个源文件（不含 `obj/`、`bin/`）。
P0 的三项已有对应实现：`Core/Navigation/PathCompletion.cs`、`Core/FileSystem/FolderSizeCalculator.cs`、
`App/Shell/ShellContextMenu.cs`；**仍然没有** `PreviewHandler`、`Tags`、`Bookmarks`、`Workspaces` 任何一类文件。

### 1.1 M0 / M1 剩余任务清单

M0（技术验证与基线）与 M1（FR-6 收尾 + 0.1.0 发版）的**未闭环项**，按"能不能挡住 0.1.0 发版"分三类。
勾选状态 = 本轮复核结论，改状态时连证据列一起改，不留"看着像做完了"的项。

**A 类：挡发版的硬缺口（不做就不能发 0.1.0）**

- [x] **AC-10 第三方依赖许可清单缺失** —— 仓库只有 `LICENSE` / `NOTICE`，`THIRD-PARTY-NOTICES.md` 不存在。
      验收原文是"含 LICENSE、NOTICE、第三方依赖许可清单"，缺一份即不达标。
      **已补**：`THIRD-PARTY-NOTICES.md`（分两类登记：随包分发 / 只依赖不分发），
      三份文件同时复制进 `publish/`（`Lingpane.App.csproj` 的 `None` 项，链接而非副本）。
- [x] **FR-10.3 便携标记口径不符** —— 需求写的是"exe 同级存在 `data` 目录或 `portable.flag`"，
      代码只认 `portable.marker`。照需求文档放标记的用户会得到非便携模式，数据仍写 `%LOCALAPPDATA%`。
      **已修**：`AppPaths.IsPortableDirectory` 三种口径等价，`portable.marker` 保留兼容；
      7 条单测 + 两次端到端走查（`D:\ac05\s9_portable.py`：`portable.flag` 与"仅 `data` 目录"各一次，
      `%LOCALAPPDATA%\Lingpane\settings.json` 的 mtime 全程不变）。
- [x] **AC-09 便携包不存在** —— 没有 `publish.ps1`（或等价脚本）产出 `Lingpane-x.y.z-portable.zip`，
      "解压即用"这条从未被验证过。
      **已补**：`tools/release/publish.ps1` —— publish → 校验（exe / 三份许可 / hostfxr / 版本串）
      → 写 `portable.flag` → zip → SHA256。默认 self-contained（框架依赖包不算"解压即用"）。
      走查 `D://ac05//s11_pkg.py`：解压到干净目录启动，`data\logs\app.log` 里
      `version=0.1.0+6c80b4a portable=True`，`%LOCALAPPDATA%\Lingpane` 的 settings 与 log 全程未变。
- [x] **`AssemblyInformationalVersion` 未设置** —— 程序集版本停在 `0.1.0.0`，与 CHANGELOG 无对应关系；
      FR-10.8 关于页要显示版本号，现在无处可读。
      **已补**：发版脚本写入 `0.1.0+<短commit>`（`Directory.Build.props` 关掉了 SDK 的自动追加，
      否则会拼成 `0.1.0+abc.<40 位 sha>`）；`AppInfo` 是唯一读取处，启动时记进 `app.start`。
      FR-10.8 关于页接 `AppInfo.DisplayVersion` 即可（页面本身仍属 0.2.0）。
- [x] **FR-6.1 地址栏自动补全**（1.5 天）—— 详见 §2.1。
      **已实现**：`Core/Navigation/PathCompletion.cs` + 21 条单测；候选 UI 用 Popup，
      `↓↑` 选、`Tab` 补全、`Enter` 跳转、`Esc` 关候选。走查 `D://ac05//s10_completion.py`。
- [x] **FR-6.16 文件夹大小按需计算**（1 天）—— 详见 §2.2。
      **已实现**：`Core/FileSystem/FolderSizeCalculator.cs` + 14 条单测；右键「计算大小」，
      行内显示「计算中…」/ `500 KB` / 部分结果带 `+`；状态栏进度 + 取消（`Esc` 同效）。
      走查 `D://ac05//s12_size.py`：big（500×1KB）= 500 KB、empty = 0 B，先算一个再算另一个时
      前一个结果保留。
- [x] **FR-6.9 系统 Shell 右键菜单**（2 天）—— 详见 §2.3。
      **已实现**：`App/Shell/ShellInterop.cs` + `ShellContextMenu.cs` + `Views/PaneView.ShellMenu.cs`；
      两个入口（右键菜单底部「更多选项…」、`Shift+右键`）。走查 `D://ac05//s13_shell.py`：
      两入口各弹出一份完整的系统菜单（本机 43 项，含 ToDesk / Bandizip / TortoiseSVN / 百度网盘等第三方扩展）；
      `D://ac05//s14_verb.py`：点系统菜单第一项「打开」→ `big` 文件夹在资源管理器里打开（PASS）。
      踩到并修掉的五个坑记在 §2.3 的表格里。

**B 类：性能门禁的欠账（不影响功能，影响"性能达标"这个卖点能不能宣称）**

- [x] **NF-05R 双窗格内存实测** —— 90 MB 是**估算值**。`performance-gate.md §9.7` 明写
      "双窗格开工前必须单独实测一次"，S4 上线时没测。不测就等于把预算当结论用。
      **已测（2026-11-06）**：探针加 `--perf-split`、脚本加 `-SplitPath`，双窗格
      10000+10000 项相对增量 39.2 MB，距 90 有 2.3 倍余量；双窗格只比单窗格多 2.8 MB 私有提交。
      阈值不调整（见 `performance-gate.md` §9.8）。
- [x] **NF-05H 托管堆 ≤ 12 MB 从未采集** —— 表里写的是"待测"，不是"通过"。
      **已测**：单窗格 5.8 MB、双窗格 5.4 MB，余量约 2 倍。ADR-0008 里"应在 9–10 MB"的预估
      偏高，原因是把私有提交增量当成了托管堆（已在 ADR 末尾补复核记录）。
      本轮环境处于高态、NF-05/NF-05R 不可判定，NF-05H 是唯一可比的内存判据——它的意义正在这里。
- [x] **非活动标签页不保留条目集合** —— **已完成（2026-11-07）**。切标签时释放上一个标签的
      目录内容（`PaneViewModel.ReleaseContent`），只留路径/历史/滚动锚点/选中路径；切回时走
      `LoadPanesAsync` 重新读盘，与"会话恢复的半开标签"同一条路径。
      A/B 对照（6 个万项标签）：强制 GC 后托管堆 **2–6 MB vs 15 MB**，私有提交 141 vs 149 MB。
      代价是切回要重新枚举一次（万项约 64 ms），选中与滚动在加载完成后按路径恢复。
      细节与两个测量陷阱见 `performance-gate.md` §9.5.1。
- [x] **O3（启动期关 IME）收益归属** —— **已完成（2026-11-07）**：收益真实存在，
      为 **−820 ms**（`activated` → `loaded`）与 **−74 MB** 峰值工作集；
      12 轮交替对照 12/12 稳定，另用最小空窗口三配置复核一致。2026-11-05 「注释掉无差异」
      是**观测段选错**——成本不在 `window` → `srcinit`，当时只在那一处读数。
      方法与数据见 `performance-gate.md` §6.2；§6.1 的测量规则据此补了第二条判据
      （`activated` → `loaded` 出现约 860 ms 即说明那行失效或被删）。
      功能面已复核：带该行时地址栏中文输入与候选框正常，不需要显式恢复。

**C 类：P1 零散项（0.1.0 留白，0.2.0 补）**

> **结案（2026-11-08）**：六项全部**不进 0.1.0**，已逐条写进 `CHANGELOG.md` 的「0.1.0 已知限制」，
> 随包文档自带说明。判断依据：六项都不影响"单窗格日常浏览可替代 Explorer"这个 0.1.0 目标，
> 而每一项都要动工具栏/导航栏布局或新增状态机，赶进发版窗口会把刚走查完的界面再翻一遍。
> 以下保留原始记录，勾选框统一置为"延后"。

- [延后] FR-10.6 上次异常退出提示（`App.xaml.cs` 无相关逻辑）
- [延后] FR-1.2.3 导航栏"网络 / 回收站"节点（`BuildQuickAccess()` 里没有）
- [延后] FR-1.2.4 导航栏宽度可拖（宽度仍是 `MinWidth`~`MaxWidth` 自适应，不可拖）
- [延后] FR-1.2.6 导航栏中键开新标签（只有列表区支持中键）
- [延后] FR-6.4 列显示配置 —— 计划建议并入 0.2.0，不占 0.1.0
- [延后] **双窗格下地址栏被挤得过窄** —— 走查 FR-6.1 时发现：窗口 1100 宽、分屏后每个窗格的地址栏
      只剩几十像素（`D://ac05//s10` 只显示得出 `D:`），补全候选跟着地址栏宽度走、同样看不清。
      属工具栏布局问题（4 个导航按钮 + 详细/列表 + 分屏按钮挤占同一行），不是补全本身的缺陷。

已闭合、不再计入剩余：AC-05 边界验收 7 项全 PASS（`ac05-boundary-acceptance.md`）、
AC-06 视觉走查（`ac06-visual-acceptance.md`）、FR-2.1–2.11、FR-3.1–3.5、FR-5.2.6。

**建议顺序**：A 类的合规两项 + 便携包（半天，纯硬缺口，零风险）→ B 类双窗格实测 + NF-05H（半天，
决定 90 MB 档是否成立）→ FR-6.1 → FR-6.16 → FR-6.9（最后，风险最高）。

---

## 二、P0：0.1.0 收尾（5 天）

前两项方案已在 `docs/m1-fr6-plan.md` §1 / §3 写定，这里只补本轮走查后的增量结论，不重复正文。

### 2.1 FR-6.1 地址栏自动补全（1.5 天）

> **状态：已实现（2026-11-06）。** `Core/Navigation/PathCompletion.cs`（纯逻辑，21 条单测）
> + `Views/PaneView.AddressCompletion.cs`（防抖 120ms / 探测超时 150ms / Popup 候选）。
> 落地时的两处调整：图标不用 MDL2 字形而复用列表行的 shell 图标转换器（字体字形在不同机器上会渲染成别的形状）；
> `Tab` 在无选中项时补第一条（要求先按 `↓` 再 `Tab` 太啰嗦）。

按 `m1-fr6-plan.md` §1 执行，四点补充：

1. **候选第一来源改用 `Entries` 快照**。当前目录已枚举的条目零 IO、命中率最高；`Directory.EnumerateDirectories` 只作为"用户输入了别的目录"时的补充，上限 200、超时 150ms。
2. **候选行要能显示"目录/文件"区分**，否则 `D:\work` 下同名的 `work` 目录与 `work.txt` 排在一起无法分辨。行模板复用 `Text.Row` + 右侧灰色 `Text.Secondary` 类型。
3. **空候选不是空弹窗**：没有候选时 `Popup` 不打开，不要显示"无匹配"——地址栏边打字边弹空框比不弹更烦。
4. **i18n**：候选区无新增界面文案（只有路径与类型名），`check-keys.py` 第 3 项可直接过。

风险不变：网络盘靠 150ms 超时降级为"只用缓存候选"。

### 2.2 FR-6.16 文件夹大小按需计算（1 天）

> **状态：已实现（2026-11-06）。** 与下文两处不同（详见 `m1-fr6-plan.md` §3 的状态行）：
> 结果挂在 `FileEntry` 上而非 `MainViewModel` 字典；`AttributesToSkip` 必须显式设成
> `ReparsePoint`（默认会跳过隐藏/系统文件）。取消除状态栏按钮外补了 `Esc`。

按 `m1-fr6-plan.md` §3 执行。两处与当前代码的衔接要提前确认：

- 大小覆盖字典放 `MainViewModel`，但 **P1 拆分后它必须跟着成为 `PaneViewModel` 的成员**（目录切换即清空）。现在的写法会让 M2 搬一次，不如一次到位：直接放 `PaneViewModel` 形态的字段，命名按窗格。
- 行模板的大小列现在是 `Text.Number` 绑 `SizeText`。加"计算中…"状态时不要塞进 `FileEntry`（Core 的不可变记录），用值转换器按 `(path, state)` 查字典。

### 2.3 FR-6.9 系统 Shell 右键菜单（2 天）—— 已实现

**状态**：已实现并走查通过。分层策略不变——日常右键走自建菜单，「更多选项…」与 `Shift+右键` 才加载系统菜单。

涉及文件：

| 文件 | 职责 |
|---|---|
| `App/Shell/ShellInterop.cs` | `IShellFolder` / `IContextMenu(2/3)` 手写 COM 声明、`SHParseDisplayName` / `SHBindToParent` / `IlFindLastId` / `TrackPopupMenuEx` |
| `App/Shell/ShellContextMenu.cs` | 六步流程：PIDL → 父 `IShellFolder` → `GetUIObjectOf` → `QueryContextMenu` → `TrackPopupMenuEx` + 消息钩子 → `InvokeCommand`；失败一律 `return false` 交回调用方降级 |
| `App/Views/PaneView.ShellMenu.cs` | 两个入口凑参数（选中项路径、owner 句柄、屏幕点）、推迟弹出、降级提示 |
| `App/Views/PaneView.xaml(.cs)` | 「更多选项…」菜单项、`Shift+右键` 拦截、`ContextMenuOpening` 取消 |

落地时实际踩到的五个坑（前三条是预判，后两条是实测才暴露的）：

| 点 | 说明 |
|---|---|
| 消息转发 | 只 Hook `WM_INITMENUPOPUP` 会得到"一级菜单正常、二级菜单空白"。`WM_DRAWITEM` / `WM_MEASUREITEM` 三条缺一不可，先给 `IContextMenu3.HandleMenuMsg2`，未处理再给 `IContextMenu2` |
| 坐标 | `TrackPopupMenuEx` 要物理像素。本机 125% DPI，`PointToScreen` 已经返回物理像素，**不要再乘缩放比** |
| 多选 | 必须传 PIDL 数组（`cidl > 1`）。只传首项时菜单能弹出来，但"发送到""压缩"只对第一个文件生效，且不报错 |
| 取末级 PIDL | **不能 P/Invoke `shlwapi!ILFindLastID`**：它在 SDK 头文件里是内联函数，动态库里没有同名导出，调用直接抛 `EntryPointNotFoundException`。现象是"点更多选项毫无反应、日志里也没有一行"——因为异常类型还落在当时那个窄 catch 过滤器之外。改为手动遍历（每个 SHITEMID 前两字节是自身长度），见 `ShellInterop.IlFindLastId` |
| 弹出时机 | 不能在 `MenuItem` 的命令回调里同步弹系统菜单：那一刻自建菜单还没关闭，它关窗时收回鼠标捕获，会把刚弹出的系统菜单一起收掉。必须 `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)` 推迟；`Shift+右键` 同理，且要靠 `ContextMenuOpening` 把自建菜单取消掉（只在 `PreviewMouseRightButtonDown` 里设 `Handled` 拦不住，自建菜单是抬起时才弹的）。系统菜单弹出前再 `SetForegroundWindow(owner)` 兜一次，否则收不到输入 |

降级：任何异常（catch 用宽口径，不用类型过滤器，理由见上表第 4 条）→ 关掉系统菜单、回落到自建菜单、
写 `app.log` 的 `shell.menu` + 状态栏一句话，不弹窗。

走查证据：

- `D:\ac05\s13_shell.py`：入口 A（自建菜单 → 更多选项…）与入口 B（`Shift+右键`）各弹出一份完整系统菜单，
  本机 43 项，含 ToDesk / Bandizip / TortoiseSVN / 百度网盘 / 火绒等第三方扩展；截图 `s13-2-shell-a.png`、`s13-4-shell-b.png`。
  `Esc` 关闭，`app.log` 无 `shell.menu` 降级记录。
- `D:\ac05\s14_verb.py`：点系统菜单第一项「打开」→ `big` 文件夹在资源管理器里打开（`RESULT: PASS`），
  证明 `InvokeCommand` 链路真的执行了，不只是"菜单画出来了"。

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

### 4.5 FR-4 彩色标签（4 天，存储引擎已复核）

**决策已定（2026-11-05）：不引入 SQLite，走单文件追加日志 + 内存索引。**
完整论证见 [`docs/tags-storage.md`](tags-storage.md)，要点：

| 指标（5000 条实测） | SQLite | 文件 + 字典 |
|---|---|---|
| 私有提交增量 | +11.96 MB（释放连接后不归还） | +1.90 MB |
| 4 万次可视区查询 | 189 ms | 200 万次 201 ms（快 47 倍） |
| 包体 | +1.4 MB（`e_sqlite3.dll`） | 0 |
| 新增依赖 | 2 个包 | 0 |

NF-05R 只剩 20 MB 余量，而 SQLite 一口气要 12 MB 且不退——在"零依赖 + 内存紧张 + 包体只剩 5MB"三条约束下，它换来的只是"更正规"的心理收益。

**真正的风险不在存储，在 FileId 的获取。** `FileSystemEnumerable`（`FindFirstFileEx`）拿不到 FileIndex，`WIN32_FIND_DATA` 里没有这个字段。必须改用 `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)` 自己枚举（一次调用返回整批，含 FileId），成本约 1–2 天。这一条比选哪个存储引擎重要十倍，原计划漏了。

**落地要点**

| 点 | 做法 |
|---|---|
| FileId 枚举 | `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)`，处理 `FILE_ID_BOTH_DIR_INFO` 结构、缓冲区不足重启、长路径、UNC。**只在"该目录存在标签记录"时才走这条慢路径**，其余目录沿用现有 `FileSystemEnumerable`（否则一万项枚举从 82ms 涨到数百 ms，击穿 NF-03） |
| 存储 | `%LOCALAPPDATA%\Lingpane\data\tags.jsonl`，每行一条 `set`/`del` 操作；`Flush(true)` 落盘；行数 > 存活数×3 且 > 2000 时后台压缩 |
| 延迟加载 | 首次"设置标签"或"显示带标签目录"才读文件建索引；启动路径不碰（否则冷启动 +几十 ms） |
| 查询 | 内存字典 `Dictionary<(uint,ulong),TagRecord>`，可视区查询直接哈希，无批量/逐项之分 |
| 渲染 | 圆点走 §3.6 的"图标+圆点合成一个 Grid"，不增加行模板元素数 |
| 上限 | 5 万条，超过拒绝新增并在状态栏提示（显式降级，不静默） |
| 跨卷失效 | 明确告知"跨盘移动后标记丢失"（需求已接受此限制），不做模糊重绑（那是 P2 需求） |

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
P4 标记（5d）───────────┼─ FR-4 彩标（FileId 枚举 → 存储 → UI）→ FR-5 设置补齐
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
| AC-09 便携 | **达标**（2026-11-08） | `publish.ps1` 出 self-contained zip，包内 `portable.flag`；解压启动走查确认数据落 `data\`、`%LOCALAPPDATA%` 全程未写 |
| AC-10 依赖清单 | **达标**（2026-11-06） | `THIRD-PARTY-NOTICES.md` 随包分发，与 `LICENSE` / `NOTICE` 一并校验非空 |
| AC-11 多语言 | 达标 | — |

---

## 七、不建议顺带做的

`docs/requirements.md` §8.3 的黑名单仍然有效，M2 期间最容易"顺手做一下"的三个：

| 项 | 为什么别做 |
|---|---|
| 图标/磁贴视图（C14） | 二维布局无法用 `VirtualizingStackPanel` 虚拟化，万项内存失控。缩略图需求已由预览窗格承担 |
| 多窗口（B12） | 改动窗口级状态（watcher、单实例、内存口径）的成本远高于收益，1.1 再议 |
| 全盘搜索（B9） | 需要索引服务，与"不常驻、不后台索引"的 P4 原则直接冲突 |
