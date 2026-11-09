using Lingpane.Core.FileSystem;
using Lingpane.Core.Tagging;

namespace Lingpane.Core.Tests;

/// <summary>
/// 标签库（FR-4.2）。重点覆盖三件容易出错的事：
/// 追加日志的折叠语义、坏行与整份损坏的容错、以及"FileId 拿不到时拒绝写入"。
/// </summary>
public sealed class TagStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lingpane-tag-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_dir, "tags.jsonl");

    public TagStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private TagStore Open(int max = TagStore.MaxRecords, int compactMin = 2000) => new(File_, max, compactMin);

    private static TagTarget Target(ulong id, string path, ulong volume = 7)
        => new(volume, id, path);

    [Fact]
    public void 首次打开不读盘也不建文件()
    {
        TagStore store = Open();

        Assert.False(store.IsLoaded);
        Assert.False(File.Exists(File_));

        store.EnsureLoaded();
        Assert.True(store.IsLoaded);
        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void 打标签后能读回来且按FileId匹配()
    {
        TagStore store = Open();
        Assert.Equal(TagSetResult.Applied, store.Set(Target(11, @"D:\a\报告.docx"), TagColors.Red));

        // 换路径、换卷也都还在：路径只是线索，键是 (卷, FileId)
        TagStore reloaded = Open();
        reloaded.EnsureLoaded();

        Assert.True(reloaded.TryGet(7, 11, out TagColors colors));
        Assert.Equal(TagColors.Red, colors);
        Assert.Equal(@"D:\a\报告.docx", Assert.Single(reloaded.Snapshot()).Path);
    }

    [Fact]
    public void 重命名后按FileId仍能查到标签()
    {
        TagStore store = Open();
        store.Set(Target(11, @"D:\a\旧名.txt"), TagColors.Blue);

        // 重命名只换路径，FileId 不变（实测行为，见 docs/fileid-enumeration.md §2.3）。
        // 此时按旧路径找不到，按 FileId 必须找得到——这正是选 FileId 当主键的理由。
        TagStore reloaded = Open();
        reloaded.EnsureLoaded();
        Assert.True(reloaded.TryGet(7, 11, out TagColors colors));
        Assert.Equal(TagColors.Blue, colors);
    }

    [Fact]
    public void 覆盖设置同一项只留最后一条()
    {
        TagStore store = Open();
        store.Set(Target(11, @"D:\a.txt"), TagColors.Red);
        store.Set(Target(11, @"D:\a.txt"), TagColors.Green);

        TagStore reloaded = Open();
        reloaded.EnsureLoaded();

        Assert.Equal(1, reloaded.Count);
        Assert.Equal(TagColors.Green, reloaded.Get(7, 11));
    }

    [Fact]
    public void 移除后写一条删除行并且读回来是干净的()
    {
        TagStore store = Open();
        store.Set(Target(11, @"D:\a.txt"), TagColors.Red);
        Assert.True(store.Remove(7, 11));
        Assert.Equal(0, store.Count);

        TagStore reloaded = Open();
        reloaded.EnsureLoaded();
        Assert.Equal(0, reloaded.Count);
    }

    [Fact]
    public void 批量设置只落一次盘且全部生效()
    {
        TagStore store = Open();
        List<TagTarget> targets = [.. Enumerable.Range(1, 20).Select(i => Target((ulong)i, $@"D:\f{i}.txt"))];

        Assert.Equal(TagSetResult.Applied, store.SetMany(targets, TagColors.Purple));

        TagStore reloaded = Open();
        reloaded.EnsureLoaded();
        Assert.Equal(20, reloaded.Count);
    }

    [Fact]
    public void 多色组合能原样存回()
    {
        TagStore store = Open();
        store.Set(Target(11, @"D:\a.txt"), TagColors.Red | TagColors.Blue);

        TagStore reloaded = Open();
        reloaded.EnsureLoaded();
        Assert.Equal(TagColors.Red | TagColors.Blue, reloaded.Get(7, 11));
        Assert.Equal(TagColors.Red, Assert.Single(reloaded.Snapshot()).Primary);
    }

    [Fact]
    public void 拿不到FileId时拒绝写入()
    {
        TagStore store = Open();

        // exFAT / 部分网络盘会给出 0。全部落成 (卷,0) 会让整目录一起变色，
        // 所以必须在写入前拒绝，而不是"写进去再说"
        Assert.Equal(TagSetResult.NoFileId, store.Set(Target(0, @"D:\a.txt"), TagColors.Red));
        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void 空路径被拒绝()
    {
        TagStore store = Open();
        Assert.Equal(TagSetResult.Invalid, store.Set(Target(11, "  "), TagColors.Red));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void 到上限后拒绝新增但允许改已有的()
    {
        TagStore store = Open(max: 2);
        Assert.Equal(TagSetResult.Applied, store.Set(Target(1, @"D:\1.txt"), TagColors.Red));
        Assert.Equal(TagSetResult.Applied, store.Set(Target(2, @"D:\2.txt"), TagColors.Red));

        Assert.Equal(TagSetResult.Full, store.Set(Target(3, @"D:\3.txt"), TagColors.Red));
        Assert.Equal(2, store.Count);

        // 改已有的不受上限影响
        Assert.Equal(TagSetResult.Applied, store.Set(Target(1, @"D:\1.txt"), TagColors.Blue));
        Assert.Equal(TagColors.Blue, store.Get(7, 1));
    }

    [Fact]
    public void 坏行跳过且不影响其余记录()
    {
        File.WriteAllLines(File_,
        [
            "{\"op\":\"set\",\"v\":7,\"f\":11,\"p\":\"D:\\\\a.txt\",\"c\":1,\"t\":1700000000}",
            "{ 这不是 JSON",
            "",
            "{\"op\":\"set\",\"v\":7,\"f\":0,\"p\":\"D:\\\\b.txt\",\"c\":1,\"t\":1700000000}",
            "{\"op\":\"set\",\"v\":7,\"f\":12,\"p\":\"D:\\\\c.txt\",\"c\":8,\"t\":1700000000}",
        ]);

        TagStore store = Open();
        store.EnsureLoaded();

        Assert.Equal(2, store.Count);
        Assert.Equal(TagColors.Green, store.Get(7, 12));
    }

    [Fact]
    public void 整份读不出来时当作空库且不锁死写入()
    {
        File.WriteAllLines(File_, ["{\"op\":\"set\",\"v\":7,\"f\":11,\"p\":\"D:\\\\a.txt\",\"c\":1,\"t\":1700000000}"]);

        TagStore store = Open();
        using (new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.EnsureLoaded();   // 被别的进程独占：读不出来，但不该抛，也不该把功能锁死
            Assert.Equal(0, store.Count);
        }

        // 独占解除后新实例照常读到那条记录
        TagStore reloaded = Open();
        reloaded.EnsureLoaded();
        Assert.Equal(1, reloaded.Count);
    }

    [Fact]
    public void 行数过多时压缩成一条记录一行()
    {
        // 阈值压到 5 行，免得为了走一次压缩真落盘两千次
        TagStore store = Open(compactMin: 5);
        for (int i = 0; i < 30; i++)
        {
            store.Set(Target(11, @"D:\a.txt"), i % 2 == 0 ? TagColors.Red : TagColors.Blue);
        }

        // 判据是"行数 > 存活数×3 且 > 阈值"。同一个键写 30 次，文件该停在阈值附近，
        // 而不是长到 30 行——上界是"阈值 + 1"：压缩发生在写完那一条、刚超过阈值的那一刻。
        int lines = File.ReadAllLines(File_).Length;
        Assert.True(lines <= 6, $"压缩后还有 {lines} 行");

        TagStore reloaded = Open();
        reloaded.EnsureLoaded();
        Assert.Equal(1, reloaded.Count);
        Assert.Equal(TagColors.Blue, reloaded.Get(7, 11));
    }

    [Fact]
    public void 清空后文件为空()
    {
        TagStore store = Open();
        store.Set(Target(1, @"D:\a.txt"), TagColors.Red);
        store.Set(Target(2, @"D:\b.txt"), TagColors.Blue);

        Assert.Equal(2, store.Clear());
        Assert.Equal(0, store.Count);
        Assert.Equal(string.Empty, File.ReadAllText(File_));
    }

    [Fact]
    public void 按颜色归类()
    {
        TagStore store = Open();
        store.Set(Target(1, @"D:\a.txt"), TagColors.Red);
        store.Set(Target(2, @"D:\b.txt"), TagColors.Red | TagColors.Blue);
        store.Set(Target(3, @"D:\c.txt"), TagColors.Green);

        Assert.Equal(2, store.OfColor(TagColors.Red).Count);
        Assert.Single(store.OfColor(TagColors.Blue));
        Assert.Single(store.OfColor(TagColors.Green));
        Assert.Empty(store.OfColor(TagColors.None));
    }

    [Fact]
    public void 首次加载完成会发通知()
    {
        TagStore store = Open();
        int raised = 0;
        store.Loaded += () => raised++;

        store.EnsureLoaded();
        store.EnsureLoaded();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void 清理失效项只删对不上的那几条()
    {
        // FR-4.2.6 / FR-5.3.6：判据是"用记录里的路径重新 Identify，结果与记录一致"。
        // 不能只判文件存不存在——同卷移动后原地被别人占用，路径还在但已经是另一个文件。
        string keep = Path.Combine(_dir, "keep.txt");
        string gone = Path.Combine(_dir, "gone.txt");
        File.WriteAllText(keep, "k");
        File.WriteAllText(gone, "g");

        Assert.True(FileIdEnumerator.TryIdentify(keep, out ulong keepVolume, out ulong keepId));
        Assert.True(FileIdEnumerator.TryIdentify(gone, out ulong goneVolume, out ulong goneId));

        TagStore store = Open();
        store.Set(new TagTarget(keepVolume, keepId, keep), TagColors.Red);
        store.Set(new TagTarget(goneVolume, goneId, gone), TagColors.Blue);

        // 一条路径还在但 FileId 对不上（相当于原地换了个文件），一条路径彻底没了
        store.Set(new TagTarget(keepVolume, keepId + 99, keep), TagColors.Green);

        File.Delete(gone);

        int removed = store.PruneStale();

        Assert.Equal(2, removed);
        Assert.Equal(1, store.Count);
        Assert.True(store.TryGet(keepVolume, keepId, out TagColors colors));
        Assert.Equal(TagColors.Red, colors);
    }

    [Fact]
    public void 没有失效项时清理不动文件()
    {
        string keep = Path.Combine(_dir, "only.txt");
        File.WriteAllText(keep, "k");

        Assert.True(FileIdEnumerator.TryIdentify(keep, out ulong volume, out ulong id));

        TagStore store = Open();
        store.Set(new TagTarget(volume, id, keep), TagColors.Red);

        long before = new FileInfo(File_).Length;

        Assert.Equal(0, store.PruneStale());

        // 没有失效项就不该追加 del 行（否则每点一次文件都长一截）
        Assert.Equal(before, new FileInfo(File_).Length);
    }
    [Fact]
    public void 按颜色计数时一条记录会计入它带的每一种颜色()
    {
        TagStore store = Open();
        store.Set(Target(11, @"D://a.txt"), TagColors.Red);
        store.Set(Target(12, @"D://b.txt"), TagColors.Red | TagColors.Blue);
        store.Set(Target(13, @"D://c.txt"), TagColors.Blue);

        IReadOnlyDictionary<TagColors, int> counts = store.CountByColor();

        Assert.Equal(2, counts[TagColors.Red]);
        Assert.Equal(2, counts[TagColors.Blue]);
        Assert.Equal(0, counts[TagColors.Green]);

        // 八色都要有键：侧栏靠"取不到就跳过"来隐藏没用过的颜色，
        // 少了键与计数为 0 是两件事，这里要求两者等价
        Assert.Equal(TagPalette.All.Count, counts.Count);
    }

    [Fact]
    public void 空库按颜色计数全是零()
    {
        TagStore store = Open();
        store.EnsureLoaded();

        IReadOnlyDictionary<TagColors, int> counts = store.CountByColor();

        Assert.All(TagPalette.All, color => Assert.Equal(0, counts[color]));
    }
}
