using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Jianlan.App.Shell;

/// <summary>
/// 系统 Shell 图标（FR-6.5 / 界面规则 9.3）。
///
/// 关键设计：按 <b>扩展名</b> 而非按文件查询图标。
/// 调用 SHGetFileInfo 时带 SHGFI_USEFILEATTRIBUTES，Shell 只按扩展名推断图标，
/// <b>不访问磁盘</b>——这是滚动流畅（R-08）的前提。
///
/// 每种扩展名只解析一次，之后走字典缓存；缓存项已 Freeze，可跨线程读取。
/// </summary>
public static class ShellIconProvider
{
    private const uint SHGFI_ICON = 0x0000_0100;
    private const uint SHGFI_PIDL = 0x0000_0008;
    private const uint SHGFI_SMALLICON = 0x0000_0001;
    private const uint SHGFI_LARGEICON = 0x0000_0000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x0000_0010;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x0000_0080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x0000_0010;

    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>系统图标序号 → 位图。同一图标在 Shell 里只有一个序号，因此天然去重。</summary>
    private static readonly Dictionary<int, ImageSource> IndexCache = new();

    private static ImageSource? _fallback;

    /// <summary>
    /// 32px 图标的独立缓存（预览窗格里那个大图标用）。
    ///
    /// 不与 16px 共用一张表：同一个 key 在两张表里是两个尺寸，混着存会让
    /// "先取过小图标"的路径把大图标也变成 16px——列表里看不出问题，预览里就是一团糊。
    /// </summary>
    private static readonly Dictionary<string, ImageSource> LargeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取 32px 图标。预览窗格里的"不支持的类型"要用它——把 16px 的列表图标放大到 48，
    /// 得到的是一团没有轮廓的白块，还不如不显示。
    /// </summary>
    public static ImageSource GetLarge(string iconKey)
    {
        lock (Gate)
        {
            if (LargeCache.TryGetValue(iconKey, out var cached)) return cached;

            var image = LoadCore(iconKey, SHGFI_LARGEICON) ?? (_fallback ??= CreateFallback());
            LargeCache[iconKey] = image;
            return image;
        }
    }

    /// <summary>
    /// 取图标。<paramref name="iconKey"/> 形如 <c>.txt</c> / <c>.folder</c> / <c>.file</c>。
    /// 未命中时同步解析（微秒级，无磁盘 IO）；失败返回透明占位，绝不抛异常。
    /// </summary>
    public static ImageSource Get(string iconKey)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(iconKey, out var cached)) return cached;

            var image = Load(iconKey) ?? (_fallback ??= CreateFallback());
            Cache[iconKey] = image;
            return image;
        }
    }

    /// <summary>
    /// 按绑定用的 token 取图：<c>#12</c> 表示"按 shell 图标序号"，其余按扩展名。
    /// 序号图必须由 <see cref="ResolvePathIconIndex"/> 先解析好，否则回落通用图标。
    /// </summary>
    public static ImageSource GetByToken(string token)
    {
        if (token.Length > 1 && token[0] == '#')
        {
            lock (Gate)
            {
                if (IndexCache.TryGetValue(int.Parse(token[1..], CultureInfo.InvariantCulture), out var image))
                {
                    return image;
                }
            }
        }

        return Get(token);
    }

    /// <summary>
    /// 按<b>真实路径</b>解析图标序号（会访问磁盘，只能在后台线程调用）。
    /// 返回 -1 表示失败。序号用于去重：几百个普通文件夹返回的是同一个序号，
    /// 只有真正有专属图标的目录才是新序号，因此不会出现"一万个位图"。
    ///
    /// 必须走 PIDL：实测直接传路径给 SHGetFileInfo 对任何目录都只返回通用文件夹图标，
    /// 专属图标（下载/桌面这类已知文件夹、以及 desktop.ini 里声明的图标）
    /// 只有在 shell 命名空间里才解析得出来。
    /// </summary>
    public static int ResolvePathIconIndex(string path, bool isDirectory)
    {
        IntPtr pidl = IntPtr.Zero;

        try
        {
            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            if (hr != 0 || pidl == IntPtr.Zero) return -1;

            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(
                pidl,
                isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_PIDL | SHGFI_ICON | SHGFI_SMALLICON);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero || info.iIcon < 0) return -1;

            try
            {
                lock (Gate)
                {
                    if (!IndexCache.ContainsKey(info.iIcon))
                    {
                        var source = Imaging.CreateBitmapSourceFromHIcon(
                            info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        IndexCache[info.iIcon] = source;
                    }
                }

                return info.iIcon;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception)
        {
            return -1;
        }
        finally
        {
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    /// <summary>
    /// 按<b>扩展名</b>解析图标序号（<c>SHGFI_USEFILEATTRIBUTES</c>，不访问磁盘）。
    ///
    /// 用途是给"按路径解析"做对照：两者序号相同就说明这个文件没有专属图标，
    /// 不必把结果写回界面。文件类型图标序号可能是 0（快捷方式就是），所以失败用 -1 表示。
    /// </summary>
    public static int ResolveExtensionIconIndex(string iconKey)
    {
        bool isDirectory = iconKey.Equals(".folder", StringComparison.OrdinalIgnoreCase);

        string probe = isDirectory
            ? "jianlan.folder"
            : iconKey.Equals(".file", StringComparison.OrdinalIgnoreCase)
                ? "jianlan.file"
                : "jianlan" + iconKey;

        var info = new SHFILEINFO();

        IntPtr result = SHGetFileInfo(
            probe,
            isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return -1;

        DestroyIcon(info.hIcon);
        return info.iIcon;
    }

    /// <summary>通用文件夹图标的序号，用来判断"这个目录没有自定义图标"。</summary>
    public static int ResolveGenericFolderIndex()
    {
        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(
            "jianlan.folder",
            FILE_ATTRIBUTE_DIRECTORY,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return -1;

        DestroyIcon(info.hIcon);
        return info.iIcon;
    }

    private static BitmapSource? Load(string iconKey) => LoadCore(iconKey, SHGFI_SMALLICON);

    private static BitmapSource? LoadCore(string iconKey, uint sizeFlag)
    {
        bool isDirectory = iconKey.Equals(".folder", StringComparison.OrdinalIgnoreCase);

        // USEFILEATTRIBUTES 模式下路径本身不参与判定，只有扩展名有意义
        string probe = isDirectory
            ? "jianlan.folder"
            : iconKey.Equals(".file", StringComparison.OrdinalIgnoreCase)
                ? "jianlan.file"
                : "jianlan" + iconKey;

        uint attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var info = new SHFILEINFO();

        IntPtr result = SHGetFileInfo(probe, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | sizeFlag | SHGFI_USEFILEATTRIBUTES);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static BitmapSource CreateFallback()
    {
        var bitmap = BitmapSource.Create(
            1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        bitmap.Freeze();
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(
        IntPtr pidl,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
