using System.Windows.Input;

namespace Lingpane.App.Commands;

/// <summary>
/// 最简 ICommand：只用于不需要可执行性判断、也不需要出现在命令表中的零星动作
/// （例如"聚焦地址栏"这种纯界面行为）。
/// 业务动作请走 <see cref="CommandRegistry"/>，否则菜单项与快捷键会各写一份。
/// </summary>
public sealed class DelegateCommand : ICommand
{
    private readonly Action _execute;

    public DelegateCommand(Action execute) => _execute = execute;

    /// <summary>始终可执行，因此不需要订阅重查询。</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
