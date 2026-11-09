using System.IO;
using System.Windows.Media.Imaging;
using Lingpane.App.Shell;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Preview;

namespace Lingpane.App.Services;

/// <summary>
/// 列表行内的缩略图（FR-6.6）。
///
/// 需求这一条有三句话，每一句都对应这里的一个机制：
/// <list type="bullet">
/// <item><b>异步加载</b> —— 一切走 <see cref="Task"/>，UI 线程只贴结果；</item>
/// <item><b>滚动时不发起请求</b> —— <see cref="Pause"/> / <see cref="Resume"/> 把请求压在
///       <see cref="WaitIdleAsync"/> 上；滚起来的时候一个 Shell 调用都不发，
///       否则"滚动就是白屏"（跨进程调用排满 UI 线程的空档）；</item>
/// <item><b><c>IShellItemImageFactory</c> 提取</b> —— 交给 <see cref="ShellThumbnail"/>，
///       它自带独立 STA 线程与超时。</item>
/// </list>
///
/// 三层缓存：内存（本类）→ 磁盘（<see cref="ThumbnailCache"/>）→ Shell。
/// 内存这层最要紧：同一目录上下滚动时，反复经过的几百行不该重复问磁盘。
/// </summary>
internal static class ThumbnailService
{
    /// <summary>生成的位图像素宽。24 逻辑像素在 4K/200% 缩放下要 96 才不糊。</summary>
    public const int Pixels = 96;

    /// <summary>同时最多几个 Shell 调用。太多会让滚动时的输入被排队拖慢。</summary>
    private const int MaxConcurrent = 3;

    /// <summary>内存里最多留这么多张。一张 96×96 的位图约 36KB，512 张不到 20MB。</summary>
    private const int MemoryCapacity = 512;

    private static readonly SemaphoreSlim Limiter = new(MaxConcurrent);
    private static readonly Dictionary<string, BitmapSource> Memory = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> Recent = new();
    private static readonly Dictionary<string, Task<BitmapSource?>> InFlight = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    private static TaskCompletionSource _idle = Completed();

    /// <summary>开始滚动：把等待中的请求压住。</summary>
    public static void Pause()
    {
        lock (Gate)
        {
            if (!_idle.Task.IsCompleted) return;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>滚动停下。等在哪里的请求会一起醒来。</summary>
    public static void Resume()
    {
        TaskCompletionSource? waiting = null;

        lock (Gate)
        {
            if (_idle.Task.IsCompleted) return;

            waiting = _idle;
            _idle = Completed();
        }

        waiting.TrySetResult();
    }

    /// <summary>滚着的时候挂住，停下再走。</summary>
    public static Task WaitIdleAsync()
    {
        lock (Gate)
        {
            return _idle.Task;
        }
    }

    /// <summary>
    /// 取一张缩略图。返回 null 表示这个文件没有可用的缩略图（调用方回落到图标）。
    /// 不抛异常：缩略图是锦上添花，它的失败不该让行渲染出错。
    /// </summary>
    public static async Task<BitmapSource?> LoadAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        if (!PreviewClassifier.SupportsThumbnail(entry.FullPath)) return null;

        // 网络路径不出缩略图：一个 1000 项的目录等于 1000 次跨网 SMB 往返，
        // 而缩略图只是"更好认"，不值得让它把列表拖成幻灯片。图标是本地生成的，零成本。
        if (PathRules.IsUncPath(entry.FullPath)) return null;

        string key = ThumbnailCache.KeyFor(
            entry.FullPath, entry.Size, entry.LastWriteTime.ToUniversalTime().Ticks, Pixels);

        Task<BitmapSource?>? existing = null;

        lock (Gate)
        {
            if (Memory.TryGetValue(key, out BitmapSource? cached)) return cached;

            // 同一张图可能被两行同时请求（重命名时新旧两行并存），让后来的等同一个任务
            InFlight.TryGetValue(key, out existing);
        }

        if (existing is not null) return await existing;

        Task<BitmapSource?> work = LoadCoreAsync(entry, key, cancellationToken);

        lock (Gate)
        {
            InFlight[key] = work;
        }

        try
        {
            return await work;
        }
        finally
        {
            lock (Gate)
            {
                InFlight.Remove(key);
            }
        }
    }

    private static async Task<BitmapSource?> LoadCoreAsync(
        FileEntry entry, string key, CancellationToken cancellationToken)
    {
        // 磁盘命中：解码一次，比跨进程问 Shell 快一个数量级
        if (ThumbnailCache.TryRead(key, out byte[] data))
        {
            BitmapSource? fromDisk = Decode(data);
            if (fromDisk is not null) return Remember(key, fromDisk);
        }

        await Limiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            BitmapSource? image = await ShellThumbnail.GetAsync(entry.FullPath, Pixels).ConfigureAwait(false);
            if (image is null) return null;

            // 写盘放在 UI 之外：PNG 编码一张 96×96 约几毫秒，几百行叠加起来就是可见的卡顿
            byte[]? encoded = await Task.Run(() => Encode(image), cancellationToken).ConfigureAwait(false);
            if (encoded is not null) ThumbnailCache.Write(key, encoded);

            return Remember(key, image);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Limiter.Release();
        }
    }

    private static BitmapSource Remember(string key, BitmapSource image)
    {
        lock (Gate)
        {
            if (!Memory.TryAdd(key, image)) return image;

            Recent.AddLast(key);
            while (Recent.Count > MemoryCapacity)
            {
                Memory.Remove(Recent.First!.Value);
                Recent.RemoveFirst();
            }
        }

        return image;
    }

    private static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = new MemoryStream(data);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // 缓存文件损坏（上次写到一半断电）：当成未命中，重新生成
            return null;
        }
    }

    private static byte[]? Encode(BitmapSource image)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>关窗格/换目录时清掉内存层。磁盘层留着——它才是"再回来一次不用重算"的价值。</summary>
    public static void ClearMemory()
    {
        lock (Gate)
        {
            Memory.Clear();
            Recent.Clear();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
