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
    "language": "auto"          // auto（跟随系统安装语言）| zh-Hans | en
  },

  "appearance": {
    "theme": "system",          // system | light | dark
    "fontSizePt": null,         // null = 跟随系统；8–24 整数
    "fontFamily": null,         // null = 默认字体栈；否则为系统字体族名
    "density": "comfortable",   // comfortable(32px) | compact(26px)
    "showNavPane": true,
    "showStatusBar": true
  },

  "behavior": {
    "defaultView": "details",   // details | list | grid
    "sortBy": "name",           // name | date | size | type
    "sortDescending": false,
    "foldersFirst": true,
    "showHidden": false,
    "showExtensions": true,
    "confirmDelete": true,
    "doubleClickOpens": true,
    "openFolderInNewTab": false,
    "restoreLastSession": true
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
    "lastPath": null
  },

  "layout": {
    "columnDateWidth": 110,     // 56–400
    "columnTypeWidth": 110,     // 56–400
    "columnSizeWidth": 90       // 56–400
  }
}
```

## 3. 字段约束

| 字段 | 约束 | 越界处理 |
|---|---|---|
| `language` | `auto` / `zh-Hans` / `en` 三值 | 无法识别的值按 `auto` 处理，不报错（手改坏了不至于起不来） |
| `theme` | 三值枚举，且系统高对比度开启时强制 `HighContrast` | 回落 `system` |
| `fontSizePt` | `null` 或 8–24 整数 | 超范围夹取到边界 |
| `fontFamily` | 必须存在于系统字体列表 | 不存在则回落默认栈，不报错 |
| `density` | 两值枚举 | 回落 `comfortable` |
| `historyLimit` | 0–500 | 夹取 |
| `cacheLimitMb` | 64–4096 | 夹取 |
| `windowWidth/Height` | ≥ 最小窗口尺寸 | 夹取 |
| `columnDate/Type/SizeWidth` | 56–400（单位：DIP） | 夹取；`0`、`NaN`、`Infinity` 回落默认值 |

> 名称列是星号列（宽度随窗口伸缩），因此**没有**对应字段，也不参与拖拽。
> 列宽只在拖拽结束时落盘一次，不跟随每个拖动事件写文件。

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
