using System.IO;

namespace Lingpane.Core.Preview;

/// <summary>
/// 预览内容的处理路径（FR-9.1.3 / FR-9.2.1）。
///
/// <see cref="Shell"/> 不是"没有预览"，而是"交给系统取图"：PDF 首页、视频首帧、
/// Office 文档缩略图都由 Shell 的缩略图提供者产出。区别在于它不是我们渲染的，
/// 失败率与耗时都不受控，因此超时与降级的防线全在这一路上。
/// </summary>
public enum PreviewKind
{
    /// <summary>目录、快捷方式、没有任何可展示内容的类型。</summary>
    None,

    /// <summary>纯文本 / 代码，内置读取渲染。</summary>
    Text,

    /// <summary>位图，内置解码渲染。</summary>
    Image,

    /// <summary>交给 Shell 取缩略图（PDF、Office、音视频、其它注册了提供者的类型）。</summary>
    Shell,
}

/// <summary>
/// 按扩展名决定一个文件走哪条预览路径，并给出体积闸门（FR-9.2.5）。
///
/// 只做字符串判断，不碰磁盘：选中项在高频变化，分类必须能在 UI 线程上按毫秒级完成。
/// 真正的"这个文件是不是二进制"要等读到内容才知道，那一步在
/// <see cref="LooksBinary"/> 里，由读取方拿着前几 KB 调用。
/// </summary>
public static class PreviewClassifier
{
    /// <summary>文本上限 1MB（FR-9.2.5）。再大就不是"看一眼"，而是拿预览窗格当编辑器用。</summary>
    public const long MaxTextBytes = 1L << 20;

    /// <summary>图片上限 50MB（FR-9.2.5）。</summary>
    public const long MaxImageBytes = 50L << 20;

    /// <summary>PDF 上限 100MB（FR-9.2.5）。</summary>
    public const long MaxDocumentBytes = 100L << 20;

    /// <summary>判定为二进制所需的 NUL 字节数。单个 NUL 就够——正常文本里不该出现。</summary>
    private const int BinarySampleBytes = 4096;

    /// <summary>
    /// 文本类扩展名。故意不收 <c>.svg</c>：它是 XML，但用户要看的几乎总是图形本身，
    /// 当文本铺出来反而不如走 Shell 拿缩略图。也不收 <c>.webp</c>——
    /// WPF 的解码器不带 WebP，当成图片会在渲染阶段失败，同样交给 Shell。
    /// </summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".rst",
        ".json", ".jsonc", ".xml", ".xsd", ".xsl", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".properties",
        ".csv", ".tsv",
        ".cs", ".vb", ".fs", ".java", ".kt", ".py", ".rb", ".go", ".rs", ".php", ".pl", ".lua", ".r",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hxx", ".m", ".mm", ".swift", ".dart",
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".vue", ".svelte",
        ".html", ".htm", ".css", ".scss", ".sass", ".less",
        ".sql", ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".reg",
        ".csproj", ".props", ".targets", ".sln", ".slnx", ".editorconfig", ".gitignore", ".gitattributes",
        ".dockerfile", ".gradle", ".cmake", ".makefile", ".diff", ".patch",
    };

    /// <summary>
    /// 位图扩展名。全部是 WPF 自带解码器能处理的格式，因此可以放心走"内置解码 + 限幅"，
    /// 不必依赖 Shell（少一次 COM 往返，也少了第三方解码器的崩溃面）。
    /// </summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico", ".wdp", ".jxr",
    };

    /// <summary>PDF 是唯一设了体积闸门的 Shell 类型——单页解析量随文件线性增长，最坏情况会吃满预算。</summary>
    private const string PdfExtension = ".pdf";

    /// <summary>判定一个条目走哪条预览路径。目录一律 <see cref="PreviewKind.None"/>。</summary>
    public static PreviewKind Classify(string? path, long size, bool isDirectory)
    {
        if (isDirectory || string.IsNullOrEmpty(path)) return PreviewKind.None;

        string extension = Path.GetExtension(path);
        if (extension.Length == 0) return PreviewKind.None;

        if (TextExtensions.Contains(extension)) return PreviewKind.Text;
        if (ImageExtensions.Contains(extension)) return PreviewKind.Image;

        return PreviewKind.Shell;
    }

    /// <summary>
    /// 该类型的体积上限，<c>0</c> 表示不设限。
    ///
    /// 只有文本、图片、PDF 三档在需求里给了数字（FR-9.2.5）。其余类型不给闸门：
    /// 视频动辄几个 GB，但那正是最需要预览的东西，用体积挡掉等于功能失效；
    /// 它们由 2s 超时兜底（FR-9.2.3），超时就显示基本信息。
    /// </summary>
    public static long LimitFor(PreviewKind kind, string? path)
    {
        return kind switch
        {
            PreviewKind.Text => MaxTextBytes,
            PreviewKind.Image => MaxImageBytes,
            PreviewKind.Shell when string.Equals(
                Path.GetExtension(path ?? string.Empty), PdfExtension, StringComparison.OrdinalIgnoreCase)
                => MaxDocumentBytes,
            _ => 0,
        };
    }

    /// <summary>是否超过该类型的体积上限。</summary>
    public static bool IsTooLarge(PreviewKind kind, string? path, long size)
    {
        long limit = LimitFor(kind, path);
        return limit > 0 && size > limit;
    }

    /// <summary>
    /// 前几 KB 的内容像不像二进制。
    ///
    /// 只看 NUL：扩展名说它是文本、内容是二进制的情况，最常见的来源是
    /// 改错了扩展名的压缩包/可执行文件，那类文件头部必有 NUL。反过来，
    /// 按"可打印字符占比"判定会把中文、表情符号所在的多字节 UTF-8 误杀——
    /// 那些字节在 ASCII 区间外，却完全正常。
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        int length = Math.Min(sample.Length, BinarySampleBytes);

        for (int i = 0; i < length; i++)
        {
            if (sample[i] == 0) return true;
        }

        return false;
    }
}
