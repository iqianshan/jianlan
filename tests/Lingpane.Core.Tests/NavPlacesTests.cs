using Lingpane.Core.Navigation;

namespace Lingpane.Core.Tests;

/// <summary>
/// 「位置」隐藏清单的规则（FR-1.2.9）。
///
/// 守的是两条最容易悄悄坏掉的性质：**默认值必须是"全部可见"**（否则升级即改界面），
/// 以及**认不出的键必须被丢掉**（否则旧版本残留会一直留在文件里，并且让"恢复全部显示"
/// 清不干净）。
/// </summary>
public sealed class NavPlacesTests
{
    [Fact]
    public void 六个已知文件夹加三项系统节点共九项()
    {
        Assert.Equal(9, NavPlaces.All.Count);
        Assert.Equal(
            ["Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos",
             "ThisPc", "RecycleBin", "Network"],
            NavPlaces.All);
    }

    [Fact]
    public void 空清单表示九项全部可见()
    {
        foreach (string key in NavPlaces.All)
        {
            Assert.False(NavPlaces.IsHidden([], key));
            Assert.False(NavPlaces.IsHidden(null, key));
        }
    }

    [Fact]
    public void 不在清单里的项可见()
    {
        IReadOnlyList<string> hidden = [NavPlaces.KeyOf(KnownFolderKind.Pictures)];

        Assert.True(NavPlaces.IsHidden(hidden, NavPlaces.KeyOf(KnownFolderKind.Pictures)));
        Assert.False(NavPlaces.IsHidden(hidden, NavPlaces.KeyOf(KnownFolderKind.Documents)));
    }

    /// <summary>主页与驱动器这类"不参与隐藏"的行没有键，任何清单都不该把它们藏起来。</summary>
    [Fact]
    public void 没有键的行永远可见()
    {
        IReadOnlyList<string> hidden = NavPlaces.All;

        Assert.False(NavPlaces.IsHidden(hidden, null));
        Assert.False(NavPlaces.IsHidden(hidden, string.Empty));
    }

    [Fact]
    public void 认不出的键被丢弃()
    {
        // 手改错的、上一版留下的、将来删掉某一项之后的残留
        IReadOnlyList<string> normalized = NavPlaces.Normalize(["Musik", "Desktop", ""]);

        Assert.Equal(["Desktop"], normalized);
    }

    [Fact]
    public void 大小写不同的重复项合并成枚举名()
    {
        IReadOnlyList<string> normalized = NavPlaces.Normalize(["desktop", "DESKTOP", "ThisPc", "thispc"]);

        Assert.Equal(["Desktop", "ThisPc"], normalized);
    }

    [Fact]
    public void 归一后按显示顺序排列()
    {
        // 用户在菜单里从下往上勾，落盘前要排回侧栏的顺序
        IReadOnlyList<string> normalized = NavPlaces.Normalize(["Network", "Downloads", "Desktop"]);

        Assert.Equal(["Desktop", "Downloads", "Network"], normalized);
    }

    [Fact]
    public void 全是垃圾键时归一为空()
    {
        // 空 = 全部可见。认不出任何一项时宁可全部显示，也不要"全隐藏"这种把人锁在外面的结果
        Assert.Empty(NavPlaces.Normalize(["Musik", "  ", null!]));
    }
}
