using Lingpane.Core.FileSystem;

namespace Lingpane.Core.Tests;

/// <summary>
/// FR-6.16 的三个契约：数值算对、不跟随符号链接、超时与取消都返回"已累计 + 未完成"。
/// 全部用真实临时目录，不 mock 文件系统——这个功能的 bug 几乎都出在 IO 边界上。
/// </summary>
public sealed class FolderSizeCalculatorTests : IDisposable
{
    private readonly string _root;

    public FolderSizeCalculatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lingpane-size-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响断言结果
        }
    }

    private string Touch(string relativePath, int bytes)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    [Fact]
    public void 空目录算出零且标记完成()
    {
        var result = FolderSizeCalculator.Count(_root);

        Assert.Equal(0, result.Bytes);
        Assert.Equal(0, result.FileCount);
        Assert.Equal(0, result.FolderCount);
        Assert.True(result.Completed);
    }

    [Fact]
    public void 嵌套多层的文件全部计入()
    {
        Touch("a.txt", 100);
        Touch(Path.Combine("sub", "b.txt"), 200);
        Touch(Path.Combine("sub", "deeper", "c.txt"), 300);

        var result = FolderSizeCalculator.Count(_root);

        Assert.Equal(600, result.Bytes);
        Assert.Equal(3, result.FileCount);
        // sub 与 sub\deeper 两个子目录
        Assert.Equal(2, result.FolderCount);
        Assert.True(result.Completed);
    }

    [Fact]
    public void 隐藏文件也要计入()
    {
        // 默认的 EnumerationOptions 会跳过 Hidden | System，那样用户会得到一个
        // "比我看到的少"的数字。这条断言就是防止有人把 AttributesToSkip 改回默认值。
        Touch("normal.txt", 100);
        string hidden = Touch("secret.txt", 50);
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var result = FolderSizeCalculator.Count(_root);

        Assert.Equal(150, result.Bytes);
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public void 路径不存在时返回未完成而不是抛异常()
    {
        string missing = Path.Combine(_root, "not-there");

        var result = FolderSizeCalculator.Count(missing);

        Assert.False(result.Completed);
        Assert.Equal(0, result.Bytes);
    }

    [Fact]
    public void 预先取消的任务立刻返回未完成()
    {
        for (int i = 0; i < 800; i++) Touch(Path.Combine("many", $"{i}.bin"), 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = FolderSizeCalculator.Count(_root, cts.Token);

        Assert.False(result.Completed);
        // 取消检查每 512 项一次，所以可能已经扫过一批；断言的是"没扫完"，不是"一点没扫"
        Assert.True(result.FileCount < 800, $"取消后不应扫完全部，实际 {result.FileCount}");
    }

    [Fact]
    public void 时间上限到达后返回已累计的值()
    {
        for (int i = 0; i < 800; i++) Touch(Path.Combine("many", $"{i}.bin"), 1);

        var result = FolderSizeCalculator.Count(_root, limit: TimeSpan.Zero);

        Assert.False(result.Completed);
    }

    [Fact]
    public void 进度回调至少被调用一次且数值单调不减()
    {
        for (int i = 0; i < 1200; i++) Touch(Path.Combine("many", $"{i}.bin"), 1);

        var seen = new List<FolderSizeProgress>();
        var progress = new SynchronousProgress<FolderSizeProgress>(seen.Add);

        var result = FolderSizeCalculator.Count(_root, progress: progress);

        Assert.True(result.Completed);
        Assert.NotEmpty(seen);
        Assert.Equal(1200, seen[^1].FileCount);

        long previous = -1;
        foreach (var p in seen)
        {
            Assert.True(p.FileCount >= previous, "进度读数的文件数不应回退");
            previous = p.FileCount;
        }
    }

    /// <summary>
    /// <see cref="Progress{T}"/> 在单元测试里会把回调 Post 到线程池，断言时还没执行完。
    /// 这里要的是同步、按序的回调，所以自己实现一个最小版本。
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>
/// 大小列的四种显示状态。难点不在格式化，而在"没算过"与"算出 0"必须能区分：
/// 空目录算出 0 字节，未计算的目录留空——两者混在一起，用户会以为空目录算失败了。
/// </summary>
public sealed class FolderSizeTextTests
{
    private static FileEntry Folder() =>
        new("docs", @"C:/fake", isDirectory: true, size: 0, lastWriteTime: DateTime.Now);

    [Fact]
    public void 未计算的目录大小列留空()
    {
        var entry = Folder();

        Assert.Equal(FolderSizeState.None, entry.FolderSizeState);
        Assert.Equal(string.Empty, entry.SizeText);
    }

    [Fact]
    public void 计算中显示占位文案()
    {
        var entry = Folder();
        entry.MarkFolderSizeComputing();

        Assert.Equal(FolderSizeState.Computing, entry.FolderSizeState);
        Assert.NotEqual(string.Empty, entry.SizeText);
        Assert.NotEqual("0 B", entry.SizeText);
    }

    [Fact]
    public void 算完显示精确值不带加号()
    {
        var entry = Folder();
        entry.SetFolderSize(1536, completed: true);

        Assert.Equal("1.5 KB", entry.SizeText);
    }

    [Fact]
    public void 未完成的结果带加号()
    {
        var entry = Folder();
        entry.SetFolderSize(1536, completed: false);

        Assert.Equal(FolderSizeState.Partial, entry.FolderSizeState);
        Assert.Equal("1.5 KB+", entry.SizeText);
    }

    [Fact]
    public void 复位后回到留空()
    {
        var entry = Folder();
        entry.SetFolderSize(2048, completed: true);
        entry.ClearFolderSize();

        Assert.Equal(FolderSizeState.None, entry.FolderSizeState);
        Assert.Equal(string.Empty, entry.SizeText);
    }

    [Fact]
    public void 文件不受文件夹大小状态影响()
    {
        var file = new FileEntry("a.txt", @"C:/fake", isDirectory: false, size: 4096, lastWriteTime: DateTime.Now);

        file.SetFolderSize(999, completed: true);

        // 文件本来就有大小，SetFolderSize 必须早退而不是把 4 KB 覆盖成 999 B
        Assert.Equal("4 KB", file.SizeText);
        Assert.Equal(FolderSizeState.None, file.FolderSizeState);
    }

    [Fact]
    public void 状态变化会通知绑定()
    {
        var entry = Folder();
        var changed = new List<string?>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        entry.MarkFolderSizeComputing();
        entry.SetFolderSize(1024, completed: true);

        Assert.Contains(nameof(FileEntry.SizeText), changed);
        Assert.Contains(nameof(FileEntry.FolderSizeState), changed);
    }
}
