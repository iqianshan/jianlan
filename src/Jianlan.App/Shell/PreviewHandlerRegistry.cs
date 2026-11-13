using System.IO;
using Microsoft.Win32;

namespace Jianlan.App.Shell;

/// <summary>
/// 查"这个扩展名有没有可用的预览处理器"（FR-9.1.3）。
///
/// 三件必须做的事，缺一个就会出现"明明装了处理器却只看到缩略图"：
/// <list type="number">
/// <item>两个注册表视图都查。中文环境下多数处理器（搜狗 PDF、金山看图、WPS 压缩包）
///       是 32 位 DLL，只注册在 <c>Wow6432Node</c> 下；64 位视图里什么都没有。</item>
/// <item>校验 CLSID 真的存在。卸载残留很常见（本机的 <c>.docx</c> 就指向一个已不存在的
///       CLSID），不校验的话每次选中都要白跑一次激活失败。</item>
/// <item>把失败记下来。处理器加载崩溃/报错后不要每次选中都再试一遍——
///       同一个扩展名在本会话里失败一次就退到缩略图。</item>
/// </list>
/// </summary>
internal static class PreviewHandlerRegistry
{
    private static readonly Dictionary<string, Guid> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Broken = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>这个文件有没有可用的处理器。没有返回 <see cref="Guid.Empty"/>。</summary>
    public static Guid Find(string? path)
    {
        string extension = SafeExtension(path);
        if (extension.Length == 0) return Guid.Empty;

        lock (Gate)
        {
            if (Broken.Contains(extension)) return Guid.Empty;
            if (Cache.TryGetValue(extension, out Guid cached)) return cached;

            Guid clsid = Lookup(extension);
            Cache[extension] = clsid;
            return clsid;
        }
    }

    /// <summary>
    /// 处理器在这个扩展名上没能干活（激活失败、初始化失败、DoPreview 返回错误）。
    ///
    /// 按扩展名而不是完整路径记：同一类文件的行为是一起的，为某一份坏文件把整类都禁掉
    /// 会误伤；反过来按路径记又会在翻文件夹时反复重试同一个类型。
    /// </summary>
    public static void MarkBroken(string? path)
    {
        string extension = SafeExtension(path);
        if (extension.Length == 0) return;

        lock (Gate)
        {
            Broken.Add(extension);
            Cache[extension] = Guid.Empty;
        }
    }

    private static Guid Lookup(string extension)
    {
        string subKey = extension + @"\ShellEx\" + PreviewHandlerInterop.HandlerKeyName;

        // 64 位优先：同进程内加载，没有跨进程往返；没有才退到 32 位（经 COM 代理）
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                using RegistryKey? handlerKey = classes.OpenSubKey(subKey);

                if (handlerKey?.GetValue(string.Empty) is not string text) continue;
                if (!Guid.TryParse(text, out Guid clsid)) continue;

                // CLSID 不存在 = 卸载残留，激活必然是 REGDB_E_CLASSNOTREG
                using RegistryKey? classKey = classes.OpenSubKey(@"CLSID\" + clsid.ToString("B"));
                if (classKey is null) continue;

                return clsid;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // 注册表读不到：当成没有处理器，不影响其它功能
            }
        }

        return Guid.Empty;
    }

    private static string SafeExtension(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        try
        {
            return Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
