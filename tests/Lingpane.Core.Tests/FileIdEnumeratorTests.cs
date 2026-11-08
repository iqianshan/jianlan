using Lingpane.Core.FileSystem;

namespace Lingpane.Core.Tests;

/// <summary>
/// FileId 枚举（FR-4 前置）。这里测的是"搬进产品代码之后仍然对"的三件事：
/// 条目与现有枚举一致、单文件 Identify 与枚举同口径、失败时降级而不是抛。
///
/// 结构体布局错了的表现是"文件名乱码 / 条目数不对"，所以第一条用例就是全量比对。
/// </summary>
public sealed class FileIdEnumeratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lingpane-fid-" + Guid.NewGuid().ToString("N"));

    public FileIdEnumeratorTests()
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"f{i:00}.txt"), new string('x', i));
        }

        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 条目与现有枚举完全一致()
    {
        Assert.True(FileIdEnumerator.TryRead(_dir, out var entries, out ulong volume, out string? error), error);

        string[] expected = [.. Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).OfType<string>()];
        string[] actual = [.. entries.Select(e => e.Name)];

        // 内核会返回 "." 与 ".."，不过滤的话这里会多两项。排序后比对名称集合。
        Assert.Equal(expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            actual.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        Assert.NotEqual(0UL, volume);
        Assert.All(entries, e => Assert.NotEqual(0UL, e.FileId));
    }

    [Fact]
    public void FileId不重复且目录标记正确()
    {
        Assert.True(FileIdEnumerator.TryRead(_dir, out var entries, out _, out _));

        Assert.Equal(entries.Count, entries.Select(e => e.FileId).Distinct().Count());

        FileIdEntry sub = Assert.Single(entries, e => e.Name == "sub");
        Assert.True(sub.IsDirectory);
        Assert.Equal(0, sub.Size);      // 目录不填大小，与 FileEntry 的口径一致

        FileIdEntry file = Assert.Single(entries, e => e.Name == "f05.txt");
        Assert.False(file.IsDirectory);
        Assert.Equal(5, file.Size);
    }

    [Fact]
    public void 单文件Identify与枚举同口径()
    {
        Assert.True(FileIdEnumerator.TryRead(_dir, out var entries, out ulong volume, out _));
        FileIdEntry entry = Assert.Single(entries, e => e.Name == "f07.txt");

        Assert.True(FileIdEnumerator.TryIdentify(Path.Combine(_dir, "f07.txt"), out ulong fileVolume, out ulong fileId));

        // 两个口径必须是同一套（都是 FileIdInfo）：混用会让同一个文件算出两个键，
        // 表现为"标签设了不显示"
        Assert.Equal(volume, fileVolume);
        Assert.Equal(entry.FileId, fileId);
    }

    [Fact]
    public void 目录也能Identify且卷序列号与目录内文件一致()
    {
        Assert.True(FileIdEnumerator.TryIdentify(_dir, out ulong dirVolume, out ulong dirId));
        Assert.True(FileIdEnumerator.TryIdentify(Path.Combine(_dir, "f00.txt"), out ulong fileVolume, out _));

        Assert.NotEqual(0UL, dirId);
        Assert.Equal(dirVolume, fileVolume);
    }

    [Fact]
    public void 不存在的路径返回失败而不是抛异常()
    {
        string missing = Path.Combine(_dir, "没有这个目录");

        Assert.False(FileIdEnumerator.TryRead(missing, out var entries, out _, out string? error));
        Assert.Empty(entries);
        Assert.NotNull(error);

        Assert.False(FileIdEnumerator.TryIdentify(missing, out _, out _));
    }

    [Fact]
    public void 长路径与正斜杠都能打开()
    {
        // CreateFileW 受 MAX_PATH 限制，必须自己补 \\?\ 并先 GetFullPath。
        // 正斜杠是那条"必须先规范化"的判据：\\?\ 关闭规范化，传 / 进去会失败。
        string forward = _dir.Replace('\\', '/');
        Assert.True(FileIdEnumerator.TryRead(forward, out var entries, out _, out string? error), error);
        Assert.Equal(21, entries.Count);
    }

    [Fact]
    public void 空目录返回空列表而不是失败()
    {
        string empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);

        Assert.True(FileIdEnumerator.TryRead(empty, out var entries, out _, out _));
        Assert.Empty(entries);
    }
}
