# 设置文件规范（settings.json）

> 对应 FR-5 / FR-10.3。启动路径上必须能在 **≤5 ms** 内读完，因此不允许引入配置框架，用 `System.Text.Json` 直读。

## 1. 位置

| 模式 | 路径 |
|---|---|
| 安装版 | `%LOCALAPPDATA%\Jianlan\settings.json` |
| 便携版（exe 同级有 `portable.marker`） | `<exe目录>\data\settings.json` |

路径解析集中在 `src/Jianlan.Core/Diagnostics/AppPaths.cs`，界面与 ViewModel 不得自行拼接。

## 2. 结构（schemaVersion = 1）

```jsonc
{
  "schemaVersion": 1,

  "general": {
    "language": "auto",         // auto（跟随系统安装语言）| zh-Hans | en
    "newTabMode": "path",       // home | path | last（FR-5.2.6）。默认 path，与 0.2.0 之前完全兼容
    "newTabPath": null          // newTabMode = path 时用它。null = 系统「下载」（每次启动重新问系统，不写死路径）
  },

  "appearance": {
    "theme": "system",          // system | light | dark | highContrast（第四项是手动开高对比度，FR-5.1.2）
    "fontSizePt": null,         // null = 跟随系统；8–24 整数
    "fontFamily": null,         // null = 默认字体栈；否则为系统字体族名
    "accent": null,             // null = 跟随系统；否则 #RRGGBB（FR-5.1.3）。派生色由 AccentService 现算
    "density": "comfortable",   // comfortable(32px) | compact(26px)
    "showNavPane": true,
    "showStatusBar": true,
    "showPreview": false,       // 预览窗格的默认状态（FR-5.2.10）。默认关闭：内存占用最大的可选功能
    "showHistory": false,       // 侧栏「历史」分区是否显示（FR-1.2.10）。与 privacy.historyEnabled 分工：
                                // 那个管"记不记"，这个管"显示不显示"。默认关：侧栏只有 184px，先摆常用项
    "tabStripPosition": "top",  // top | bottom（FR-2.4），无法识别时回落 top
    "immersiveTitleBar": true,  // 隐藏系统标题栏，标签栏充当标题栏（FR-10.11）。**改后需重启**
    "tagColorNames": {          // 旧口径（≤0.2.1），只读兼容：装载时并入 tags 后清空，见下
      "Red": "紧急"
    },
    "tags": [                   // 标签定义（FR-4.1.7）。槽位 0–31，顺序即显示顺序
      { "slot": 0, "name": "紧急" },   // name 为 null = 用该槽位的默认色名（跟随界面语言）
      { "slot": 1, "name": null }
    ]                           // 默认八项（0–7）。删除标签 = 从这份表里移除，不动 tags.jsonl
  },

  "behavior": {
    "defaultView": "details",   // details | list | grid
    "sortBy": "name",           // name | date | size | type
    "sortDescending": false,
    "foldersFirst": true,
    "showHidden": false,
    "showSystem": false,        // 系统文件单独一位（FR-5.2.4）；带 H+S 的条目要两个开关都开
    "showExtensions": true,
    "confirmDelete": true,
    "confirmBatch": true,       // 一次处理两项及以上时确认（FR-5.2.8 的「批量」）
    "confirmOverwrite": true,   // 粘贴时目标处已有同名项是否先问一句（FR-5.2.8 的「覆盖」）。
                                // 三项统一语义：开 = 先问，关 = 直接执行；关掉时覆盖就是覆盖，不退回自动改名
    "naturalSort": true,        // 自然排序（FR-5.2.3）。false = 逐字符比，file10 会排在 file2 前
    "warnOnExecutable": false,  // 双击可执行文件时先确认（FR-5.2.11）。与上面三项相反：默认关
    "caseSensitiveSort": false, // 区分大小写排序。开启后 Zebra.txt 排在 apple.txt 前（大写字母码位更小）
    "doubleClickOpens": true,
    "openFolderInNewTab": false,      // 双击/回车进目录时开新标签（FR-2.10）；分屏时不生效，进目录恒在当前窗格（FR-3.2）
    "openNewTabInBackground": false,  // 中键/Ctrl+左键开出的标签是否留在后台（FR-2.10 可选项）
    "restoreLastSession": true,
    "startupWorkspace": null,   // 启动时自动打开的工作区名（FR-8.4）。非 null 时 restoreLastSession 强制为 false
    "closeLastTabToHome": true, // 关掉最后一个标签时回默认目录，而不是关窗口（FR-2.8）
    "previewAutoOpen": true     // 选中文件后自动预览（FR-5.2.10 的「触发方式」）。关掉后要按预览里的「立即预览」
  },

  "privacy": {
    "historyEnabled": true,
    "historyLimit": 100,
    "cacheLimitMb": 500       // 缩略图缓存上限（FR-5.3.5）。设置页只给五档，手改的其他值打开设置页时吸附到最近一档
  },

  "session": {
    "windowWidth": 1100,
    "windowHeight": 720,
    "windowMaximized": false,
    "windowLeft": null,         // 左上角坐标（FR-10.2）。null = 没记过，启动时居中（0.2.0 之前的文件都是 null）
    "windowTop": null,          // 副屏在主屏左侧时是负数，原样保存
    "lastPath": null,
    "tabs": [],                 // 顺序即标签顺序；最多 12 条（SessionSettings.MaxTabs）
    "activeTabIndex": 0,        // 恢复后激活第几个标签
    "tabSecondPaths": [],       // 与 tabs 平行等长；空串 = 该标签没开分屏（ADR-0009）
    "tabPinned": []             // 与 tabs 平行等长；true = 固定标签（FR-2.9）
  },

  "layout": {
    "columns": {                // 列配置按目录类型分档（FR-6.4）
      "default": {
        "order": ["date", "type", "size"],   // 除名称外的三列顺序
        "showDate": true,
        "showType": true,
        "showSize": true,
        "dateWidth": 110,       // 56–400
        "typeWidth": 110,
        "sizeWidth": 90
      },
      "pictures": { ... }       // 未出现过的档位一律回落 default
    },
    "secondaryColumns": {},     // 第二窗格自己的列配置（FR-3.7）。空对象 = 从未单独调过，沿用 columns
    "primaryPane": {            // 第一窗格的视图与排序记忆（FR-3.7）。三项都可空
      "view": null,             // null = 这一侧还没自己选过，沿用 behavior.defaultView
      "sortBy": null,           // null = 沿用 behavior.sortBy
      "sortDescending": null
    },
    "secondaryPane": {          // 同上，第二窗格
      "view": null,
      "sortBy": null,
      "sortDescending": null
    },
    "splitEnabled": false,      // 双窗格开关，窗口级（FR-3.3）
    "splitRatio": 0.5,          // 第一窗格占比，0.15–0.85
    "splitVertical": false,     // false = 左右，true = 上下（FR-3.1）
    "previewWidth": 320,        // 预览窗格宽度，240–720（FR-9.1.1）。与 showPreview 分开：关掉再打开要回到原宽度
    "navWidth": 184,            // 导航栏宽度，160–320（FR-1.2.4）。拖完分隔条才落盘，双击分隔条回 184
    "thisPcExpanded": false,    // 侧栏「此电脑」是否展开（FR-1.2.7）。默认折叠：侧栏只有 184px 宽时先摆目录与收藏
    "navHiddenPlaces": []       // 侧栏「位置」被隐藏的项（FR-1.2.9）。空 = 九项全显示；键见下表
  },

  "shortcuts": {                // 自定义键位（FR-10.12）。**只存差异**，见 §4
    "overrides": {              // 命令 Id → 键位文本。键不存在 = 用内置默认；空串 = 用户明确解绑
      "file.rename": "F4"
    },
    "showWindow": null          // 全局唤起键；null = 关闭（默认）。写法同 overrides 的值
  }
}
```

`navHiddenPlaces` 的键（取值即枚举名，与 `knownFolderPaths` 的键同一套写法）：

| 键 | 对应 | 键 | 对应 |
|---|---|---|---|
| `Desktop` | 桌面 | `ThisPc` | 此电脑 |
| `Documents` | 文档 | `RecycleBin` | 回收站 |
| `Downloads` | 下载 | `Network` | 网络 |
| `Pictures` | 图片 | | |
| `Music` | 音乐 | | |
| `Videos` | 视频 | | |

## 3. 字段约束

| 字段 | 约束 | 越界处理 |
|---|---|---|
| `language` | `auto` / `zh-Hans` / `en` 三值 | 无法识别的值按 `auto` 处理，不报错（手改坏了不至于起不来） |
| `newTabPath` | 绝对路径或 `null` | 空白归一为 `null`；**不校验存在性**（盘可能暂时不在），解析时目录不可用则回落到系统「下载」 |
| `theme` | 四值枚举，且系统高对比度开启时强制 `HighContrast` | 回落 `system`。`highContrast` 是用户手动开启的路径（FR-5.1.2）；系统那边开着时以系统为准——那个开关的语义是"我看不清"，比应用内偏好更强 |
| `windowLeft/windowTop` | `null` 或任意整数坐标（负数是副屏在主屏左侧/上方） | 不夹取：夹到非负会把副屏用户的布局每次启动拽回主屏。启动时若与虚拟屏的相交面积不足（宽 <120 或高 <40）就回主屏正中，见 `WindowPlacement` |
| `thisPcExpanded` | 布尔 | 默认 `false`（折叠）。改动当场落盘，与收藏分组折叠同一口径 |
| `navHiddenPlaces` | 上面那张键表的子集，数组 | 认不出的键**丢弃**、重复项合并、顺序归一到侧栏显示顺序（`NavPlaces.Normalize`）。空数组 = 九项全显示，也是默认值——老用户的文件里没有这个字段，读出来就是升级前的样子。存"隐藏了哪些"而不是"显示了哪些"：将来新增一项时老用户自动看得见 |
| `fontSizePt` | `null` 或 8–24 整数 | 超范围夹取到边界 |
| `fontFamily` | 必须存在于系统字体列表 | 不存在则回落默认栈，不报错 |
| `accent` | `null` 或 `#RRGGBB` | 三位写法、颜色名、非十六进制一律归为 `null`（跟随系统）：让界面去猜颜色名只会得到一块读不出来的黑洞。派生色（悬停/按下/浅底）不落盘，由 `AccentService` 按当前主题现算 |
| `showSystem` | 布尔 | 与 `showHidden` 各管一位；带 H+S 的条目（`desktop.ini`）要两个开关都开才显示。只做内存过滤，不重新枚举 |
| `confirmBatch` | 布尔 | 与 `confirmDelete` 分开：允许"单删不弹、批量弹"。一次处理 ≥2 项时用这一项 |
| `doubleClickOpens` | 布尔 | `false` = 单击打开（FR-5.2.5）。切换后列表悬停高亮的延迟随之变化（单击模式 600ms） |
| `density` | 两值枚举 | 回落 `comfortable` |
| `historyLimit` | 0–500 | 夹取 |
| `cacheLimitMb` | 64–4096，默认 500（与 `ThumbnailCache.DefaultCapBytes` 同值） | 夹取；打开设置页时吸附到 128/256/500/1024/2048 最近一档并落盘，否则下拉框、占用行与实际淘汰各说一套 |
| `windowWidth/Height` | ≥ 最小窗口尺寸 | 夹取 |
| `session.tabs` | 每条为非空路径；最多 12 条 | 空串剔除；超出截断到前 12 条 |
| `session.activeTabIndex` | 0 ≤ 索引 < `tabs.length` | 夹取 |
| `session.tabSecondPaths` | 与 `tabs` 等长；每条为空串或非空路径 | 短了补空串、长了截断（补齐而不是补 `null`：`null` 会把"没开分屏"和"没写这个字段"混成一种） |
| `session.tabPinned` | 与 `tabs` 等长 | 短了补 `false`（默认不固定）、长了截断。固定是用户主动设的意图，丢了会被当成功能失效，所以必须落盘而不是只活在内存里 |
| `appearance.tabStripPosition` | `top` / `bottom` | 无法识别回落 `top`：落到一个不存在的位置上，标签栏会直接看不见 |
| `behavior.startupWorkspace` | `null` 或非空工作区名 | 空白归一为 `null`；**不校验该工作区是否还在**（工作区文件按需加载，启动恢复时才去找，找不到就退回会话恢复）。非 `null` 时 `Normalize()` 把 `restoreLastSession` 置 `false`：两项都表达"启动时放什么"，显式选择的那一项胜出，而不是让两套逻辑抢 |
| `columns` | 键是档位名（`default` / `desktop` / `documents` / `downloads` / `pictures` / `music` / `videos`），值见下 | 键大小写归一为小写并去重（手写 `Pictures` 也能认）；缺失的档位回落 `default`；`default` 不存在时自动建一份 |
| `columns.*.order` | 三个元素，取值 `date`/`type`/`size` | 未知项、重复项、`name` 一律剔除（名称列恒在首位）；缺失项按默认顺序补在末尾。少一项会让某列消失，重复会让两个列元素叠在同一格 |
| `columns.*.showDate/Type/Size` | 布尔 | 三项可同时为 `false`（只剩文件名，等同列表视图）；名称列不可隐藏，属输入校验 |
| `columns.*.date/Type/SizeWidth` | 56–400（单位：DIP，字号基线下的逻辑宽度） | 夹取；`0`、`NaN`、`Infinity` 回落默认值。实际显示宽度 = 本值 × 字号缩放，因此换字号不会把用户拖出来的宽度改掉 |
| `splitRatio` | 0.15–0.85 | 夹取；`NaN`/`Infinity` 回落 0.5。夹到 0.15 以下等于把一个窗格压成 0 宽，没有意义 |
| `previewWidth` | 240–720（DIP） | 夹取；`0`、负数、`NaN`、`Infinity` 回落 320。下限是"还能看清一张缩略图"，上限是别把列表挤没 |
| `navWidth` | 160–320（DIP） | 夹取；`0`、负数、`NaN`、`Infinity` 回落 184。下限是"图标 + 四个汉字"还放得下，上限是侧栏不该比内容区宽。`showNavPane=false` 时不生效（列宽归零，但仍保留这个值，打开侧栏时恢复原宽度） |
| `newTabMode` | `home` / `path` / `last` | 无法识别回落 `path`（与 0.2.0 之前完全一致）。只在"没给路径"时生效——拖动打开的文件夹、右键"在新标签打开某路径"都已明确指定位置，不受这个设置影响 |
| `tags` | 数组，每项 `{ slot: 0–31, name: string 或 null }`，最多 32 项 | 槽位去重（先出现的胜出）、越界槽位丢弃、按用户顺序保留（顺序即显示顺序）；`name` 空白视为 `null`、超 24 字符截断；`null` 表示"没改过"，显示该槽位的默认色名——存全表的话切成英文之后名字还是旧语言的。表为空时回落默认八色（空了就没有标签可点，等于把功能关死）。**旧字段 `tagColorNames` 在归一阶段并入本表后清空**，两份真相只留一份 |
| `tagColorNames` | 键为八色枚举名之一（`Red`…`Black`），值为 1–24 字符 | 仅作旧版兼容读入：键大小写不敏感、未知键丢弃、值超长截断、空白值视为"没改过"。装载时按枚举名→槽位折进 `tags`，随后清空本字段 |

| `confirmOverwrite` | 布尔 | 与 `confirmDelete` / `confirmBatch` 同一语义：**开 = 先问一句，关 = 直接执行**。关掉时覆盖就是覆盖，不会退回自动改名——否则"我就想覆盖"的用户没有任何入口 |
| `naturalSort` | 布尔，默认 `true` | 默认开与 0.2.0 之前的行为一致（`file2` 在 `file10` 前）。关掉是逐字符比（`file10` 在 `file2` 前） |
| `warnOnExecutable` | 布尔，默认 `false` | 双击可执行文件前问一句（FR-5.2.11）。**与上面三项确认开关方向相反：默认关**。三项确认防的是"手滑删掉/覆盖掉"，代价是把要做的事多问一遍；这一项防的是"手滑跑起一个程序"，而正常运行每次都要多一道弹窗，代价落在每一天。它也不是安全检查——判定只按扩展名，不认识内容好坏，用户点了"是"就照原样交给系统。扩展名清单显式列举（`.exe` `.com` `.scr` `.pif` `.bat` `.cmd` `.ps1` `.vbs` `.vbe` `.js` `.jse` `.wsf` `.wsh` `.hta` `.msi` `.msp` `.msix` `.appx` `.reg` `.lnk` `.url`），宁可多拦一个，也不看图时顺手把程序跑起来 |
| `caseSensitiveSort` | 布尔，默认 `false` | 默认关 = 与资源管理器一致。开启后按码位比，`Zebra.txt` 会排到 `apple.txt` 前 |
| `layout.secondaryColumns` | 与 `columns` 同结构 | 空对象 = 第二窗格从未单独调过，读的时候沿用 `columns`。**没调过就不写盘**，否则从没碰过分屏的用户文件里会凭空多出一份重复配置 |
| `layout.primaryPane` / `secondaryPane` | `view` / `sortBy` / `sortDescending` 三项，均可为 `null` | `null` = 这一侧还没自己选过，沿用 `behavior.defaultView` / `behavior.sortBy`。写死初值会让设置页那两项默认值形同虚设 |
| `previewAutoOpen` | 布尔 | `false` = 选中不自动预览，要按窗格里的「立即预览」。网络路径与可移动设备不受这一项影响，它们无论怎么设都不自动预览（FR-9.2.8） |
| `shortcuts.overrides` | 键为命令 Id（`KeyBindings.Catalog` 里存在的那几个，≤120 字符），值为**规范化后的键位文本**或空串 | 认不出的键名、无法解析的写法一律**丢弃并回落默认**（留着一个坏值会让那条命令彻底没有快捷键，比回落更糟）；空串保留为"解绑"，不当作"没写"。写回前统一成 `Ctrl+Alt+Shift+Key` 的固定顺序与规范键名（`ctrl+comma` → `Ctrl+,`），这样同一套键位在文件里只有一种写法 |
| `shortcuts.showWindow` | `null`（默认，关闭）或合法键位文本 | 空白、非法写法、以及 `ShortcutPolicy` 拒收的组合（`Win+*`、无修饰非功能键、系统保留）统一归一为 `null`。**不校验是否与其他程序冲突**：那是注册时才知道的结果（`RegisterHotKey` 返回失败），落盘时判不了；失败在设置页明说 |
| `showHistory` | 布尔，默认 `false` | 侧栏「历史」分区的显示开关（FR-1.2.10）。与 `privacy.historyEnabled` 各管一件事：前者管"显示不显示"，后者管"记不记"。两项都开才看得到历史行 |

> **路径合法性不在归一化里判**：盘符可能暂时不在（U 盘没插），判了就把标签吃掉。
> 恢复时按 `Directory.Exists` 过滤（`MainWindow.RestoreTabs`），宁少一个标签，不留死标签。
> 三份平行列表必须**先按下标配对、再逐条筛**：反过来（先过滤路径再拿新下标回别的列表取）
> 会在中间某个目录被删掉时整体错位，固定标记与第二窗格会落到相邻标签上。
>
> 名称列是星号列（宽度随窗口伸缩），因此**没有**对应字段，也不参与拖拽。
> 列宽只在拖拽结束时落盘一次，不跟随每个拖动事件写文件。
>
> **列配置为什么按目录类型分档而不是按路径逐个记**：逐路径记会在用户拖几次列宽之后
> 让这个文件里长出一张与访问历史等长的表，而收益只在"回到同一目录"时体现。
> 档位用系统给的已知文件夹（`KnownFolderService`），数量固定、不需要内容探测；
> 判定走最长前缀匹配，所以 `Documents\Downloads` 归下载档而不是文档档。
> 分档是**每个窗格各算各的**：双窗格可以左边图片、右边代码，两边的列宽本来就该不同。
>
> `navWidth` 与 `previewWidth` 同一套规则：拖完分隔条才落盘，拖动过程中不写文件。
> 它与 `appearance.showNavPane` 不是一回事——一个是"多宽"，一个是"开不开"，
> 关掉侧栏不应该把用户拖出来的宽度丢掉。
>
> `splitEnabled` / `splitRatio` / `splitVertical` 三者都是**用户动作当场落盘**
> （`F3`、拖完分隔条、双击复位、右键改方向），不走"退出时尺寸变了才存"的兜底路径：
> 那条路径的判据里没有这三项，只拖一下分隔条就关窗口会把比例丢掉。
>
> **工作区不在这个文件里**：`behavior.startupWorkspace` 只存一个名字，工作区本体在独立的
> `workspaces.json`（同目录，`AppPaths.WorkspacesFile`）。放进 `settings.json` 等于每次启动
> 都要解析一遍所有工作区的标签列表，与"不参与冷启动默认路径"的要求相违。
>
> **`shortcuts` 只存差异，不存全表。** 没改过的命令不出现在 `overrides` 里，用的是程序内置
> 默认值（`KeyBindings.Catalog`）。存全表会把"这一版的默认值"永久冻进用户文件，以后想调整
> 默认键位再也到不了老用户——与标签只存改过的名字、第二窗格没调过不写 `secondaryColumns`
> 是同一个口径。
>
> 空串与"键不存在"是两件事，必须分开：前者是明确意图（这条命令不要快捷键），后者是没改过。
> 合并之后，"解绑"会变成"下次启动又回来"，用户只能反复解绑。

## 4. 读写规则

**读取**

1. 文件不存在 → 使用内置默认值，**不写盘**（首次改动设置时才创建文件）
2. 解析失败 → 使用默认值，把原文件重命名为 `settings.json.bak`，记一条警告日志
3. 未知字段 → 忽略但**保留**（写回时原样带回，避免降级版本吃掉新字段）
4. 读取必须在首帧之后或后台线程；首帧前只读 `session` 与 `appearance` 两组
5. `general.language` 在读完设置后、创建视图模型前立即应用（启动埋点 `i18n`，约 1ms）：
   视图模型构造期就会取文案，晚于它应用会让列头/侧栏停在上一次的语言

**写入**

1. 原子写：写 `settings.json.tmp` → `File.Replace` → 备份为 `.bak`
2. 写失败不得抛到 UI 线程，只在状态栏提示"设置未能保存"
3. 每次写入不得触发全量重载

## 5. 版本迁移

| schemaVersion | 变更 |
|---|---|
| 1 | 初版 |

规则：

- 只做**向前迁移**（旧 → 新），不写反向迁移
- 迁移函数必须独立可测，位于 `Jianlan.Core/Settings/SettingsMigrations.cs`
- 无法识别的高版本 `schemaVersion` → 只读模式，禁止覆盖，提示用户升级程序

## 6. 与 Token 的关系

设置变更通过服务写入资源字典，**界面不得直接改控件属性**：

| 设置项 | 写入目标 | 入口 |
|---|---|---|
| `fontSizePt` / `density` | `Font.Size.*`、`Size.Row.*`、`Column.*` | `FontScaleService.Apply` |
| `fontFamily` | `Font.Family.UI` | `FontScaleService.ApplyFontFamily` |
| `theme` | 替换主题字典 `Source` | `ThemeService.Apply`（M1） |

约束见《界面实现规则》§15.3 A-01。
