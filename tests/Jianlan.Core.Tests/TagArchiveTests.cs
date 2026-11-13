using Jianlan.Core.Tagging;

namespace Jianlan.Core.Tests;

/// <summary>
/// 标签库导入导出（FR-4.2.7 / FR-5.3.6）。全部在临时目录里跑。
///
/// 覆盖三件事：导出文件能被自己读回来、认不出格式的文件必须报错而不是"静默零条"、
/// 写失败时不留下半成品。
/// </summary>
public sealed class TagArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jianlan-archive-" + Guid.NewGuid().ToString("N"));

    public TagArchiveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string File_ => Path.Combine(_dir, "tags.json");

    private static TagRecord Record(ulong id, TagColors colors, string path = @"D:\a.txt", ulong volume = 7)
        => new(volume, id, path, colors, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    [Fact]
    public void 导出后能读回来_主键与颜色都不丢()
    {
        TagRecord[] records = [Record(1, TagColors.Red | TagColors.Blue), Record(2, TagColors.Green)];

        int written = TagArchive.Write(records, File_);
        TagArchiveDocument document = TagArchive.Read(File_);
        List<TagRecord> restored = TagArchive.ToRecords(document);

        Assert.Equal(2, written);
        Assert.Equal(TagArchiveDocument.CurrentKind, document.Kind);
        Assert.Equal(2, restored.Count);
        Assert.Equal(1UL, restored[0].FileId);
        Assert.Equal(TagColors.Red | TagColors.Blue, restored[0].Colors);
        Assert.Equal(7UL, restored[0].Volume);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), restored[0].UpdatedAt);
    }

    [Fact]
    public void 导出一份空库也是合法文件()
    {
        Assert.Equal(0, TagArchive.Write([], File_));

        TagArchiveDocument document = TagArchive.Read(File_);

        Assert.Empty(document.Records);
        Assert.Empty(TagArchive.ToRecords(document));
    }

    [Fact]
    public void 不是标签库的文件要报错而不是导入零条()
    {
        File.WriteAllText(File_, """{"app":"jianlan","kind":"settings","records":[]}""");

        Assert.Throws<TagArchiveException>(() => TagArchive.Read(File_));
    }

    [Fact]
    public void 坏JSON报错()
    {
        File.WriteAllText(File_, "{ not json");

        Assert.Throws<TagArchiveException>(() => TagArchive.Read(File_));
    }

    [Fact]
    public void 读不存在的文件报错()
    {
        Assert.Throws<TagArchiveException>(() => TagArchive.Read(Path.Join(_dir, "missing.json")));
    }

    [Fact]
    public void 导入时丢弃FileId为0的项()
    {
        File.WriteAllText(File_, """
            {"app":"jianlan","kind":"tags","version":1,"records":[
              {"volume":7,"fileId":0,"path":"D:\\bad.txt","colors":["Red"],"updatedAt":"2023-11-14T22:13:20+00:00"},
              {"volume":7,"fileId":5,"path":"D:\\ok.txt","colors":["Red"],"updatedAt":"2023-11-14T22:13:20+00:00"}
            ]}
            """);

        List<TagRecord> records = TagArchive.ToRecords(TagArchive.Read(File_));

        Assert.Single(records);
        Assert.Equal(5UL, records[0].FileId);
        Assert.Equal(TagColors.Red, records[0].Colors);
    }

    [Fact]
    public void 导出写成颜色名与ISO时刻且中文路径不转义()
    {
        TagArchive.Write([Record(1, TagColors.Red | TagColors.Purple, @"D:\资料\报表.txt")], File_);

        string json = File.ReadAllText(File_);

        // 位值是内部约定，"Purple" 才是别的工具能认的意思
        Assert.Contains("\"Red\"", json);
        Assert.Contains("\"Purple\"", json);
        Assert.DoesNotContain("\"colors\": 65", json);

        // 时刻要能一眼看出是哪天，而不是一串秒数
        Assert.Contains("2023-11-14T22:13:20", json);

        // 中文路径原样写出。转义后虽然仍是合法 JSON，但人就别想读懂了
        Assert.Contains("报表.txt", json);
        Assert.DoesNotContain("\\u62A5", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 认不出的颜色名跳过而不是整份文件失败()
    {
        File.WriteAllText(File_, """
            {"app":"jianlan","kind":"tags","version":1,"records":[
              {"volume":7,"fileId":9,"path":"D:\\x.txt","colors":["Red","Aquamarine"],"updatedAt":"2023-11-14T22:13:20+00:00"}
            ]}
            """);

        List<TagRecord> records = TagArchive.ToRecords(TagArchive.Read(File_));

        // 将来加了新颜色，旧版本应当保住认得的那几色，而不是一条都导不进来
        Assert.Equal(TagColors.Red, Assert.Single(records).Colors);
    }

    [Fact]
    public void 覆盖导出不留下tmp文件()
    {
        TagArchive.Write([Record(1, TagColors.Red)], File_);
        TagArchive.Write([Record(2, TagColors.Blue)], File_);

        Assert.False(File.Exists(File_ + ".tmp"));

        TagArchiveDocument document = TagArchive.Read(File_);
        Assert.Equal(2UL, Assert.Single(document.Records).FileId);
    }
}
