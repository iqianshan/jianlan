# ADR-0006：目录图标按 shell 命名空间解析，且只在空闲时后台做

- 状态：已接受
- 日期：2026-11-04
- 相关：ADR-0002（内存预算）、`docs/performance-gate.md` §9、`Shell/ShellIconProvider.cs`、`Services/FolderIcons.cs`

## 背景

M1 清单里有一项"真实文件夹图标"：当前所有目录共用同一个通用文件夹图标，
看起来像半成品。要求补成"和资源管理器一致"。

实现前必须先回答一个问题：**取哪一层的数据**。

## 结论

### 1. 必须走 PIDL，不能按路径直接问

实测（2026-11-04，本机 Windows）：

| 调用方式 | `C:\Windows` | `Downloads` | `Desktop` | 带 desktop.ini 的自定义目录 |
|---|---|---|---|---|
| `SHGetFileInfo(路径)` | 3（通用） | 3（通用） | 3（通用） | 3（通用） |
| `SHParseDisplayName` + `SHGetFileInfo(SHGFI_PIDL)` | 3 | **50** | **51** | **44** |

左侧两列说明：按路径查对任何目录都返回同一个通用图标序号，连 desktop.ini 都不认。
必须在 shell 命名空间里解析（PIDL），才能拿到已知文件夹（下载/桌面）与自定义图标。

**代价**：`SHParseDisplayName` 会访问磁盘、依赖 COM，属于"不能放 UI 线程"的调用。

### 2. 只在 ApplicationIdle 之后、后台线程做，且严格限量

三条限制写在 `FolderIcons.Resolve`：

1. 只解析本地盘路径（`PathRules.IsLocalDrivePath`）。UNC 在网络对端离线时
   一次调用能阻塞几十秒——这属于"用户没做错任何事却卡住"，必须排除；
2. 每次导航最多 240 个目录；
3. 结果按 shell 图标序号去重：普通目录的序号与通用文件夹图标相同，直接丢弃，
   因此只有当目录真的有专属图标时才产生一次界面通知。

首屏路径上**没有**任何新增磁盘 IO：解析排在 `DispatcherPriority.ApplicationIdle`，
首帧早已完成；连续快速导航用代次号丢弃过期结果。

### 3. Core 不引入 WPF 类型

`FileEntry` 只增加 `IconToken`（字符串，形如 `.folder` 或 `#50`），
位图仍由 App 层的 `ShellIconProvider` 提供。这样 Core 的单测不需要 WPF。

## 已知代价

1. 240 个以后、跨次导航的目录仍是通用图标（缓存未跨导航保留）——实测很少触发；
2. 网络路径永不解析；
3. 大图标视图若将来实现，图标需按需升到 48px，当前只解析 16px；
4. 目录内容变化（desktop.ini 改动）不会自动刷新图标，要重进目录。

## 实测

- 带 `desktop.ini`（`IconResource=shell32.dll,44`）的目录在列表里显示为自定义图标 ✅
- `Downloads` / `Desktop` 返回 50 / 51，与系统一致 ✅
- 首次导航多出一次后台任务，首帧时间未变化（与上一版二进制交替测量，见 `docs/performance-gate.md` §11）
