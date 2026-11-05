using Lingpane.Core.Localization;

namespace Lingpane.Core.FileSystem;

/// <summary>
/// 文件系统异常 → 错误码 / 用户可读原因（docs/logging.md §4、§6）。
///
/// 为什么不直接把 <see cref="Exception.Message"/> 显示给用户：
/// 运行时抛出的消息来自 .NET 自带资源，跟随 **系统语言**，与应用内切换的界面语言无关。
/// 实测：界面切到中文后，状态栏仍是
/// <c>新建文件夹失败：Access to the path '...' is denied.</c>
/// 一半中文一半英文。所以界面只显示这里的译文，原文进 app.log。
/// </summary>
public static class FsErrors
{
    public const string PathNotFound = "E1001";
    public const string AccessDenied = "E1002";
    public const string DeviceNotReady = "E1003";
    public const string PathTooLong = "E1004";
    public const string InUse = "E1005";
    public const string DiskFull = "E1006";

    /// <summary>删除被拒但原因判不细（Shell 只给了个未文档化的返回码）。</summary>
    public const string DeleteRefused = "E1007";

    /// <summary>按异常类型与 Win32 码归类。认不出来时归到"路径不存在"，不硬猜更细的码。</summary>
    public static string CodeOf(Exception ex) => ex switch
    {
        UnauthorizedAccessException => AccessDenied,
        PathTooLongException => PathTooLong,
        DirectoryNotFoundException or FileNotFoundException => PathNotFound,
        DriveNotFoundException => DeviceNotReady,
        IOException io => CodeOfWin32(io.HResult),
        _ => PathNotFound,
    };

    /// <summary>
    /// 按异常文本归类。只用于已经拿到字符串、拿不到异常对象的场合
    /// （目录枚举把原因当字符串返回，见 <see cref="DirectoryEnumerator"/>）。
    /// 文本可能被系统语言本地化，所以中英两套关键词都要认。
    /// </summary>
    public static string CodeOf(string? message)
    {
        if (string.IsNullOrEmpty(message)) return PathNotFound;

        // 中文系统给的是"对路径…的访问被拒绝"，"拒绝"两字顺序与"拒绝访问"相反，
        // 只匹配完整词会漏掉一半句式
        if (message.Contains("拒绝", StringComparison.OrdinalIgnoreCase) ||          // i18n-ignore：识别系统异常文本，不是文案
            message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return AccessDenied;
        }

        if (message.Contains("占用", StringComparison.OrdinalIgnoreCase) ||          // i18n-ignore：识别系统异常文本，不是文案
            message.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("in use", StringComparison.OrdinalIgnoreCase))
        {
            return InUse;
        }

        if (message.Contains("空间不足", StringComparison.OrdinalIgnoreCase) ||      // i18n-ignore：识别系统异常文本，不是文案
            message.Contains("disk full", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
        {
            return DiskFull;
        }

        if (message.Contains("设备未就绪", StringComparison.OrdinalIgnoreCase) ||    // i18n-ignore：识别系统异常文本，不是文案
            message.Contains("device is not ready", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceNotReady;
        }

        if (message.Contains("过长", StringComparison.OrdinalIgnoreCase) ||          // i18n-ignore：识别系统异常文本，不是文案
            message.Contains("too long", StringComparison.OrdinalIgnoreCase))
        {
            return PathTooLong;
        }

        return PathNotFound;
    }

    /// <summary>导航失败的原因。认不出来时按"找不到此位置"处理。</summary>
    public static string DescribeForNavigation(string code) => code switch
    {
        AccessDenied => Strings.Error_AccessDenied,
        DeviceNotReady => Strings.Error_DeviceNotReady,
        PathTooLong => Strings.Error_PathTooLong,
        InUse => Strings.Error_InUse,
        DiskFull => Strings.Error_DiskFull,
        _ => Strings.Error_PathNotFound,
    };

    /// <summary>
    /// 文件操作失败的原因。与导航不同，认不出来时说"未知错误"而不是"路径不存在"——
    /// 复制失败最常见的原因不是路径不对，硬套会误导。
    /// </summary>
    public static string DescribeForOperation(string code) => code switch
    {
        AccessDenied => Strings.Error_AccessDenied,
        DeviceNotReady => Strings.Error_DeviceNotReady,
        PathTooLong => Strings.Error_PathTooLong,
        InUse => Strings.Error_InUse,
        DiskFull => Strings.Error_DiskFull,
        PathNotFound => Strings.Error_PathNotFound,
        DeleteRefused => Strings.Error_DeleteRefused,
        _ => Strings.Error_Unknown,
    };

    private static string CodeOfWin32(int hresult) => (hresult & 0xFFFF) switch
    {
        5 => AccessDenied,                          // ERROR_ACCESS_DENIED
        32 or 33 => InUse,                          // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        39 or 112 => DiskFull,                      // ERROR_HANDLE_DISK_FULL / ERROR_DISK_FULL
        19 or 21 => DeviceNotReady,                 // ERROR_WRITE_PROTECT / ERROR_NOT_READY
        206 => PathTooLong,                         // ERROR_FILENAME_EXCED_RANGE
        _ => PathNotFound,                          // 含 2 / 3：文件或路径不存在
    };
}
