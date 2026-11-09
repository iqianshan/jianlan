using System.Text;

namespace Lingpane.Core.Diagnostics;

/// <summary>
/// 「复制诊断信息」要贴进 issue 的那一段（FR-10.8）。
///
/// 字段由调用方采集后传进来，本类只负责拼装：运行环境那几个值来自
/// <c>RuntimeInformation</c> 与 <c>Environment</c>，测试进程里读到的
/// 与用户机器上读到的不一样，直接读会让断言变成"跟当前机器绑定"。
///
/// 键名刻意保持英文：这段文本的读者是维护者，粘贴到 issue 里之后
/// 不应该因为界面语言而变成两份不同的东西（也不进资源文件）。
/// </summary>
public sealed record DiagnosticSnapshot
{
    /// <summary>完整版本串，含构建标识（<c>0.1.0+2d31cdc</c>）。</summary>
    public required string Version { get; init; }

    /// <summary>.NET 运行时描述，如 <c>.NET 9.0.0</c>。</summary>
    public required string Runtime { get; init; }

    /// <summary>操作系统描述，如 <c>Microsoft Windows 10.0.22631</c>。</summary>
    public required string OperatingSystem { get; init; }

    /// <summary>进程位数，如 <c>x64</c> / <c>x86</c>。</summary>
    public string? ProcessBits { get; init; }

    /// <summary>进程架构，如 <c>X64</c> / <c>Arm64</c>。</summary>
    public string? Architecture { get; init; }

    /// <summary>当前工作集，如 <c>128 MB</c>。取不到时留空。</summary>
    public string? Memory { get; init; }

    public required string ConfigDir { get; init; }

    public required string LogsDir { get; init; }

    public required string DataDir { get; init; }
}

public static class DiagnosticInfo
{
    /// <summary>
    /// 拼成"一行一项、冒号对齐"的纯文本。末尾只有一个换行：
    /// 多出来的空行贴进 issue 会变成一段空白引用块。
    /// </summary>
    public static string Build(DiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var text = new StringBuilder();
        text.Append("Lingpane ").AppendLine(snapshot.Version);
        Append(text, "Runtime", snapshot.Runtime);
        Append(text, "OS", snapshot.OperatingSystem);

        // 位数与架构合并成一行：两者分开写会让"x64 上跑 x86 进程"这件事
        // 需要读者自己对两行，而这正是最常需要一眼看出来的那种情况。
        string arch = CombineArchitecture(snapshot.Architecture, snapshot.ProcessBits);
        if (arch.Length > 0) Append(text, "Arch", arch);

        if (!string.IsNullOrWhiteSpace(snapshot.Memory)) Append(text, "Memory", snapshot.Memory);
        Append(text, "Config", snapshot.ConfigDir);
        Append(text, "Logs", snapshot.LogsDir);
        Append(text, "Data", snapshot.DataDir);

        return text.ToString();
    }

    private static string CombineArchitecture(string? architecture, string? processBits)
    {
        bool hasArch = !string.IsNullOrWhiteSpace(architecture);
        bool hasBits = !string.IsNullOrWhiteSpace(processBits);

        if (hasArch && hasBits) return $"{architecture} ({processBits} process)";
        if (hasArch) return architecture!;
        if (hasBits) return $"{processBits} process";
        return string.Empty;
    }

    private static void Append(StringBuilder text, string label, string value) =>
        text.Append(label.PadRight(8)).Append(": ").AppendLine(value);
}
