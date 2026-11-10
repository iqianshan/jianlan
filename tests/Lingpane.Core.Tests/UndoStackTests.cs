using Lingpane.Core.FileSystem;

namespace Lingpane.Core.Tests;

/// <summary>
/// 撤销栈（FR-6.8）的单元测试。
///
/// 关注点不是"能不能撤"，而是**不该撤的时候有没有忍住**：改名撞名、内容被改过、
/// 回收站还原不了，这三种情况下必须原地不动。所以断言里除了结果，还要检查磁盘现状。
///
/// 回收站用一个假实现：真的往回收站扔文件会让测试跑几轮就塞满用户的回收站，
/// 而且无法断言"扔进去的是什么"。
/// </summary>
public class UndoStackTests : IDisposable
{
    private readonly string _root;
    private readonly FakeRecycleBin _bin = new();

    public UndoStackTests()
    {
        _root = Path.Join(Path.GetTempPath(), "lingpane-undo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    private string PathOf(string name) => Path.Join(_root, name);

    private string Write(string name, string content = "x")
    {
        string path = PathOf(name);
        File.WriteAllText(path, content);
        return path;
    }

    // ── 重命名 / 移动 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 重命名可以撤回原名()
    {
        string before = Write("a.txt");
        string after = PathOf("b.txt");
        File.Move(before, after);

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Rename, [new UndoStep(before, after)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.True(outcome.Ok);
        Assert.True(File.Exists(before));
        Assert.False(File.Exists(after));
    }

    [Fact]
    public async Task 原名已被占用时不覆盖并报原因()
    {
        string before = Write("a.txt", "原来的 a");
        string after = PathOf("b.txt");
        File.Move(before, after);

        // 用户在这之后又建了一个同名文件——撤销绝不能把它顶掉
        Write("a.txt", "新的 a");

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Rename, [new UndoStep(before, after)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.False(outcome.Ok);
        Assert.Equal(0, outcome.Undone);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal("新的 a", File.ReadAllText(before));
        Assert.True(File.Exists(after));
    }

    [Fact]
    public async Task 原目录不在了就不动()
    {
        string before = Path.Join(_root, "gone", "a.txt");
        string after = Write("a.txt");

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Move, [new UndoStep(before, after)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.False(outcome.Ok);
        Assert.Equal(1, outcome.Skipped);
        Assert.True(File.Exists(after));
    }

    [Fact]
    public async Task 移动撤回原目录()
    {
        Directory.CreateDirectory(PathOf("src"));
        string moved = Write("moved.txt");
        string original = Path.Join(_root, "src", "moved.txt");
        File.Move(moved, original);

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Move, [new UndoStep(moved, original)]);

        await stack.UndoAsync();

        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(original));
    }

    // ── 新建 / 复制 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 新建未改动可以撤掉()
    {
        string created = Write("new.txt", "内容");

        (long size, long ticks) = UndoStack.Snapshot(created);

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Create, [new UndoStep(string.Empty, created, size, ticks)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.True(outcome.Ok);
        Assert.False(File.Exists(created));
        Assert.Contains(created, _bin.Deleted);
    }

    [Fact]
    public async Task 新建后被编辑过就不删()
    {
        string created = Write("new.txt", "短");
        (long size, long ticks) = UndoStack.Snapshot(created);

        // 用户建完文件就开始写东西了
        File.WriteAllText(created, "长得多的一段内容");
        File.SetLastWriteTimeUtc(created, DateTime.UtcNow.AddMinutes(5));

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Create, [new UndoStep(string.Empty, created, size, ticks)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.False(outcome.Ok);
        Assert.Equal(1, outcome.Skipped);
        Assert.True(File.Exists(created));
        Assert.Empty(_bin.Deleted);
    }

    [Fact]
    public async Task 复制出来的目录被加了文件就不删()
    {
        string source = PathOf("dir");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Join(source, "a.txt"), "x");

        string copy = PathOf("dir (2)");
        Directory.CreateDirectory(copy);
        File.Copy(Path.Join(source, "a.txt"), Path.Join(copy, "a.txt"));

        (long size, long ticks) = UndoStack.Snapshot(copy);

        // 撤销前用户往副本里放了自己的东西
        File.WriteAllText(Path.Join(copy, "mine.txt"), "别删我");

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Copy, [new UndoStep(source, copy, size, ticks)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.False(outcome.Ok);
        Assert.True(Directory.Exists(copy));
        Assert.True(File.Exists(Path.Join(copy, "mine.txt")));
    }

    [Fact]
    public async Task 复制未改动可以撤掉()
    {
        string source = Write("a.txt");
        string copy = PathOf("a (2).txt");
        File.Copy(source, copy);

        (long size, long ticks) = UndoStack.Snapshot(copy);

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Copy, [new UndoStep(source, copy, size, ticks)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.True(outcome.Ok);
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task 指纹未知时不敢删()
    {
        string created = Write("new.txt");

        // Size 为"没拍到"的哨兵值
        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Create, [new UndoStep(string.Empty, created, -1, 0)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.False(outcome.Ok);
        Assert.True(File.Exists(created));
    }

    // ── 删除 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 删除可以还原()
    {
        string path = Write("gone.txt");

        _bin.Restorable.Add(path);

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Delete, [new UndoStep(path, string.Empty)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(1, outcome.Undone);
    }

    [Fact]
    public async Task 还原不了就如实报失败()
    {
        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Delete, [new UndoStep(PathOf("gone.txt"), string.Empty)]);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.False(outcome.Ok);
        Assert.Equal(0, outcome.Undone);
        Assert.Equal(1, outcome.Skipped);
        Assert.NotNull(outcome.Reason);
    }

    [Fact]
    public async Task 批量删除算一个撤销单元()
    {
        string[] paths = [Write("1.txt"), Write("2.txt"), Write("3.txt")];
        foreach (string p in paths) _bin.Restorable.Add(p);

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Delete, paths.Select(p => new UndoStep(p, string.Empty)).ToArray());

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.True(outcome.Ok);
        Assert.Equal(3, outcome.Undone);
        Assert.False(stack.CanUndo);
    }

    // ── 栈本身 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task 只给一次机会()
    {
        string created = Write("new.txt");
        (long size, long ticks) = UndoStack.Snapshot(created);

        var stack = new UndoStack(_bin);
        stack.Push(UndoKind.Create, [new UndoStep(string.Empty, created, size, ticks)]);

        await stack.UndoAsync();
        UndoOutcome second = await stack.UndoAsync();

        Assert.False(second.Ok);
        Assert.Equal(0, second.Undone + second.Skipped);
    }

    [Fact]
    public async Task 空栈撤销什么都不做()
    {
        var stack = new UndoStack(_bin);

        UndoOutcome outcome = await stack.UndoAsync();

        Assert.False(outcome.Ok);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void 新记录覆盖旧记录()
    {
        var stack = new UndoStack(_bin);

        stack.Push(UndoKind.Rename, [new UndoStep(PathOf("a"), PathOf("b"))]);
        stack.Push(UndoKind.Copy, [new UndoStep(string.Empty, PathOf("c"), 1, 1)]);

        Assert.Equal(UndoKind.Copy, stack.Kind);
        Assert.Equal(1, stack.Count);
    }

    [Fact]
    public void 零步记录等于清空()
    {
        var stack = new UndoStack(_bin);

        stack.Push(UndoKind.Rename, [new UndoStep(PathOf("a"), PathOf("b"))]);
        stack.Push(UndoKind.Copy, []);

        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void 清空后不可撤销()
    {
        var stack = new UndoStack(_bin);

        stack.Push(UndoKind.Rename, [new UndoStep(PathOf("a"), PathOf("b"))]);
        stack.Clear();

        Assert.False(stack.CanUndo);
    }

    /// <summary>
    /// 假的回收站：删除就是真删（测试目录里的东西），还原按登记表放行。
    /// 还原不动磁盘——测试断言的是"栈有没有把它算成还原成功"，不是 Shell 能不能干活。
    /// </summary>
    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> Deleted { get; } = [];

        public HashSet<string> Restorable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Restore(IReadOnlyList<string> originalPaths)
            => originalPaths.Where(Restorable.Contains).ToList();

        public Task<bool> DeleteToRecycleBinAsync(IReadOnlyList<string> paths)
        {
            foreach (string path in paths)
            {
                Deleted.Add(path);

                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }

            return Task.FromResult(true);
        }
    }
}
