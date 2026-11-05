// i18n-ignore-file：本文件的中文字符串只进日志/诊断输出，不进界面，不参与翻译。
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingpane.Core.Diagnostics;

namespace Lingpane.App.Views;

/// <summary>
/// 列表性能探针（仅诊断用，正常启动不进入此路径）。
///
/// 由 <c>--perf-probe &lt;目录&gt; [--perf-frames N]</c> 触发，采集 NF-03 / NF-04 / NF-05：
///   枚举耗时、首屏耗时、滚动帧率、工作集（峰值与稳定值）、句柄与 GDI 对象数。
///
/// 设计约束：
///   1. 只在窗口已显示后接管，测的是真实 UI 路径，不构造假环境。
///   2. 滚动步长按"每帧 3 行"驱动，模拟滚轮/拖动内容，而非拖动滚动条跳转。
///      后者会在一帧内把数百项拉进视口，测到的是极端场景而非日常滚动。
///   3. 工作集分两个值：滚动中的峰值、GC 后的稳定值。NF-05 用稳定值判定。
/// </summary>
public partial class MainWindow
{
    /// <summary>非 null 时进入探针模式，跳过默认目录导航。</summary>
    internal string? ProbePath { get; set; }

    /// <summary>滚动采样帧数，默认 240 帧。传 0 表示只测加载、不滚动。</summary>
    internal int ProbeFrames { get; set; } = 240;

    internal async Task RunPerfProbeAsync()
    {
        var r = new PerfProbeResult { Target = ProbePath ?? "(null)" };

        try
        {
            // 渲染层级：2 = 硬件加速，1 = 部分加速，0 = 软件渲染。
            // 软件渲染会让私有提交和 GDI 对象大幅上涨，此时的 NF-05 数字不代表正常环境，
            // 必须先记录层级再判断内存是否真的超标。
            r.RenderTier = RenderCapability.Tier >> 16;

            // 主题影响渲染成本（深色/高对比度画笔路径不同），记录实际生效值才能解释数字差异
            r.AppliedTheme = Design.ThemeService.Current.ToString();
            r.WindowSize = $"{Math.Round(ActualWidth)}x{Math.Round(ActualHeight)}";

            var sw = Stopwatch.StartNew();
            await _viewModel.NavigateAsync(ProbePath!);

            r.ItemCount = _viewModel.Entries.Count;
            r.EnumerateMs = _viewModel.LastEnumerateMs;

            // 打不开的目录会返回 0 项，单看 ItemCount 与"空目录"无法区分。
            // AC-05 的边界场景正是靠这个字段判断"优雅失败"还是"静默空表"。
            r.NavigateError = _viewModel.LastNavigationError;

            // 状态栏是用户唯一能看到的失败提示，截图在窗口较高时会被裁掉底部，
            // 探针直接把文本带出来，验收脚本据此核对文案。
            r.StatusText = _viewModel.StatusText;
            r.NavigateReturnMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);

            // 首屏：导航完成 → 列表完成一次排版与绘制
            await WaitForRenderAsync();
            r.FirstScreenMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);

            // 再放一帧，让首屏图标的加载与缓存落定，避免计入滚动成本
            await WaitForRenderAsync();

            // 加载完成后的工作集（未滚动）
            r.WorkingSetAfterLoadMb = CurrentWorkingSetMb();

            var sv = FindScrollViewer(FileList);
            if (sv is null)
            {
                r.Error = "未找到 ScrollViewer（模板可能变更）";
            }
            else
            {
                r.ScrollableHeight = Math.Round(sv.ScrollableHeight, 0);

                if (ProbeFrames > 0)
                {
                    double step = Math.Max(48, GetItemHeight(FileList) * 3);
                    r.ScrollStepPx = Math.Round(step, 0);

                    var stats = await SampleScrollAsync(sv, ProbeFrames, step);
                    r.Frames = stats.Frames;
                    r.DurationMs = Math.Round(stats.DurationMs, 1);
                    r.Fps = Math.Round(stats.Fps, 1);
                    r.AvgFrameMs = Math.Round(stats.AvgFrameMs, 2);
                    r.P95FrameMs = Math.Round(stats.P95FrameMs, 2);
                    r.MaxFrameMs = Math.Round(stats.MaxFrameMs, 2);
                    r.SlowFrames = stats.SlowFrames;
                    r.PeakWorkingSetMb = stats.PeakWorkingSetMb;
                    r.Samples = stats.Samples;
                }
            }

            r.HandleCountBefore = CurrentHandleCount();

            // 稳定值：回收后重新取，NF-05 以此判定（对应"浏览 10 分钟后"的常驻状态）
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await WaitForRenderAsync();

            r.WorkingSetMb = CurrentWorkingSetMb();
            r.PrivateMb = CurrentPrivateMb();
            r.HandleCount = CurrentHandleCount();
            r.GdiObjects = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        }
        catch (Exception ex)
        {
            r.Error = ex.ToString();
        }

        // 语言切换验证（AC-11 第 1 条：不重启、无闪烁、<200ms）。
        // 探针先按 settings 里的语言显示窗口，再在窗口已渲染后切换，
        // 外部截图看到的就是"同一次进程内换语言"的结果。
        // 支持逗号分隔的多次切换（如 "zh-Hans,en,zh-Hans"）：只切一次测不出第二次是否
        // 因为绑定逐层失效重算而变慢，AC-11 要求的是连续 3 次都达标。
        string? switchLanguage = Environment.GetEnvironmentVariable("LINGPANE_PERF_LANG");
        if (!string.IsNullOrEmpty(switchLanguage))
        {
            string[] languages = switchLanguage.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            r.LanguageSwitchMsList = new List<long>(languages.Length);
            foreach (string language in languages)
            {
                var watch = Stopwatch.StartNew();

                // 三个设置值走 Apply(string)，其余按 BCP-47 名字直接换文化。
                // 伪语言走查（qps-ploc）就走这条：它不是用户可选语言，
                // Apply(string) 会把它当未知值折回 auto，界面上就看不到伪文字。
                Core.Localization.LocalizationService.Instance.Apply(
                    language is Core.Localization.LocalizationService.SettingAuto
                             or Core.Localization.LocalizationService.SettingChinese
                             or Core.Localization.LocalizationService.SettingEnglish
                        ? Core.Localization.LocalizationService.Resolve(language)
                        : System.Globalization.CultureInfo.GetCultureInfo(language));
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                long elapsed = (long)watch.Elapsed.TotalMilliseconds;

                r.LanguageSwitchMsList.Add(elapsed);
                r.LanguageSwitchMs = elapsed;
                r.LanguageAfterSwitch = Core.Localization.LocalizationService.Instance.Culture.Name;
            }

            // 取一条真实资源值，证明切换后索引器确实返回新语言的文案，
            // 而不是只有 Culture 变了、绑定没重取。
            r.LanguageSampleText = Core.Localization.Strings.Menu_NewFolder;
        }

        r.CompletedAt = DateTime.Now;
        WriteResult(r);

        // 诊断停留：AC-05/AC-06 需要看清提示文案与版式，探针默认跑完即退，窗口一闪而过。
        // 环境变量而不是命令行参数，避免正常用户的启动参数里多出一种语义。
        if (int.TryParse(Environment.GetEnvironmentVariable("LINGPANE_PERF_HOLD"), out int hold) && hold > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(hold, 60)));
        }

        Application.Current.Shutdown();
    }

    private static double GetItemHeight(ListBox list)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement fe && fe.ActualHeight > 0)
        {
            return fe.ActualHeight;
        }
        return 32;
    }

    private static double CurrentWorkingSetMb()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1);
    }

    private static double CurrentPrivateMb()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return Math.Round(p.PrivateMemorySize64 / 1024.0 / 1024.0, 1);
    }

    private static int CurrentHandleCount()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.HandleCount;
    }

    private static Task WaitForRenderAsync()
    {
        var tcs = new TaskCompletionSource();

        void OnRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            tcs.TrySetResult();
        }

        CompositionTarget.Rendering += OnRendering;

        // 无视觉变化时 Rendering 可能不触发，2 秒兜底
        _ = Task.Delay(2000).ContinueWith(_ => tcs.TrySetResult(), TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>
    /// 逐帧驱动滚动并记录帧间隔。滚动会不断把新项拉进视口，
    /// 因此测的是"项容器复用 + 图标查询"的综合成本，而非纯绘制。
    ///
    /// 每 <see cref="SampleEvery"/> 帧采一次句柄/GDI/私有提交，用于判断资源增长是
    /// 线性泄漏还是一次性预热——只看首尾两个点无法区分二者。
    /// </summary>
    private static async Task<FrameStats> SampleScrollAsync(ScrollViewer sv, int frames, double step)
    {
        var intervals = new List<double>(frames);
        var samples = new List<ResourceSample>();
        var tcs = new TaskCompletionSource();
        long last = 0;
        int count = 0;
        double offset = 0;
        double max = sv.ScrollableHeight;
        double peakWs = 0;

        void OnRendering(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            if (last != 0) intervals.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last = now;

            offset += step;
            if (offset > max) offset = 0;
            sv.ScrollToVerticalOffset(offset);

            // 每 30 帧采一次，避免每帧都做系统调用
            if (count % 30 == 0)
            {
                double ws = CurrentWorkingSetMb();
                if (ws > peakWs) peakWs = ws;
                samples.Add(new ResourceSample
                {
                    Frame = count,
                    PrivateMb = CurrentPrivateMb(),
                    Handles = CurrentHandleCount(),
                    Gdi = GetGuiResources(Self.Handle, 0),
                });
            }

            if (++count >= frames)
            {
                CompositionTarget.Rendering -= OnRendering;
                tcs.TrySetResult();
            }
        }

        CompositionTarget.Rendering += OnRendering;

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (finished != tcs.Task)
        {
            CompositionTarget.Rendering -= OnRendering;
            return new FrameStats { Frames = intervals.Count, PeakWorkingSetMb = peakWs, Samples = samples };
        }

        double total = intervals.Sum();
        var sorted = intervals.OrderBy(x => x).ToArray();

        return new FrameStats
        {
            Frames = intervals.Count,
            DurationMs = total,
            Fps = total > 0 ? intervals.Count * 1000.0 / total : 0,
            AvgFrameMs = intervals.Count > 0 ? total / intervals.Count : 0,
            P95FrameMs = sorted.Length > 0
                ? sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)]
                : 0,
            MaxFrameMs = sorted.Length > 0 ? sorted[^1] : 0,
            // 超过 2 帧预算（33ms）计为一次可感知卡顿
            SlowFrames = sorted.Count(x => x > 33.3),
            PeakWorkingSetMb = peakWs,
            Samples = samples,
        };
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;

        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private static void WriteResult(PerfProbeResult r)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var json = JsonSerializer.Serialize(r, PerfProbeJson.Default.PerfProbeResult);
            File.WriteAllText(Path.Combine(AppPaths.LogsDir, "perf-probe.json"), json);
        }
        catch
        {
            // 探针失败不应影响进程退出
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    /// <summary>
    /// 采样用的进程句柄缓存。原先每次 <c>Process.GetCurrentProcess()</c> 都新建对象并打开进程句柄，
    /// 高频采样时自身就会推高句柄计数，污染被测指标。
    /// </summary>
    private static readonly Process Self = Process.GetCurrentProcess();

    private struct FrameStats
    {
        public int Frames;
        public double DurationMs;
        public double Fps;
        public double AvgFrameMs;
        public double P95FrameMs;
        public double MaxFrameMs;
        public int SlowFrames;
        public double PeakWorkingSetMb;
        public List<ResourceSample> Samples;
    }
}

/// <summary>滚动过程中的一次资源快照，用于判断增长是线性泄漏还是预热。</summary>
internal sealed class ResourceSample
{
    public int Frame { get; set; }
    public double PrivateMb { get; set; }
    public int Handles { get; set; }
    public uint Gdi { get; set; }
}

internal sealed class PerfProbeResult
{
    public string Target { get; set; } = string.Empty;
    public int ItemCount { get; set; }

    /// <summary>导航失败原因（null 表示目录成功打开）。边界场景验收据此区分"空目录"与"打不开"。</summary>
    public string? NavigateError { get; set; }

    /// <summary>导航完成后状态栏实际显示的文字。</summary>
    public string? StatusText { get; set; }

    /// <summary>WPF 渲染层级：2 硬件加速 / 1 部分加速 / 0 软件渲染。非 2 时内存与帧率均不可比。</summary>
    public int RenderTier { get; set; }

    /// <summary>实际生效的主题（Light / Dark / HighContrast），用于解释渲染成本差异。</summary>
    public string AppliedTheme { get; set; } = string.Empty;

    /// <summary>窗口客户区尺寸，确认 session 设置生效。</summary>
    public string WindowSize { get; set; } = string.Empty;

    /// <summary>目录枚举耗时（后台线程）。</summary>
    public long EnumerateMs { get; set; }

    /// <summary>NavigateAsync 返回到 UI 线程的时刻。</summary>
    public double NavigateReturnMs { get; set; }

    /// <summary>列表完成首帧绘制的耗时（NF-03）。</summary>
    public double FirstScreenMs { get; set; }

    public int Frames { get; set; }
    public double DurationMs { get; set; }
    public double Fps { get; set; }
    public double AvgFrameMs { get; set; }
    public double P95FrameMs { get; set; }
    public double MaxFrameMs { get; set; }
    public int SlowFrames { get; set; }
    public double ScrollableHeight { get; set; }
    public double ScrollStepPx { get; set; }

    /// <summary>加载完成、未滚动时的工作集。</summary>
    public double WorkingSetAfterLoadMb { get; set; }

    /// <summary>滚动过程中的工作集峰值。</summary>
    public double PeakWorkingSetMb { get; set; }

    /// <summary>GC 后的稳定工作集（NF-05 判定依据）。</summary>
    public double WorkingSetMb { get; set; }

    public double PrivateMb { get; set; }
    public int HandleCountBefore { get; set; }
    public int HandleCount { get; set; }
    public uint GdiObjects { get; set; }

    /// <summary>滚动期间的资源快照序列，用于区分线性泄漏与一次性预热。</summary>
    public List<ResourceSample>? Samples { get; set; }

    public string? Error { get; set; }

    /// <summary>探针内切语言耗时（毫秒）。未设置 LINGPANE_PERF_LANG 时为 null。</summary>
    public long? LanguageSwitchMs { get; set; }

    /// <summary>连续切换时每次的耗时（毫秒），顺序与 LINGPANE_PERF_LANG 一致。</summary>
    public List<long>? LanguageSwitchMsList { get; set; }

    /// <summary>切换后索引器取到的样本文案，证明资源层真的换了语言。</summary>
    public string? LanguageSampleText { get; set; }

    /// <summary>切换后的界面语言，用于确认切换真的生效。</summary>
    public string? LanguageAfterSwitch { get; set; }

    public DateTime CompletedAt { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(PerfProbeResult))]
internal sealed partial class PerfProbeJson : JsonSerializerContext
{
}
