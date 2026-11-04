using Lingpane.Core.Commands;

namespace Lingpane.Core.Tests;

public sealed class CommandRegistryTests
{
    private static AppCommand Make(string id, Action? execute = null, Func<bool>? canExecute = null) =>
        new() { Id = id, Title = id, Execute = execute ?? (() => { }), CanExecute = canExecute };

    [Fact]
    public void 重复注册同一Id应抛异常()
    {
        var registry = new CommandRegistry();
        registry.Register(Make("a"));

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register(Make("a")));
        Assert.Contains("a", ex.Message);
    }

    [Fact]
    public void 不可执行的命令不会被invoke且返回false()
    {
        var registry = new CommandRegistry();
        bool ran = false;
        registry.Register(Make("a", () => ran = true, () => false));

        Assert.False(registry.Invoke("a"));
        Assert.False(ran);
    }

    [Fact]
    public void 可执行时invoke会调用一次并返回true()
    {
        var registry = new CommandRegistry();
        int count = 0;
        registry.Register(Make("a", () => count++));

        Assert.True(registry.Invoke("a"));
        Assert.Equal(1, count);
    }

    [Fact]
    public void 未注册的Id不应静默忽略执行()
    {
        var registry = new CommandRegistry();
        Assert.False(registry.Invoke("missing"));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("missing"));
    }

    [Fact]
    public void CanExecute为空视为始终可执行()
    {
        var registry = new CommandRegistry();
        registry.Register(Make("a"));

        Assert.True(registry.Get("a").CanRun());
    }
}
