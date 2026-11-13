using Jianlan.Core.FileSystem;
using Jianlan.Core.Navigation;

namespace Jianlan.Core.Tests;

/// <summary>
/// 地址栏补全（FR-6.1）。这里锁的是"路径怎么拆"和"候选怎么排"两件事——
/// 它们错了不会崩，只会让补全变得难用：候选全是文件、或者把 <c>C:</c> 当成目录去枚举。
/// </summary>
public sealed class PathCompletionTests
{
    private static FileEntry Dir(string name, string parent) =>
        new(name, parent, isDirectory: true, size: 0, lastWriteTime: DateTime.UnixEpoch);

    private static FileEntry File(string name, string parent) =>
        new(name, parent, isDirectory: false, size: 10, lastWriteTime: DateTime.UnixEpoch);

    // ── Split ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"D:\work\re", @"D:\work", "re")]
    [InlineData(@"D:\work\", @"D:\work\", "")]
    [InlineData(@"D:\", @"D:\", "")]
    [InlineData("D:", @"D:\", "")]
    [InlineData(@"\\srv\share", @"\\srv\share", "")]
    [InlineData(@"\\srv\share\wo", @"\\srv\share", "wo")]
    [InlineData("re", @"D:\work", "re")]
    [InlineData("", @"D:\work", "")]
    public void Split_基本形态(string text, string expectedDir, string expectedStem)
    {
        var (dir, stem) = PathCompletion.Split(text, @"D:\work");

        Assert.Equal(expectedDir, dir);
        Assert.Equal(expectedStem, stem);
    }

    [Fact]
    public void Split_正斜杠按反斜杠处理()
    {
        var (dir, stem) = PathCompletion.Split("D:/work/re", @"C:\");

        Assert.Equal(@"D:\work", dir);
        Assert.Equal("re", stem);
    }

    [Fact]
    public void Split_裸词干用当前目录()
    {
        var (dir, _) = PathCompletion.Split("report", @"E:\data");

        Assert.Equal(@"E:\data", dir);
    }

    // ── Complete ────────────────────────────────────────────────────────────

    [Fact]
    public void Complete_词干为空不返回候选()
    {
        // 刚敲完分隔符就弹整个目录，等于把列表重复一遍挡住视线
        var entries = new[] { Dir("alpha", @"D:\work"), Dir("beta", @"D:\work") };

        var result = PathCompletion.Complete(@"D:\work\", @"D:\work", entries);

        Assert.Empty(result);
    }

    [Fact]
    public void Complete_前缀命中排在前缀未命中的前面()
    {
        // "re" 同时出现在 report 与 core 里，但只有 report 是前缀命中
        var entries = new[]
        {
            Dir("core", @"D:\work"),
            Dir("report", @"D:\work"),
        };

        var result = PathCompletion.Complete(@"D:\work\re", @"D:\work", entries);

        Assert.Equal(2, result.Count);
        Assert.Equal("report", result[0].Name);
    }

    [Fact]
    public void Complete_同组内目录排在文件前面()
    {
        var entries = new[]
        {
            File("report.txt", @"D:\work"),
            Dir("report-old", @"D:\work"),
        };

        var result = PathCompletion.Complete(@"D:\work\report", @"D:\work", entries);

        Assert.True(result[0].IsDirectory);
        Assert.False(result[1].IsDirectory);
    }

    [Fact]
    public void Complete_数字按自然序()
    {
        var entries = new[]
        {
            Dir("item10", @"D:\work"),
            Dir("item2", @"D:\work"),
        };

        var result = PathCompletion.Complete(@"D:\work\item", @"D:\work", entries);

        Assert.Equal(new[] { "item2", "item10" }, result.Select(c => c.Name));
    }

    [Fact]
    public void Complete_大小写不敏感且大小写不同算同一条()
    {
        var entries = new[] { Dir("Reports", @"D:\work") };

        // 缓存给报告目录，探测又返回同一个目录的另一种写法
        var result = PathCompletion.Complete(
            @"D:\work\rep", @"D:\work", entries,
            (_, _) => new[] { @"D:\WORK\Reports" });

        Assert.Single(result);
        Assert.Equal("Reports", result[0].Name);
    }

    [Fact]
    public void Complete_超过上限时截断()
    {
        var entries = Enumerable.Range(0, 30)
            .Select(i => Dir($"dir{i:D2}", @"D:\work"))
            .ToArray();

        var result = PathCompletion.Complete(@"D:\work\dir", @"D:\work", entries);

        Assert.Equal(PathCompletion.MaxCandidates, result.Count);
    }

    [Fact]
    public void Complete_探测抛异常时降级为只用缓存()
    {
        // 网络盘断连时抛异常是正常现象，不该让补全把地址栏卡住
        var entries = new[] { Dir("alpha", @"D:\work") };

        var result = PathCompletion.Complete(
            @"D:\work\a", @"D:\work", entries,
            (_, _) => throw new IOException("网络不可达"));

        Assert.Single(result);
        Assert.Equal("alpha", result[0].Name);
    }

    [Fact]
    public void Complete_缓存不是当前目录时不使用缓存()
    {
        // 用户在地址栏输入了另一个目录，此时当前目录的条目与输入无关
        var entries = new[] { Dir("alpha", @"D:\work") };

        var result = PathCompletion.Complete(
            @"E:\other\a", @"D:\work", entries,
            (dir, pattern) =>
            {
                Assert.Equal(@"E:\other", dir);
                Assert.Equal("a*", pattern);
                return new[] { @"E:\other\alpha", @"E:\other\alpine" };
            });

        Assert.Equal(2, result.Count);
        Assert.All(result, c => Assert.StartsWith(@"E:\other", c.FullPath));
    }

    [Fact]
    public void Complete_裸词干按当前目录补全()
    {
        var entries = new[] { Dir("alpha", @"D:\work") };

        var result = PathCompletion.Complete("al", @"D:\work", entries);

        Assert.Single(result);
        Assert.Equal(@"D:\work\alpha", result[0].FullPath);
    }

    [Fact]
    public void Complete_探测结果里的目录名从路径取()
    {
        var result = PathCompletion.Complete(
            @"D:\work\rep", @"D:\work", null,
            (_, _) => new[] { @"D:\work\reports\" });

        Assert.Single(result);
        Assert.Equal("reports", result[0].Name);
        Assert.True(result[0].IsDirectory);
    }

    [Fact]
    public void ToInputText_目录补尾分隔符以便继续输入()
    {
        Assert.Equal(@"D:\work\reports\",
            PathCompletion.ToInputText(new PathCandidate(@"D:\work\reports", "reports", PathCandidateKind.Directory)));

        Assert.Equal(@"D:\work\a.txt",
            PathCompletion.ToInputText(new PathCandidate(@"D:\work\a.txt", "a.txt", PathCandidateKind.File)));
    }
}
