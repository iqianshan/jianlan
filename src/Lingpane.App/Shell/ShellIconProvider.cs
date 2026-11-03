using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lingpane.App.Shell;

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
    private const uint SHGFI_SMALLICON = 0x0000_0001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x0000_0010;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x0000_0080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x0000_0010;

    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static ImageSource? _fallback;

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

    private static BitmapSource? Load(string iconKey)
    {
        bool isDirectory = iconKey.Equals(".folder", StringComparison.OrdinalIgnoreCase);

        // USEFILEATTRIBUTES 模式下路径本身不参与判定，只有扩展名有意义
        string probe = isDirectory
            ? "lingpane.folder"
            : iconKey.Equals(".file", StringComparison.OrdinalIgnoreCase)
                ? "lingpane.file"
                : "lingpane" + iconKey;

        uint attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var info = new SHFILEINFO();

        IntPtr result = SHGetFileInfo(probe, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
