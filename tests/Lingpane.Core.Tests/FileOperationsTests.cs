using Lingpane.Core.FileSystem;

namespace Lingpane.Core.Tests;

/// <summary>
/// 文件操作的单元测试。全部在临时目录里跑，不碰用户真实文件。
/// 删除用例只删自己创建的临时项，回收站里会留下同名空目录，可接受。
/// </summary>
public class FileOperationsTests : IDisposable
{
    private readonly string _root;

    public FileOperationsTests()
    {
        _root = Path.Join(Path.GetTempPath(), "lingpane-tests-" + Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData("正常名称", true)]
    [InlineData("a.txt", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData("a?b", false)]
    [InlineData("结尾点.", false)]
    [InlineData("结尾空格 ", false)]
    [InlineData("CON", false)]
    [InlineData("con.txt", false)]
    public void TryValidateName_按规则判定(string name, bool expected)
    {
        Assert.Equal(expected, FileOperations.TryValidateName(name, out _));
    }

    [Fact]
    public void TryValidateName_超长拒绝()
    {
        string name = new('a', FileOperations.MaxNameLength + 1);
        Assert.False(FileOperations.TryValidateName(name, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void UniqueName_重名追加序号()
    {
        Directory.CreateDirectory(Path.Join(_root, "新建文件夹"));
        Directory.CreateDirectory(Path.Join(_root, "新建文件夹 (2)"));

        Assert.Equal("新建文件夹 (3)", FileOperations.UniqueName(_root, "新建文件夹", isDirectory: true));
    }

    [Fact]
    public void UniqueName_保留扩展名()
    {
        File.WriteAllText(Path.Join(_root, "a.txt"), "x");

        Assert.Equal("a (2).txt", FileOperations.UniqueName(_root, "a.txt", isDirectory: false));
    }

    [Fact]
    public void TryCreateFolder_自动避开重名()
    {
        Assert.True(FileOperations.TryCreateFolder(_root, null, out string first, out _));
        Assert.True(FileOperations.TryCreateFolder(_root, null, out string second, out _));

        Assert.Equal("新建文件夹", Path.GetFileName(first));
        Assert.Equal("新建文件夹 (2)", Path.GetFileName(second));
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    public void TryCreateFolder_非法名返回原因()
    {
        Assert.False(FileOperations.TryCreateFolder(_root, "a*b", out string created, out string? error));
        Assert.Equal(string.Empty, created);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryRename_文件改名()
    {
        string source = Path.Join(_root, "old.txt");
        File.WriteAllText(source, "x");

        Assert.True(FileOperations.TryRename(source, "new.txt", out string renamed, out _));
        Assert.Equal(Path.Join(_root, "new.txt"), renamed);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void TryRename_同名不报错()
    {
        string source = Path.Join(_root, "a.txt");
        File.WriteAllText(source, "x");

        Assert.True(FileOperations.TryRename(source, "a.txt", out string renamed, out _));
        Assert.Equal(source, renamed);
    }

    [Fact]
    public void TryRename_目标已存在则拒绝()
    {
        string a = Path.Join(_root, "a.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(Path.Join(_root, "b.txt"), "b");

        Assert.False(FileOperations.TryRename(a, "b.txt", out _, out string? error));
        Assert.NotNull(error);
        Assert.Equal("a", File.ReadAllText(a));
    }

    [Fact]
    public async Task CopyAsync_复制目录并保留结构()
    {
        string source = Path.Join(_root, "src");
        Directory.CreateDirectory(Path.Join(source, "inner"));
        File.WriteAllText(Path.Join(source, "a.txt"), "a");
        File.WriteAllText(Path.Join(source, "inner", "b.txt"), "b");

        string target = Path.Join(_root, "dst");
        Directory.CreateDirectory(target);

        var result = await FileOperations.CopyAsync([source], target);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal("a", File.ReadAllText(Path.Join(target, "src", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Join(target, "src", "inner", "b.txt")));
    }

    [Fact]
    public async Task CopyAsync_重名不覆盖()
    {
        string target = Path.Join(_root, "dst");
        Directory.CreateDirectory(target);
        string source = Path.Join(target, "a.txt");
        File.WriteAllText(source, "new");

        var result = await FileOperations.CopyAsync([source], target);

        Assert.True(result.Ok);
        Assert.Equal("new", File.ReadAllText(Path.Join(target, "a (2).txt")));
        Assert.Equal("new", File.ReadAllText(source));
    }

    [Fact]
    public async Task CopyAsync_拒绝把目录复制到自身之下()
    {
        string source = Path.Join(_root, "src");
        Directory.CreateDirectory(source);

        var result = await FileOperations.CopyAsync([source], Path.Join(source, "child"));

        Assert.False(result.Ok);
        Assert.Equal(0, result.Succeeded);
    }

    [Fact]
    public async Task MoveAsync_移动到目标目录()
    {
        string source = Path.Join(_root, "a.txt");
        File.WriteAllText(source, "a");
        string target = Path.Join(_root, "dst");
        Directory.CreateDirectory(target);

        var result = await FileOperations.MoveAsync([source], target);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Join(target, "a.txt")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task MoveAsync_同目录剪切粘贴是空操作()
    {
        string source = Path.Join(_root, "a.txt");
        File.WriteAllText(source, "a");

        var result = await FileOperations.MoveAsync([source], _root);

        Assert.True(result.Ok);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Join(_root, "a (2).txt")));
    }

    [Fact]
    public async Task DeleteAsync_永久删除后消失()
    {
        string target = Path.Join(_root, "gone.txt");
        File.WriteAllText(target, "x");

        var result = await FileOperations.DeleteAsync([target], permanent: true);

        Assert.True(result.Ok);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task DeleteAsync_删除到回收站后源路径消失()
    {
        string folder = Path.Join(_root, "trash-me");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Join(folder, "x.txt"), "x");

        var result = await FileOperations.DeleteAsync([folder]);

        Assert.True(result.Ok);
        Assert.False(Directory.Exists(folder));
    }
}
