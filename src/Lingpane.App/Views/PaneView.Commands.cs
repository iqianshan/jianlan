using Lingpane.App.Commands;
using Lingpane.Core.Commands;
using Lingpane.Core.Localization;
using Lingpane.Core.Settings;
using Lingpane.Core.Tagging;

namespace Lingpane.App.Views;

/// <summary>
/// 窗格自己的命令表。
///
/// 为什么命令要跟着窗格走：命令的 <c>CanExecute</c> 读的是"当前目录"与"当前选中项"，
/// 而双窗格下这两个状态每个窗格各有一份。命令表放在窗口上时，所有 CanExecute 都要问
/// "活动窗格是谁"，然后每个命令的 Execute 也要再问一遍。放在窗格里，命令天然作用于
/// 自己所属的窗格，一句 if 都不需要。
///
/// 窗口级命令（设置、主题、标签页操作）仍留在 MainWindow。快捷键派发时
/// 先查活动窗格的表，查不到再落到窗口表——见 MainWindow.Dispatch。
/// </summary>
public partial class PaneView
{
    private readonly CommandRegistry _commands = new();

    public CommandAdapter UpCommand { get; private set; } = null!;
    public CommandAdapter BackCommand { get; private set; } = null!;
    public CommandAdapter ForwardCommand { get; private set; } = null!;
    public CommandAdapter RefreshCommand { get; private set; } = null!;
    public CommandAdapter DetailsCommand { get; private set; } = null!;
    public CommandAdapter ListViewCommand { get; private set; } = null!;
    public CommandAdapter ToggleSplitCommand { get; private set; } = null!;
    public CommandAdapter OpenItemCommand { get; private set; } = null!;
    public CommandAdapter NewFolderCommand { get; private set; } = null!;
    public CommandAdapter NewFileCommand { get; private set; } = null!;
    public CommandAdapter RenameCommand { get; private set; } = null!;
    public CommandAdapter DeleteCommand { get; private set; } = null!;
    public CommandAdapter DeletePermanentCommand { get; private set; } = null!;
    public CommandAdapter CopyItemsCommand { get; private set; } = null!;
    public CommandAdapter CutItemsCommand { get; private set; } = null!;
    public CommandAdapter PasteCommand { get; private set; } = null!;
    public CommandAdapter CopyPathCommand { get; private set; } = null!;
    public CommandAdapter PropertiesCommand { get; private set; } = null!;
    public CommandAdapter CalculateSizeCommand { get; private set; } = null!;
    public CommandAdapter MoreOptionsCommand { get; private set; } = null!;
    public CommandAdapter TagRemoveCommand { get; private set; } = null!;

    /// <summary>请求开关第二窗格。分屏是窗口形态，窗格只把意图告诉宿主（FR-3.3）。</summary>
    public event Action? SplitToggleRequested;

    internal CommandRegistry Commands => _commands;

    /// <summary>按 Id 执行本窗格的命令。快捷键派发用。</summary>
    internal bool TryInvoke(string id) => _commands.Invoke(id);

    /// <summary>本窗格是否注册了这条命令（区别于"注册了但当前不可执行"）。</summary>
    internal bool HasCommand(string id) => _commands.TryGet(id, out _);

    private void RegisterCommands()
    {
        _commands.Register(new AppCommand
        {
            Id = CommandIds.GoUp,
            TitleFactory = () => Strings.Command_GoUp,
            Gesture = "Alt+↑",
            CanExecute = CanGoUp,
            Execute = GoUp,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.GoBack,
            TitleFactory = () => Strings.Command_GoBack,
            Gesture = "Alt+←",
            CanExecute = () => _viewModel is { IsLoading: false, CanGoBack: true },
            Execute = () => _ = _viewModel?.GoBackAsync(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.GoForward,
            TitleFactory = () => Strings.Command_GoForward,
            Gesture = "Alt+→",
            CanExecute = () => _viewModel is { IsLoading: false, CanGoForward: true },
            Execute = () => _ = _viewModel?.GoForwardAsync(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Refresh,
            TitleFactory = () => Strings.Command_Refresh,
            Gesture = "F5",
            CanExecute = () => _viewModel is { IsLoading: false },
            Execute = () => _ = _viewModel?.RefreshAsync(),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ViewDetails,
            TitleFactory = () => Strings.Command_ViewDetails,
            Gesture = "Ctrl+Shift+1",
            Execute = () => _viewModel?.SetViewMode(DefaultViewKind.Details),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ViewList,
            TitleFactory = () => Strings.Command_ViewList,
            Gesture = "Ctrl+Shift+2",
            Execute = () => _viewModel?.SetViewMode(DefaultViewKind.List),
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.FocusAddress,
            TitleFactory = () => Strings.Command_FocusAddress,
            Gesture = "Ctrl+L",
            Execute = BeginAddressEdit,
        });

        // 分屏开关按钮。**故意不进 _commands 注册表**：F3 已经由窗口表处理，
        // 同一个 Id 出现在窗格表里会让 Dispatch 的"先窗格后窗口"规则多出一条
        // 语义相同的岔路，排查时反而分不清按 F3 走的是哪一条。
        // 这里只做一个纯粹的按钮适配器，出口仍是宿主的那一个 ToggleSplit。
        ToggleSplitCommand = new CommandAdapter(new AppCommand
        {
            Id = CommandIds.ToggleSplit + ".button",   // 不进注册表，Id 仅用于诊断
            TitleFactory = () => Strings.Command_ToggleSplit,
            Execute = () => SplitToggleRequested?.Invoke(),
        });

        // 文件操作。可执行性由选中状态与当前目录决定，
        // 右键菜单与快捷键共用这一份判断，不各写一遍。
        _commands.Register(new AppCommand
        {
            Id = CommandIds.OpenItem,
            TitleFactory = () => Strings.Command_Open,
            Execute = OpenSelection,
            CanExecute = HasSingleSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.NewFolder,
            TitleFactory = () => Strings.Command_NewFolder,
            Execute = () => _ = NewFolderAsync(),
            CanExecute = CanCreateInCurrentDirectory,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.NewFile,
            TitleFactory = () => Strings.Command_NewFile,
            Execute = () => _ = NewFileAsync(),
            CanExecute = CanCreateInCurrentDirectory,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Rename,
            TitleFactory = () => Strings.Command_Rename,
            Execute = () => _ = RenameSelectionAsync(),
            CanExecute = HasSingleSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Delete,
            TitleFactory = () => Strings.Command_Delete,
            Execute = () => _ = DeleteSelectionAsync(permanent: false),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.DeletePermanent,
            TitleFactory = () => Strings.Command_DeletePermanent,
            Execute = () => _ = DeleteSelectionAsync(permanent: true),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CopyItems,
            TitleFactory = () => Strings.Command_Copy,
            Execute = () => CopySelection(cut: false),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CutItems,
            TitleFactory = () => Strings.Command_Cut,
            Execute = () => CopySelection(cut: true),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Paste,
            TitleFactory = () => Strings.Command_Paste,
            Execute = () => _ = PasteAsync(),
            CanExecute = CanPaste,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CopyPath,
            TitleFactory = () => Strings.Command_CopyPath,
            Execute = CopyPathSelection,
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.Properties,
            TitleFactory = () => Strings.Command_Properties,
            Execute = ShowProperties,
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.MoreOptions,
            TitleFactory = () => Strings.Menu_MoreOptions,
            Execute = ShowShellContextMenuFromMenu,
        });
        // 彩色标签（FR-4.1.3）。设色走预设表 + EventSetter，不进注册表；
        // "移除"有确定语义，进注册表以便将来给快捷键。
        _commands.Register(new AppCommand
        {
            Id = CommandIds.RemoveTag,
            TitleFactory = () => Strings.Menu_Tag_Remove,
            Execute = () => _ = ApplyTagAsync(TagColors.None),
            CanExecute = HasSelection,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.CalculateSize,
            TitleFactory = () => Strings.Menu_CalculateSize,
            Execute = () => _ = _viewModel?.CalculateFolderSizesAsync(_viewModel.SelectedEntries),
            CanExecute = HasSelectedFolder,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ShowContextMenu,
            TitleFactory = () => Strings.Command_More,
            Execute = ShowContextMenu,
        });
        _commands.Register(new AppCommand
        {
            Id = CommandIds.SelectAll,
            TitleFactory = () => Strings.Command_SelectAll,
            Execute = SelectAllItems,
            CanExecute = () => _viewModel is { IsLoading: false } && ItemCount > 0,
        });

        UpCommand = new CommandAdapter(_commands.Get(CommandIds.GoUp));
        BackCommand = new CommandAdapter(_commands.Get(CommandIds.GoBack));
        ForwardCommand = new CommandAdapter(_commands.Get(CommandIds.GoForward));
        RefreshCommand = new CommandAdapter(_commands.Get(CommandIds.Refresh));
        DetailsCommand = new CommandAdapter(_commands.Get(CommandIds.ViewDetails));
        ListViewCommand = new CommandAdapter(_commands.Get(CommandIds.ViewList));
        OpenItemCommand = new CommandAdapter(_commands.Get(CommandIds.OpenItem));
        NewFolderCommand = new CommandAdapter(_commands.Get(CommandIds.NewFolder));
        NewFileCommand = new CommandAdapter(_commands.Get(CommandIds.NewFile));
        RenameCommand = new CommandAdapter(_commands.Get(CommandIds.Rename));
        DeleteCommand = new CommandAdapter(_commands.Get(CommandIds.Delete));
        DeletePermanentCommand = new CommandAdapter(_commands.Get(CommandIds.DeletePermanent));
        CopyItemsCommand = new CommandAdapter(_commands.Get(CommandIds.CopyItems));
        CutItemsCommand = new CommandAdapter(_commands.Get(CommandIds.CutItems));
        PasteCommand = new CommandAdapter(_commands.Get(CommandIds.Paste));
        CopyPathCommand = new CommandAdapter(_commands.Get(CommandIds.CopyPath));
        PropertiesCommand = new CommandAdapter(_commands.Get(CommandIds.Properties));
        CalculateSizeCommand = new CommandAdapter(_commands.Get(CommandIds.CalculateSize));
        MoreOptionsCommand = new CommandAdapter(_commands.Get(CommandIds.MoreOptions));
        TagRemoveCommand = new CommandAdapter(_commands.Get(CommandIds.RemoveTag));
    }
}
