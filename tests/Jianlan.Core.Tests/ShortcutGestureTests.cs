using Jianlan.Core.Input;

namespace Jianlan.Core.Tests;

/// <summary>
/// 快捷键文本的解析与格式化（FR-10.12）。
///
/// 这层必须钉死：设置文件里存的就是这段文本，解析失败会静默回落默认值，
/// 用户只会看到"我改的键位重启后又变回去了"。往返测试是唯一能挡住这类退化的手段。
/// </summary>
public sealed class ShortcutGestureTests
{
    [Theory]
    [InlineData("Ctrl+C", "Ctrl+C")]
    [InlineData("ctrl+c", "Ctrl+C")]
    [InlineData("Shift+Ctrl+T", "Ctrl+Shift+T")]
    [InlineData("Alt+Left", "Alt+Left")]
    [InlineData("Ctrl+Shift+F5", "Ctrl+Shift+F5")]
    [InlineData("Space", "Space")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    [InlineData("Ctrl+Shift+;", "Ctrl+Shift+;")]
    [InlineData("Alt+Enter", "Alt+Enter")]
    [InlineData("F11", "F11")]
    [InlineData("Ctrl+PageDown", "Ctrl+PageDown")]
    [InlineData("Ctrl+PgDn", "Ctrl+PageDown")]
    [InlineData("Ctrl+Esc", "Ctrl+Escape")]
    public void 解析后格式化回规范写法(string input, string expected)
    {
        Assert.True(ShortcutGesture.TryParse(input, out ShortcutGesture gesture));
        Assert.Equal(expected, gesture.Format());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt+")]
    [InlineData("Hyper+C")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Plus")]
    public void 认不出的写法一律失败(string? input)
    {
        Assert.False(ShortcutGesture.TryParse(input, out ShortcutGesture gesture));
        Assert.True(gesture.IsEmpty);
    }

    [Fact]
    public void 功能键的名字与键码对得上()
    {
        Assert.Equal("F1", ShortcutGesture.KeyName(0x70));
        Assert.Equal("F12", ShortcutGesture.KeyName(0x7B));
        Assert.Equal("F24", ShortcutGesture.KeyName(0x87));
    }

    [Fact]
    public void 字母键按大写规范化()
    {
        Assert.True(ShortcutGesture.TryParse("Ctrl+q", out ShortcutGesture gesture));
        Assert.Equal("Ctrl+Q", gesture.Format());
    }
}

/// <summary>"这条键位能不能给用户绑"的规则。默认表不受它约束，它只管用户在设置页录入的组合。</summary>
public sealed class ShortcutPolicyTests
{
    [Theory]
    [InlineData("T")]
    [InlineData("Shift+T")]
    [InlineData("Delete")]
    [InlineData("Space")]
    public void 没有控制键又不是功能键的拒绝(string text)
    {
        Assert.True(ShortcutGesture.TryParse(text, out ShortcutGesture gesture));
        Assert.Equal(ShortcutProblem.NeedsModifier, ShortcutPolicy.Check(gesture));
    }

    [Theory]
    [InlineData("F5")]
    [InlineData("F12")]
    [InlineData("Ctrl+Alt+J")]
    [InlineData("Alt+`")]
    public void 允许的组合(string text)
    {
        Assert.True(ShortcutGesture.TryParse(text, out ShortcutGesture gesture));
        Assert.Equal(ShortcutProblem.None, ShortcutPolicy.Check(gesture));
    }

    [Theory]
    [InlineData("Win+E")]
    [InlineData("Ctrl+Win+J")]
    [InlineData("Win+Shift+F9")]
    public void 带Win键的拒绝(string text)
    {
        Assert.True(ShortcutGesture.TryParse(text, out ShortcutGesture gesture));
        Assert.Equal(ShortcutProblem.WinNotAllowed, ShortcutPolicy.Check(gesture));
    }

    [Theory]
    [InlineData("Win+Shift+F9")]
    [InlineData("Win+J")]
    [InlineData("Ctrl+Win+J")]
    public void 全局唤起键允许Win组合(string text)
    {
        Assert.True(ShortcutGesture.TryParse(text, out ShortcutGesture gesture));
        Assert.Equal(ShortcutProblem.None, ShortcutPolicy.Check(gesture, allowWin: true));
    }

    [Theory]
    [InlineData("Shift+Delete")]
    [InlineData("Shift+Insert")]
    [InlineData("Shift+Home")]
    [InlineData("Shift+Left")]
    public void Shift配不产生文字的键允许(string text)
    {
        Assert.True(ShortcutGesture.TryParse(text, out ShortcutGesture gesture));
        Assert.Equal(ShortcutProblem.None, ShortcutPolicy.Check(gesture));
    }

    [Theory]
    [InlineData("Shift+Space")]
    [InlineData("Shift+Tab")]
    public void Shift配会打字或抢焦点的键仍拒绝(string text)
    {
        Assert.True(ShortcutGesture.TryParse(text, out ShortcutGesture gesture));
        Assert.Equal(ShortcutProblem.NeedsModifier, ShortcutPolicy.Check(gesture));
    }

    [Theory]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+Shift+Esc")]
    [InlineData("Ctrl+Alt+Delete")]
    public void 系统保留组合拒绝(string text)
    {
        Assert.True(ShortcutGesture.TryParse(text, out ShortcutGesture gesture));
        Assert.Equal(ShortcutProblem.Reserved, ShortcutPolicy.Check(gesture));
    }

    [Fact]
    public void 空手势是Empty()
    {
        Assert.Equal(ShortcutProblem.Empty, ShortcutPolicy.Check(ShortcutGesture.None));
    }

    [Theory]
    [InlineData("Ctrl+C", true)]
    [InlineData("Ctrl+V", true)]
    [InlineData("Ctrl+Shift+C", false)]
    [InlineData("Ctrl+Alt+C", false)]
    [InlineData("Ctrl+K", false)]
    public void 输入框会让位的组合(string text, bool expected)
    {
        Assert.True(ShortcutGesture.TryParse(text, out ShortcutGesture gesture));
        Assert.Equal(expected, ShortcutPolicy.IsTextEditingGesture(gesture));
    }
}
