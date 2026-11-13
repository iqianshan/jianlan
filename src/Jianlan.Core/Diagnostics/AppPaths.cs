namespace Jianlan.Core.Diagnostics;

/// <summary>
/// 应用数据目录解析。便携模式下全部落在 exe 同级 data\，
/// 否则落在 %LOCALAPPDATA%\Jianlan（FR-10.3）。
/// 启动路径上只做几次 File.Exists / Directory.Exists 判断，不枚举、不创建目录。
/// </summary>
public static class AppPaths
{
    /// <summary>便携标记文件名（FR-10.3 的正式口径）。</summary>
    public const string PortableFlag = "portable.flag";

    /// <summary>
    /// 0.1.0 早期的标记文件名。需求定稿前的包已经带着它出去过，
    /// 改名不能把这些包变成"放哪儿都不算便携"，所以继续认。
    /// </summary>
    public const string LegacyPortableMarker = "portable.marker";

    /// <summary>exe 同级的数据目录名：便携模式下它既是标记也是落点。</summary>
    public const string PortableDataDir = "data";

    /// <summary>非便携模式下的数据目录名（<c>%LOCALAPPDATA%\Jianlan</c>）。</summary>
    public const string AppDataFolder = "Jianlan";

    /// <summary>
    /// 改名前的数据目录名。0.2.x 及更早的版本叫 Lingpane，装过那些版本的机器上
    /// 收藏、标签、会话、设置都在这个目录里。
    /// </summary>
    public const string LegacyAppDataFolder = "Lingpane";

    public static string Root { get; } = ResolveRoot();

    public static bool IsPortable { get; private set; }

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string WorkspacesFile => Path.Combine(Root, "workspaces.json");

    public static string DataDir => Path.Combine(Root, PortableDataDir);

    /// <summary>
    /// 彩色标签库（FR-4.2.1）。放在 <c>data\</c> 而不是根目录：它是"用户数据"，
    /// 与 settings.json（配置）分开，备份或清空用户数据时边界清楚。
    /// 早期口径叫 <c>tags.db</c>（当初打算用 SQLite），存储引擎改追加日志后一并改名。
    /// </summary>
    public static string TagsFile => Path.Combine(DataDir, "tags.jsonl");

    public static string LogsDir => Path.Combine(Root, "logs");

    public static string CacheDir => Path.Combine(Root, "cache");

    /// <summary>
    /// 某个目录旁边是否需要按便携模式解析（FR-10.3）：存在 <c>portable.flag</c>、
    /// 早期口径 <c>portable.marker</c>，或直接存在 <c>data</c> 子目录。
    ///
    /// 三者等价，判定顺序不影响结果——它们最终都指向同一个落点 <c>exeDir\data</c>。
    /// 单独抽成公开方法是为了可测：<see cref="Root"/> 是静态只读属性、进程内只算一次，
    /// 要验证三种标记各自生效，就得能对任意目录单独问一次。
    /// </summary>
    public static bool IsPortableDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;

        return File.Exists(Path.Combine(directory, PortableFlag))
            || File.Exists(Path.Combine(directory, LegacyPortableMarker))
            || Directory.Exists(Path.Combine(directory, PortableDataDir));
    }

    private static string ResolveRoot()
    {
        string exeDir = AppContext.BaseDirectory;

        // 便携模式标记（FR-10.3）：exe 同级放 portable.flag、老口径 portable.marker，
        // 或者干脆已经有一个 data 目录——三种写法都算，落点都是 exe\data。
        if (IsPortableDirectory(exeDir))
        {
            IsPortable = true;
            return Path.Combine(exeDir, PortableDataDir);
        }

        return ResolveUserRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>
    /// 非便携模式的根目录，顺带把改名前留下的数据搬过来。
    ///
    /// 产品在 0.2.x 叫 Lingpane，数据落在 <c>%LOCALAPPDATA%\Lingpane</c>。更名后若不做处理，
    /// 用户升级一次看到的就是"收藏没了、标签没了、上次开的标签页也没了"。
    /// 迁移用 <see cref="Directory.Move(string, string)"/>：同卷上是改名，不复制字节。
    ///
    /// 三条守卫：新目录已存在就不动（用户可能已经在用新版本，别把旧数据叠进来）；
    /// 旧目录不存在就无事可做；迁移失败（权限、跨卷、被占用）时静默放弃——
    /// 改个名字不该让程序起不来，旧数据也留在原处，用户随时可以手工搬。
    ///
    /// 抽成公开方法是为了可测：真实调用点读的是机器上的 %LOCALAPPDATA%，
    /// 想验证"搬还是不搬"不可能去动那台机器的真实目录。
    /// </summary>
    public static string ResolveUserRoot(string localAppData)
    {
        string current = Path.Combine(localAppData, AppDataFolder);
        string legacy = Path.Combine(localAppData, LegacyAppDataFolder);

        if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;

        try
        {
            Directory.Move(legacy, current);
        }
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or ArgumentException
                                    or System.Security.SecurityException)
        {
            // 静默：启动路径上不弹窗、不写日志（日志目录可能就在这个根下面）
        }

        return current;
    }
}
