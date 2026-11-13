using Jianlan.Core.Preview;

namespace Jianlan.Core.Tests;

/// <summary>
/// 预览路由（FR-9.1.3）。重点不是"分支正确"，而是"文本与图片永远不会被处理器抢走"——
/// 那条路会把可选中的文本换成一张画出来的图，功能上等于退步。
/// </summary>
public sealed class PreviewRouterTests
{
    [Theory]
    [InlineData(PreviewKind.Text)]
    [InlineData(PreviewKind.Image)]
    public void 文本与图片即使有处理器也走内建(PreviewKind kind)
    {
        Assert.Equal(PreviewRoute.BuiltIn, PreviewRouter.Decide(kind, hasHandler: true));
        Assert.Equal(PreviewRoute.BuiltIn, PreviewRouter.Decide(kind, hasHandler: false));
        Assert.False(PreviewRouter.NeedsHandlerLookup(kind));
    }

    [Fact]
    public void Shell类型有处理器时优先走处理器()
    {
        // 关键路径：PDF / 视频只有走处理器才能翻页与播放
        Assert.Equal(PreviewRoute.Handler, PreviewRouter.Decide(PreviewKind.Shell, hasHandler: true));
        Assert.True(PreviewRouter.NeedsHandlerLookup(PreviewKind.Shell));
    }

    [Fact]
    public void Shell类型没有处理器时回落缩略图()
    {
        Assert.Equal(PreviewRoute.Thumbnail, PreviewRouter.Decide(PreviewKind.Shell, hasHandler: false));
    }

    [Fact]
    public void 没有内容可展示时不路由()
    {
        Assert.Equal(PreviewRoute.None, PreviewRouter.Decide(PreviewKind.None, hasHandler: true));
        Assert.False(PreviewRouter.NeedsHandlerLookup(PreviewKind.None));
    }
}
