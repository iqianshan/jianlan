using System.IO;
using Lingpane.App.Shell;
using Lingpane.Core.FileSystem;

namespace Lingpane.App.Services;

/// <summary>
/// 目录真实图标的后台解析（ADR-0006）。
///
/// 为什么必须后台做：<c>SHGetFileInfo</c> 按真实路径取图标会访问磁盘（读 desktop.ini、
/// 判断是否特殊文件夹），放同步路径上等于给枚举加一次全量磁盘扫描，直接违反 NF-01。
///
/// 三条限制保证开销可控：
///   1. 只解析本批目录中的"本地盘路径"（UNC 与网络盘一律跳过，冷启动可能几百毫秒）；
///   2. 每次导航最多解析 <see cref="MaxPerPass"/> 个目录，其余保持通用图标；
///   3. 结果按 shell 图标序号去重——普通目录的序号与通用文件夹图标相同，直接丢弃。
/// 因此首屏与滚动路径上不存在任何新增磁盘 IO。
/// </summary>
internal static class FolderIcons
{
    /// <summary>单次导航最多解析的目录数。超过这个量，继续解析的收益远低于代价。</summary>
    private const int MaxPerPass = 240;

    private static int _genericFolderIndex = -2;      // -2 = 尚未解析，-1 = 解析失败

    /// <summary>
    /// 在后台线程解析一批目录图标，返回需要更新界面的 (条目, token)。
    /// 调用方负责把结果切回 UI 线程应用。
    /// </summary>
    public static List<(FileEntry Entry, string Token)> Resolve(IReadOnlyList<FileEntry> entries)
    {
        var updates = new List<(FileEntry, string)>(16);

        // shell 命名空间解析需要 COM，线程池线程上第一次进来时要初始化
        ShellIconProvider.EnsureComInitialized();

        int generic = GenericFolderIndex;
        if (generic == -1) return updates;

        int resolved = 0;

        foreach (FileEntry entry in entries)
        {
            if (!entry.IsDirectory) continue;
            if (resolved >= MaxPerPass) break;

            string path = entry.FullPath;
            if (!PathRules.IsLocalDrivePath(path)) continue;

            resolved++;

            int index = ShellIconProvider.ResolvePathIconIndex(path, isDirectory: true);
            if (index <= 0) continue;

            // 与通用文件夹图标同序号 = 这个目录没有自定义图标，无需更新（绝大多数情况）
            if (index == generic) continue;

            updates.Add((entry, "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return updates;
    }

    /// <summary>是否本地固定磁盘路径。网络路径可能因离线而阻塞数十秒，一律不解析。</summary>
    public static bool IsLocalPath(string path) => PathRules.IsLocalDrivePath(path);

    private static int GenericFolderIndex
    {
        get
        {
            int cached = Volatile.Read(ref _genericFolderIndex);
            if (cached != -2) return cached;

            int index = ShellIconProvider.ResolveGenericFolderIndex();
            Volatile.Write(ref _genericFolderIndex, index);
            return index;
        }
    }
}
