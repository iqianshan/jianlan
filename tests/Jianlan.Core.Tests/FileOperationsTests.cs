using Jianlan.Core.FileSystem;
using Jianlan.Core.Localization;

namespace Jianlan.Core.Tests;

/// <summary>
/// 文件操作的单元测试。全部在临时目录里跑，不碰用户真实文件。
/// 删除用例只删自己创建的临时项，回收站里会留下同名空目录，可接受。
/// </summary>
public class FileOperationsTests : IDisposable
{
    private readonly string _root;

    public FileOperationsTests()
    {
        _root = Path.Join(Path.GetTempPath(), "jianlan-tests-" + Guid.NewGuid().ToString("N"));
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

        // 默认名跟随界面语言：断言文案来源而不是写死中文，否则英文环境跑测试必挂
        Assert.Equal(Strings.Menu_NewFolder, Path.GetFileName(first));
        Assert.Equal(Strings.Menu_NewFolder + " (2)", Path.GetFileName(second));
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
    public void TryCreateFile_建出空文件且带默认扩展名()
    {
        Assert.True(FileOperations.TryCreateFile(_root, null, out string created, out _));

        Assert.Equal(FileOperations.DefaultFileName, Path.GetFileName(created));
        Assert.True(File.Exists(created));
        Assert.Equal(0, new FileInfo(created).Length);
    }

    [Fact]
    public void TryCreateFile_自动避开重名且保留扩展名()
    {
        Assert.True(FileOperations.TryCreateFile(_root, null, out string first, out _));
        Assert.True(FileOperations.TryCreateFile(_root, null, out string second, out _));

        Assert.Equal(Strings.File_NewTextFileName, Path.GetFileName(first));
        Assert.Equal(Path.GetFileNameWithoutExtension(Strings.File_NewTextFileName) + " (2).txt",
            Path.GetFileName(second));
    }

    [Fact]
    public void TryCreateFile_非法名返回原因()
    {
        Assert.False(FileOperations.TryCreateFile(_root, "a|b.txt", out string created, out string? error));
        Assert.Equal(string.Empty, created);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryCreateFile_已存在同名时不截断原文件()
    {
        string existing = Path.Join(_root, "keep.txt");
        File.WriteAllText(existing, "原有内容");

        // 走 CreateNew（重名会走 UniqueName，这里直接指定已被占用的名字验证不覆盖语义）
        Assert.True(FileOperations.TryCreateFile(_root, "keep.txt", out string created, out _));

        Assert.NotEqual(existing, created);
        Assert.Equal("原有内容", File.ReadAllText(existing));
    }

    [Fact]
    public void TryCreateFile_目录不存在时返回错误而不抛()
    {
        // 目录的 ReadOnly 属性在 Windows 上不阻止写入，无法用来构造无权限场景；
        // 这里验证"路径不可写"的失败路径本身：不抛异常、给出原因、不留半成品
        string missing = Path.Join(_root, "not-exist");

        Assert.False(FileOperations.TryCreateFile(missing, null, out string created, out string? error));
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

    // ── 覆盖式粘贴（FR-5.2.8）────────────────────────────────────────────

    [Fact]
    public void FindConflicts_只报目标目录里已存在的名字()
    {
        string from = Path.Join(_root, "from");
        string to = Path.Join(_root, "to");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);

        File.WriteAllText(Path.Join(from, "both.txt"), "src");
        File.WriteAllText(Path.Join(from, "only-source.txt"), "src");
        File.WriteAllText(Path.Join(to, "both.txt"), "dst");

        IReadOnlyList<string> conflicts = FileOperations.FindConflicts(
            [Path.Join(from, "both.txt"), Path.Join(from, "only-source.txt")], to);

        Assert.Equal(["both.txt"], conflicts);
    }

    [Fact]
    public async Task 覆盖式复制会替换目标里的同名文件()
    {
        string from = Path.Join(_root, "cfrom");
        string to = Path.Join(_root, "cto");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);

        string source = Path.Join(from, "a.txt");
        string target = Path.Join(to, "a.txt");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");

        var result = await FileOperations.CopyAsync([source], to, ConflictPolicy.Overwrite);

        Assert.True(result.Ok);
        Assert.Equal("new", File.ReadAllText(target));

        // 覆盖不等于移动：源还在
        Assert.True(File.Exists(source));

        // 也不该顺手多出一个 "a (2).txt"
        Assert.False(File.Exists(Path.Join(to, "a (2).txt")));
    }

    [Fact]
    public async Task 保留两者时不碰目标原文件且生成副本()
    {
        string from = Path.Join(_root, "kfrom");
        string to = Path.Join(_root, "kto");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);

        string source = Path.Join(from, "b.txt");
        string target = Path.Join(to, "b.txt");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");

        var result = await FileOperations.CopyAsync([source], to);

        Assert.True(result.Ok);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Equal("new", File.ReadAllText(Path.Join(to, "b (2).txt")));
    }

    [Fact]
    public async Task 覆盖式复制目录是合并不是清空目标()
    {
        string from = Path.Join(_root, "dfdrom", "shared");
        string to = Path.Join(_root, "dto");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(Path.Join(to, "shared"));

        File.WriteAllText(Path.Join(from, "in-source.txt"), "src");
        File.WriteAllText(Path.Join(from, "same.txt"), "new");
        File.WriteAllText(Path.Join(to, "shared", "same.txt"), "old");
        File.WriteAllText(Path.Join(to, "shared", "only-target.txt"), "keep");

        var result = await FileOperations.CopyAsync([Path.Join(_root, "dfdrom", "shared")], to,
            ConflictPolicy.Overwrite);

        Assert.True(result.Ok);
        Assert.Equal("new", File.ReadAllText(Path.Join(to, "shared", "same.txt")));
        Assert.Equal("src", File.ReadAllText(Path.Join(to, "shared", "in-source.txt")));

        // 目标独有的文件必须留着——"覆盖同名"不是"清空这个目录"
        Assert.Equal("keep", File.ReadAllText(Path.Join(to, "shared", "only-target.txt")));
    }
}
