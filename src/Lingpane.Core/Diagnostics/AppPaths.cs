namespace Lingpane.Core.Diagnostics;

/// <summary>
/// 应用数据目录解析。便携模式下全部落在 exe 同级 data\，
/// 否则落在 %LOCALAPPDATA%\Lingpane（FR-10.3）。
/// 启动路径上只做一次 File.Exists 判断，不枚举、不创建目录。
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static bool IsPortable { get; private set; }

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string WorkspacesFile => Path.Combine(Root, "workspaces.json");

    public static string DataDir => Path.Combine(Root, "data");

    public static string TagsDbFile => Path.Combine(DataDir, "tags.db");

    public static string LogsDir => Path.Combine(Root, "logs");

    public static string CacheDir => Path.Combine(Root, "cache");

    private static string ResolveRoot()
    {
        string exeDir = AppContext.BaseDirectory;

        // 便携模式标记：与 exe 同级放一个 portable.marker 即生效
        if (File.Exists(Path.Combine(exeDir, "portable.marker")))
        {
            IsPortable = true;
            return Path.Combine(exeDir, "data");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lingpane");
    }
}
