using Jianlan.Core.Settings;

namespace Jianlan.Core.Tests;

/// <summary>
/// <c>shortcuts</c> 段的归一（settings-schema §3）。
///
/// 三条不能松的性质：写坏的键丢弃并回落默认、"空白"表示明确解绑且必须保留、
/// 规范形式要写回去（否则同一组键在文件里会有好几种写法）。
/// </summary>
public sealed class ShortcutSettingsTests
{
    [Fact]
    public void 认不出的键位被丢弃()
    {
        var settings = new ShortcutSettings
        {
            Overrides = new Dictionary<string, string>
            {
                ["app.refresh"] = "Ctrl+Shift+R",
                ["file.delete"] = "Left+Right",     // 认不出
                ["file.rename"] = "Ctrl+",          // 认不出
            },
        };

        settings.Normalize();

        Assert.Equal("Ctrl+Shift+R", settings.Overrides["app.refresh"]);
        Assert.False(settings.Overrides.ContainsKey("file.delete"));
        Assert.False(settings.Overrides.ContainsKey("file.rename"));
    }

    [Fact]
    public void 空白值表示解绑并被保留()
    {
        var settings = new ShortcutSettings
        {
            Overrides = new Dictionary<string, string>
            {
                ["file.delete"] = "",
                ["file.copy"] = "   ",
            },
        };

        settings.Normalize();

        Assert.Equal(string.Empty, settings.Overrides["file.delete"]);
        Assert.Equal(string.Empty, settings.Overrides["file.copy"]);
    }

    [Fact]
    public void 写法被规范化()
    {
        var settings = new ShortcutSettings
        {
            Overrides = new Dictionary<string, string>
            {
                ["app.refresh"] = "shift+ctrl+r",
                ["file.copy"] = "ctrl+INS",
            },
        };

        settings.Normalize();

        Assert.Equal("Ctrl+Shift+R", settings.Overrides["app.refresh"]);
        Assert.Equal("Ctrl+Insert", settings.Overrides["file.copy"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Ctrl+")]
    [InlineData("Nonsense")]
    public void 唤起键的坏值回落关闭(string? text)
    {
        var settings = new ShortcutSettings { ShowWindow = text };

        settings.Normalize();

        Assert.Null(settings.ShowWindow);
    }

    [Fact]
    public void 唤起键写成规范形式()
    {
        var settings = new ShortcutSettings { ShowWindow = "alt+ctrl+j" };

        settings.Normalize();

        Assert.Equal("Ctrl+Alt+J", settings.ShowWindow);
    }

    [Fact]
    public void 空对象归一后仍是空字典()
    {
        var settings = new ShortcutSettings { Overrides = null! };

        settings.Normalize();

        Assert.NotNull(settings.Overrides);
        Assert.Empty(settings.Overrides);
    }
}
