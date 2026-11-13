using Jianlan.Core.FileSystem;

namespace Jianlan.Core.Tests;

public class DirectoryEnumeratorTests : IDisposable
{
    private readonly string _root;

    public DirectoryEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jianlan-test-" + Guid.NewGuid().ToString("N"));
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
            // 测试清理失败不影响结论
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadSorted_目录优先且名称升序()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zFolder"));
        Directory.CreateDirectory(Path.Combine(_root, "aFolder"));
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");

        var entries = DirectoryEnumerator.ReadSorted(_root);

        Assert.Equal(4, entries.Count);
        Assert.Equal("aFolder", entries[0].Name);
        Assert.Equal("zFolder", entries[1].Name);
        Assert.Equal("a.txt", entries[2].Name);
        Assert.Equal("b.txt", entries[3].Name);
        Assert.All(entries.Take(2), e => Assert.True(e.IsDirectory));
    }

    [Fact]
    public void ReadSorted_名称比较忽略大小写()
    {
        File.WriteAllText(Path.Combine(_root, "Alpha.txt"), "1");
        File.WriteAllText(Path.Combine(_root, "beta.txt"), "2");

        var entries = DirectoryEnumerator.ReadSorted(_root);

        Assert.Equal("Alpha.txt", entries[0].Name);
        Assert.Equal("beta.txt", entries[1].Name);
    }

    [Fact]
    public void TryReadSorted_不存在的路径返回false且不抛异常()
    {
        string missing = Path.Combine(_root, "not-exists-" + Guid.NewGuid().ToString("N"));

        bool ok = DirectoryEnumerator.TryReadSorted(missing, out var entries, out string? error);

        Assert.False(ok);
        Assert.Empty(entries);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryReadSorted_空目录返回成功而不是失败()
    {
        // 0 项时会额外探一次根目录可读性（区分"空目录"与"打不开"），
        // 这里守住它不会把真正的空目录误判成错误。
        string empty = Path.Combine(_root, "empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);

        bool ok = DirectoryEnumerator.TryReadSorted(empty, out var entries, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(entries);
    }

    [Fact]
    public void ReadSorted_记录文件大小()
    {
        File.WriteAllText(Path.Combine(_root, "data.bin"), new string('x', 2048));

        var entries = DirectoryEnumerator.ReadSorted(_root);
        var file = Assert.Single(entries);

        Assert.Equal(2048, file.Size);
        Assert.Equal("2 KB", file.SizeText);
    }
}
