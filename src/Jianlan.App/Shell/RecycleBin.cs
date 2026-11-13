using System.IO;
using System.Runtime.InteropServices;
using Jianlan.Core.FileSystem;

namespace Jianlan.App.Shell;

/// <summary>
/// 回收站还原（FR-6.8 的删除撤销）。
///
/// Windows 没有公开的"从回收站还原某个文件"的 API，只有 Shell 的交互式命令。
/// 这里走 <c>Shell.Application</c> 的回收站命名空间：按"原位置 + 名字"定位条目，
/// 再逐个尝试还原动词。
///
/// 两条设计纪律：
/// 1. **动词名靠"试 + 验"**。还原动词是本地化的（简体中文是"还原"、英文是"Restore"），
///    没有稳定名字可用，所以按候选表逐个试，**每次都回头查文件是否真的回到原位**，
///    没回来就继续试下一个。宁可试不出来，也不能凭一个"调用没抛异常"就宣称成功。
/// 2. **失败就是失败**。还原不了的文件仍留在回收站里，用户可以手动还原；
///    这里绝不做"还原不成改删除"之类的补救。
/// </summary>
public sealed class RecycleBin : IRecycleBin
{
    /// <summary>回收站命名空间（<c>ssfBITBUCKET</c>）。</summary>
    private const int BitBucket = 10;

    /// <summary>"原位置"那一列在回收站视图里的列号。列序与语言无关，与列名无关。</summary>
    private const int OriginalLocationColumn = 1;

    /// <summary>单个文件等它回到原位的时长。资源管理器要挪文件，机械盘上偶尔不快。</summary>
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 送进回收站。复用 <see cref="FileOperations.DeleteAsync"/> 那套（它已经在 STA 线程上跑），
    /// 不再写一遍 Shell 调用——两处实现迟早会有一处忘记 allowUndo。
    /// </summary>
    public async Task<bool> DeleteToRecycleBinAsync(IReadOnlyList<string> paths)
    {
        FileOpResult result = await FileOperations.DeleteAsync(paths, permanent: false).ConfigureAwait(false);
        return result.Ok;
    }

    public IReadOnlyList<string> Restore(IReadOnlyList<string> originalPaths)
    {
        if (originalPaths.Count == 0) return [];

        // Shell 的自动化对象要求 STA。放在后台线程上做：回收站里有几万项时
        // 枚举会持续好几秒，不能挂在 UI 线程上。
        List<string> restored = [];

        var thread = new Thread(() =>
        {
            try
            {
                restored = RestoreCore(originalPaths);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException
                or ArgumentException or IOException or UnauthorizedAccessException)
            {
                // 还原失败留给上层报"未能从回收站还原"，不冒充成功
            }
        })
        {
            IsBackground = true,
            Name = "Jianlan.RecycleBin",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // 等不到就按失败返回：宁可告诉用户"没还原"，也不能让 UI 一直卡着。
        // 预算按项数算——写死一个数，文件一多就必然"超时"，那是自己造出来的失败
        TimeSpan budget = TimeSpan.FromSeconds(6) + (RestoreTimeout * originalPaths.Count);
        if (!thread.Join(budget))
        {
            return [];
        }

        return restored;
    }

    private static List<string> RestoreCore(IReadOnlyList<string> originalPaths)
    {
        var restored = new List<string>();

        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return restored;

        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell is null) return restored;

        dynamic? bin = shell.NameSpace(BitBucket);
        if (bin is null) return restored;

        var wanted = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (object item in bin.Items())
        {
            string location = bin.GetDetailsOf(item, OriginalLocationColumn) as string ?? string.Empty;
            if (location.Length == 0) continue;

            string name = ((dynamic)item).Name as string ?? string.Empty;
            if (name.Length == 0) continue;

            string full = Path.Join(location, name);
            if (!wanted.Contains(full)) continue;

            // 同一路径可能被删过多次，取第一个能还原的即可
            found.TryAdd(full, item);
        }

        foreach (string path in originalPaths)
        {
            if (!found.TryGetValue(path, out object? item)) continue;

            foreach (object verb in RestoreCandidates(item))
            {
                try
                {
                    ((dynamic)verb).DoIt();
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
                {
                    continue;
                }

                // 判定标准永远是"文件回没回来"。InvokeVerb/DoIt 传错名字不会抛异常，
                // 它只是什么都不做——所以调用成功不等于还原成功
                if (!WaitFor(path)) continue;

                restored.Add(path);
                break;
            }
        }

        return restored;
    }

    /// <summary>
    /// 挑出可用于还原的动词。先按词根认名字，认不出来再退回第一项——
    /// 回收站条目的第一项一直就是"还原"。两者都可能失败，所以调用方还要回头验文件。
    /// </summary>
    private static List<object> RestoreCandidates(object item)
    {
        var ordered = new List<object>();
        object? first = null;
        bool firstMatched = false;

        foreach (object verb in ((dynamic)item).Verbs())
        {
            first ??= verb;

            if (!RecycleVerbNames.IsRestore(((dynamic)verb).Name as string)) continue;

            if (ReferenceEquals(verb, first)) firstMatched = true;
            ordered.Add(verb);
        }

        if (first is not null && !firstMatched) ordered.Add(first);

        return ordered;
    }
    /// <summary>等文件真的回到原位。等不到就当这个动词没生效。</summary>
    private static bool WaitFor(string path)
    {
        DateTime deadline = DateTime.UtcNow + RestoreTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) || Directory.Exists(path)) return true;
            Thread.Sleep(120);
        }

        return File.Exists(path) || Directory.Exists(path);
    }
}
