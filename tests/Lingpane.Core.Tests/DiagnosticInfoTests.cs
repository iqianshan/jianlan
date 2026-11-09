using Lingpane.Core.Diagnostics;

namespace Lingpane.Core.Tests;

/// <summary>
/// 「复制诊断信息」的文本拼装（FR-10.8）。
/// 断言只看字段有没有如实落到文本里，不在这里造运行环境——
/// 那些值由界面层采集后传进来。
/// </summary>
public sealed class DiagnosticInfoTests
{
    private static DiagnosticSnapshot Full() => new()
    {
        Version = "0.1.0+2d31cdc",
        Runtime = ".NET 9.0.0",
        OperatingSystem = "Microsoft Windows 10.0.22631",
        ProcessBits = "x64",
        Architecture = "X64",
        Memory = "128 MB",
        ConfigDir = @"C:\Users\me\AppData\Local\Lingpane",
        LogsDir = @"C:\Users\me\AppData\Local\Lingpane\logs",
        DataDir = @"C:\Users\me\AppData\Local\Lingpane\data",
    };

    [Fact]
    public void 首行是版本号且带构建标识()
    {
        string text = DiagnosticInfo.Build(Full());

        Assert.StartsWith("Lingpane 0.1.0+2d31cdc", text);
    }

    [Fact]
    public void 环境与三个目录都在文本里()
    {
        string text = DiagnosticInfo.Build(Full());

        Assert.Contains(".NET 9.0.0", text);
        Assert.Contains("Microsoft Windows 10.0.22631", text);
        Assert.Contains(@"C:\Users\me\AppData\Local\Lingpane\logs", text);
        Assert.Contains(@"C:\Users\me\AppData\Local\Lingpane\data", text);
    }

    [Fact]
    public void 架构与进程位数合并成一行()
    {
        string text = DiagnosticInfo.Build(Full());

        Assert.Contains("X64 (x64 process)", text);
    }

    [Fact]
    public void 缺项整行省略而不是留空标签()
    {
        DiagnosticSnapshot snapshot = Full() with
        {
            Architecture = null,
            ProcessBits = null,
            Memory = null,
        };

        string text = DiagnosticInfo.Build(snapshot);

        Assert.DoesNotContain("Arch", text);
        Assert.DoesNotContain("Memory", text);
    }

    [Fact]
    public void 只有进程位数时仍能写出架构行()
    {
        DiagnosticSnapshot snapshot = Full() with { Architecture = null };

        Assert.Contains("Arch    : x64 process", DiagnosticInfo.Build(snapshot));
    }

    [Fact]
    public void 末尾只有一个换行()
    {
        string text = DiagnosticInfo.Build(Full());

        Assert.EndsWith(Environment.NewLine, text);
        Assert.False(text.EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal));
    }

    [Fact]
    public void 传入null抛异常()
    {
        Assert.Throws<ArgumentNullException>(() => DiagnosticInfo.Build(null!));
    }
}
