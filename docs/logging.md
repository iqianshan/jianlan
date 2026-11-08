# 日志与错误码

> 对应 NF-12 / FR-10.7。目标是崩溃后能凭一份日志定位问题，而不是靠用户口述"它卡了一下"。

## 1. 文件布局

| 文件 | 内容 | 滚动 |
|---|---|---|
| `logs/startup.log` | 每次启动一行，供性能门禁解析 | 追加，超过 512 KB 轮转为 `startup.1.log` |
| `logs/app.log` | 运行期诊断 | 单文件 ≤5 MB，保留 3 份（`app.log` / `app.1.log` / `app.2.log`） |
| `logs/crash.log` | 未处理异常 | 同上 |
| `logs/install.log` | 安装/卸载记录（安装包写，程序只读） | 不滚动 |

路径由 `AppPaths.LogsDir` 决定，便携模式下在 `<exe>\data\logs\`。

## 2. start.log 格式（固定，供脚本解析）

```
2026-11-03T14:41:13.307+08:00 | total=594 | begin=0ms resources=131ms window=334ms render=376ms loaded=544ms firstframe=574ms | ws=130MB
```

- 字段顺序固定：时间戳 | total | 各阶段 | 工作集
- 修改字段即为破坏门禁脚本，必须同步改 `tools/startup-bench/Measure-Startup.ps1`

## 3. app.log 格式

```
2026-11-03 14:41:13.307 [INF] nav.open path="D:\Docs" items=1243 cost=38ms
2026-11-03 14:41:15.002 [WRN] icon.fallback ext=".xyz" reason=no-association
2026-11-03 14:41:16.881 [ERR] fs.enumerate path="X:\" code=E1003 msg="设备未就绪"
```

| 段 | 含义 |
|---|---|
| 时间 | `yyyy-MM-dd HH:mm:ss.fff`，本地时间 |
| 级别 | `TRC` `DBG` `INF` `WRN` `ERR` `CRT` |
| 事件 | `域.动作`，全小写点分 |
| 字段 | `key=value`，值含空格时加引号 |

**规则**

1. 日志写入不在 UI 线程同步执行：先进内存环形队列，后台每 200 ms 批量落盘
2. 单条日志不得拼接超过 512 字符，长内容截断并标注 `…(+N)`
3. 禁止记录文件内容、用户输入的搜索词、完整命令行
4. 路径记录完整路径；如后续加隐私模式，改为仅记录盘符与层级数

## 4. 错误码段

| 段 | 域 | 已登记 | 写入点 |
|---|---|---|---|
| `E1xxx` | 文件系统 | `E1001` 路径不存在 · `E1002` 无权限 · `E1003` 设备未就绪 · `E1004` 路径过长 · `E1005` 文件被占用 · `E1006` 磁盘空间不足 · `E1007` 删除被拒（原因判不细） | `MainViewModel.NavigateAsync` → `nav.open`；`FileOperations` → `fs.createFolder` / `fs.createFile` / `fs.rename` / `fs.copy` / `fs.move` / `fs.delete` |
| `E2xxx` | Shell 互操作 | `E2001` 图标提取失败 · `E2002` 右键菜单失败 · `E2003` 属性页失败 | `MainWindow.Icons` → `icons.resolve` |
| `E3xxx` | 标签存储与 FileId 枚举 | `E3001` 标签库读取失败（备份重建）· `E3002` 跳过坏行 · `E3003` 写入失败（转只读）· `E3004` 压缩失败（转只读）· `E3005` 备份失败 · `E3006` 取不到 FileId（回落普通枚举）· `E3007` 初始化异常 | `TagStore` → `tags.load` / `tags.write` / `tags.compact` / `tags.backup`；`PaneViewModel` → `tags.enum`；`MainWindow` → `tags.init` |
| `E4xxx` | 设置 | `E4001` 解析失败 · `E4002` 写入失败 · `E4003` 版本过高 | `App` → `settings.load` / `settings.save` |
| `E5xxx` | 目录监视 | `E5001` 启动失败 · `E5002` 运行期错误 | `MainWindow.Watcher` → `watcher.start` / `watcher.error` |
| `E6xxx` | 标签页 | `E6001` 复制路径写剪贴板失败 · `E6002` 已达标签数上限（改为当前窗格导航） | `TabStrip` → `tab.copyPath`；`MainWindow` → `tab.limit` |
| `E7xxx` | 窗口与输入 | `E7001` 全局热键注册失败 | `MainWindow` → `hotkey.register` |
| `E8xxx` | 预览 | `E8001` Handler 缺失 · `E8002` 超时 · `E8003` 崩溃 | 预留（0.2.0） |
| `E9xxx` | 组织（工作区） | `E9001` 工作区读取失败（备份重建）· `E9002` 写入失败 · `E9003` 读入告警（含版本过高） | `WorkspaceStore` → `workspace.load` / `workspace.save`；`MainWindow` → `workspace.load` |

新错误码必须先在此登记，代码与文档同时提交。段内的"预留"表示该段已被占用、子项待实现，
不得挪作他用——`E5xxx` 曾按预览规划，实际先被目录监视占用，预览因此改用 `E8xxx`。

> **2026-11-09 补记**：工作区（FR-8）与彩色标签（FR-4）落地时都曾把码写进 `E8xxx`，
> 与预览段撞了。已一并迁出：工作区进新开的 `E9xxx`，标签进本来就写着"标签存储 预留"的 `E3xxx`。
> 教训是"先写代码再补登记"——码段冲突不会报错，只会在真去查日志时发现两个不相干的模块共用一个码。

## 5. 崩溃处理

```csharp
DispatcherUnhandledException        → 记录 + 阻止进程退出（能救则救）
AppDomain.CurrentDomain.UnhandledException → 记录（无法阻止）
TaskScheduler.UnobservedTaskException      → 记录 + 标记已观察
```

崩溃记录内容：

1. 异常类型、消息、完整堆栈
2. 当前路径、视图模式、标签页数量、是否双窗格
3. 启动耗时摘要（判断是否发生在启动期）
4. 程序版本、OS 版本、.NET 版本、DPI 缩放、主题

**不做**：不弹"发送报告"对话框，不联网上传。崩溃日志留在本地，由用户自行决定是否附到 issue。

## 6. 用户可见的错误提示

日志与提示分离：

| 场景 | 用户看到 | 日志级别 |
|---|---|---|
| 目录无权限 | 列表区居中提示"无法访问此文件夹" + 原因 | `WRN` |
| 单个文件操作失败 | 状态栏一行红字，3 秒后淡出 | `ERR` |
| 致命错误 | 模态对话框 + "打开日志目录"按钮 | `CRT` |

用户提示**不得**出现堆栈、错误码原文、英文异常消息。错误码只在日志里。

**实现约束**：运行时抛出的 `Exception.Message` 来自 .NET 自带资源，跟随**系统语言**，
不受应用内切换的界面语言影响。实测中文界面下曾出现半句中文半句英文：

```
新建文件夹失败：Access to the path 'D:\ac05\aclnowrite\新建文件夹' is denied.
```

所以凡是异常原因进界面的地方，都必须先经 `Core/FileSystem/FsErrors` 归类成错误码，
界面显示该码对应的译文，原始消息连同码一起写 `app.log`。`LogSink` 是 Core 写日志的出口
（依赖方向 App → Core，由 `App` 启动时装配）。
