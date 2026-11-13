using System.Security.Cryptography;
using System.Text;

namespace Jianlan.Core.Preview;

/// <summary>
/// 缩略图的磁盘缓存（FR-6.6 / FR-5.3.5）。
///
/// 为什么必须有它：缩略图来自 <c>IShellItemImageFactory</c>，一次调用要跨进程问 Shell，
/// 图片还要解码。没有缓存的话，每次回到同一个目录都要重来一遍——而"回到同一个目录"
/// 是文件管理里最高频的动作。
///
/// 键里带上大小与修改时间：文件被覆盖后，旧缩略图必须失效，否则用户会看到一张
/// 已经不存在的画面，比没有缩略图更糟。
/// </summary>
public static class ThumbnailCache
{
    /// <summary>
    /// 缓存上限默认 500MB（FR-5.3.5）。与 <c>Settings.Privacy.CacheLimitMb</c> 的默认值是同一个数，
    /// 改一个必须改另一个——这里只是"设置还没读到时"的兜底。
    /// </summary>
    public const long DefaultCapBytes = 500L << 20;

    /// <summary>淘汰到上限的九成，避免"刚删完又写满、每分钟淘汰一次"。</summary>
    private const double TrimTarget = 0.9;

    private static readonly object Gate = new();
    private static readonly string RootPath = ResolveRoot();
    private static int _writesSinceTrim;
    private static long _capBytes = DefaultCapBytes;

    /// <summary>缓存目录。<c>%LOCALAPPDATA%\Jianlan\cache\thumbs</c>。</summary>
    public static string Root => RootPath;

    /// <summary>
    /// 生效中的上限（FR-5.3.5）。设置页改上限时由 App 层赋值，自动淘汰按它算。
    ///
    /// 不用构造函数注入：缓存是"到处都在写"的静态设施，缩略图服务、预览、
    /// 列表滚动都会调用，把上限沿调用链传下去只会让每个调用点多一个参数。
    /// </summary>
    public static long CapBytes
    {
        get => Interlocked.Read(ref _capBytes);
        set => Interlocked.Exchange(ref _capBytes, value > 0 ? value : DefaultCapBytes);
    }

    /// <summary>
    /// 缓存键：路径 + 大小 + 修改时间 + 目标像素。
    ///
    /// 用 SHA1 而不是直接拼路径：路径里有大小写、非法字符与长度上限（260），
    /// 拼出来的文件名既可能超长也可能撞车。
    /// </summary>
    public static string KeyFor(string path, long size, long modifiedTicks, int pixels)
    {
        string raw = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{path.ToLowerInvariant()}|{size}|{modifiedTicks}|{pixels}");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>键对应的文件路径。分两级目录：一级目录放十万个文件时，枚举会明显变慢。</summary>
    public static string PathFor(string key) => Path.Combine(RootPath, key[..2], key + ".png");

    /// <summary>读缓存。命中返回 true 并给出字节；任何 IO 问题都当成未命中。</summary>
    public static bool TryRead(string key, out byte[] data)
    {
        data = [];

        try
        {
            string path = PathFor(key);
            if (!File.Exists(path)) return false;

            data = File.ReadAllBytes(path);
            return data.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 写缓存。先写临时文件再改名：直接写目标文件时，进程刚好在这时退出会留下
    /// 半张图，下次读出来就是一条损坏的缩略图。
    /// </summary>
    public static void Write(string key, byte[] data)
    {
        try
        {
            string path = PathFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temp = path + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);

            if (Interlocked.Increment(ref _writesSinceTrim) >= 200)
            {
                Interlocked.Exchange(ref _writesSinceTrim, 0);
                _ = Task.Run(() => Trim());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去不影响预览，下次重新生成即可
        }
    }

    /// <summary>当前占用字节数。</summary>
    public static long Measure()
    {
        long total = 0;

        try
        {
            if (!Directory.Exists(RootPath)) return 0;

            foreach (string file in Directory.EnumerateFiles(RootPath, "*.png", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // 正好被删掉
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return total;
        }

        return total;
    }

    /// <summary>
    /// 超上限时按"最久未写"淘汰到九成。
    ///
    /// 用写入时间而不是访问时间：Windows 默认关闭最后访问时间的更新，
    /// 拿它做 LRU 会得到一堆同样时间戳，淘汰顺序变成随机的。
    /// 缩略图重新生成的代价只有一次 Shell 调用，按写入时间淘汰已经够好。
    /// </summary>
    public static void Trim(long capBytes = 0)
    {
        lock (Gate)
        {
            try
            {
                if (!Directory.Exists(RootPath)) return;

                var files = new List<CacheEntry>();

                foreach (string path in Directory.EnumerateFiles(RootPath, "*.png", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(path);
                        files.Add(new CacheEntry(info.FullName, info.Length, info.LastWriteTimeUtc));
                    }
                    catch (IOException)
                    {
                        // 并发删除
                    }
                }

                foreach (CacheEntry doomed in PlanTrim(files, capBytes))
                {
                    try
                    {
                        File.Delete(doomed.Path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 正被占用：跳过，下一轮再说
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 缓存清理失败不该影响任何可见功能
            }
        }
    }

    /// <summary>
    /// 算出该删哪些文件。返回空列表表示不用删。
    ///
    /// 单拎成纯函数是为了能测："淘汰到九成"以前只能在磁盘上造 500MB 缩略图才验得了，
    /// 而 FR-5.3.5 让上限变成用户可调的数字，边界会被人一个个试过去，得有断言兜着。
    /// 规则与 <see cref="Trim"/> 完全一致，只是不碰磁盘。
    /// </summary>
    public static IReadOnlyList<CacheEntry> PlanTrim(IReadOnlyList<CacheEntry> files, long capBytes)
    {
        if (files.Count == 0) return [];

        long cap = capBytes > 0 ? capBytes : CapBytes;

        long total = 0;
        foreach (CacheEntry file in files) total += file.Size;

        if (total <= cap) return [];

        long target = (long)(cap * TrimTarget);

        // OrderBy 是稳定排序：同一次写入批次里的文件时间戳常常一样，
        // 稳定排序让"同样的输入得到同样的结果"，否则测试会随机失败。
        var ordered = files.OrderBy(static f => f.LastWriteUtc).ToList();

        var doomed = new List<CacheEntry>();

        foreach (CacheEntry file in ordered)
        {
            if (total <= target) break;

            total -= file.Size;
            doomed.Add(file);
        }

        return doomed;
    }

    /// <summary>清空缓存（设置页的"清除"按钮）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 同上：清不掉就说清不掉，不要抛到界面上
            }
        }
    }

    private static string ResolveRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Jianlan", "cache", "thumbs");
    }
}

/// <summary>缓存目录里的一个文件。<see cref="ThumbnailCache.PlanTrim"/> 的输入单位。</summary>
public readonly record struct CacheEntry(string Path, long Size, DateTime LastWriteUtc);
