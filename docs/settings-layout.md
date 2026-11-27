# 设置页对照表（控件 ↔ 页 ↔ 字段）

设置页在 0.2.1 从"一列八个分区"改成**左侧八页导航 + 右侧单页滚动**（0.2.1 中段新增「标签」页，
0.2.2 新增「快捷键」页），分页口径从
"设置项属于哪个对象"变成"用户想干什么"。这一步把 `GeneralSettings` / `AppearanceSettings` /
`BehaviorSettings` 的对象边界打散了，同一个对象里的字段现在分属不同页。

这份表是给**改代码的人**看的：新增一个设置项时，先在这里找到它该去的页；找不到，
说明归类没想清楚，不要先写 XAML。它也记录了哪些字段的"页位置"与"对象位置"不一致——
那正是按页恢复最容易出错的地方。

> 最后核对：2026-11-26，对应 `SettingsWindow.xaml` 的八页结构。字段路径为 `settings.json` 中的实际键名
> （camelCase），规范见 `docs/settings-schema.md`。

---

## 1. 八页总览

| # | 页 | 设置项数 | 承载的设置对象 | 按页恢复方法 |
|---|---|---|---|---|
| 1 | 常规 | 5 | `general` + `behavior` 的启动项 | `ResetGeneralPage()` |
| 2 | 外观 | 10 | `appearance` + `behavior.previewAutoOpen` | `ResetAppearancePage()` |
| 3 | 文件列表 | 11 | `behavior` 的视图排序 + `appearance.tabStripPosition` + `columns` | `ResetListPage()` |
| 4 | 操作与确认 | 8 | `behavior` 的打开与确认项 | `ResetActionPage()` |
| 5 | 隐私与存储 | 4 | `privacy` + 标签库 | `ResetPrivacyPage()` |
| 6 | 标签 | 3 | `appearance.tags` | `ResetTagsPage()` |
| 7 | 快捷键 | 0（列表项不是"字段"） | `shortcuts` | `ResetShortcutsPage()` |
| 8 | 关于 | 0 | 无（内嵌 `AboutView`，全只读） | 无（隐藏恢复按钮） |

"设置项"按**一个可写字段**计：复合控件（字号 `−`/`+` 与"跟随系统"勾选、上限下拉与"清空"按钮）
算一项；只读块（缓存占用、标签条数、数据目录）与纯动作按钮（导出/导入/打开目录）不计数。

「快捷键」页没有开关/下拉这类"设置项"：它整页是一张命令表，改动落在 `shortcuts` 里，
所以"设置项数"记 0。

八页之外还有三处全局控件：页标题栏的「恢复本页默认」、底栏的「恢复全部设置」与「关闭」、
底栏状态位。见第 10 节。

---

## 2. 页 1：常规

| 控件 `x:Name` | 界面标签（资源键） | 字段 | 取值 |
|---|---|---|---|
| `LanguageCombo` | `Settings.Language` | `general.language` | `auto` / `zh-Hans` / `en` |
| `StartupCombo` | `Settings.Behavior.Startup` | `behavior.restoreLastSession` | 不恢复 / 恢复上次会话 |
| `StartupWorkspaceCombo` | `Settings.Behavior.Startup.Workspace` | `behavior.startupWorkspace` | 工作区名或 `null` |
| `NewTabModeCombo` | `Settings.Tabs.NewTabMode` | `general.newTabMode` | `home` / `path` / `last` |
| `NewTabPathBox` + 「浏览」 | `Settings.Tabs.NewTabPath` | `general.newTabPath` | 绝对路径或 `null` |
| `NewTabPathHint` | — | 只读提示 | `newTabMode != path` 时才显示 |

**恢复本页默认覆盖**：`NewTabMode`、`NewTabPath`、`RestoreLastSession`、`StartupWorkspace`。

**界面语言不在恢复范围内**——按一下就把整个界面切回跟随系统，用户会以为程序坏了。
这一条写在 `ResetGeneralPage()` 的注释里，改动前先读。

`startupWorkspace` 非空时 `Normalize()` 会把 `restoreLastSession` 置 `false`：两项都表达
"启动时放什么"，显式选择的那一项胜出。因此这两行的控件存在互斥联动，改其中一行要检查另一行。

---

## 3. 页 2：外观

| 控件 `x:Name` | 界面标签 | 字段 | 取值 |
|---|---|---|---|
| `ThemeCombo` | `Settings.Appearance.Theme` | `appearance.theme` | `system` / `light` / `dark` / `highContrast` |
| `AccentCombo` | `Settings.Appearance.Accent` | `appearance.accent` | `null`（跟随系统）或 `#RRGGBB` |
| `FontCombo` | `Settings.Appearance.Font` | `appearance.fontFamily` | 字体名或 `null`（默认栈） |
| `FontSizeText` + 「−」「+」 + `FollowSystemFontCheck` | `Settings.Appearance.FontSize` | `appearance.fontSizePt` | `null`（跟随系统）或 8–24 整数 |
| `DensityCombo` | `Settings.Appearance.Density` | `appearance.density` | `comfortable` / `compact` |
| `ShowNavCheck` | `Settings.Appearance.ShowNav` | `appearance.showNavPane` | 布尔 |
| `ShowStatusCheck` | `Settings.Appearance.ShowStatus` | `appearance.showStatusBar` | 布尔 |
| `ShowPreviewCheck` | `Settings.Appearance.ShowPreview` | `appearance.showPreview` | 布尔 |
| `ImmersiveTitleBarCheck` | `Settings.Appearance.ImmersiveTitleBar` | `appearance.immersiveTitleBar` | 布尔（**改后需重启**） |
| `PreviewAutoCheck` | `Settings.Behavior.PreviewAuto` | **`behavior.previewAutoOpen`** | 布尔 |
| `PreviewAutoHint` | `Settings.Behavior.PreviewAuto.Hint` | 只读提示 | 常显。写明"仅在预览窗格显示时生效" |
| `FontHint` | — | 只读提示 | 字体族回落到默认栈时才写（FONT-03） |

**跨对象项**：`PreviewAutoCheck` 控的是预览行为，落在外观页——它与上面三条同属
"窗格长什么样"，与「操作与确认」页那组"打开/确认"不是一类。这是**唯一**一个
`behavior.*` 字段落在外观页，`AppearancePageFields` 里就是为它单独列的一项。

**这两个预览项是依赖关系，不是并列关系**，界面必须替用户表达出来：
`ShowPreview`（默认关）决定那一栏存不存在，`PreviewAutoOpen`（默认开）只决定栏里要不要
自动装内容。后者在前者为假时完全空转——`RefreshPreview()` 第一行就是 `if (!_previewOpen) return;`。
所以：

1. 勾上「选中文件后自动预览」时，若「显示预览窗格」没勾，**一并打开**（并把那颗勾同步勾上）。
   反向不联动：关掉窗格时保留这一项，下次打开仍然是自动。
2. `ApplyPreviewSettings()` 在 `_previewOpen` 由关变开时补一次 `RefreshPreview()`，
   否则栏出现了、内容还是空态，得再点一下文件。
3. 勾选框下常显一行 `PreviewAutoHint`，把依赖关系写在用户眼前——不靠界面上两行相邻来暗示。

**唯一需要重启的一项**：`ImmersiveTitleBarCheck` 决定窗口是否用自绘标题栏（FR-10.11）。
`WindowStyle` 一旦创建窗口就改不了，运行期切换要重建 HWND，所以这里只写设置、不调
`ApplySettingsToUi()`——强行让主窗口局部响应，只会得到"系统标题栏撤了、无边框还在"的半成品。
开关下方的提示文字明确写了"更改后需重启应用"。

**恢复本页默认**：整段换掉 `AppearanceSettings`，但**把两样东西带过去**——
标签颜色名（显示在第 5 页，由 `ResetPrivacyPage()` 负责）与标签栏位置（控件在第 3 页，
由 `ResetListPage()` 负责）。旧版把颜色名显示在"隐私"段下、却由"恢复默认外观"清掉，
操作与显示的位置对不上；标签栏位置是同一个问题在 0.2.1 分页结构里的翻版，一并纠正。

---

## 4. 页 3：文件列表

| 控件 `x:Name` | 界面标签 | 字段 | 取值 |
|---|---|---|---|
| `TabStripCombo` | `Settings.Appearance.TabStrip` | **`appearance.tabStripPosition`** | `top` / `bottom` |
| `ViewCombo` | `Settings.Behavior.DefaultView` | `behavior.defaultView` | `details` / `list` |
| `SortCombo` | `Settings.Behavior.DefaultSort` | `behavior.sortBy` | `name` / `date` / `type` / `size` |
| `DescendingCheck` | `Sort.Descending` | `behavior.sortDescending` | 布尔 |
| `FoldersFirstCheck` | `Settings.Behavior.FoldersFirst` | `behavior.foldersFirst` | 布尔 |
| `NaturalSortCheck` | `Settings.Behavior.NaturalSort` | `behavior.naturalSort` | 布尔 |
| `CaseSensitiveCheck` | `Settings.Behavior.CaseSensitiveSort` | `behavior.caseSensitiveSort` | 布尔 |
| `ShowHiddenCheck` | `Settings.Behavior.ShowHidden` | `behavior.showHidden` | 布尔 |
| `ShowExtensionsCheck` | `Settings.Behavior.ShowExtensions` | `behavior.showExtensions` | 布尔 |
| `ShowSystemCheck` | `Settings.Behavior.ShowSystem` | `behavior.showSystem` | 布尔 |
| `ColumnDateCheck` / `ColumnTypeCheck` / `ColumnSizeCheck` | `Column.DateModified` 等 | `columns.<档位>.showDate/Type/Size` | 布尔 |
| `ResetColumnsButton` | `Settings.Columns.Reset` | 同上一行 | 动作 |

**页面有 9 个 `behavior.*` 控件，`ListPageFields` 也正是这 9 项。** 但用户在页上看到的
还有标签栏位置（属 `appearance`，由本页重置）与列（属 `columns`）。所以这一页的「恢复本页默认」=
9 个行为字段 + `appearance.tabStripPosition` + 两侧窗格的视图/排序记忆
（`layout.primaryPane` / `secondaryPane` 清空，回落 `behavior` 的全局默认）+
`columns` 标准档位（`layout.standard`）的三列显示开关。

标签栏位置由这一页重置，不由「外观」页：控件摆在这里，"恢复本页默认"就该管它。
反过来，让「外观」页去改一个它自己页上看不到的控件，用户点完只会觉得"恢复了却没变化"。

**不重置的部分**：「恢复本页默认」不动列的**顺序**（`columns.*.order`）与**列宽**
（`columns.*.dateWidth` 等），只清标准档位的三列显示开关——列宽是用户一个个拖出来的，
一次点击只做一件事。

「重置列宽」按钮走的是 `Settings.ResetLayout()`，**全部档位**（列宽与列顺序一起）。
两个入口刻意不同：它的理由是"我把列弄乱了，全部回原样"，只清默认档会留下几个还保持
原样的目录类型，反而更难收拾。

---

## 5. 页 4：操作与确认

| 控件 `x:Name` | 界面标签 | 字段 | 默认 |
|---|---|---|---|
| `OpenModeCombo` | `Settings.Behavior.OpenMode` | `behavior.doubleClickOpens` | 开（双击） |
| `OpenFolderInNewTabCheck` | `Settings.Behavior.OpenFolderInNewTab` | `behavior.openFolderInNewTab` | 关 |
| `BackgroundTabCheck` | `Settings.Behavior.OpenNewTabInBackground` | `behavior.openNewTabInBackground` | 关 |
| `CloseLastToHomeCheck` | `Settings.Behavior.CloseLastToHome` | `behavior.closeLastTabToHome` | 开 |
| `ConfirmDeleteCheck` | `Settings.Behavior.ConfirmDelete` | `behavior.confirmDelete` | 开 |
| `ConfirmBatchCheck` | `Settings.Behavior.ConfirmBatch` | `behavior.confirmBatch` | 开 |
| `ConfirmOverwriteCheck` | `Settings.Behavior.ConfirmOverwrite` | `behavior.confirmOverwrite` | 开 |
| `WarnOnExecutableCheck` | `Settings.Behavior.WarnOnExecutable` | `behavior.warnOnExecutable` | **关** |

这一页的八个控件与 `ActionPageFields` 的八项**一一对应**，不多不少——是四份清单里
唯一完全同构的一页。整页恢复 = `ResetBehaviorFields(ActionPageFields)`，无附加动作。

`warnOnExecutable` 默认关，与同页另三项确认的方向相反：那三项防"手滑删改"，代价是
多问一句；这一项防"手滑跑起程序"，而正常运行每次都要多一道弹窗，代价落在每一天。
它不是安全检查——只按扩展名判定，不认识内容好坏。

---

## 6. 页 5：隐私与存储

| 控件 `x:Name` | 界面标签 | 字段 / 数据源 | 说明 |
|---|---|---|---|
| `RememberPathCheck` | `Settings.Privacy.RememberPath` | `privacy.historyEnabled` | 布尔 |
| `HistoryLimitCombo` | `Settings.Privacy.HistoryLimit` | `privacy.historyLimit` | 50 / 100 / 200 / 500 |
| 「清空历史」按钮 | `Settings.Privacy.ClearHistory` | 历史文件（非 settings.json） | 动作，不可撤销 |
| `ThumbCacheText` | `Settings.Privacy.ThumbCache.*` | 只读，实测占用 | 空/加载中/占用字节数 |
| `CacheLimitCombo` | `Settings.Privacy.ThumbCache.Limit` | `privacy.cacheLimitMb` | 128 / 256 / 500 / 1024 / 2048 |
| 「清空缓存」按钮 | `Settings.Privacy.ThumbCache.Clear` | 缩略图缓存目录 | 动作 |
| `TagCountText` | `Settings.Privacy.TagStore.Loading` | 标签库条数（只读） | 与三个按钮同一行 |
| 「清理/导出/导入」 | `Settings.Privacy.TagPrune` 等 | `tags.json`（非 settings.json） | FR-4.2.7 / FR-5.3.6 |
| `DataDirText` + 「打开目录」 | `Settings.Privacy.DataDir` | 只读，`AppPaths.Root` | 三列 `Grid` |

**恢复本页默认**：`ResetPrivacy()` 三项（历史开关、历史条数、缓存上限）。

0.2.1 中段把**标签命名与增删**从这一页搬到了独立的「标签」页（见 §7）：它既是"标签叫什么"
（`appearance.*`），也是"要有哪几个标签"（标签库的前置条件），挂在隐私页下本身就说不通——
用户去隐私页是来找"清空/删除"的，不是来给标签改名的。

这里有一处"页边界跨对象"要记住：标签库与历史、缓存**不在** `settings.json` 里，是独立文件。
恢复本页默认不碰它们（那是用户数据，不是设置），要清必须走各自的按钮。

`CacheLimitCombo` 打开设置页时会**吸附到最近一档并落盘**：旧版本留下的默认值可能不在档位里，
不归一就会出现"下拉框显示 1024、占用行写着上限 512 MB、实际淘汰按第三个值"三套说法。

---

## 7. 页 6：标签

| 控件 `x:Name` | 界面标签 | 字段 | 取值 |
|---|---|---|---|
| `TagListPanel`（代码生成的行） | `Settings.Group.Tags` | `appearance.tags[].name` | 每个槽位一行：色块 + 名称输入框 + 「删除」 |
| `TagAddButton` | `Settings.Tags.Add` | 追加 `{ slot, name: null }` | 占用最小的空槽位，焦点落到新行输入框 |
| `TagLimitHint` | `Settings.Tags.Limit` | 只读提示 | 32 个槽位用尽时才显示，「新建标签」同时置灰 |

**名称一栏提交时与默认名比较**：一样就存 `null`（回落成跟随界面语言的默认色名），
不一样才存自定义名。否则没动过的"红色"在切成英文后仍显示"红"，等于把语言钉死。

**「删除」只从定义里移除，不动 `tags.jsonl`。** 确认框必须写明"标记记录仍在、重新添加同一种颜色
即可恢复"——用户看到"删除"两个字时默认以为标记也没了，不说清就是欺骗。
只剩一个标签时拒绝删除：删到零会让右键菜单里的「标签」变成一个空弹层。

**恢复本页默认**：`ResetTagsPage()` 回到默认八色（槽位 0–7、名字全 `null`），
**不动** `appearance.tagColorNames`（已废弃）与 `tags.jsonl`（用户数据）。

---

## 8. 页 7：快捷键（FR-10.12）

| 控件 `x:Name` | 界面标签 | 字段 | 取值 |
|---|---|---|---|
| `ShortcutFilterBox` | `Settings.Shortcuts.Filter.Hint` | 只影响显示 | 按动作名 / 键位文本 / 命令 Id 过滤，不写盘 |
| `ShortcutListPanel`（代码生成的行） | — | `shortcuts.overrides` / `shortcuts.showWindow` | 每组一段：动作名 + 键位块 + 「修改」+「恢复默认」 |

这一页**没有固定字段清单**：行是按 `KeyBindings.Catalog` 现生成的，改一条键位就是一次
"命令 Id → 键位文本"的写入。`AppSettings` 里没有对应的字段清单，所以单测
`SettingsPageFieldTests` 不覆盖它——它的"清单"是 `Catalog` 本身，另有
`ShortcutSettingsTests` 守解析与归一。

**行布局是定宽三列**（键位 168 / 「修改」72 / 「恢复默认」96）。用 `Auto` 会让每一行按自己的
内容宽度排：带「恢复默认」的行整块左移、不带的那行右移，四十多行看下来键位列像锯口一样
参差（第一版走查即为此）。动作名那一栏用 `Text.Row` 而不是 `Text.FieldLabel`：
后者是给"标签 + 右侧控件"的两栏表单用的定宽样式，定宽配默认 Stretch 会把文字在星列里居中，
看着像被缩进了一大截。

**录制态**：点「修改」后该行进入录制，键位块显示"请按下新的组合…"并高亮边框，同时
`SetShortcutCaptureMode(true)` 临时摘掉 `Ctrl+,` 与唤起键两条系统热键——不摘的话，用户按到
这两条时按键被系统直接投递给主窗口，设置窗口收不到，看起来像录制框坏了。退出录制（提交、
`Esc`、点别处、切页、关窗）都要把热键装回去。

**恢复本页默认**：`ResetShortcutsPage()` 清空 `shortcuts.overrides` 并把 `showWindow` 置 `null`
（即关闭唤起键）。与"恢复全部设置"的区别只在于作用范围；两项都会立即重建匹配表并重注册热键。

**为什么不做成"一条命令一行复选框列"**：这一页要看的是"现在按什么"，键位必须是主角；
把四十多条命令排成表格再加两列按钮，760 宽的设置窗放不下。

---

## 9. 页 8：关于

| 控件 `x:Name` | 界面标签 | 数据源 |
|---|---|---|
| `AboutPanel` | — | `views:AboutView` 用户控件，内容整块内嵌（版本号、运行环境、许可三键、目录三键、诊断与更新、隐私声明） |
| 「查看许可证全文 / NOTICE / 第三方依赖」 | `About.ViewLicense` 等 | 打开独立 `LicenseWindow`（11KB 法律文本不内嵌） |

这一页**没有可写字段**，因此页标题栏的「恢复本页默认」在此页隐藏（`ShowPage` 里控制）。

0.2.1 起不再有「关于 Jianlan」按钮与独立窗口：整块内容就是这一页本身（`AboutWindow` 已删除）。
版本号取自 `AboutView` 内部的 `AppInfo.DisplayVersion`（= `InformationalVersion`，发版时由脚本写入
`1.0.0+<短commit>`）。

---

## 10. 全局控件

| 控件 | 位置 | 作用 |
|---|---|---|
| `ResetPageButton` | 页标题栏右侧 | 恢复**当前页**默认，静态文案 + 结果落到 `StatusText` |
| 「恢复全部设置」 | 底栏 | `Settings.ResetAll()`，先弹确认框；收藏、历史、标签、工作区不受影响 |
| 「关闭」 | 底栏 | `OnClose` |
| `StatusText` | 底栏左侧 | 全局唯一的状态反馈位，14 处写入 |
| `NavList` | 左侧 | 八页 `ListBox`，`Ctrl+Tab` / `Ctrl+Shift+Tab` 翻页，记忆上次停留页（`static _lastPage`） |

**状态反馈只有一处**。重构前是 12 处写「隐私状态」、3 处借用字体提示行——同一件事出现在
三个位置，用户不知道看哪里。新增任何"操作完成/失败"的提示，一律写 `StatusText`，
不要新起一个 `TextBlock`。

「恢复本页默认」在页标题栏、「恢复全部设置」在底栏，**位置本身说明作用范围**。
页级动作不要挪进内容区，全局动作不要塞进某一页。

### 10.1 不进设置页的设置项

有些偏好**故意不在这八页里**——因为它的调整入口就在被调整的那个东西旁边，
放进设置页反而让人先去别处找一遍再回来：

| 设置 | 字段 | 入口 | 为什么不做进设置页 |
|---|---|---|---|
| 导航栏宽度 | `layout.navWidth` | 拖侧栏分隔条，双击回 184 | 调宽度时眼睛盯着那条线 |
| 导航栏开合 | `appearance.showNavPane` | 页 2「外观」（这一项**在**设置里，因为它是"整窗样子"） | — |
| 「位置」显示哪些项 | `layout.navHiddenPlaces` | 侧栏「位置」标题右端的齿轮 | 调的是"这一列显示什么"，注意力此刻就在这一列上 |
| 列宽 / 列顺序 | `layout.columns.*` | 拖列头、右键列头 | 同上 |
| 侧栏「历史」分区显示 | `appearance.showHistory` | `Ctrl+H` | 调的是「这一列显示什么」，与「位置」显示项同一类；做成勾选框还要在 184px 宽的侧栏语义里解释「分区」是什么 |

新增这一类设置时，**要在本表登记一行**，写清字段与入口。否则下次有人翻设置页找不到它，
会以为是漏做，然后再做一份进设置页——同一件事两个入口，改哪边生效就成了玄学。

---

## 11. 字段反向索引

按 `settings.json` 对象查"该去第几页改"。

| 对象 | 字段 | 页 | 按页恢复归属 |
|---|---|---|---|
| `general` | `language` | 1 常规 | **不重置** |
| `general` | `newTabMode` / `newTabPath` | 1 常规 | `ResetGeneralPage` |
| `appearance` | `theme` | 2 外观 | `ResetAppearancePage` |
| `appearance` | `accent` | 2 外观 | `ResetAppearancePage` |
| `appearance` | `fontFamily` | 2 外观 | `ResetAppearancePage` |
| `appearance` | `fontSizePt` | 2 外观 | `ResetAppearancePage` |
| `appearance` | `density` | 2 外观 | `ResetAppearancePage` |
| `appearance` | `showNavPane` / `showStatusBar` / `showPreview` / `immersiveTitleBar` | 2 外观 | `ResetAppearancePage` |
| `appearance` | `tabStripPosition` | **3 文件列表** | `ResetListPage` |
| `appearance` | `tags` | **6 标签** | `ResetTagsPage` |
| `appearance` | `tagColorNames`（旧版，只读兼容） | **6 标签** | 不重置（装载时并入 `tags` 后清空） |
| `behavior` | `restoreLastSession` / `startupWorkspace` | 1 常规 | `ResetGeneralPage` |
| `behavior` | `previewAutoOpen` | **2 外观** | `ResetAppearancePage` |
| `behavior` | `defaultView` / `sortBy` / `sortDescending` | 3 文件列表 | `ResetListPage` |
| `behavior` | `foldersFirst` / `naturalSort` / `caseSensitiveSort` | 3 文件列表 | `ResetListPage` |
| `behavior` | `showHidden` / `showSystem` / `showExtensions` | 3 文件列表 | `ResetListPage` |
| `behavior` | `doubleClickOpens` / `openFolderInNewTab` / `openNewTabInBackground` | 4 操作 | `ResetActionPage` |
| `behavior` | `closeLastTabToHome` | 4 操作 | `ResetActionPage` |
| `behavior` | `confirmDelete` / `confirmBatch` / `confirmOverwrite` / `warnOnExecutable` | 4 操作 | `ResetActionPage` |
| `privacy` | `historyEnabled` / `historyLimit` / `cacheLimitMb` | 5 隐私 | `ResetPrivacyPage` |
| `appearance` | `showHistory` | **无界面**（`Ctrl+H`） | 不重置——与侧栏「位置」显示项同一类，见 §10.1 |
| `shortcuts` | `overrides` / `showWindow` | 7 快捷键 | `ResetShortcutsPage` |
| `columns` | `*.showDate` / `showType` / `showSize` | 3 文件列表 | `ResetListPage`（仅标准档位 `layout.standard`） |
| `columns` | `*.order` / `*.dateWidth` 等 | 3 文件列表 | 不重置（由「重置列宽」按钮统一清全部档位） |
| `layout` | `primaryPane` / `secondaryPane` | 3 文件列表 | `ResetListPage`（清空，回落 `behavior`） |
| `session` / `history` / `bookmarks` / `layout.splitRatio` 等 | — | **无界面** | 运行时状态，不进设置页 |

`behavior.*` 共 20 个可写字段（另有 `extra` 保留未识别键，不参与任何清单）。
四份清单 9 + 8 + 2 + 1 = 20，无一重复、无一遗漏。
单测 `SettingsPageFieldTests` 用反射守着这条性质：新增 `BehaviorSettings` 属性却忘了归类，
测试直接挂。

---

## 12. 新增一个设置项时的判断顺序

按顺序问，第一个答"是"的就是答案：

1. **它改变的是"看到什么"还是"操作后发生什么"？**
   前者去 2 或 3，后者去 4。
2. **它只影响列表内容的呈现（显示/排序/列）吗？** 是 → 页 3 文件列表。
3. **它影响整个窗口的样子（主题/字体/密度/窗格显隐）吗？** 是 → 页 2 外观。
   - 注意：涉及"新建标签页"的归页 1，"标签栏本身"的归页 3。
4. **它是"打开/删除时问一句"吗？** 是 → 页 4 操作与确认。
5. **它涉及本地数据的存在与保留（历史/缓存/标签）吗？** 是 → 页 5 隐私与存储。
6. **它是启动瞬间发生的事吗？** 是 → 页 1 常规。
7. **它的调整入口就在被调整的那个东西旁边吗**（导航栏宽度拖分隔条、「位置」显示项走分区齿轮、
   列宽拖列头）？是 → **不进设置页**，在 §10.1 登记一行，写清字段与入口。
8. **以上都不是** → 先别写 XAML，回头想清楚它属于哪个用户场景。

定好页之后，还必须做完三件事，少一件都会变成下一个"恢复了却没生效"：

| 步骤 | 位置 | 漏做的后果 |
|---|---|---|
| 把字段名加进该页的字段清单 | `AppSettings.ListPageFields` 等四份 | 单测立刻挂（这是设计好的拦截） |
| 若字段不属于 `BehaviorSettings`，在对应 `ResetXxxPage()` 里单独处理 | `AppSettings.ResetXxxPage` | 按页恢复静默漏掉这一项 |
| 若控件需随字号缩放，宽度绑令牌而非写死像素 | `Size.Combo.Width` 等 | 24pt 下截断（AC-06） |

分页的**视觉**约定（一屏高度、导航宽度上限、字号缩放）见 `docs/ui-guidelines.md` 规则 15.7 与 15.8；
字段的**取值范围与越界处理**见 `docs/settings-schema.md`。本文只负责"哪个控件放哪页、动哪个字段"。

---

## 13. 已知缺口

| 项 | 说明 | 处置 |
|---|---|---|
| 按页拆 `UserControl` | 八页在两个 code-behind 里（`SettingsWindow.xaml.cs` 1544 行 + `SettingsWindow.Shortcuts.cs` 444 行），**已过 1600 行阈值** | 快捷键页是最先该拆出去的：它的行是代码生成的，与其余七页的静态 XAML 没有耦合。拆的时候按页拆 `UserControl`，不要再往主窗加 partial 文件 |
| 设置项搜索 | 八页 + 折叠低频项之后未做全局搜索框 | 快捷键页已有页内过滤；跨页搜索等页面数再涨一页时做 |
| 侧栏显隐 / 历史分区只有键位 | `Ctrl+B`、`Ctrl+H` 没有菜单或工具栏入口 | 计划在工具栏补一颗侧栏按钮；在此之前，用户在设置 →「快捷键」里能看到这两条，不算「只有快捷键才知道」 |
| 备选键位不可改 | `shortcuts` 只存主键位，备用键位（如 `Ctrl+R`、`Backspace`）固定只读 | v1 取舍：备用键位是「主键被别的程序占了」时的第二条路，允许改就得同时列出两条并保证不互相撞车，界面与校验都要翻倍。真要改主键位就够用了 |

已修的一项留档：`appearance.tabStripPosition` 的控件在「文件列表」页、重置却走「外观」页，
导致页 3 点恢复不动它、页 2 点恢复会动一个页 2 看不见的控件。2026-11-11 按方案一改为
由 `ResetListPage()` 重置，`ResetAppearancePage()` 把它原样带过；两条单测守着这个边界。
