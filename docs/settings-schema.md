# 设置文件规范（settings.json）

> 对应 FR-5 / FR-10.3。启动路径上必须能在 **≤5 ms** 内读完，因此不允许引入配置框架，用 `System.Text.Json` 直读。

## 1. 位置

| 模式 | 路径 |
|---|---|
| 安装版 | `%LOCALAPPDATA%\Lingpane\settings.json` |
| 便携版（exe 同级有 `portable.marker`） | `<exe目录>\data\settings.json` |

路径解析集中在 `src/Lingpane.Core/Diagnostics/AppPaths.cs`，界面与 ViewModel 不得自行拼接。

## 2. 结构（schemaVersion = 1）

```jsonc
{
  "schemaVersion": 1,

  "general": {
    "language": "auto",         // auto（跟随系统安装语言）| zh-Hans | en
    "newTabPath": null          // 新标签页默认目录。null = 系统「下载」（每次启动重新问系统，不写死路径）
  },

  "appearance": {
    "theme": "system",          // system | light | dark
    "fontSizePt": null,         // null = 跟随系统；8–24 整数
    "fontFamily": null,         // null = 默认字体栈；否则为系统字体族名
    "accent": null,             // null = 跟随系统；否则 #RRGGBB（FR-5.1.3）。派生色由 AccentService 现算
    "density": "comfortable",   // comfortable(32px) | compact(26px)
    "showNavPane": true,
    "showStatusBar": true,
    "tabStripPosition": "top"   // top | bottom（FR-2.4），无法识别时回落 top
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
    "doubleClickOpens": true,
    "openFolderInNewTab": false,      // 双击/回车进目录时开新标签（FR-2.10）；分屏时不生效，进目录恒在当前窗格（FR-3.2）
    "openNewTabInBackground": false,  // 中键/Ctrl+左键开出的标签是否留在后台（FR-2.10 可选项）
    "restoreLastSession": true,
    "startupWorkspace": null,   // 启动时自动打开的工作区名（FR-8.4）。非 null 时 restoreLastSession 强制为 false
    "closeLastTabToHome": true  // 关掉最后一个标签时回默认目录，而不是关窗口（FR-2.8）
  },

  "privacy": {
    "historyEnabled": true,
    "historyLimit": 100,
    "cacheLimitMb": 512
  },

  "session": {
    "windowWidth": 1100,
    "windowHeight": 720,
    "windowMaximized": false,
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
    "splitEnabled": false,      // 双窗格开关，窗口级（FR-3.3）
    "splitRatio": 0.5,          // 第一窗格占比，0.15–0.85
    "splitVertical": false      // false = 左右，true = 上下（FR-3.1）
  }
}
```

## 3. 字段约束

| 字段 | 约束 | 越界处理 |
|---|---|---|
| `language` | `auto` / `zh-Hans` / `en` 三值 | 无法识别的值按 `auto` 处理，不报错（手改坏了不至于起不来） |
| `newTabPath` | 绝对路径或 `null` | 空白归一为 `null`；**不校验存在性**（盘可能暂时不在），解析时目录不可用则回落到系统「下载」 |
| `theme` | 三值枚举，且系统高对比度开启时强制 `HighContrast` | 回落 `system` |
| `fontSizePt` | `null` 或 8–24 整数 | 超范围夹取到边界 |
| `fontFamily` | 必须存在于系统字体列表 | 不存在则回落默认栈，不报错 |
| `accent` | `null` 或 `#RRGGBB` | 三位写法、颜色名、非十六进制一律归为 `null`（跟随系统）：让界面去猜颜色名只会得到一块读不出来的黑洞。派生色（悬停/按下/浅底）不落盘，由 `AccentService` 按当前主题现算 |
| `showSystem` | 布尔 | 与 `showHidden` 各管一位；带 H+S 的条目（`desktop.ini`）要两个开关都开才显示。只做内存过滤，不重新枚举 |
| `confirmBatch` | 布尔 | 与 `confirmDelete` 分开：允许"单删不弹、批量弹"。一次处理 ≥2 项时用这一项 |
| `doubleClickOpens` | 布尔 | `false` = 单击打开（FR-5.2.5）。切换后列表悬停高亮的延迟随之变化（单击模式 600ms） |
| `density` | 两值枚举 | 回落 `comfortable` |
| `historyLimit` | 0–500 | 夹取 |
| `cacheLimitMb` | 64–4096 | 夹取 |
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
> `splitEnabled` / `splitRatio` / `splitVertical` 三者都是**用户动作当场落盘**
> （`F3`、拖完分隔条、双击复位、右键改方向），不走"退出时尺寸变了才存"的兜底路径：
> 那条路径的判据里没有这三项，只拖一下分隔条就关窗口会把比例丢掉。
>
> **工作区不在这个文件里**：`behavior.startupWorkspace` 只存一个名字，工作区本体在独立的
> `workspaces.json`（同目录，`AppPaths.WorkspacesFile`）。放进 `settings.json` 等于每次启动
> 都要解析一遍所有工作区的标签列表，与"不参与冷启动默认路径"的要求相违。

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
- 迁移函数必须独立可测，位于 `Lingpane.Core/Settings/SettingsMigrations.cs`
- 无法识别的高版本 `schemaVersion` → 只读模式，禁止覆盖，提示用户升级程序

## 6. 与 Token 的关系

设置变更通过服务写入资源字典，**界面不得直接改控件属性**：

| 设置项 | 写入目标 | 入口 |
|---|---|---|
| `fontSizePt` / `density` | `Font.Size.*`、`Size.Row.*`、`Column.*` | `FontScaleService.Apply` |
| `fontFamily` | `Font.Family.UI` | `FontScaleService.ApplyFontFamily` |
| `theme` | 替换主题字典 `Source` | `ThemeService.Apply`（M1） |

约束见《界面实现规则》§15.3 A-01。
