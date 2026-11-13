using System.IO;
using System.Runtime.InteropServices;
using Jianlan.Core.Navigation;

namespace Jianlan.App.Services;

/// <summary>
/// 已知文件夹解析与缓存（FR-1.1 的实现约束）。
///
/// 为什么不用 <see cref="Environment.SpecialFolder"/>：那个枚举里没有 Downloads，
/// 而且用户把桌面/文档重定向到别的盘、或被 OneDrive 接管之后，环境变量给的仍是旧路径。
/// 真路径只有外壳知道，所以问 <c>SHGetKnownFolderPath</c>。
///
/// 为什么还要缓存：这个调用首次会加载 shell32 并读注册表，属于冷启动路径上的不确定项。
/// 按需求"启动从缓存读、检测到变化才刷新"，首帧不碰外壳，校验放到首帧之后的后台任务里。
/// 缓存里没有某项（首次运行 / 新增的 kind）时才在启动路径上直接问一次。
/// </summary>
internal static class KnownFolderService
{
    private static readonly Dictionary<KnownFolderKind, Guid> FolderIds = new()
    {
        [KnownFolderKind.Desktop] = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        [KnownFolderKind.Documents] = new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        [KnownFolderKind.Downloads] = new Guid("374DE290-123F-4565-9164-39C4925E467B"),
        [KnownFolderKind.Pictures] = new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        [KnownFolderKind.Music] = new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"),
        [KnownFolderKind.Videos] = new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        [KnownFolderKind.UserProfile] = new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173"),
    };

    private static IReadOnlyList<KnownFolder> _all = [];
    private static Dictionary<string, string> _cache = [];
    private static bool _initialized;

    /// <summary>路径集合变了（后台校验发现系统改动）。界面据此重建卡片与导航项。</summary>
    public static event Action? Changed;

    /// <summary>常用目录，按界面顺序、已剔除不存在的项。</summary>
    public static IReadOnlyList<KnownFolder> All => _all;

    /// <summary>
    /// 启动期初始化。返回 true 表示缓存被补全/修正过，调用方应落盘。
    ///
    /// 缓存命中（路径仍在）就不调用外壳；未命中才直接问一次——
    /// 不问就永远补不上缓存，而"用户重定向了下载夹"正是这项功能要解决的情况。
    /// </summary>
    public static bool Initialize(Dictionary<string, string> cache)
    {
        _cache = cache ?? [];
        _initialized = true;

        bool dirty = false;
        var resolved = new List<KnownFolder>(KnownFolders.DisplayOrder.Count);

        foreach (KnownFolderKind kind in EnumerateKinds())
        {
            string key = KnownFolders.CacheKey(kind);

            if (_cache.TryGetValue(key, out string? cached) &&
                !string.IsNullOrWhiteSpace(cached) &&
                Directory.Exists(cached))
            {
                if (kind != KnownFolderKind.UserProfile) resolved.Add(new KnownFolder(kind, cached));
                continue;
            }

            string path = QueryShell(kind);
            if (string.IsNullOrEmpty(path)) path = KnownFolders.FallbackPath(kind);

            if (!string.IsNullOrEmpty(path) && !string.Equals(path, cached, StringComparison.OrdinalIgnoreCase))
            {
                _cache[key] = path;
                dirty = true;
            }

            if (!string.IsNullOrEmpty(path) && kind != KnownFolderKind.UserProfile)
            {
                resolved.Add(new KnownFolder(kind, path));
            }
        }

        _all = resolved;
        return dirty;
    }

    /// <summary>
    /// 首帧之后的后台校验（FR-1.1 实现约束"仅在检测到系统变化时刷新"）。
    /// 只问外壳、不判存在性——路径指向一个被拔掉的移动硬盘是合法的，不该把缓存清掉。
    /// 返回 true 表示缓存有改动，调用方应落盘。
    /// </summary>
    public static async Task<bool> RefreshAsync()
    {
        if (!_initialized) return false;

        Dictionary<KnownFolderKind, string> latest;
        try
        {
            latest = await Task.Run(() =>
            {
                var map = new Dictionary<KnownFolderKind, string>();
                foreach (KnownFolderKind kind in EnumerateKinds())
                {
                    string path = QueryShell(kind);
                    if (!string.IsNullOrEmpty(path)) map[kind] = path;
                }
                return map;
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 校验是锦上添花：失败就继续用缓存，不能因为外壳抽风把首帧已有的导航项弄没
            Diagnostics.AppLog.Warn("knownfolders.refresh", "E9001", $"{ex.GetType().Name}: {ex.Message}");  // i18n-ignore：只进 app.log
            return false;
        }

        bool changed = false;
        foreach ((KnownFolderKind kind, string path) in latest)
        {
            string key = KnownFolders.CacheKey(kind);
            if (_cache.TryGetValue(key, out string? cached) &&
                string.Equals(cached, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _cache[key] = path;
            changed = true;
        }

        if (!changed) return false;

        // 重建列表：缓存已更新，Initialize 会全部命中，不会再问外壳
        Initialize(_cache);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 系统「下载」目录。取不到（极少数精简系统）时返回空串，
    /// 由调用方退回"文档 → 用户目录"的兜底链（见 MainWindow.ResolveNewTabPath）。
    /// </summary>
    public static string Downloads()
        => _all.FirstOrDefault(f => f.Kind == KnownFolderKind.Downloads)?.Path
           ?? KnownFolders.FallbackPath(KnownFolderKind.Downloads);

    private static IEnumerable<KnownFolderKind> EnumerateKinds()
        => KnownFolders.DisplayOrder.Append(KnownFolderKind.UserProfile);

    /// <summary>
    /// 问外壳要一次路径。失败码 / 异常一律返回空串——
    /// 这里在启动路径上，任何抛出的异常都会变成"启动失败"。
    /// </summary>
    private static string QueryShell(KnownFolderKind kind)
    {
        if (!FolderIds.TryGetValue(kind, out Guid id)) return string.Empty;

        nint buffer = 0;
        try
        {
            // KF_FLAG_DEFAULT = 0：跟随用户当前的已知文件夹设置。传本地副本：静态只读字段不能按 ref 传
            int hr = SHGetKnownFolderPath(ref id, 0, 0, out buffer);
            if (hr != 0 || buffer == 0) return string.Empty;

            string? path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return string.Empty;
        }
        finally
        {
            if (buffer != 0) Marshal.FreeCoTaskMem(buffer);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}
