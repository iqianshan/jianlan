using System.Runtime.InteropServices;

namespace Lingpane.App.Services;

/// <summary>
/// 已知文件夹解析。目前只用到「下载」。
///
/// 为什么不用 <see cref="Environment.SpecialFolder"/>：那个枚举里没有 Downloads，
/// 而 `Path.Combine(UserProfile, "Downloads")` 在两种很常见的情况下是错的——
/// 用户把下载夹重定向到别的盘，或者被 OneDrive 接管。这两种情况的真路径只有
/// 外壳知道，所以问一次 <c>SHGetKnownFolderPath</c>。
/// </summary>
internal static class KnownFolders
{
    /// <summary>FOLDERID_Downloads = {374DE290-123F-4565-9164-39C4925E467B}</summary>
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>
    /// 系统「下载」目录的绝对路径。取不到（极少数精简系统）时返回空串，
    /// 由调用方退回"文档 → 用户目录"的兜底链。
    /// </summary>
    public static string Downloads()
    {
        nint buffer = 0;
        try
        {
            // KF_FLAG_DEFAULT = 0：跟随用户当前的已知文件夹设置。
            // 传本地副本：静态只读字段不能按 ref 传
            Guid id = DownloadsId;
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
