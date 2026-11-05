using System.Windows.Input;
using Lingpane.Core.Commands;

namespace Lingpane.App.Commands;

/// <summary>
/// 把 Core 的 <see cref="AppCommand"/> 适配成 WPF 可绑定的 ICommand。
/// 可执行性变化依赖 CommandManager 的自动重查询（焦点、键鼠输入后触发），
/// 不手工维护 CanExecuteChanged，避免忘记触发导致按钮一直是灰的。
/// </summary>
public sealed class CommandAdapter : ICommand
{
    private readonly AppCommand _command;

    public CommandAdapter(AppCommand command) => _command = command;

    public AppCommand Source => _command;

    public string Title => _command.ResolveTitle();

    public string? Gesture => _command.Gesture;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _command.CanRun();

    public void Execute(object? parameter)
    {
        // 快捷键与按钮走同一路径，不可执行时静默忽略
        if (_command.CanRun()) _command.Execute();
    }
}
