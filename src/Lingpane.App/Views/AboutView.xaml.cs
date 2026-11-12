using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Lingpane.App.Services;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Localization;

namespace Lingpane.App.Views;

/// <summary>
/// 关于内容（FR-10.8 / FR-10.9）。原先是独立窗口 <c>AboutWindow</c>，现改为用户控件，
/// 直接嵌在设置页的「关于」分区里——入口本来就只有设置页一颗按钮，
/// 再开一扇窗只是让用户多按一次关闭。
///
/// 与设置页同一个取向：不引入视图模型，全是"采集一次 → 写进控件"的直线逻辑。
/// 所有数据都在构造时采集，之后再不碰 IO。
/// </summary>
public partial class AboutView : UserControl
{
    /// <summary>发布页地址。不发任何网络请求，只是交给浏览器打开（FR-5.3.7 的"不联网"）。</summary>
    private const string ReleasesUrl = "https://github.com/lingpane/lingpane/releases";

    public AboutView()
    {
        InitializeComponent();

        FillVersion();
        FillEnvironment();

        RootPathText.Text = AppPaths.Root;
        RootPathText.ToolTip = AppPaths.Root;

        // 便携模式要在界面上说清楚：同一个 exe 放在 U 盘里数据就在旁边，
        // 用户找配置文件时第一个会问"到底存在哪"。与路径同一行，不另起一段。
        if (AppPaths.IsPortable)
        {
            RootPathText.Text = $"{AppPaths.Root}  ·  {Strings.About_Paths_Portable}";
        }
    }

    /// <summary>宿主窗口。<see cref="LicenseWindow"/> 与文件对话框都需要一个 Owner。</summary>
    private Window? HostWindow => Window.GetWindow(this);

    private void FillVersion()
    {
        VersionText.Text = AppInfo.DisplayVersion;

        if (AppInfo.BuildTag is { } tag)
        {
            BuildTagText.Text = string.Format(CultureInfo.CurrentCulture, Strings.About_Build, tag);
            return;
        }

        // 没有构建号（本地随手构建）时连分隔点一起收掉，不留一个孤立的"·"
        BuildTagText.Visibility = Visibility.Collapsed;
        BuildSeparator.Visibility = Visibility.Collapsed;
    }

    private void FillEnvironment()
    {
        RuntimeText.Text = RuntimeInformation.FrameworkDescription;
        OsText.Text = $"{RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})";
        ArchText.Text = $"{RuntimeInformation.ProcessArchitecture} · {(Environment.Is64BitProcess ? "x64" : "x86")}";

        long bytes = Environment.WorkingSet;
        MemoryText.Text = string.Format(
            CultureInfo.CurrentCulture, Strings.About_Env_MemoryValue, bytes / (1024 * 1024));
    }

    // ── 许可文本 ────────────────────────────────────────────────────────────

    private void OnViewLicense(object sender, RoutedEventArgs e) =>
        OpenLegal(LegalResources.License, Strings.About_License_View);

    private void OnViewNotice(object sender, RoutedEventArgs e) =>
        OpenLegal(LegalResources.Notice, Strings.About_License_Notice);

    private void OnViewThirdParty(object sender, RoutedEventArgs e) =>
        OpenLegal(LegalResources.ThirdParty, Strings.About_License_ThirdParty);

    private void OpenLegal(string logicalName, string title)
    {
        if (LegalResources.Read(logicalName) is not { Length: > 0 } text)
        {
            // 文件缺失是打包配置错误，不该把用户丢进一个空白窗口
            ShowStatus(Strings.About_License_Missing);
            return;
        }

        // LicenseWindow 要一个 Owner 才能在设置窗之上居中；游离状态下宁可不弹，
        // 也不弹一扇没有归属的窗口（本控件目前只挂在设置窗里，这里只是兜底）
        if (HostWindow is not { } owner)
        {
            ShowStatus(Strings.About_License_Missing);
            return;
        }

        LicenseWindow.ShowText(owner, title, text);
    }

    // ── 目录入口（FR-10.9）────────────────────────────────────────────────

    private void OnOpenConfigDir(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.Root);

    private void OnOpenLogsDir(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogsDir);

    private void OnOpenDataDir(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataDir);

    private void OpenFolder(string path)
    {
        try
        {
            // 日志目录在首次运行前不存在，直接 Process.Start 会以"找不到"失败
            Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            string code = FsErrors.CodeOf(ex);
            Diagnostics.AppLog.Warn("about.openFolder", code, $"{path}: {ex.GetType().Name}: {ex.Message}");

            ShowStatus(string.Format(
                CultureInfo.CurrentCulture,
                Strings.About_Paths_Failed,
                FsErrors.DescribeForOperation(code)));
        }
    }

    // ── 诊断与更新 ──────────────────────────────────────────────────────────

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        string text = BuildDiagnostics();

        try
        {
            Clipboard.SetText(text);
            ShowStatus(Strings.About_Diagnostics_Copied);
        }
        catch (ExternalException)
        {
            // 剪贴板被别的进程占着是常态（各类输入法、远程桌面），重试一次是用户的动作
            Diagnostics.AppLog.Warn("about.diagnostics", "clipboard_busy", "Clipboard.SetText failed");
            ShowStatus(Strings.About_Diagnostics_Failed);
        }
    }

    /// <summary>
    /// 诊断快照（版本 / 系统 / 三个目录）。「复制」与「导出日志」共用同一份内容——
    /// 两处各拼一遍，用户贴给我们的信息与包里那份就会不一致。
    /// </summary>
    private static string BuildDiagnostics() => DiagnosticInfo.Build(new DiagnosticSnapshot
    {
        Version = AppInfo.Version,
        Runtime = RuntimeInformation.FrameworkDescription,
        OperatingSystem = $"{RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})",
        ProcessBits = Environment.Is64BitProcess ? "x64" : "x86",
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Memory = $"{Environment.WorkingSet / (1024 * 1024)} MB",
        ConfigDir = AppPaths.Root,
        LogsDir = AppPaths.LogsDir,
        DataDir = AppPaths.DataDir,
    });

    /// <summary>
    /// 一键导出日志（FR-10.5）。需求原文是"支持一键导出"，验收写明"导出为 zip"。
    ///
    /// 为什么仍然要让用户选保存位置：日志里带完整路径，可能含客户名、项目名。
    /// 直接丢到某个固定目录再让他去找，等于替用户决定了"这份东西放在哪"。
    /// </summary>
    private void OnExportLogs(object sender, RoutedEventArgs e)
    {
        if (LogArchive.CollectFiles(AppPaths.LogsDir).Count == 0)
        {
            // 空包会让用户以为导出成功了但打开是空的，不如直接说还没有日志
            ShowStatus(Strings.About_Logs_Empty);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.About_Logs_SaveTitle,
            FileName = $"Lingpane-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExt = ".zip",
            Filter = "ZIP (*.zip)|*.zip",
            AddExtension = true,
            OverwritePrompt = true,
        };

        bool confirmed = HostWindow is { } owner
            ? dialog.ShowDialog(owner) == true
            : dialog.ShowDialog() == true;

        if (!confirmed) return;

        try
        {
            LogArchive.Export(dialog.FileName, AppPaths.LogsDir, BuildDiagnostics());
            ShowStatus(string.Format(CultureInfo.CurrentCulture, Strings.About_Logs_Exported, dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string code = FsErrors.CodeOf(ex);
            Diagnostics.AppLog.Warn("about.exportLogs", code, $"{ex.GetType().Name}: {ex.Message}");

            ShowStatus(string.Format(
                CultureInfo.CurrentCulture, Strings.About_Logs_Failed, FsErrors.DescribeForOperation(code)));
        }
    }

    private void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ReleasesUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Diagnostics.AppLog.Warn("about.updates", "open_failed", $"{ex.GetType().Name}: {ex.Message}");
            ShowStatus(Strings.About_Update_Failed);
        }
    }

    private void ShowStatus(string message) => StatusText.Text = message;
}
