namespace Lingpane.Core.Preview;

/// <summary>
/// 一次预览实际走的路径（FR-9.1.3）。
///
/// 与 <see cref="PreviewKind"/> 的区别：<see cref="PreviewKind"/> 只说"文件是什么类型"，
/// 这里说"这次准备怎么做"。同一个 <see cref="PreviewKind.Shell"/> 可能走
/// <see cref="Handler"/>（系统里有这个类型的预览处理器，能翻页、能播放），
/// 也可能只有 <see cref="Thumbnail"/>（处理器没装或激活失败，只出一张图）。
/// </summary>
public enum PreviewRoute
{
    /// <summary>没有可展示的内容（目录、无扩展名）。</summary>
    None,

    /// <summary>内置读取/解码。文本与位图永远走这条。</summary>
    BuiltIn,

    /// <summary>拉起系统预览处理器，由它自己往宿主窗口里画。</summary>
    Handler,

    /// <summary>向 Shell 要一张缩略图。</summary>
    Thumbnail,
}

/// <summary>
/// 决定一次预览走哪条路径。
///
/// 单独抽出来的理由是这一条判断错得很隐蔽：PDF 明明装了处理器，却因为路由写错
/// 一直显示成缩略图，界面上看起来"能用"，用户不会报 bug，但翻页和播放永远没有。
/// 抽成纯函数就能用单元测试钉住。
/// </summary>
public static class PreviewRouter
{
    /// <summary>
    /// <paramref name="hasHandler"/> 表示系统里注册了这个扩展名可用的预览处理器。
    /// </summary>
    public static PreviewRoute Decide(PreviewKind kind, bool hasHandler) => kind switch
    {
        PreviewKind.Text => PreviewRoute.BuiltIn,
        PreviewKind.Image => PreviewRoute.BuiltIn,

        // 处理器优先：它能翻页、能播放，而缩略图只有第一页/首帧
        PreviewKind.Shell => hasHandler ? PreviewRoute.Handler : PreviewRoute.Thumbnail,

        _ => PreviewRoute.None,
    };

    /// <summary>
    /// 值不值得为这个类型去查注册表。文本与位图不用查——内建路径既快又可控，
    /// 查到处理器也不会用。这里的判断与 <see cref="Decide"/> 必须一致，
    /// 否则会出现"查到了但不用"的无谓往返。
    /// </summary>
    public static bool NeedsHandlerLookup(PreviewKind kind) => kind == PreviewKind.Shell;
}
