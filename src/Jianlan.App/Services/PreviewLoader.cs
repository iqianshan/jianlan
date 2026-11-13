using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Jianlan.App.Shell;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Preview;

namespace Jianlan.App.Services;

/// <summary>
/// 预览取不到内容的原因。机器可判定的原因用枚举而不是现成文案：
/// 文案要随界面语言切换（FR-10.x），而取内容这一层不该知道当前是哪种语言。
/// </summary>
internal enum PreviewNote
{
    /// <summary>有内容可展示。</summary>
    None,

    /// <summary>超过该类型的体积上限（FR-9.2.5）。</summary>
    TooLarge,

    /// <summary>扩展名说是文本，内容却是二进制。</summary>
    Binary,

    /// <summary>读盘失败：被占用、权限不足、文件已消失。</summary>
    Unreadable,

    /// <summary>渲染超时（FR-9.2.3）。</summary>
    Timeout,

    /// <summary>系统里没有这个类型可用的缩略图提供者。</summary>
    NoThumbnail,
}

/// <summary>
/// 一次预览的产物。<see cref="Image"/> 与 <see cref="Text"/> 至多有一个非空。
/// </summary>
internal sealed class PreviewResult
{
    public PreviewNote Note { get; init; }

    public string? Text { get; init; }

    /// <summary>文本被截断（超过 <see cref="PreviewLoader.MaxTextChars"/>）。</summary>
    public bool Truncated { get; init; }

    public BitmapSource? Image { get; init; }

    /// <summary><see cref="PreviewNote.TooLarge"/> 时该类型的阈值，用于把数字告诉用户。</summary>
    public long LimitBytes { get; init; }

    /// <summary>解码/读入占用的估算字节数，用于对照预览内存预算（FR-9.2.7）。</summary>
    public long EstimatedBytes { get; init; }

    public static PreviewResult Fail(PreviewNote note, long limitBytes = 0)
        => new() { Note = note, LimitBytes = limitBytes };
}

/// <summary>
/// 把"选中一个文件"变成"可以画出来的一屏内容"（FR-9.1.3）。
///
/// 三条铁律：
/// <list type="number">
/// <item>任何失败都不抛异常，一律折成 <see cref="PreviewNote"/>。预览是辅助动作，
///       它的失败不该让选中动作报错。</item>
/// <item>每次调用都吃一个 <see cref="CancellationToken"/>（FR-9.2.4）。用户快速上下键时
///       会连续发起多次，旧结果回来时新结果已经在屏幕上，必须让它自己作废。</item>
/// <item>不读全图、不全量读文件。图片按窗格宽度限幅解码，文本最多读
///       <see cref="MaxTextChars"/> 个字符——预览窗格不是编辑器。</item>
/// </list>
/// </summary>
internal static class PreviewLoader
{
    /// <summary>单次渲染上限（FR-9.2.3）。超时显示"无法预览"而不是无限等待。</summary>
    public const int TimeoutMs = 2000;

    /// <summary>
    /// 文本最多展示的字符数。
    ///
    /// 需求给的上限是"文件 ≤1MB"，但 1MB 纯文本铺进一个 TextBox 会有几十万个字符，
    /// 排版与滚动都要几百毫秒，还占去十几 MB 托管堆。截到 20 万字符足够看清
    /// "这是什么文件"，超出部分给一句提示。
    /// </summary>
    public const int MaxTextChars = 200_000;

    /// <summary>图片解码的最大高度（逻辑像素）。宽度由窗格宽度决定，高度给一个上限防止长条图撑爆内存。</summary>
    public const int MaxImageHeight = 1600;

    public static async Task<PreviewResult> LoadAsync(
        FileEntry entry, int decodeWidth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        PreviewKind kind = PreviewClassifier.Classify(entry.FullPath, entry.Size, entry.IsDirectory);

        if (kind == PreviewKind.None) return PreviewResult.Fail(PreviewNote.NoThumbnail);

        if (PreviewClassifier.IsTooLarge(kind, entry.FullPath, entry.Size))
        {
            return PreviewResult.Fail(
                PreviewNote.TooLarge, PreviewClassifier.LimitFor(kind, entry.FullPath));
        }

        int width = Math.Clamp(decodeWidth, 64, 1024);

        return kind switch
        {
            PreviewKind.Text => await WithTimeoutAsync(
                () => Task.Run(() => ReadText(entry, cancellationToken), cancellationToken),
                PreviewResult.Fail(PreviewNote.Timeout)).ConfigureAwait(false),

            PreviewKind.Image => await WithTimeoutAsync(
                () => Task.Run(() => DecodeImage(entry, width, cancellationToken), cancellationToken),
                PreviewResult.Fail(PreviewNote.Timeout)).ConfigureAwait(false),

            _ => await WithTimeoutAsync(
                () => LoadThumbnailAsync(entry, width),
                PreviewResult.Fail(PreviewNote.Timeout)).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// 超时护栏。超时后不等后台任务——它可能卡在第三方解码器里，
    /// 强行等待等于把超时做成假的。让它自己跑完，返回值直接丢掉。
    /// </summary>
    private static async Task<PreviewResult> WithTimeoutAsync(
        Func<Task<PreviewResult>> factory, PreviewResult onTimeout)
    {
        Task<PreviewResult> work = factory();

        Task finished = await Task.WhenAny(work, Task.Delay(TimeoutMs)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, work)) return onTimeout;

        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PreviewResult.Fail(PreviewNote.Timeout);
        }
        catch (Exception)
        {
            return PreviewResult.Fail(PreviewNote.Unreadable);
        }
    }

    private static async Task<PreviewResult> LoadThumbnailAsync(FileEntry entry, int width)
    {
        BitmapSource? image = await ShellThumbnail.GetAsync(entry.FullPath, width).ConfigureAwait(false);

        if (image is null) return PreviewResult.Fail(PreviewNote.NoThumbnail);

        return new PreviewResult
        {
            Image = image,
            EstimatedBytes = (long)image.PixelWidth * image.PixelHeight * 4,
        };
    }

    // ── 文本 ────────────────────────────────────────────────────────────────

    private static PreviewResult ReadText(FileEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            // FileShare.ReadWrite | Delete：正在被别的程序写的日志也要能看，
            // 独占打开会让"预览一个正在下载的文件"直接失败。
            using var stream = new FileStream(
                entry.FullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

            byte[] head = new byte[(int)Math.Min(entry.Size, 4096)];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            // UTF-16 文本里全是 NUL，必须先认 BOM 再判断二进制，否则打开一个 .txt 会被告知"是二进制文件"
            if (!HasTextBom(head.AsSpan(0, read)) && PreviewClassifier.LooksBinary(head.AsSpan(0, read)))
            {
                return PreviewResult.Fail(PreviewNote.Binary);
            }

            stream.Position = 0;

            using var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: false);

            var builder = new StringBuilder(Math.Min(MaxTextChars, 64 * 1024));
            bool truncated = false;

            while (builder.Length < MaxTextChars)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int next = reader.Read();
                if (next < 0) break;

                builder.Append((char)next);
            }

            // 填满了再看一眼：还有一个字符就说明被截断了，否则刚好读到底也是"完整"
            if (builder.Length >= MaxTextChars && reader.Peek() >= 0) truncated = true;

            string text = builder.ToString();
            if (text.Length == 0) return PreviewResult.Fail(PreviewNote.Binary);

            return new PreviewResult
            {
                Text = text,
                Truncated = truncated,
                EstimatedBytes = (long)text.Length * 2,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return PreviewResult.Fail(PreviewNote.Unreadable);
        }
    }

    private static bool HasTextBom(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
        {
            return true;
        }

        return head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    // ── 图片 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 解码位图，按"同时塞进窗格宽高"的比例限幅。
    ///
    /// 只设 <c>DecodePixelWidth</c> 是不够的：一张 100×20000 的长条图在宽 512 时
    /// 高会变成 102400，512×102400×4 就是 200MB——一次预览就能吃掉整档内存预算。
    /// 因此先读头部尺寸（只读元数据，不解码像素），再决定限哪一边。
    /// </summary>
    private static PreviewResult DecodeImage(FileEntry entry, int maxWidth, CancellationToken cancellationToken)
    {
        try
        {
            (int pixelWidth, int pixelHeight) = ReadImageSize(entry.FullPath);
            if (pixelWidth <= 0 || pixelHeight <= 0) return PreviewResult.Fail(PreviewNote.Unreadable);

            double scale = Math.Min(
                1.0,
                Math.Min(maxWidth / (double)pixelWidth, MaxImageHeight / (double)pixelHeight));

            int targetWidth = Math.Max(1, (int)Math.Round(pixelWidth * scale));

            cancellationToken.ThrowIfCancellationRequested();

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

            if (targetWidth < pixelWidth)
            {
                bitmap.DecodePixelWidth = targetWidth;
            }

            // 用流而不是 UriSource：UriSource 在文件被独占或路径含特殊字符时会静默失败
            using (var stream = new FileStream(
                entry.FullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }

            bitmap.Freeze();

            long estimate = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;

            return new PreviewResult { Image = bitmap, EstimatedBytes = estimate };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return PreviewResult.Fail(PreviewNote.Unreadable);
        }
    }

    /// <summary>
    /// 只读图片头部取像素尺寸。<c>DelayCreation</c> + <c>CacheOption.None</c> 让解码器
    /// 只解析头，不解像素——这一步的耗时与文件大小无关，几十毫秒封顶。
    /// </summary>
    private static (int Width, int Height) ReadImageSize(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

        BitmapDecoder decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

        if (decoder.Frames.Count == 0) return (0, 0);

        BitmapFrame frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>把字节数说成人话（走 <see cref="FileEntry.FormatSizeText"/>，与列表里的大小列一致）。</summary>
    public static string DescribeSize(long bytes) => FileEntry.FormatSizeText(bytes);

    /// <summary>阈值提示里用的"1 MB"这类写法：不带小数、按当前区域格式化。</summary>
    public static string DescribeLimit(long bytes) => bytes switch
    {
        >= 1L << 20 => string.Format(
            CultureInfo.CurrentCulture, "{0} MB", bytes >> 20),
        >= 1L << 10 => string.Format(
            CultureInfo.CurrentCulture, "{0} KB", bytes >> 10),
        _ => string.Format(CultureInfo.CurrentCulture, "{0} B", bytes),
    };
}
