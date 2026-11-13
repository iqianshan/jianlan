using System.Collections.Concurrent;
using System.IO;
using Jianlan.App.Shell;
using Jianlan.Core.FileSystem;

namespace Jianlan.App.Services;

/// <summary>
/// 真实图标的后续解析（ADR-0006 目录图标 + FR-6.5 快捷方式图标）。
///
/// 为什么必须后台做：<c>SHGetFileInfo</c> 按真实路径取图标会访问磁盘（读 desktop.ini、
/// 解析 .lnk 的目标、判断是否特殊文件夹），放同步路径上等于给枚举加一次全量磁盘扫描，
/// 直接违反 NF-01。
///
/// 解析对象只有两类，都是"按扩展名画不准"的：
///   · 目录：下载/桌面这类已知文件夹有专属图标（通用图标序号相同则丢弃）；
///   · 快捷方式（<c>.lnk</c>/<c>.url</c>）：按扩展名只能画出通用"空白页+箭头"，
///     按真实路径才能拿到目标程序图标（Shell 会自动把箭头合成上去）。
/// 其余扩展名一律保持按类型的图标，不额外花一次磁盘查询。
///
/// 三条限制保证开销可控：
///   1. 只解析"本地盘路径"（UNC 与网络盘一律跳过，可能阻塞数十秒）；
///   2. 每次导航最多解析 <see cref="MaxPerPass"/> 项，其余保持原图标；
///   3. 结果按 shell 图标序号去重——与按扩展名解析的序号相同就丢弃，不写回界面。
/// 因此首屏与滚动路径上不存在任何新增磁盘 IO。
/// </summary>
internal static class FolderIcons
{
    /// <summary>单次导航最多解析的项数。超过这个量，继续解析的收益远低于代价。</summary>
    private const int MaxPerPass = 240;

    private static int _genericFolderIndex = -2;      // -2 = 尚未解析，-1 = 解析失败

    /// <summary>
    /// 扩展名 → 类型图标序号。一个目录里几十个 <c>.lnk</c> 会重复问同一个问题，
    /// 这里只查一次。失败（-1）也缓存，避免对同一类型反复付出调用代价。
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> ExtensionIndexCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 在后台线程解析一批图标，返回需要更新界面的 (条目, token)。
    /// 调用方负责把结果切回 UI 线程应用。
    /// </summary>
    public static List<(FileEntry Entry, string Token)> Resolve(IReadOnlyList<FileEntry> entries)
    {
        var updates = new List<(FileEntry, string)>(16);

        // shell 命名空间解析需要 COM，线程池线程上第一次进来时要初始化
        ShellIconProvider.EnsureComInitialized();

        int genericFolder = GenericFolderIndex;
        int resolved = 0;

        foreach (FileEntry entry in entries)
        {
            if (resolved >= MaxPerPass) break;

            // 先按"要不要处理"筛，再计数：无关项不该吃掉配额
            bool isShortcut = !entry.IsDirectory && IsShortcut(entry.IconKey);
            if (!entry.IsDirectory && !isShortcut) continue;

            if (entry.IsDirectory && genericFolder == -1) continue;

            string path = entry.FullPath;
            if (!PathRules.IsLocalDrivePath(path)) continue;

            resolved++;

            int index = ShellIconProvider.ResolvePathIconIndex(path, entry.IsDirectory);
            if (index < 0) continue;

            // 与按扩展名/通用文件夹解析的序号相同 = 没有专属图标（绝大多数情况），不必写回
            int generic = entry.IsDirectory
                ? genericFolder
                : ExtensionIndexCache.GetOrAdd(entry.IconKey, ShellIconProvider.ResolveExtensionIconIndex);

            if (index == generic) continue;

            updates.Add((entry, "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return updates;
    }

    /// <summary>是否本地固定磁盘路径。网络路径可能因离线而阻塞数十秒，一律不解析。</summary>
    public static bool IsLocalPath(string path) => PathRules.IsLocalDrivePath(path);

    /// <summary>
    /// 只有快捷方式值得按真实路径查图标。
    /// 共享标记、云同步状态随文件实时变化，必须逐项查且无法缓存，不做（见 ADR-0006 的取舍）。
    /// </summary>
    private static bool IsShortcut(string iconKey)
        => iconKey.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
           || iconKey.Equals(".url", StringComparison.OrdinalIgnoreCase);

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
