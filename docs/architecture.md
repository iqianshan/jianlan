# 架构

## 1. 分层

```
Lingpane.App (WPF)          ← UI、视图模型、Shell 互操作封装
      │  单向依赖
      ▼
Lingpane.Core               ← 文件系统、设置、标签存储、诊断
```

**依赖方向不可逆。** `Lingpane.Core` 不得引用 `PresentationFramework` / `PresentationCore` /
`WindowsBase`，违反即为设计错误——它决定了 Core 能否被无 UI 单测覆盖。

| 项目 | 允许依赖 | 禁止 |
|---|---|---|
| `Lingpane.Core` | BCL、`System.IO.Enumeration` | 任何 WPF 程序集、任何 `System.Windows.*` |
| `Lingpane.App` | Core、WPF、Win32 P/Invoke | 直接操作磁盘/数据库（必须经 Core） |

## 2. 目录职责

### Lingpane.Core

| 目录 | 职责 | 关键类型 |
|---|---|---|
| `FileSystem/` | 枚举、排序、路径归一化、文件操作 | `DirectoryEnumerator`、`FileEntry` |
| `Navigation/` | 常用目录、历史、前进后退栈 | `KnownFolders` |
| `Settings/` | 设置读写、默认值、版本迁移 | `AppSettings`、`SettingsMigrations` |
| `Tagging/` | 彩色标签的存储与查询（M2） | `TagStore` |
| `Diagnostics/` | 路径解析、启动埋点、日志 | `AppPaths`、`StartupTrace` |

### Lingpane.App

| 目录 | 职责 |
|---|---|
| `Design/` | 资源字典：DesignTokens、主题、控件样式。**唯一允许定义视觉常量的位置** |
| `Typography/` | 字号与字体度量下发 | 
| `Views/` | 窗口与页面 XAML |
| `ViewModels/` | 视图模型，不含 `System.Windows` 类型（`ICommand` 除外） |
| `Shell/` | 系统集成：图标、右键菜单、属性页、预览 Handler |
| `Converters/` | 值转换器，一律单例 `x:Static` |

## 3. 启动路径

约束：**首帧之前不得出现磁盘 IO、数据库访问、Shell COM 调用。**

```
App()                         StartupTrace.Begin()
  └─ InitializeComponent()    合并 DesignTokens / 主题 / 样式字典
OnStartup()
  ├─ FontScaleService.Apply   写入字号 token
  ├─ new MainWindow()         构造视图树（空列表）
  └─ Show()                   ← 首帧
Loaded
  └─ NavigateAsync()          后台线程枚举 → 回到 UI 线程整体替换 ItemsSource
```

任何新增代码若要插入启动路径，必须回答：**能不能挪到 `Loaded` 之后？** 答案是否则不加。

## 4. 关键设计决定

| 决定 | 理由 | 记录 |
|---|---|---|
| WPF 而非 WinForms | Shell 互操作资料最厚；代价是启动基线 +350ms | [ADR-0001](adr/0001-ui-framework.md) |
| 列表用 `IReadOnlyList` 整体替换 | 万项逐条 `Add` 触发 N 次 `CollectionChanged` | [ADR-0003](adr/0003-list-replacement.md) |
| 图标按扩展名而非按文件 | 滚动零磁盘 IO；代价是特殊文件夹图标不准 | `docs/filesystem-and-icons.md` §4 |
| 彩色标签用 File ID + 路径冗余 | ADS 复制即丢、纯路径重命名即丢 | `docs/requirements.md` FR-4 |
| 不使用 DI 容器 | 对象图深度为 1，容器初始化 20–30ms 不值得 | [ADR-0004](adr/0004-no-di-container.md) |
| 收藏书签存 JSON 不建库 | 500 条以内约 60KB，读取 <5ms | FR-7 |
| 内存分档 150/185/230 MB | WPF 框架底 81MB 吃掉 120MB 阈值的 68% | [ADR-0002](adr/0002-memory-budget.md) |

## 5. 线程模型

| 工作 | 线程 |
|---|---|
| 目录枚举、排序 | 线程池（`Task.Run`） |
| 图标解析与缓存 | 调用方线程，缓存内部加锁（已 `Freeze` 的位图可跨线程读） |
| 预览 Handler | 独立 STA 线程（M1） |
| 日志落盘 | 后台队列，每 200ms 批量（M1） |
| 一切 UI 对象创建与修改 | UI 线程 |

**禁止**：在 `Loaded` / `SizeChanged` / 绑定转换器内做同步阻塞操作（红线 R-10）。

## 6. 后续演进约束

1. 双窗格不是"两套 UI"，是**两个 `PaneViewModel` 绑定同一个列表模板**。任何为单窗格写的特例都要复查
2. 标签页各自持有独立导航栈与视图模式，共享设置与标签数据
3. 新功能若需要新增常驻后台任务，须先通过启动预算评审（`docs/performance-gate.md` §2）
