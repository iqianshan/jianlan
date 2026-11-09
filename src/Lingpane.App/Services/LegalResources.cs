using System.IO;
using System.Reflection;

namespace Lingpane.App.Services;

/// <summary>
/// 开源合规文本的读取入口（FR-10.8 / AC-10）。
///
/// 三份文件在构建期以 <c>EmbeddedResource</c> 打进程序集（见 Lingpane.App.csproj），
/// 同时作为普通文件复制到发布目录——前者保证"程序内一定读得到"，
/// 后者满足 Apache-2.0 "随分发物提供 LICENSE / NOTICE" 的字面要求。
///
/// 只在用户点开时才读（每次 &lt;1ms），不进启动路径。
/// 读不到返回 <c>null</c> 而不是抛异常：构建配置错了应该显示一句"文件缺失"，
/// 不能因为一个关于页把整个程序带崩。
/// </summary>
internal static class LegalResources
{
    public const string License = "Legal.LICENSE";
    public const string Notice = "Legal.NOTICE";
    public const string ThirdParty = "Legal.ThirdParty";

    public static string? Read(string logicalName)
    {
        Assembly asm = typeof(LegalResources).Assembly;

        try
        {
            using Stream? stream = asm.GetManifestResourceStream(logicalName);
            if (stream is null)
            {
                Diagnostics.AppLog.Warn("about.legal", "missing", logicalName);
                return null;
            }

            // 许可文本是纯 ASCII/UTF-8，且我们要原样显示换行，不做任何规范化。
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            Diagnostics.AppLog.Warn("about.legal", "read_failed", $"{logicalName}: {ex.Message}");
            return null;
        }
    }
}
