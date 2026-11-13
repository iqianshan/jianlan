using Jianlan.Core.FileSystem;

namespace Jianlan.Core.Tests;

/// <summary>
/// 回收站还原动词的识别。
///
/// 这组用例的由来是一个实际踩过的坑：按完整名字列候选表（"Restore"、"还原(&amp;R)"…）
/// 在简体中文 Windows 上一个都对不上（真实名字是"还原(&amp;E)"），而 InvokeVerb
/// 传错名字不抛异常——删除能撤、还原静默失败，界面上只看到"未能撤销"。
/// </summary>
public class RecycleVerbNamesTests
{
    [Theory]
    [InlineData("还原(&E)")]
    [InlineData("还原(&R)")]
    [InlineData("还原")]
    [InlineData("复原(&E)")]
    [InlineData("復原(&E)")]
    [InlineData("Restore")]
    [InlineData("&Restore")]
    [InlineData("RESTORE")]
    [InlineData("undelete")]
    [InlineData("Un&Delete")]
    public void 认得各种写法的还原动词(string name)
        => Assert.True(RecycleVerbNames.IsRestore(name), name);

    [Theory]
    [InlineData("剪切(&T)")]
    [InlineData("删除(&D)")]
    [InlineData("属性(&R)")]
    [InlineData("Cut")]
    [InlineData("Delete")]
    [InlineData("Properties")]
    [InlineData("Open")]
    [InlineData("")]
    [InlineData(null)]
    public void 别把别的动作当成还原(string? name)
        => Assert.False(RecycleVerbNames.IsRestore(name), name ?? "(null)");

    [Theory]
    [InlineData("还原(&E)", "还原")]
    [InlineData("&Restore", "Restore")]
    [InlineData("剪切(&T)", "剪切")]
    [InlineData("Restore", "Restore")]
    [InlineData("", "")]
    public void 归一化去掉助记符与快捷键后缀(string input, string expected)
        => Assert.Equal(expected, RecycleVerbNames.Normalize(input));

    [Fact]
    public void 空名字归一化不炸()
        => Assert.Equal(string.Empty, RecycleVerbNames.Normalize(null));
}
