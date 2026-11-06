# M1 收尾：FR-6 剩余项实现方案

> 对应 `docs/requirements.md` v1.1 §7「0.1.0 = 单窗格可用版」。
> 0.1.0 阶段 FR-6 还差 5 项 + 1 项待定，本文逐项给出可执行的实现路径。
> 每项含：现状差距、实现步骤、涉及文件、风险与降级、验证方式、工作量档位。

## 总览

| 序 | 需求 | 内容 | 工作量 | 建议顺序 | 状态 |
|---|---|---|---|---|---|
| 1 | FR-6.1 | 地址栏自动补全 | 1.5 天 | ② | **已完成**（2026-11-06，见 m2-plan §2.1） |
| 2 | FR-6.8 | 新建文件 | 0.5 天 | ① | **已完成** |
| 3 | FR-6.16 | 文件夹大小按需计算 | 1 天 | ③ | 待做 |
| 4 | FR-6.5 | 覆盖图标（快捷方式箭头等） | 0.5 天 | ① | **已完成**（改按真实路径解析，见 ADR-0007） |
| 5 | FR-6.9 | 系统 Shell 右键菜单 | 2 天 | ⑤（最后，风险最高） | 待做 |
| — | FR-6.4 | 列显示配置 | 0.5 天（最小实现） | 建议并入 0.2.0，见末节 | 待定 |

工作量按"一人、熟悉本仓库、含自测"估算，不含返工。

---

## 1. FR-6.1 地址栏自动补全

### 现状

`MainWindow.xaml` 的地址栏是双态：`BreadcrumbHost`（面包屑）与 `AddressBox`（`TextBox`，绑定 `AddressText`，
`UpdateSourceTrigger=PropertyChanged`），由 `IsAddressEditing` 切换；`OnAddressKeyDown` 处理 Enter / Esc。
**没有候选列表**。

### 实现步骤

**(1) 补全逻辑单独成类，放 Core，纯函数可测**

`src/Lingpane.Core/Navigation/PathCompletion.cs`

```
输入文本 → 拆成 (目录部分, 词干)
  ├─ 文本以 \ 或 / 结尾        → 目录部分 = 全文，词干 = ""
  ├─ Path.GetDirectoryName 成功 → 目录部分 = 它，词干 = 文件名部分
  ├─ 形如 C: / C:\ / \\srv\share → 目录部分 = 该根，词干 = ""
  └─ 都不成立（裸词干）          → 目录部分 = 当前目录，词干 = 全文
```

候选来源，按优先级合并去重（大小写不敏感）：

1. 当前目录已枚举的 `Entries`（零 IO，命中率最高）；
2. `Directory.EnumerateDirectories(目录部分, 词干 + "*")`，`EnumerationOptions{ IgnoreInaccessible = true, AttributesToSkip = ReparsePoint }`，**上限 200 条**；
3. 父目录的兄弟目录（用户往回改一层时用得上），同上限。

排序：前缀匹配优先于子串匹配 → 目录优先于文件 → 同组按自然序（复用 `EntrySort`）。总上限 8 条。

根路径形态复用 `PathRules.TrimSeparators`（已保证 `C:\` 不被削成 `C:`）。

**(2) 候选 UI 用 Popup，不用 ComboBox**

`AddressBox` 下方挂 `Popup`（`PlacementTarget=AddressBox`、`StaysOpen=True`、`AllowsTransparency=True`）+ `ListBox`。
不用 `ComboBox`：`ComboBox` 自带 `TextBox` 与下拉的焦点争夺，在"边打字边跳"的场景下会丢焦点。

`IsOpen` 条件 = 有候选 && `AddressBox.IsKeyboardFocusWithin` && 词干非空。

**(3) 键盘与防抖**

| 键 | 行为 |
|---|---|
| `↓` / `↑` | 移动选中项（不改变 `AddressBox.Text`） |
| `Tab` | 用选中项补全到 `AddressBox`，不导航 |
| `Enter` | 有选中项→跳到该项；无选中→原导航逻辑 |
| `Esc` | 有候选→先关候选；无候选→退出编辑态（原逻辑） |

防抖 120ms（比搜索的 250ms 短：地址栏输入节奏快）。每次输入变化重启计时器并 `CancellationToken` 取消上一轮枚举；
枚举在 `Task.Run`，回 UI 前比对 `AddressText` 是否仍是发起时的值，防止旧结果覆盖新输入。

**(4) 超时降级**

目标目录枚举超过 **150ms** 未返回则本轮放弃，只用当前目录缓存与兄弟目录。
UNC 与网络盘必然触发这条降级——宁可无候选，不可让地址栏卡住。

### 风险与降级

| 风险 | 降级 |
|---|---|
| 网络盘枚举挂起 | 150ms 超时 + `CancellationToken`；只显示缓存候选 |
| 输入法组合态下按键被吞 | 沿用 `KeyBindings.Normalize` 的 `ImeProcessed` 归一化，不在组合态拦键 |
| 长路径（>260）| 直接透传，不补全（`PathRules` 已支持 `\\?\`） |
| 候选窗口遮挡列表 | `Popup` 用 `PlacementMode.Bottom`，`MaxHeight=240`，滚动 |

### 验证

- 单测（`PathCompletionTests`，≥10 条）：根路径、`C:\`、UNC、裸词干、以分隔符结尾、多级回退、超时降级、去重、上限。
- 手工：`Ctrl+L` → 输入 `D:\work` → 出现候选 → `↓` `Tab` `Enter`；UNC 路径不卡。

---

## 2. FR-6.8 新建文件

> **状态：已实现（2026-11-05）。** `FileOperations.TryCreateFile`（`FileMode.CreateNew` 原子创建）
> + 命令 `file.newFile` + `Ctrl+Shift+M` + 右键「新建文本文档」，建完刷新选中并进重命名。
> 单测 5 条覆盖正常创建、重名改名保留扩展名、非法名、不截断同名文件、目录不存在。

### 现状

`FileOperations.TryCreateFolder` 已有；命令 `Ctrl+Shift+N` 只建文件夹；`TextPromptWindow` 已可复用。

### 实现步骤

**(1) Core 加一个方法，与建文件夹同构**

`src/Lingpane.Core/FileSystem/FileOperations.cs`

```
TryCreateFile(directory, name, out path, out error)
  ├─ TryValidateName（同一套非法字符/保留名/结尾点校验）
  ├─ UniqueName（重名自动追加 (2)，与文件夹一致）
  └─ FileMode.CreateNew（原子，天然防并发重名）
```

错误映射沿用 `TryCreateFolder` 的既有文案键（`Error.AccessDenied` 等）。

**(2) 菜单与快捷键**

右键空白处 → 「新建」子菜单：

- 文件夹　`Ctrl+Shift+N`（已有）
- 文本文档　`Ctrl+Shift+M`

只给"文本文档"一种，**不猜类型**：`.md`/`.docx` 是否存在模板取决于装了哪些软件，猜错就是给用户建一个打不开的文件。
第一版固定 `.txt`，建完由用户改扩展名（扩展名显示已可开关）。

`Ctrl+Shift+M` 未与现有键位冲突（现有表见 `docs/shortcuts.md`，`Ctrl+Shift` 段只有 N/C/D/E/P/A）。

**(3) 建完即选中并进入重命名**

与 Explorer 一致：插入新行 → 选中 → 触发重命名（复用 `F2` 那条路径）。
`FileSystemWatcher` 的 400ms 去抖刷新随后到达时，按路径恢复选中（已有逻辑），不会打断重命名。

### 风险与降级

| 风险 | 降级 |
|---|---|
| 只读目录 / 无权限 | 统一错误提示（`Error.*` 键，走 i18n） |
| 与 watcher 刷新竞争导致选中丢失 | 先本地插入并选中，watcher 刷新按路径恢复 |
| 用户在重命名中按 Esc | 保留 `新建文本文档.txt`（与 Explorer 一致），不删除 |

### 验证

- 单测（≥4）：正常创建、重名追加、非法名拒绝、无权限返回错误。
- 手工：建文件 → 直接改名 → 刷新后仍存在且选中。

---

## 3. FR-6.16 文件夹大小按需计算

> **状态：已实现（2026-11-06）。** 落地时三处与本文不同的地方：
> 1) 结果存在 `FileEntry` 上而不是 `MainViewModel` 的字典里 —— 每个条目多 9 字节
>    （万项目录约 90KB），换来"结果跟着行走"，不必让列表按路径去查表，也不必在
>    目录切换时维护一张随时会串值的字典；
> 2) 除 `ReparsePoint` 外**必须**显式覆盖默认的 `AttributesToSkip`（默认跳过 Hidden|System），
>    否则隐藏文件不计入，用户会拿到比实际小的数字；
> 3) 取消入口除状态栏按钮外补了 `Esc`（焦点在列表上时）。
> 走查 `D://ac05//s12_size.py`。

### 现状

`FileEntry.Size` 对目录为 0，`大小` 列显示空。需求明确要求"仅由用户显式触发，严禁自动递归"。

### 实现步骤

**(1) 计算器**

`src/Lingpane.Core/FileSystem/FolderSizeCalculator.cs`

```
CountAsync(path, ct, progress)
  ├─ new FileSystemEnumerable<long>(...)
  │    EnumerationOptions { RecurseSubdirectories = true,
  │                         IgnoreInaccessible = true,
  │                         AttributesToSkip = FileAttributes.ReparsePoint }
  ├─ 累加 Length
  ├─ 每 512 项 ct.ThrowIfCancellationRequested()
  └─ 每 200ms 通过 IProgress 回报一次（避免 UI 消息洪泛）
返回 (bytes, fileCount, folderCount, completed)
```

`AttributesToSkip = ReparsePoint` 同时满足 T-05（不跟符号链接，防循环）与"不跨挂载点"。

**(2) 触发入口**

- 选中文件夹 → 右键「计算大小」；
- 列头右键「计算大小」（对当前选中项）；
- 状态栏临时显示「正在计算：已扫描 N 项 · 1.2 GB」，右侧「取消」（`Esc` 同效）。

按需求表述，"列右键"理解为选中项右键菜单；`Alt+Enter` 保持打开属性页不变（属性页本身会显示大小，但那是 Shell 的递归，不归我们管）。

**(3) 结果落点**

不改 `FileEntry`（Core 的不可变记录），在 `MainViewModel` 加一层覆盖字典
`Dictionary<string, long> _folderSizes`，行模板的大小列走 `FolderSizeText` 转换器：
字典有值→格式化显示；计算中→"计算中…"；无值→空。

目录切换 / 刷新时清空该字典（避免跨目录串值），计算任务随之取消。

### 风险与降级

| 风险 | 降级 |
|---|---|
| 网络路径 / 机械盘极慢 | 30s 上限，到点停止并标注"部分结果（已扫描 N 项）" |
| U 盘热拔 | 异常吞掉，返回已累计值 + 标记"未完成" |
| 10 万文件耗时 | 后台 + 可取消；实测目标 ≤3s（SSD），只在显式触发时发生 |
| 用户连续点多个文件夹 | 单实例任务，新请求取消旧请求 |

### 验证

- 单测（≥5）：空目录、嵌套多层、含无权限子目录、含符号链接不跟随、取消后不再回调。
- 手工：10 万项目录测耗时；U 盘拔插；取消按钮立即生效。

---

## 4. FR-6.5 覆盖图标（快捷方式箭头等）

> **状态：已实现（2026-11-05）。** 但做法与本节的初版设想不同——实测否掉了"按扩展名加
> `SHGFI_LINKOVERLAY`"这条路，改为对 `.lnk`/`.url` 按真实路径解析。详见
> [ADR-0007](adr/0007-shortcut-icons.md)。

### 现状

`ShellIconProvider` 走 `SHParseDisplayName` + `SHGetFileInfo(SHGFI_PIDL | SHGFI_SMALLICON)`，
按扩展名缓存。ADR-0006 已把目录专属图标的路径打通。

### 实现步骤（实际采用）

**(1) 实测：按扩展名加覆盖标志无效**

按类型查询（`SHGFI_USEFILEATTRIBUTES`）时追加 `SHGFI_LINKOVERLAY`，位图对比结果：

| 类型 | 加标志后 | 结论 |
|---|---|---|
| `.lnk` | 位图不变 | 类型图标**本身已带箭头**（空白页+箭头），再加是重复 |
| `.url` | 位图不变 | 同上（地球+箭头） |
| `.txt` | 位图不变 | 无覆盖 |
| `.exe` | **多了箭头** | 标志确实生效，但给普通程序加箭头是错误语义 |

所以这条路既不能给快捷方式加分，还会给别的类型加错。

**(2) 改成按真实路径解析 `.lnk` / `.url`**

`ResolvePathIconIndex(path, isDirectory: false)` 已经能把"目标程序图标 + 自动合成的箭头"取回来
（对照位图确认：Chrome 彩色圆环、Obsidian 紫色宝石，均带左下箭头）。

接进已有的后台通道 `FolderIcons.Resolve`：与目录图标共享配额、本地盘限制、`ApplicationIdle` 时序；
判重改为"与按类型解析出的序号相同则丢弃"（链接损坏或目标无权访问时会回落到类型图标，此时不写回）。
新增 `ResolveExtensionIconIndex(iconKey)` 与扩展名序号缓存，避免几十个 `.lnk` 反复问同一个问题。

**(3) 不做按文件状态的覆盖**

共享标记、云同步状态、杀软标记都需要逐项查询，万项目录下违反 NF-01 与 R-08，
已作为 §8.3 的 C15 记入黑名单。目录图标不受影响（仍走 ADR-0006 的 PIDL 专属图标）。

### 风险与降级

| 风险 | 降级 |
|---|---|
| 深色主题下箭头是浅色位图 | 系统本就如此，不重绘（避免与系统观感不一致） |
| `.lnk` 解析失败（链接损坏、目标无权限） | 序号回落到类型图标 → 判重时丢弃，界面保持"空白页+箭头"，不报错 |
| 目录里 `.lnk` 极多（>240 项） | 超出单次配额，其余保持通用快捷方式图标，滚动不受影响 |
| 网络盘上的 `.lnk` | 不解析（本地盘限制），保持类型图标 |

### 验证（已完成）

- 桌面 10 个快捷方式：改造前全部显示同一个"空白页+箭头"，改造后各自显示目标程序图标 + 箭头
  （CodeBuddy、Chrome、Obsidian、ima、Hermes 各不相同），与资源管理器逐项一致 ✅
- 启动耗时：与改动前二进制交替测量，各阶段差 +2~+10ms、`preShow` 差 0，属噪声（见 `docs/performance-gate.md` §11）

---

## 5. FR-6.9 系统 Shell 右键菜单（风险最高，放最后）

### 现状

`Controls.Menu.xaml` + `MainWindow.xaml.cs` 自建精简菜单，覆盖新建/重命名/复制/剪切/粘贴/删除/复制路径/属性。
不包含「打开方式」「发送到」「压缩」「云盘同步」等由第三方 Shell 扩展提供的项。

### 为什么不直接全用系统菜单

`QueryContextMenu` 会把**所有已注册的 Shell 扩展**都实例化一遍。实机上常见的 200–400ms 首响还算好的，
坏情况是某个扩展挂起（压缩软件、网盘、杀软都会），右键后整个 UI 卡死几秒——这类问题无法在我们代码里超时兜底，
因为 COM 调用在 UI 线程上无法中断。这直接踩 R2（Shell 互操作稳定性）。

### 实现步骤

**(1) 分层：默认自建，显式触发系统菜单**

| 入口 | 行为 |
|---|---|
| 右键 | 自建精简菜单（现状） |
| 自建菜单底部「更多选项…」 | 系统 Shell 菜单 |
| `Shift + 右键` | 直接系统 Shell 菜单 |

理由：日常操作走自建（快、稳、可 i18n），需要"打开方式/发送到/第三方扩展"时用户是主动要，可接受等待。

**(2) COM 封装**

`src/Lingpane.App/Shell/ShellContextMenu.cs`

```
SHParseDisplayName(path) → pidl
SHBindToParent / SHGetDesktopFolder → IShellFolder
IShellFolder.GetUIObjectOf(hwnd, [pidl], IID_IContextMenu) → IContextMenu
IContextMenu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL)
IContextMenu2 / IContextMenu3 同时 QueryInterface
TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_TOPALIGN, x, y, hwnd, null)
→ 返回 id > 0 时 InvokeCommand
```

多选：`GetUIObjectOf` 传 PIDL 数组（`cidl > 1`），Shell 返回支持多选的 `IContextMenu`。

**(3) 唯一的真难点：消息转发**

`TrackPopupMenuEx` 是一条**阻塞**的消息循环。子菜单（「打开方式」「发送到」）是 owner-draw 的，
Shell 扩展靠 `WM_INITMENUPOPUP` / `WM_DRAWITEM` / `WM_MEASUREITEM` 才能把内容画出来。
不转发这三个消息，表现就是子菜单一片空白。必须：

```
HwndSource.AddHook：WM_INITMENUPOPUP / WM_DRAWITEM / WM_MEASUREITEM
  → 先给 IContextMenu3.HandleMenuMsg2(msg, wParam, lParam, out result)
  → 返回 false（未处理）时再给 IContextMenu2.HandleMenuMsg
  → 都未处理才交给默认
```

`IContextMenu3` 优先（支持 `HandleMenuMsg2` 的返回值语义），`IContextMenu2` 兜底。

**(4) 释放**

`DestroyMenu`、逐级 `Marshal.ReleaseComObject`、`CoTaskMemFree(pidl)`。
顺序错了会泄漏 GDI 句柄（NF-13 要求 CI 有泄漏检测）。

**(5) 失败一律降级**

任何 HRESULT 失败 / COM 异常 → 关闭系统菜单、只显示自建菜单、写 `crash.log`（不弹窗）。
系统菜单不允许成为"右键没反应"的原因。

### 风险与降级

| 风险 | 降级 |
|---|---|
| 第三方扩展挂起（R2，最高） | 只在显式入口触发；无代码层解法，记录为已知限制 |
| 子菜单空白 | 严格实现三条消息转发（见上）；缺一条就是空白 |
| GDI/COM 泄漏 | `finally` 里统一释放；`Measure-List.ps1` 加句柄/GDI 采样 |
| 高 DPI 下菜单错位 | 传物理像素坐标（`PointToScreen` 已返回物理像素；注意本机 120 DPI 的换算） |
| 多选时 Shell 只对首项生效 | 必须传 PIDL 数组，不是只传第一项 |

### 验证

- 手工矩阵：桌面、普通文件夹、多选项、快捷方式、只读文件、OneDrive 目录、网络路径。
- 重点项：「打开方式」「发送到 → 桌面快捷方式」「属性」、7-Zip 的「解压到」。
- 泄漏：连续打开系统菜单 100 次，GDI 对象增量 <50、句柄增量 <50（NF-13）。
- 失败注入：故意传不存在的路径，确认降级到自建菜单且不崩溃。

---

## 6. FR-6.4 列显示配置（第 6 项，建议不放进 0.1.0）

### 现状

四列固定（名称/修改日期/类型/大小），可拖宽、可排序，不可增删或隐藏。

### 为什么建议推到 0.2.0

现在列头与行模板是**两套独立的固定 `Grid` 列宽资源**（`Column.*.Width` 通过 `DynamicResource` 对齐）。
要做"任意增删列"，必须把"列定义"变成数据（`ObservableCollection<ColumnDefinition>` 驱动表头与行模板），
而 0.2.0 的标签页/双窗格本来就要重构列表容器——现在改一次，那轮再改一次。

### 如果非要现在做：最小实现

只加"隐藏类型列""隐藏大小列"两个开关（设置在「行为」分节）：

- 列宽资源置 0 + 该列 `Visibility=Collapsed`；
- 表头列宽 Thumb 的 `Tag` 同步禁用；
- 记忆到 `settings.json` 的 `layout` 段（已有 `LayoutSettings`）。

半天量级，不动行模板结构。**但仍建议等 0.2.0。**

---

## 建议执行顺序与理由

1. **新建文件 + 覆盖图标**（各半天，互不相干，先清掉两个明显的"Explorer 有我没有"）
2. **地址栏补全**（独占一轮，因为要新增 Popup 与新测试类）
3. **文件夹大小**（纯后台任务 + 取消语义，需要仔细测）
4. **系统 Shell 菜单**（风险最高，单独一轮，做完要过泄漏与降级两组验证）

前四项做完，0.1.0 的 FR-6 验收项即闭合；系统菜单若在实机上问题过多，可先只保留"更多选项"入口并标注已知限制，
不阻塞发版——它属于"锦上添花"，而前四项属于"缺了就还得开 Explorer"。
