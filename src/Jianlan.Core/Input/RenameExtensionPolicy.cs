using System;

namespace Jianlan.Core.Input;

/// <summary>
/// 改名时"扩展名算不算变了"。判定放在 Core 是为了能用单测钉住，界面层只负责弹窗。
///
/// 为什么需要这条：文件类型是靠扩展名认的，`.txt` 改成 `.docx` 之后双击多半打不开。
/// 改名又是一次性落盘、没有回收站式的回退，所以动手之前得问一句。
///
/// 三条取舍：
///   · **只比扩展名，不比整名**。`报告.docx` → `报告-2026.docx` 是改词干，不打扰；
///   · **大小写不算改动**。Windows 与默认配置的 macOS 都不区分大小写，`.TXT` 改成 `.txt`
///     文件照样能打开，弹一次窗只是噪音；
///   · **目录不参与**。文件夹名里的点是名字的一部分（`v1.2` 不是"版本 2"的扩展名），
///     资源管理器对目录改名也从不提示扩展名。
/// </summary>
public static class RenameExtensionPolicy
{
    /// <summary>
    /// 从 <paramref name="oldName"/> 改成 <paramref name="newName"/> 是否改变了扩展名。
    /// </summary>
    public static bool ChangesExtension(string? oldName, string? newName, bool isDirectory)
    {
        if (isDirectory) return false;
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return false;

        return !string.Equals(
            ExtensionOf(oldName),
            ExtensionOf(newName),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取扩展名（含点）。没有扩展名时返回空串。
    ///
    /// 与 <see cref="System.IO.Path.GetExtension"/> 的差别只在点开头的名字：
    /// 那边把 `.gitignore` 的扩展名也算成 `.gitignore`，这里按"点开头且没别的点"
    /// 视为无扩展名——`.gitignore` 改成 `.gitignore2` 是换了个配置文件名，
    /// 不是把 txt 改成 docx 那种"文件类型变了"，不该弹窗。
    /// </summary>
    public static string ExtensionOf(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[dot..] : string.Empty;
    }
}
