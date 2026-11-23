using Jianlan.Core.Input;

namespace Jianlan.Core.Tests;

/// <summary>
/// “改扩展名要不要拦一下”的判定。
///
/// 每一条对应一个真实场景：该弹的没弹——文件被改成打不开的类型；
/// 不该弹的弹了——只是改词干却被拦一次，用户很快就学会闭眼点"是"。
/// </summary>
public class RenameExtensionPolicyTests
{
    [Fact]
    public void 换扩展名_要问()
        => Assert.True(RenameExtensionPolicy.ChangesExtension("a.txt", "a.md", isDirectory: false));

    [Fact]
    public void 只改词干_不问()
        => Assert.False(RenameExtensionPolicy.ChangesExtension("报告.docx", "报告-2026.docx", isDirectory: false));

    [Fact]
    public void 扩展名大小写变化_不问()
        => Assert.False(RenameExtensionPolicy.ChangesExtension("a.txt", "a.TXT", isDirectory: false));

    [Fact]
    public void 删掉扩展名_要问()
        => Assert.True(RenameExtensionPolicy.ChangesExtension("a.txt", "a", isDirectory: false));

    [Fact]
    public void 加上扩展名_要问()
        => Assert.True(RenameExtensionPolicy.ChangesExtension("README", "README.md", isDirectory: false));

    [Fact]
    public void 目录里带点_不算扩展名()
        => Assert.False(RenameExtensionPolicy.ChangesExtension("v1.2", "v1.3", isDirectory: true));

    [Fact]
    public void 点开头的配置文件_换名字不问()
        => Assert.False(RenameExtensionPolicy.ChangesExtension(".gitignore", ".gitignore2", isDirectory: false));

    [Fact]
    public void 点开头的配置文件_加了后缀要问()
        => Assert.True(RenameExtensionPolicy.ChangesExtension(".gitignore", ".gitignore.bak", isDirectory: false));

    [Fact]
    public void 名字没变_不问()
        => Assert.False(RenameExtensionPolicy.ChangesExtension("a.txt", "a.txt", isDirectory: false));

    [Theory]
    [InlineData("a.txt", ".txt")]
    [InlineData("报告.docx", ".docx")]
    [InlineData("归档.tar.gz", ".gz")]
    [InlineData("无扩展名", "")]
    [InlineData(".gitignore", "")]
    [InlineData("v1.2", ".2")]
    [InlineData("", "")]
    public void 取扩展名(string name, string expected)
        => Assert.Equal(expected, RenameExtensionPolicy.ExtensionOf(name));
}
