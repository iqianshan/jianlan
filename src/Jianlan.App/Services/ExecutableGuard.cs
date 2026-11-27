using System.Globalization;
using System.IO;
using System.Windows;
using Jianlan.App.Views;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Localization;

namespace Jianlan.App.Services;

/// <summary>
/// 打开可执行文件前的确认（FR-5.2.11）。
///
/// 判定和问句只有这一份，因为"打开一个文件"的入口不止一条：列表双击 / 回车、
/// 预览窗格的「打开」、拖到标签栏、从收藏里点。以前只有第一条经过确认，
/// 拖拽那条直接 <c>Process.Start</c>——把一批文件拖进窗口时混着一个 <c>.exe</c>
/// 就绕过去了，而"手滑拖进来"与"手滑双击"在用户那边是同一件事。
/// </summary>
public static class ExecutableGuard
{
    /// <summary>
    /// 问一句再打开。返回 <c>true</c> 表示继续。
    ///
    /// 设置项关闭或不是可执行文件时直接放行，不进任何对话框——这是绝大多数调用的实际路径，
    /// 不要为了"统一"把判断挪到调用方，那会让四处的口径重新散开。
    /// </summary>
    public static bool Confirm(Window? owner, string fullPath)
    {
        if (!App.Settings.Behavior.WarnOnExecutable) return true;
        if (!Executables.IsRisky(fullPath)) return true;

        // 问句里同时给文件名和**完整路径**：只给文件名的话，"报告.exe"和"报告.exe"
        // 长得一模一样，而真正要看的就是它在哪个目录——伪装成文档的程序都靠这一点露馅。
        return ConfirmWindow.Ask(
            owner,
            Strings.Dialog_ExecutableTitle,
            string.Format(
                CultureInfo.CurrentCulture,
                Strings.Dialog_ExecutableBody,
                Path.GetFileName(fullPath),
                fullPath));
    }
}
