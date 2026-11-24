using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Jianlan.App.ViewModels;
using Jianlan.Core.Commands;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Localization;

namespace Jianlan.App.Views;

/// <summary>
/// 空格预览浮层（FR-9.1.7）。按一下空格在列表上盖一块预览，再按一下、按 Esc、
/// 点任意处或窗口失去激活都会收起。
///
/// 与预览窗格（FR-9.1）的分工：
/// <list type="bullet">
/// <item>窗格是<b>常驻一栏</b>，拖得动、有操作按钮、能选中文本、PDF/Office 走系统预览处理器；</item>
/// <item>浮层是<b>按一下看一眼</b>，不占版面、不抢焦点，翻页能用方向键连着看。</item>
/// </list>
/// 两者各用一份 <see cref="PreviewViewModel"/>：共用一份会在开合与切选中时互相取消在途任务，
/// 表现为"内容闪一下变空"。代价是同一文件可能取两遍缩略图——Shell 缩略图有磁盘缓存，可接受。
/// </summary>
public partial class MainWindow
{
    private readonly PreviewViewModel _quickLook = new()
    {
        // 浮层里挂不住预览处理器：HwndHost 是真实子窗口，而浮层不激活、鼠标穿透。
        // PDF/Office 在浮层里给一张 Shell 缩略图，能翻页能播放的场合仍看预览窗格。
        UseShellHandler = false,

        // 按空格是显式请求：网络盘与可移动设备上的文件也要真的取一次内容。
        // 否则浮层会显示"点击立即预览"，而浮层鼠标穿透——那个按钮根本点不到。
        ExplicitRequest = true,
    };

    private QuickLookWindow? _quickLookWindow;

    private bool IsQuickLookOpen => _quickLookWindow is not null;

    private void InitializeQuickLook()
    {
        // 解码尺寸跟着浮层内容区的实际大小走：这个区域比窗格宽、但比窗格矮，
        // 竖图（手机截图、H5 设计稿）在这里受高度限制，沿用窗格的宽度会多解一倍有余的像素。
        _quickLook.DecodeSizeProvider = () => _quickLookWindow?.ContentPixelSize() ?? (640, 480);

        // 选中变化只跟活动窗格：另一个窗格里的选中是"上次点过"，不是"我此刻在看"。
        // 浮层关着时这里什么都不做——一次预览要读盘/解码，看不见的结果不该花钱。
        Pane.SelectionChanged += OnQuickLookSelectionChanged;
        Pane2.SelectionChanged += OnQuickLookSelectionChanged;

        // 鼠标"点别处就收起"。浮层是 WS_EX_TRANSPARENT，所有点击都落到主窗这一层，
        // 所以不需要给浮层单独挂监听；用 handledEventsToo 是因为列表会把手按下标成已处理。
        AddHandler(
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnQuickLookMouseDown),
            handledEventsToo: true);

        // 切到别的程序（或弹出模态确认窗）就收起：浮层背后已经不是"刚才那屏"了
        Deactivated += (_, _) => CloseQuickLook();
    }

    /// <summary>Alt+P 之外的入口：空格（FR-9.1.7）。</summary>
    private void ToggleQuickLook()
    {
        if (IsQuickLookOpen)
        {
            CloseQuickLook();
            return;
        }

        if (Tab.Kind != TabKind.Files) return;

        ShowQuickLook();
    }

    private void ShowQuickLook()
    {
        IReadOnlyList<FileEntry> selection = Vm.SelectedEntries;

        // 没有选中项就不弹：弹一块空板比什么都不发生更像出错
        if (selection.Count == 0) return;

        // 先把窗格那份停掉。同一个文件两边各解一遍，而浮层正好挡在它前面——
        // 两份并发实测各要 95–104ms，等于把翻页速度砍半，还白占磁盘带宽。
        _preview.Suspend();

        var window = new QuickLookWindow { DataContext = _quickLook };

        // 先登记再 Show：Show 之后主窗可能立刻收到 Deactivated（焦点还在切换），
        // 那时 CloseQuickLook 要能找到这个实例。
        _quickLookWindow = window;

        window.AttachTo(this);

        // Update 现在是"没有在途就立刻加载"，不需要再补一次 Retry 跳过延后——
        // 早先那两行是给 300ms 防抖开的侧门，现在门没了。
        _quickLook.Update(selection);

        window.Show();
    }

    private void CloseQuickLook()
    {
        if (_quickLookWindow is not { } window) return;

        _quickLookWindow = null;
        window.Close();

        // 丢位图引用。这里不调 Release()：它会连 Shell 缩略图的全局缓存一起清掉，
        // 而预览窗格可能正开着，被顺带清理等于让窗格下次重取一遍。
        _quickLook.Update([]);

        // 窗格在浮层期间是挂起的，这里补一次当前选中，否则它会停在浮层打开之前那张
        _preview.Resume();
        RefreshPreview();
    }

    private void OnQuickLookSelectionChanged(PaneView view)
    {
        if (!ReferenceEquals(view, ActiveView)) return;
        if (!IsQuickLookOpen) return;

        IReadOnlyList<FileEntry> selection = Vm.SelectedEntries;

        // 选到空处就收起：留着上一份内容的浮层会让人以为"这些文件都长这样"
        if (selection.Count == 0)
        {
            CloseQuickLook();
            return;
        }

        _quickLook.Update(selection);
    }

    private void OnQuickLookMouseDown(object sender, MouseButtonEventArgs e) => CloseQuickLook();

    private void RegisterQuickLookCommand()
    {
        _commands.Register(new AppCommand
        {
            Id = CommandIds.ToggleQuickLook,
            TitleFactory = () => Strings.Command_QuickLook,
            Gesture = "Space",
            Execute = ToggleQuickLook,

            // 只对目录标签里的非空选中可执行：主页/标签结果页没有"这个文件"可看
            CanExecute = () => Tab.Kind == TabKind.Files && Vm.SelectedEntries.Count > 0,
        });
    }
}
