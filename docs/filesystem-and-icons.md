# 文件系统与图标

> 对应 FR-6。本文件规定 `Jianlan.Core/FileSystem` 与 `Jianlan.App/Shell` 的实现边界，
> 目的是让"目录浏览"这条主路径在任何目录规模下都不阻塞 UI。

## 1. 目录枚举

**实现**：`System.IO.Enumeration.FileSystemEnumerable<T>`（`DirectoryEnumerator`）。

选型理由：

| 方案 | 结论 |
|---|---|
| `Directory.GetFiles` | 先返回完整 `string[]`，万项目录多一次全量分配 |
| `DirectoryInfo.EnumerateFileSystemInfos` | 每项一个 `FileSystemInfo` 对象，字段访问会回查磁盘 |
| **`FileSystemEnumerable<T>`** | **一次缓冲、回调直转目标类型，可自定义跳过规则。采用** |
| 手写 `FindFirstFileExW` | 理论最快，但需自行处理长路径、重解析点、缓冲区增长，收益不足以抵消风险 |

配置：

```csharp
new EnumerationOptions {
    RecurseSubdirectories = false,
    IgnoreInaccessible = true,   // 无权限子项跳过，不中断整次枚举
    AttributesToSkip = 0,        // 隐藏/系统文件也要显示
}
```

**线程约束**：只允许在后台线程调用。`Loaded` / `SizeChanged` / 绑定转换器内禁止直接调用（红线 R-10）。

**异常**：`TryReadSorted` 捕获 `IOException` / `UnauthorizedAccessException` / `ArgumentException`
/ `NotSupportedException` / `SecurityException`，返回 `false` + 可展示原因。**不向上抛**。

## 2. 排序

默认顺序（`DirectoryEnumerator.DefaultOrder`）：

1. 文件夹优先
2. 名称升序，`OrdinalIgnoreCase`

用 `List.Sort(Comparison<T>)` 原地排序，不走 LINQ `OrderBy`（多一次分配与迭代器）。

后续版本的排序项（日期 / 大小 / 类型）沿用同一比较器结构，在 `Jianlan.Core` 内实现，
不放到 ViewModel。

## 3. 长路径

`app.manifest` 已声明 `longPathAware=true`，配合 `net9.0-windows10.0.17763.0` 目标框架，
`>260` 字符路径可直接枚举与访问。

**注意**：跨 `\\?\` 前缀与普通路径的字符串比较会不一致，路径归一化集中在
`Jianlan.Core/FileSystem/PathNormalizer.cs`（M1 落地）。

## 4. 图标提取

**方案**：`SHGetFileInfo` + `SHGFI_USEFILEATTRIBUTES`，按**扩展名**取图标。

关键点：

- `SHGFI_USEFILEATTRIBUTES` 让 Shell 只按传入的属性与扩展名推断图标，**不访问磁盘**。
  这是滚动流畅的前提（红线 R-08），也是本方案与"按文件取图标"的核心区别。
- 缓存键是扩展名（`.txt` / `.folder` / `.file`），一种扩展名只解析一次。
- 转换出的 `BitmapSource` 立即 `Freeze()`，之后可跨线程读取，无锁竞争。
- `HICON` 用完必须 `DestroyIcon`，否则每次冷门扩展名泄漏一个图标句柄。

**已知限制（可接受，写在此处避免后人当 bug 修）**：

1. 文件夹统一显示系统默认文件夹图标，不区分空/满/共享。
2. 自定义了图标的特殊文件夹（如 OneDrive、Git 仓库）显示通用图标。
3. 关联程序变更（装/卸软件改了文件关联）后图标不会自动刷新，需重启程序。

**尺寸**：列表行固定 16px，大图标视图 24px。**不随字号缩放**（FONT-06）。

## 5. 后续待办

| 项 | 阶段 | 说明 |
|---|---|---|
| 缩略图（图片/视频首帧） | M1 | `IShellItemImageFactory`，必须设 `DecodePixelWidth` |
| 真实文件夹图标（含自定义图标） | M1 | `SHGFI_ICON` 按路径，逐项异步 + 缓存 |
| 文件 ID 取卷序列号 + 索引 | M2 | 彩色标签的稳定标识，`GetFileInformationByHandleEx` |
| 磁盘变化监听 | M1 | `ReadDirectoryChangesW`，防抖 200ms 后增量刷新 |

## 6. 性能红线

| 红线 | 含义 |
|---|---|
| 枚举期间 UI 线程零阻塞 | ViewModel 只 `await`，不做同步枚举 |
| 图标解析不碰磁盘 | 只用 `SHGFI_USEFILEATTRIBUTES` |
| 滚动期间不发起新请求 | 滚动中不调用 Shell、不查数据库、不算文件夹大小 |
| 文件夹大小不自动计算 | 只在用户显式请求时递归，且可取消（FR-6.16） |
