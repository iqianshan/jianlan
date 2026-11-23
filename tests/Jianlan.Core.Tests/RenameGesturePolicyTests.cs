using Jianlan.Core.Input;

namespace Jianlan.Core.Tests;

/// <summary>
/// “再点一次改名”的判定规则。这张真值表对应资源管理器的手感，
/// 每一条都对应一个会让人“点了没反应”或“手一抖改了名”的真实场景。
/// </summary>
public class RenameGesturePolicyTests
{
    private static bool Arm(
        int clickCount = 1,
        bool doubleClickOpens = true,
        bool hasModifiers = false,
        bool hasFocus = true,
        bool isSelected = true,
        int selectionCount = 1,
        bool onNameCell = true,
        bool alreadyEditing = false)
        => RenameGesturePolicy.ShouldArmRename(
            doubleClickOpens, clickCount, hasModifiers, hasFocus, isSelected, selectionCount, onNameCell, alreadyEditing);

    [Fact]
    public void 慢速第二次点击已选中项的名称_进入改名()
        => Assert.True(Arm());

    [Fact]
    public void 第一次点击_只选中不改名()
        => Assert.False(Arm(isSelected: false));

    [Fact]
    public void 双击序列里的第二次按下_不算独立点击()
        => Assert.False(Arm(clickCount: 2));

    [Fact]
    public void 带修饰键_让给多选与新标签()
    {
        Assert.False(Arm(hasModifiers: true));
    }

    [Fact]
    public void 多选状态下_先收敛成单选()
        => Assert.False(Arm(selectionCount: 2));

    [Fact]
    public void 点在图标或行尾空白_不改名()
        => Assert.False(Arm(onNameCell: false));

    [Fact]
    public void 单击打开模式下_第二次点击是打开()
        => Assert.False(Arm(doubleClickOpens: false));

    [Fact]
    public void 列表没焦点时_第一下只切换活动窗格()
        => Assert.False(Arm(hasFocus: false));

    [Fact]
    public void 已经在编辑中_不重复进入()
        => Assert.False(Arm(alreadyEditing: true));

    [Theory]
    [InlineData("报告.docx", false, 0, 2)]
    [InlineData("a.txt", false, 0, 1)]
    [InlineData("无扩展名", false, 0, 4)]
    [InlineData(".gitignore", false, 0, 10)]
    [InlineData("照片", true, 0, 2)]
    [InlineData("带.点的文件夹", true, 0, 7)]
    [InlineData("", false, 0, 0)]
    public void 预选范围_文件选词干_文件夹整名(string name, bool isDirectory, int start, int length)
    {
        (int actualStart, int actualLength) = RenameGesturePolicy.StemSelection(name, isDirectory);

        Assert.Equal(start, actualStart);
        Assert.Equal(length, actualLength);
    }
}
