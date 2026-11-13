# FileId 枚举可行性（FR-4 前置 spike）

- 结论：**可行，且成本低于预期。** `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)` 一次调用
  同时给出名称、大小、修改时间、属性、FileId，不需要"先枚举再逐个查 FileId"。
- 测量日期：2026-11-08
- 探针：`tools/spike-fileid`（引用 `Jianlan.Core`，用真实 `DirectoryEnumerator` 做基线；不进 `Jianlan.sln`）
- 环境：Windows 10.0.26300 · .NET 9.0 · D: 卷 NTFS 本地盘

---

## 1. 为什么先做这个

`docs/m2-plan.md` §4.5 把 FR-4 的存储引擎定为"JSONL + 内存索引"后，剩下的风险只有一条：
**`FileSystemEnumerable`（`FindFirstFileEx`）拿不到 FileId**，`WIN32_FIND_DATA` 里没有这个字段。
要么换成 `GetFileInformationByHandleEx`，要么退化成"对每个文件单独打开句柄查一次"——
后者在万项目录里就是一万次系统调用，必然击穿 NF-03。

这条如果验证不通过，FR-4 的方案要整体改（退回"按路径存标签"并接受重命名失效）。
所以它必须在 P4 开工前跑完，而不是在实现到一半时发现。

## 2. 实测数据

### 2.1 性能（10000 项目录，5 轮交替，热态）

| 指标 | 现有 `FileSystemEnumerable` | `FileIdBothDirectoryInfo` | 对比 |
|---|---|---|---|
| 纯枚举 P50 | 11.7 ms | **6.2 ms** | 快约一倍 |
| 枚举 + 转 `FileEntry` + 排序 P50 | 40.7 ms | 37.8 ms | 持平 |
| 系统调用次数 | —（内部缓冲） | 22 次 | 64 KB 缓冲，0 次扩容 |

FileId 路径更快的原因很直接：一次内核调用返回整批记录，且每条记录自带全部字段，
不需要 `FileSystemEntry` 那层托管包装。**排序成本两边相同**，所以"枚举 + 排序"这一项几乎打平。

NF-03 是首屏 ≤ 800 ms（当前实测 233 ms），枚举只占其中一段。按上表，换成 FileId 路径不会让首屏变慢。

> **这一轮是热态**（目录项在系统缓存里）。冷态未单独测——本机无法可靠清空文件系统缓存。
> 但两组在同一轮里交替执行，缓存条件相同，**相对比较有效**；绝对耗时不代表冷启动。

### 2.2 正确性（10000 项全量比对）

| 检查项 | 结果 |
|---|---|
| 条目数与现有枚举一致 | 10000 = 10000 ✅ |
| 仅一方有的条目 | 各 0 项 ✅ |
| 重复 FileId | 0 组 ✅ |
| FileId 与 `FileIdInfo` 单文件查询一致 | 抽查 20 项，0 不一致 ✅ |
| 目录卷序列号 == 目录内文件的卷序列号 | 0 不一致 ✅ |

### 2.3 FileId 稳定性

| 操作 | FileId | 结论 |
|---|---|---|
| 新建 `spike-original.txt` | 281474977138187 | — |
| 同目录重命名 | 281474977138187 | **不变** ✅ |
| 同卷移动到子目录 | 281474977138187 | **不变** ✅ |
| 目录枚举路径取到 | 281474977138187 | 与句柄一致 ✅ |
| 跨卷移动到 C: | 246009129645351155 | **变了** ✅ 印证"跨卷移动丢标签"是文件系统行为，不是实现缺陷 |

这三条正是需求 `FR-4.2`「重命名、同卷移动后仍生效；跨卷移动丢失（需明示）」的实现依据。

### 2.4 边界场景

| 场景 | 结果 |
|---|---|
| 空目录 | 0 项，正常收尾 ✅ |
| UNC（`\\localhost\D$\ac05\10k`） | 10000 项与本地完全一致，FileId 相同 ✅ |
| 352 字符长路径 | 加 `\\?\` 前缀后正常；**不加会失败**（见 §3.3） |
| 无权限目录（`C:\System Volume Information`） | 抛 `Win32Exception`（ACCESS_DENIED） |

## 3. 五个必须做对的地方

这五条都是实测撞出来的，照抄网上常见写法会全部踩中，而且**每一条都不报错**。

### 3.1 `BY_HANDLE_FILE_INFORMATION` 必须用 Explicit 布局

三个时间字段是 `FILETIME`（两个 DWORD，对齐 **4**），不是 `LARGE_INTEGER`（对齐 8）。
用 `long` 写，C# 会在 `dwFileAttributes` 后补 4 字节，从第一处时间字段起整块错位：

```
错位后 VolumeSerialNumber 读到的是「最后写入时间」的低 32 位，
nFileIndexHigh/Low 同理 —— 得到一个"看起来像 FileId 的随机数"。
```

本次就是这样撞上的：第一版 `Identify` 交叉校验 20 项全不一致，
一度以为目录枚举的 FileId 读错了，实际是单文件查询的结构体错位。

### 3.2 会返回 `.` 和 `..`

`FileIdBothDirectoryInfo` **包含** `.` 和 `..`，而 `FileSystemEnumerable` 不包含。
不过滤的话条目数恒多 2（实测 10002 vs 10000）。这个差异不报错，
只会在某处变成"项数对不上"或"排序里混进两个点目录"。

### 3.3 必须自己加 `\\?\` 前缀，而且必须先 `Path.GetFullPath`

`CreateFileW` 受 MAX_PATH(260) 限制。**系统开了 `LongPathsEnabled=1` 也不够** ——
那条注册表只对 manifest 里声明了 `longPathAware` 的进程生效。

实测：352 字符的目录直接打开失败（ERROR_PATH_NOT_FOUND），加上前缀后正常。
`FileSystemEnumerable` 内部就是这么做的，所以现有枚举能吃长路径；
自己写 P/Invoke 时这一步要补，否则"深目录下标签不显示"会成为一个查不出的 bug ——
它只在深目录出现，而且异常会被降级分支吞掉。

顺序不能反：`\\?\` 会**关闭**路径规范化，传 `D:/a//b` 或含 `..` 的路径进去同样失败。
必须先 `Path.GetFullPath` 再拼前缀。本次也是先漏了这一步，症状是"反斜杠能开、正斜杠打不开"。

### 3.4 卷序列号要单独取，目录内共用

`FILE_ID_BOTH_DIR_INFO` **不带卷序列号**，只有 FileId。标签键是 `(VolumeSerial, FileId)` 的组合，
所以要对**目录句柄**调一次 `GetFileInformationByHandleEx(FileIdInfo)` 拿到卷序列号，
该目录下所有条目共用。一次调用，成本可忽略；实测目录与目录内文件的卷序列号 0 不一致。

两个口径不要混：

| 来源 | 卷序列号 | FileId |
|---|---|---|
| `FILE_ID_INFO`（推荐） | **8 字节** `0x0A92BD1192BD01ED` | 64 位（ReFS 上另有高 64 位） |
| `BY_HANDLE_FILE_INFORMATION`（旧） | **4 字节** `0x92BD01ED` | 64 位 |

实测两者的关系：`FILE_ID_INFO` 的**低 32 位**就是传统卷序列号，高 32 位是额外信息。
混用两种口径会让同一个文件算出两个不同的键，表现为"标签设了不显示"。

> NTFS 上 `FileIndex` 与 `FileId` 的 64 位值**实测相同**（都含序列号高位）。
> 但不要依赖这一点：`FILE_ID_BOTH_DIR_INFO.FileId` 的定义是「序列号 << 48 | MFT 记录号」，
> 而 `BY_HANDLE_FILE_INFORMATION` 在 ReFS 上只有低 64 位。统一走 `FileIdInfo` 最省心。

### 3.5 无权限目录会抛异常，必须降级

现有 `DirectoryEnumerator` 用 `IgnoreInaccessible = true`，对无权限目录**返回 0 项**；
FileId 路径在 `CreateFileW` 这一步就抛 `Win32Exception`。

集成时不能让它冒泡到界面：捕获后回落现有枚举路径（标签不显示），
与 `TryRead` 的既有降级口径一致。否则打开 `C:\System Volume Information` 之类的目录会从
"显示为空"变成"报错弹窗"——比现状更差。

## 4. 尚未验证

| 场景 | 状态 | 影响 |
|---|---|---|
| ReFS / exFAT / FAT32 | 【待核实】无环境 | exFAT 常见于 U 盘。若不支持或返回合成 ID，需在"该卷不支持标签"时明示降级 |
| 真实远程 SMB（非本机管理共享） | 【待核实】 | 本次只测了 `\\localhost\D$`（同机 SMB，透传 NTFS 的 FileId）。Samba / 非 NTFS 服务器可能返回 0 或不支持 |
| MFT 记录复用后的序列号行为 | 【待核实】 | NTFS 上 FileId 含 16 位序列号，理论上能区分"删旧建新"。未构造用例验证 |
| 冷缓存绝对耗时 | 【待核实】 | 见 §2.1 注 |

这四项都不挡 FR-4 开工（都能在实现期用降级分支兜住），但要在验收前补测：
跨卷/可移动设备场景本来就在 AC-05 的六类边界里。

## 5. 对 FR-4 排期的影响

| 项 | 原估 | 现估 | 说明 |
|---|---|---|---|
| FileId 枚举层 | 1–2 天（风险项） | **0.5 天** | 探针已跑通，P/Invoke 声明、结构体布局、长路径、点目录、降级策略都验过了，搬进 `Core/FileSystem` 即可 |
| FR-4 总工作量 | 4 天 | **4 天** | 不变，但不确定性从"可能推翻方案"降为"照着清单实现" |

实现时的一条硬约束（`m2-plan` §4.5 已有，这里再确认）：**只在该目录存在标签记录时才走 FileId 路径**。
FileId 枚举虽然更快，但它要求"有标签的目录"才付出这份额外路径判断，
普通目录继续走现有 `FileSystemEnumerable`，两个实现并存但互不干扰。

## 6. 复现方式

```powershell
cd tools\spike-fileid
dotnet build -c Release

$exe = "bin\Release\net9.0-windows10.0.17763.0\SpikeFileId.exe"
& $exe dump   D:\ac05\10k 5      # 看前 5 项，自检结构体布局
& $exe verify D:\ac05\10k        # 条目数、FileId 一致性、卷序列号一致性
& $exe bench  D:\ac05\10k 5      # 与现有实现的耗时对比
& $exe id     D:\ac05\10k\a_00044.txt   # 单文件的新旧口径对照
& $exe stability D:\ac05         # 重命名 / 同卷移动 / 跨卷
```

补测未验证场景时，直接拿 U 盘（exFAT）、ReFS 卷、真实远程共享做参数即可，
不需要改代码——`dump` / `verify` 在任何路径上都能跑。
