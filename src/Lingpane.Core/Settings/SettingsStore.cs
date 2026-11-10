// i18n-ignore-file：本文件的中文字符串只进日志/诊断输出，不进界面，不参与翻译。
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lingpane.Core.Diagnostics;

namespace Lingpane.Core.Settings;

/// <summary>
/// settings.json 的读取与写入。规范见 docs/settings-schema.md。
/// 不引入配置框架：一个约 2KB 的 JSON 用 System.Text.Json 直读即可，
/// 加一层框架只会把启动路径变长（ADR-0004）。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            // 不写 null 会丢失"显式设为 null"的语义，且字体字段需要保留
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        // 枚举按 camelCase 字符串读写；非法值回落默认，不让整个文件解析失败
        o.Converters.Add(new LenientEnumConverter<ThemeKind>());
        o.Converters.Add(new LenientEnumConverter<DensityKind>());
        o.Converters.Add(new LenientEnumConverter<DefaultViewKind>());
        o.Converters.Add(new LenientEnumConverter<SortFieldKind>());
        o.Converters.Add(new LenientEnumConverter<NewTabModeKind>());

        // 收藏的 folder/file。不登记的话默认会写成数字 0/1——settings.json 是用户会打开看的，
        // 而且需求里写的就是 { kind(folder|file) }（docs/m2-plan.md §4.3）。
        o.Converters.Add(new LenientEnumConverter<BookmarkKind>());

        // 用源生成的元数据替掉反射元数据：省掉首次反序列化的类型构建
        o.TypeInfoResolver = SettingsJsonContext.Default;
        return o;
    }

    /// <summary>默认路径（安装版 / 便携版由 AppPaths 决定）。</summary>
    public static string DefaultPath => AppPaths.SettingsFile;

    /// <summary>
    /// 读取设置。永不抛异常：文件缺失、损坏、版本过高都返回可用的默认值。
    /// </summary>
    /// <param name="path">用于测试注入；默认走 AppPaths。</param>
    public static SettingsLoadResult Load(string? path = null)
    {
        path ??= DefaultPath;

        if (!File.Exists(path))
        {
            // 首次运行：用内置默认值，且不写盘
            return new SettingsLoadResult(new AppSettings(), null, false);
        }

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(new AppSettings(), $"设置文件无法读取：{ex.Message}", false);
        }

        AppSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch (JsonException ex)
        {
            // 损坏：原名备份为 .bak，用默认值继续，不阻塞启动
            string? warn = $"设置文件解析失败，已备份为 .bak：{ex.Message}";
            try
            {
                File.Move(path, path + ".bak", overwrite: true);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                warn += $"（备份也失败：{moveEx.Message}）";
            }

            return new SettingsLoadResult(new AppSettings(), warn, true);
        }

        if (parsed is null)
        {
            return new SettingsLoadResult(new AppSettings(), "设置文件内容为空，已使用默认值", false);
        }

        // 高版本：只读模式，禁止覆盖，避免抹掉新版本写入的字段
        bool readOnly = parsed.SchemaVersion > AppSettings.CurrentSchemaVersion;
        string? warning = readOnly
            ? $"设置文件版本 {parsed.SchemaVersion} 高于本程序支持的 {AppSettings.CurrentSchemaVersion}，本次以只读方式使用"
            : null;

        parsed.Normalize();
        return new SettingsLoadResult(parsed, warning, false) { IsReadOnly = readOnly };
    }

    /// <summary>
    /// 原子写入：先写 .tmp，再 File.Replace 并保留 .bak。
    /// 只读模式（高版本文件）下不写盘，返回 false。
    /// </summary>
    /// <returns>true 表示已落盘。</returns>
    public static bool Save(AppSettings settings, string? path = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        path ??= DefaultPath;

        if (isReadOnly) return false;

        settings.Normalize();

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        string json = JsonSerializer.Serialize(settings, Options);

        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path))
        {
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }

        return true;
    }
}

/// <summary>
/// 读取结果。Warning 非空时由调用方记一条日志并在状态栏提示，
/// 但不得中断启动——设置问题永远不该让程序打不开。
/// </summary>
public readonly record struct SettingsLoadResult(AppSettings Settings, string? Warning, bool IsCorrupt)
{
    /// <summary>文件版本高于本程序：可读不可写。</summary>
    public bool IsReadOnly { get; init; }
}

/// <summary>
/// 宽松枚举转换：非法字符串回落为该枚举的默认值（0），而不是抛异常。
/// 依据 settings-schema §3「theme 非法 → 回落 system」。
/// </summary>
internal sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? raw = reader.GetString();
            if (!string.IsNullOrEmpty(raw)
                && Enum.TryParse<T>(raw, ignoreCase: true, out var value)
                && Enum.IsDefined(value))
            {
                return value;
            }

            return default;
        }

        // 数字或 null：跳过并回落默认值
        if (reader.TokenType != JsonTokenType.Null) reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}
