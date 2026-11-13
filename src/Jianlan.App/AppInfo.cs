using System.Reflection;

namespace Jianlan.App;

/// <summary>
/// 程序版本号的唯一出口。
///
/// 读 <see cref="AssemblyInformationalVersionAttribute"/> 而不是 <c>Assembly.GetName().Version</c>：
/// 后者是四段数字（<c>0.1.0.0</c>），既没有预发布标记，也没有发版脚本追加的短 commit。
/// 后者恰恰是排查"用户到底跑的哪一次构建"的唯一线索。
///
/// 属性由发版脚本 <c>tools/release/publish.ps1</c> 通过 <c>-p:InformationalVersion</c> 写入
/// （形如 <c>0.1.0+a1b2c3d</c>）；本地直接 build 时退回 <c>Directory.Build.props</c> 的
/// <c>$(Version)</c>，只有三段数字。
/// </summary>
internal static class AppInfo
{
    /// <summary>完整版本串，含 <c>+短commit</c>（若发版脚本提供了的话）。</summary>
    public static string Version { get; } = Resolve();

    /// <summary>
    /// 给人看的版本串：去掉 <c>+</c> 之后的构建元数据。
    /// 关于页/窗口标题这类位置用这个；日志用 <see cref="Version"/>，两处用途不同。
    /// </summary>
    public static string DisplayVersion { get; } = CutBuildMetadata(Version);

    /// <summary>
    /// <c>+</c> 之后那段构建标识（通常是 7 位短 commit），没有时为 <c>null</c>。
    /// 关于页把它排在版本号旁边的小字里：用户报 bug 时，三段版本号只能定位到"哪个版本"，
    /// 带短号才能定位到"哪一次构建"。
    /// </summary>
    public static string? BuildTag { get; } = ResolveBuildTag();

    private static string? ResolveBuildTag()
    {
        int plus = Version.IndexOf('+');
        if (plus < 0 || plus == Version.Length - 1) return null;

        return Version[(plus + 1)..].Trim();
    }

    private static string Resolve()
    {
        Assembly asm = typeof(AppInfo).Assembly;

        string? informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational)) return informational.Trim();

        // 理论上走不到：SDK 一定会生成该特性。留兜底是为了"版本号读不到"不至于是空字符串。
        Version? fallback = asm.GetName().Version;
        return fallback is null ? "0.0.0" : $"{fallback.Major}.{fallback.Minor}.{fallback.Build}";
    }

    private static string CutBuildMetadata(string value)
    {
        int plus = value.IndexOf('+');
        return plus > 0 ? value[..plus] : value;
    }
}
