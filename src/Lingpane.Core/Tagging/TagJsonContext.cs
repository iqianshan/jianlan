using System.Text.Json.Serialization;

namespace Lingpane.Core.Tagging;

/// <summary>
/// <c>tags.jsonl</c> 里的一行。字段名压到一两个字母不是省字节（一条记录 160 B 里
/// 字段名占不到 20 B），而是为了"用户打开文件手改"时不至于一行占满整个屏幕。
///
/// 每行是一条**操作**而不是一条状态：<c>set</c> 与 <c>del</c>。
/// 追加日志的好处是打一次标签只写一行、不需要重写整个文件；
/// 代价是同一个键会出现多行，读的时候按顺序折叠（后写的赢）。
/// </summary>
internal sealed class TagOpLine
{
    [JsonPropertyName("op")] public string? Op { get; set; }

    /// <summary>卷序列号。</summary>
    [JsonPropertyName("v")] public ulong V { get; set; }

    /// <summary>FileId。</summary>
    [JsonPropertyName("f")] public ulong F { get; set; }

    /// <summary>冗余路径，只作线索。</summary>
    [JsonPropertyName("p")] public string? P { get; set; }

    /// <summary>
    /// 颜色位掩码（FR-4.1.2 的多色就靠它）。可空：<c>del</c> 行没有颜色与时间，
    /// 写成 <c>"c":0,"t":0</c> 只会让这份用户会打开看的文件多两列无意义字段。
    /// </summary>
    [JsonPropertyName("c")] public byte? C { get; set; }

    /// <summary>Unix 秒。同样只在 <c>set</c> 行出现。</summary>
    [JsonPropertyName("t")] public long? T { get; set; }
}

/// <summary>
/// 标签日志的源生成元数据。用源生成而不是反射：本项目零 <c>PackageReference</c>、
/// 且在启动路径上不发反射开销（与 <c>SettingsJsonContext</c> 同一套做法）。
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TagOpLine))]
internal sealed partial class TagJsonContext : JsonSerializerContext
{
}
