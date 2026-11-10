using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lingpane.Core.Tagging;

/// <summary>
/// 标签库导出文件（FR-4.2.7 / FR-5.3.6）。
///
/// 为什么单独定义一套 DTO 而不是直接序列化 <see cref="TagRecord"/>：
/// 磁盘上的 <c>tags.jsonl</c> 是**操作日志**（set/del 行），而导出要的是**状态快照**——
/// 把日志原样拷出去，导回来还得再折叠一遍，且同一项可能出现多行。
/// 导出文件的字段名用全称，它是给人看、也可能被别的工具读的，不必像日志那样压成一两个字母。
///
/// 版本号独立于 settings 的 schemaVersion：标签格式将来若改（比如颜色超过 8 位），
/// 读旧文件仍要能认出来。
/// </summary>
public sealed class TagArchiveDocument
{
    public const string CurrentKind = "tags";
    public const int CurrentVersion = 1;

    /// <summary>写出方标识。导入时不校验具体值，只在缺失时提示"这可能不是标签库文件"。</summary>
    public string App { get; set; } = "lingpane";

    public string Kind { get; set; } = CurrentKind;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset ExportedAt { get; set; }

    public List<TagArchiveEntry> Records { get; set; } = [];
}

public sealed class TagArchiveEntry
{
    /// <summary>卷序列号。<see cref="FileId"/> 一起构成主键。</summary>
    public ulong Volume { get; set; }

    public ulong FileId { get; set; }

    /// <summary>导出时的路径。换台机器时它就是"这一项现在在哪"的唯一线索，所以必须带上。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 颜色名，如 <c>["Red","Blue"]</c>。
    ///
    /// 写名字而不是位值：位值是本程序的内部约定，将来加色、调顺序都会变；
    /// 而"红色"这种名字是语义，别的工具（哪怕不是 .NET 写的）照着也能用。
    /// 空数组表示该项其实没有颜色——正常不会出现，但读到别处造的文件时要能容下。
    /// </summary>
    public List<string> Colors { get; set; } = [];

    /// <summary>
    /// ISO 8601 时刻。写时刻而不是 Unix 秒：秒数在文本里既看不出是哪天，
    /// 也不带时区偏移；合并时要判"谁更新"，读的人得先自己做一遍换算。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>导入结果。三项分开报，是为了让状态栏能说清"为什么只进来一半"。</summary>
public readonly record struct TagImportResult(int Added, int Updated, int Skipped)
{
    public int Total => Added + Updated + Skipped;
}

/// <summary>导出/导入文件本身的失败原因（读不了、不是标签库）。</summary>
public sealed class TagArchiveException(string message) : Exception(message);

/// <summary>
/// 标签库的 JSON 导出与读取（FR-4.2.7）。合并策略在 <see cref="TagStore.Import"/> 里，
/// 这里只负责"文件和对象之间"这一段。
/// </summary>
public static class TagArchive
{
    /// <summary>把快照写成 JSON。返回写出的条数。</summary>
    /// <exception cref="TagArchiveException">目录不可写、磁盘满等。</exception>
    public static int Write(IReadOnlyList<TagRecord> records, string path, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var document = new TagArchiveDocument
        {
            ExportedAt = at ?? DateTimeOffset.UtcNow,
            Records = new List<TagArchiveEntry>(records.Count),
        };

        foreach (TagRecord record in records)
        {
            document.Records.Add(new TagArchiveEntry
            {
                Volume = record.Volume,
                FileId = record.FileId,
                Path = record.Path,
                Colors = ColorNames(record.Colors),
                UpdatedAt = record.UpdatedAt,
            });
        }

        try
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

            // 先写 .tmp 再改名：与 tags.jsonl 的压缩同一套。写到一半失败时，
            // 用户拿到的是一个"打不开的 tmp"，而不是一个"看起来正常、其实少了后半截"的导出文件。
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp,
                JsonSerializer.Serialize(document, TagArchiveJsonContext.Readable.TagArchiveDocument));
            System.IO.File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new TagArchiveException(ex.Message);
        }

        return document.Records.Count;
    }

    /// <summary>读入一份导出文件。格式不对时抛 <see cref="TagArchiveException"/>。</summary>
    public static TagArchiveDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            json = System.IO.File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TagArchiveException(ex.Message);
        }

        TagArchiveDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, TagArchiveJsonContext.Default.TagArchiveDocument);
        }
        catch (JsonException ex)
        {
            throw new TagArchiveException(ex.Message);
        }

        if (document is null)
        {
            throw new TagArchiveException("empty document");
        }

        // "kind" 是这份文件唯一的自证。校验它不是排外，而是避免用户把 settings.json
        // 拖进来之后，导入静默成功、条数为 0——那种"没报错也没反应"最难排查。
        if (!string.Equals(document.Kind, TagArchiveDocument.CurrentKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new TagArchiveException($"unexpected kind: {document.Kind}");
        }

        // 高版本文件仍尽量读：字段只会增不会改语义，多出来的字段被忽略即可。
        // 真正改语义时才会 bump 到 2，那时这里要加分支。
        document.Records ??= [];

        return document;
    }

    /// <summary>把文档里的记录还原成 <see cref="TagRecord"/>（非法项丢弃）。</summary>
    public static List<TagRecord> ToRecords(TagArchiveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var records = new List<TagRecord>(document.Records.Count);

        foreach (TagArchiveEntry entry in document.Records)
        {
            // FileId 为 0 的项在标签库里没有意义（见 TagStore.SetMany 的说明），
            // 导入时直接丢掉，不让它们占着 MaxRecords 的名额
            if (entry.FileId == 0) continue;

            records.Add(new TagRecord(
                entry.Volume,
                entry.FileId,
                entry.Path ?? string.Empty,
                ParseColors(entry.Colors),
                entry.UpdatedAt));
        }

        return records;
    }

    /// <summary>把位掩码摊成颜色名，按 <see cref="TagPalette.All"/> 的顺序。</summary>
    private static List<string> ColorNames(TagColors colors)
    {
        var names = new List<string>();

        foreach (TagColors one in TagPalette.All)
        {
            if (one != TagColors.None && (colors & one) == one) names.Add(one.ToString());
        }

        return names;
    }

    /// <summary>
    /// 颜色名还原成位掩码。认不出的名字跳过而不是整份文件报错：
    /// 将来版本加了新颜色，旧版本读的时候应当丢掉那一色、保住其余。
    /// </summary>
    private static TagColors ParseColors(List<string>? names)
    {
        TagColors colors = TagColors.None;

        if (names is null) return colors;

        foreach (string name in names)
        {
            if (Enum.TryParse(name, ignoreCase: true, out TagColors one) && one != TagColors.None)
            {
                colors |= one;
            }
        }

        return colors;
    }
}

/// <summary>导出文件的源生成元数据。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(TagArchiveDocument))]
internal sealed partial class TagArchiveJsonContext : JsonSerializerContext
{
    /// <summary>
    /// 写文件时用的那一份：不转义非 ASCII。
    /// 源生成上下文的默认编码器会把中文路径转成 <c>\uXXXX</c>——对"给人看的交换格式"是灾难。
    ///
    /// 这里必须把 attribute 上那几项**再写一遍**：传给上下文的 options 会整体替换默认那份，
    /// 少写一项就少一项。第一次只写了 Encoder，结果文件名全成了 PascalCase、缩进也没了。
    /// 带 <c>JsonSerializerOptions</c> 的构造函数由源生成器自己发出，这里只是提供一份预置选项。
    /// </summary>
    public static TagArchiveJsonContext Readable { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
