# 设置页对照表（控件 ↔ 页 ↔ 字段）

设置页在 0.2.1 从"一列八个分区"改成**左侧六页导航 + 右侧单页滚动**，分页口径从
"设置项属于哪个对象"变成"用户想干什么"。这一步把 `GeneralSettings` / `AppearanceSettings` /
`BehaviorSettings` 的对象边界打散了，同一个对象里的字段现在分属不同页。

这份表是给**改代码的人**看的：新增一个设置项时，先在这里找到它该去的页；找不到，
说明归类没想清楚，不要先写 XAML。它也记录了哪些字段的"页位置"与"对象位置"不一致——
那正是按页恢复最容易出错的地方。

> 最后核对：2026-11-11，对应 `SettingsWindow.xaml` 的六页结构。字段路径为 `settings.json` 中的实际键名
> （camelCase），规范见 `docs/settings-schema.md`。

---

## 1. 六页总览

| # | 页 | 设置项数 | 承载的设置对象 | 按页恢复方法 |
|---|---|---|---|---|
| 1 | 常规 | 5 | `general` + `behavior` 的启动项 | `ResetGeneralPage()` |
| 2 | 外观 | 10 | `appearance` + `behavior.previewAutoOpen` | `ResetAppearancePage()` |
| 3 | 文件列表 | 11 | `behavior` 的视图排序 + `appearance.tabStripPosition` + `columns` | `ResetListPage()` |
| 4 | 操作与确认 | 8 | `behavior` 的打开与确认项 | `ResetActionPage()` |
| 5 | 隐私与存储 | 4 | `privacy` + `appearance.tagColorNames` + 标签库 | `ResetPrivacyPage()` |
| 6 | 关于 | 0 | 无（只读 + 入口） | 无（隐藏恢复按钮） |

"设置项"按**一个可写字段**计：复合控件（字号 `−`/`+` 与"跟随系统"勾选、上限下拉与"清空"按钮）
算一项；只读块（缓存占用、标签条数、数据目录）与纯动作按钮（导出/导入/打开目录）不计数。

六页之外还有三处全局控件：页标题栏的「恢复本页默认」、底栏的「恢复全部设置」与「关闭」、
底栏状态位。见第 7 节。

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
| `FontHint` | — | 只读提示 | 字体族回落到默认栈时才写（FONT-03） |

**跨对象项**：`PreviewAutoCheck` 控的是预览行为，落在外观页——它与上面三条同属
"窗格长什么样"，与「操作与确认」页那组"打开/确认"不是一类。这是**唯一**一个
`behavior.*` 字段落在外观页，`AppearancePageFields` 里就是为它单独列的一项。

**唯一需要重启的一项**：`ImmersiveTitleBarCheck` 决定窗口是否用自绘标题栏（FR-10.11）。
`WindowStyle` 一旦创建窗口就改不了，运行期切换要重建 HWND，所以这里只写设置、不调
`ApplySettingsToUi()`——强行让主窗口局部响应，只会得到"系统标题栏撤了、无边框还在"的半成品。
开关下方的提示文字明确写了"更改后需重启应用"。

**恢复本页默认**：整段换掉 `AppearanceSettings`，但**把两样东西带过去**——
标签颜色名（显示在第 5 页，由 `ResetPrivacyPage()` 负责）与标签栏位置（控件在第 3 页，
由 `ResetListPage()` 负责）。旧版把颜色名显示在"隐私"段下、却由"恢复默认外观"清掉，
操作与显示的位置对不上；标签栏位置是同一个问题在 0.2.1 六页结构里的翻版，一并纠正。

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
| `TagColorNameSummary` / `TagColorNameToggle` / `TagColorNamePanel` | `Settings.Tags.ColorNames` | **`appearance.tagColorNames`** | 默认折叠 |
| `DataDirText` + 「打开目录」 | `Settings.Privacy.DataDir` | 只读，`AppPaths.Root` | 三列 `Grid` |

**恢复本页默认**：`ResetPrivacy()` 三项 + **清掉 `appearance.tagColorNames`**。

这里有两处"页边界跨对象"要记住：

1. `appearance.tagColorNames` 虽然叫 `appearance.*`，但显示位置与重置归属都在这一页。
   旧版它挂在"隐私"段下、却由"恢复默认外观"清掉——两边对不上，本次一并纠正。
2. 标签库与历史、缓存**不在** `settings.json` 里，是独立文件。恢复本页默认不碰它们
   （那是用户数据，不是设置），要清必须走各自的按钮。

`CacheLimitCombo` 打开设置页时会**吸附到最近一档并落盘**：旧版本留下的默认值可能不在档位里，
不归一就会出现"下拉框显示 1024、占用行写着上限 512 MB、实际淘汰按第三个值"三套说法。

---

## 7. 页 6：关于

| 控件 `x:Name` | 界面标签 | 数据源 |
|---|---|---|
| `AboutText` | — | `AppInfo.DisplayVersion`（= `InformationalVersion`，发版时由脚本写入 `0.2.1+<短commit>`） |
| 「关于 Lingpane」按钮 | `About.Open` | 打开关于窗口（版本、运行环境、许可证全文、依赖清单、目录入口） |
| 隐私声明段落 | `Settings.About.Privacy` | 静态文案（FR-5.3.7） |

这一页**没有可写字段**，因此页标题栏的「恢复本页默认」在此页隐藏（`ShowPage` 里控制）。
版本号全文不在这里展开：许可证 11KB，放进来会把别的内容推下首屏。

---

## 8. 全局控件

| 控件 | 位置 | 作用 |
|---|---|---|
| `ResetPageButton` | 页标题栏右侧 | 恢复**当前页**默认，静态文案 + 结果落到 `StatusText` |
| 「恢复全部设置」 | 底栏 | `Settings.ResetAll()`，先弹确认框；收藏、历史、标签、工作区不受影响 |
| 「关闭」 | 底栏 | `OnClose` |
| `StatusText` | 底栏左侧 | 全局唯一的状态反馈位，14 处写入 |
| `NavList` | 左侧 | 六页 `ListBox`，`Ctrl+Tab` / `Ctrl+Shift+Tab` 翻页，记忆上次停留页（`static _lastPage`） |

**状态反馈只有一处**。重构前是 12 处写「隐私状态」、3 处借用字体提示行——同一件事出现在
三个位置，用户不知道看哪里。新增任何"操作完成/失败"的提示，一律写 `StatusText`，
不要新起一个 `TextBlock`。

「恢复本页默认」在页标题栏、「恢复全部设置」在底栏，**位置本身说明作用范围**。
页级动作不要挪进内容区，全局动作不要塞进某一页。

### 8.1 不进设置页的设置项

有些偏好**故意不在这六页里**——因为它的调整入口就在被调整的那个东西旁边，
放进设置页反而让人先去别处找一遍再回来：

| 设置 | 字段 | 入口 | 为什么不做进设置页 |
|---|---|---|---|
| 导航栏宽度 | `layout.navWidth` | 拖侧栏分隔条，双击回 184 | 调宽度时眼睛盯着那条线 |
| 导航栏开合 | `appearance.showNavPane` | 页 2「外观」（这一项**在**设置里，因为它是"整窗样子"） | — |
| 「位置」显示哪些项 | `layout.navHiddenPlaces` | 侧栏「位置」标题右端的齿轮 | 调的是"这一列显示什么"，注意力此刻就在这一列上 |
| 列宽 / 列顺序 | `layout.columns.*` | 拖列头、右键列头 | 同上 |

新增这一类设置时，**要在本表登记一行**，写清字段与入口。否则下次有人翻设置页找不到它，
会以为是漏做，然后再做一份进设置页——同一件事两个入口，改哪边生效就成了玄学。

---

## 9. 字段反向索引

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
| `appearance` | `tagColorNames` | **5 隐私与存储** | `ResetPrivacyPage` |
| `behavior` | `restoreLastSession` / `startupWorkspace` | 1 常规 | `ResetGeneralPage` |
| `behavior` | `previewAutoOpen` | **2 外观** | `ResetAppearancePage` |
| `behavior` | `defaultView` / `sortBy` / `sortDescending` | 3 文件列表 | `ResetListPage` |
| `behavior` | `foldersFirst` / `naturalSort` / `caseSensitiveSort` | 3 文件列表 | `ResetListPage` |
| `behavior` | `showHidden` / `showSystem` / `showExtensions` | 3 文件列表 | `ResetListPage` |
| `behavior` | `doubleClickOpens` / `openFolderInNewTab` / `openNewTabInBackground` | 4 操作 | `ResetActionPage` |
| `behavior` | `closeLastTabToHome` | 4 操作 | `ResetActionPage` |
| `behavior` | `confirmDelete` / `confirmBatch` / `confirmOverwrite` / `warnOnExecutable` | 4 操作 | `ResetActionPage` |
| `privacy` | `historyEnabled` / `historyLimit` / `cacheLimitMb` | 5 隐私 | `ResetPrivacyPage` |
| `columns` | `*.showDate` / `showType` / `showSize` | 3 文件列表 | `ResetListPage`（仅标准档位 `layout.standard`） |
| `columns` | `*.order` / `*.dateWidth` 等 | 3 文件列表 | 不重置（由「重置列宽」按钮统一清全部档位） |
| `layout` | `primaryPane` / `secondaryPane` | 3 文件列表 | `ResetListPage`（清空，回落 `behavior`） |
| `session` / `history` / `bookmarks` / `layout.splitRatio` 等 | — | **无界面** | 运行时状态，不进设置页 |

`behavior.*` 共 20 个可写字段（另有 `extra` 保留未识别键，不参与任何清单）。
四份清单 9 + 8 + 2 + 1 = 20，无一重复、无一遗漏。
单测 `SettingsPageFieldTests` 用反射守着这条性质：新增 `BehaviorSettings` 属性却忘了归类，
测试直接挂。

---

## 10. 新增一个设置项时的判断顺序

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
   列宽拖列头）？是 → **不进设置页**，在 §8.1 登记一行，写清字段与入口。
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

## 11. 已知缺口

| 项 | 说明 | 处置 |
|---|---|---|
| 按页拆 `UserControl` | 六页仍在一个 `SettingsWindow.xaml` 里，code-behind 约 1400 行 | 未到 1600 行阈值，暂不拆 |
| 设置项搜索 | 六页 + 折叠低频项之后未做搜索框 | 页面数超 8 页或单页需滚动两屏以上时再评估 |

已修的一项留档：`appearance.tabStripPosition` 的控件在「文件列表」页、重置却走「外观」页，
导致页 3 点恢复不动它、页 2 点恢复会动一个页 2 看不见的控件。2026-11-11 按方案一改为
由 `ResetListPage()` 重置，`ResetAppearancePage()` 把它原样带过；两条单测守着这个边界。
