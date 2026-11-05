using System.Text.Json.Serialization;

namespace Lingpane.Core.Settings;

/// <summary>
/// settings.json 的源生成序列化元数据。
/// 目的是把首次反序列化时的反射类型构建（约 20–40ms）挪到编译期；
/// 开关、命名策略必须与 <see cref="SettingsStore"/> 里的运行时选项保持一致。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
