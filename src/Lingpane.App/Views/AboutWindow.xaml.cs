using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Lingpane.App.Services;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.FileSystem;
using Lingpane.Core.Localization;

namespace Lingpane.App.Views;

/// <summary>
/// 关于页（FR-10.8 / FR-10.9）。
///
/// 与设置页同一个取向：不引入视图模型，全是"采集一次 → 写进控件"的直线逻辑。
/// 所有数据都在构造时采集，窗口打开之后再没有任何 IO，关闭即释放。
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>发布页地址。不发任何网络请求，只是交给浏览器打开（FR-5.3.7 的"不联网"）。</summary>
    private const string ReleasesUrl = "https://github.com/lingpane/lingpane/releases";

    public AboutWindow()
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

    /// <summary>非模态打开，同一时刻只留一个实例（重复点「关于」不该叠出五扇窗）。</summary>
    public static void ShowFor(Window owner)
    {
        if (Application.Current.Windows.OfType<AboutWindow>().FirstOrDefault() is { } existing)
        {
            existing.Activate();
            return;
        }

        new AboutWindow { Owner = owner }.Show();
    }

    private void FillVersion()
    {
        VersionText.Text = AppInfo.DisplayVersion;

        if (AppInfo.BuildTag is { } tag)
        {
            BuildTagText.Text = string.Format(CultureInfo.CurrentCulture, Strings.About_Build, tag);
        }
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

        LicenseWindow.ShowText(this, title, text);
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
        string text = DiagnosticInfo.Build(new DiagnosticSnapshot
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
