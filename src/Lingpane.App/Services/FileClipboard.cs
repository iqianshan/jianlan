namespace Lingpane.App.Services;

/// <summary>
/// 内部剪贴板。只记录路径与"复制/剪切"，不与系统剪贴板互通用：
///   · 系统剪贴板要走 OLE（<c>CF_HDROP</c>），既要跨进程格式转换，又会在
///     其他程序占用剪贴板时抛 <c>COMException</c>，出错概率远大于收益；
///   · 需求只要求程序内复制粘贴，M1 不做跨程序拖放。
/// 与资源管理器互通放到 M2，届时改这里一处即可。
/// </summary>
internal static class FileClipboard
{
    public static IReadOnlyList<string> Paths { get; private set; } = [];

    /// <summary>true = 剪切（粘贴后删除源），false = 复制。</summary>
    public static bool IsCut { get; private set; }

    public static bool HasData => Paths.Count > 0;

    public static void Set(IReadOnlyList<string> paths, bool cut)
    {
        Paths = paths.ToArray();
        IsCut = cut;
    }

    public static void Clear()
    {
        Paths = [];
        IsCut = false;
    }
}
