using System.Windows;
using System.Windows.Threading;
using Lingpane.App.Diagnostics;
using Lingpane.App.Services;
using Lingpane.Core.Commands;

namespace Lingpane.App.Views;

/// <summary>
/// 窗口级输入的接线：驱动器插拔（FR-1.2.2）、显示器配置变化（FR-10.2）、鼠标侧键（FR-6.2）。
///
/// 三者都放在窗口这一层，因为它们跟"哪个标签、哪一半窗格"无关：
/// 插一个 U 盘时导航栏要更新的是同一个节点，拔掉外接屏时要挪的是同一扇窗。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 是否已排入一次驱动器刷新。
    ///
    /// 合并的必要性来自实测的消息节奏：插一个 U 盘会先来 <c>DBT_DEVNODES_CHANGED</c>、
    /// 再来 <c>DBT_DEVICEARRIVAL</c>，拔的时候同样两条。不去重就是每插一次盘重建两遍侧栏，
    /// 而重建会打断用户正在做的选择。
    /// </summary>
    private bool _driveRefreshQueued;

    /// <summary>
    /// 设备插拔后刷新「此电脑」那一段（FR-1.2.2 验收："无需重启"）。
    ///
    /// 用 <see cref="DispatcherPriority.Background"/> 而不是立即执行：
    /// 插拔瞬间系统正在忙枚举卷，此刻读 <c>DriveInfo</c> 最容易读到"还没就绪"的半成品。
    /// </summary>
    private void ScheduleDriveRefresh()
    {
        if (_driveRefreshQueued) return;
        _driveRefreshQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            _driveRefreshQueued = false;

            try
            {
                await _nav.ReloadDrivesAsync();
            }
            catch (Exception ex)
            {
                // 刷新失败不该有任何界面副作用：列表保持上一次的样子
                AppLog.Warn("nav.drives", "E9002", $"{ex.GetType().Name}: {ex.Message}");  // i18n-ignore：只进 app.log
            }
        }));
    }

    /// <summary>
    /// 鼠标侧键（FR-6.2）：XButton1 后退、XButton2 前进，与资源管理器一致。
    ///
    /// 走窗口消息而不是 WPF 的 <c>MouseDown</c>：后者在离屏/未激活窗口里收不到合成的
    /// <c>WM_XBUTTONDOWN</c>（实测投递后路径纹丝不动），而这正是快捷键表那套
    /// "不信 WPF 输入链路"的同一个理由。窗口消息这条路上，物理按键与投递的消息一视同仁。
    /// </summary>
    private void HandleMouseButtonMessage(int wParam, ref bool handled)
    {
        // XBUTTON 编号在 wParam 的高字：1 = 后退键，2 = 前进键
        int button = (wParam >> 16) & 0xFFFF;

        switch (button)
        {
            case 1:
                handled = true;
                Dispatch(CommandIds.GoBack);
                break;

            case 2:
                handled = true;
                Dispatch(CommandIds.GoForward);
                break;
        }
    }

    /// <summary>
    /// 屏幕配置变化后，把跑到可视区外的窗口挪回主屏中央（FR-10.2）。
    ///
    /// 只在"确实看不见"时动手：改分辨率、改缩放也会触发 <c>WM_DISPLAYCHANGE</c>，
    /// 那种情况下窗口位置通常仍然合法，无条件居中会让窗口在用户调显示设置时乱跳。
    /// </summary>
    private void EnsureVisibleOnScreen()
    {
        // 最大化/最小化时 Left/Top 是系统算的，这里改它没有意义
        if (WindowState != WindowState.Normal) return;

        var bounds = new Rect(Left, Top, Width, Height);
        if (WindowPlacement.IsSufficientlyVisible(bounds)) return;

        Rect target = WindowPlacement.CenterOnPrimary(bounds);

        Left = target.Left;
        Top = target.Top;
        Width = target.Width;
        Height = target.Height;

        AppLog.Info("window.recenter", $"屏幕配置变化后窗口越界，已居中到 ({target.Left:0}, {target.Top:0})");  // i18n-ignore：只进 app.log
    }
}
