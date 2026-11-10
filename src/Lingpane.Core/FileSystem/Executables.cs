using System.IO;

namespace Lingpane.Core.FileSystem;

/// <summary>
/// 可执行文件的判定（FR-5.2.11）。
///
/// 名单是**显式列举**而不是"看扩展名像不像程序"：多拦一个 .txt 只是多一次确认，
/// 漏拦一个 .scr 就是用户在看图时被执行了屏保程序。判断依据只有扩展名——
/// 读 PE 头能更准，但那要为每一次双击多开一次文件句柄，而这一项的目的
/// 只是"在明显危险的地方拦一下"，不是杀毒。
/// </summary>
public static class Executables
{
    /// <summary>
    /// 会被"双击即执行"的扩展名。涵盖可执行、脚本、安装包、快捷方式四类。
    /// </summary>
    private static readonly HashSet<string> Risky = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif",              // 可执行
        ".bat", ".cmd", ".ps1", ".vbs", ".vbe",      // 脚本
        ".js", ".jse", ".wsf", ".wsh", ".hta",      // 脚本宿主
        ".msi", ".msp", ".msix", ".appx",           // 安装包
        ".reg",                                     // 注册表
        ".lnk", ".url",                             // 快捷方式（指向别处，看不出来）
    };

    /// <summary>这个路径双击会不会直接执行程序。目录一律不算。</summary>
    public static bool IsRisky(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        string extension = Path.GetExtension(path);
        return extension.Length > 0 && Risky.Contains(extension);
    }
}
