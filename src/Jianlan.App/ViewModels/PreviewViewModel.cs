using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Jianlan.App.Services;
using Jianlan.App.Shell;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Localization;
using Jianlan.Core.Preview;

namespace Jianlan.App.ViewModels;

/// <summary>
/// 预览窗格的状态（FR-9.1）。
///
/// 一次预览要跨过三道异步：300ms 防抖（<see cref="DebounceMs"/>）、读盘/解码、
/// 以及随时可能到来的下一次选中。因此这里有一份 <c>_generation</c> 递增票据——
/// 结果回来时票号对不上就整份丢掉。只靠 <see cref="CancellationToken"/> 不够：
/// 取消是协作式的，已经算完的结果照样会回来。
/// </summary>
public sealed class PreviewViewModel : INotifyPropertyChanged
{
    /// <summary>选中后延迟多久才真正去读文件（FR-9.1.2）。</summary>
    public const int DebounceMs = 300;

    /// <summary>
    /// 预览内容的内存预算（FR-9.2.7）。超了就不放内容，只留基本信息。
    ///
    /// 这里的口径是"这一次预览自己占了多少"，不是进程工作集：
    /// 工作集里混着框架、列表虚拟化缓存等与本功能无关的部分，拿它做判据会让
    /// 降级在别的模块吃内存时莫名其妙地触发。解码出来的像素数 + 文本字符数是可核算的。
    /// </summary>
    public const long BudgetBytes = 40L * 1024 * 1024;

    private readonly DispatcherTimer _timer;

    private IReadOnlyList<FileEntry> _selection = [];
    private FileEntry? _current;
    private CancellationTokenSource? _cts;
    private int _generation;

    private bool _autoPreview = true;

    private bool _isEmpty = true;
    private bool _isMultiple;
    private bool _hasImage;
    private bool _hasText;
    private bool _hasNote;
    private bool _canRetry;
    private bool _hasHandler;

    private string _handlerPath = string.Empty;
    private Guid _handlerClsid;

    private string _titleText = string.Empty;
    private string _metaText = string.Empty;
    private string _pathText = string.Empty;
    private string _noteText = string.Empty;
    private string _footerText = string.Empty;
    private string _textContent = string.Empty;
    private ImageSource? _image;

    public PreviewViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            StartPreview();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 解码宽度（物理像素）。由视图在每次请求前提供——窗格宽度可以拖动，
    /// 拖窄之后还按 1024 解码等于白占内存。
    /// </summary>
    public Func<int>? DecodeWidthProvider { get; set; }

    /// <summary>选中后是否自动预览（FR-5.2.10 的"触发方式"）。</summary>
    public bool AutoPreview
    {
        get => _autoPreview;
        set => _autoPreview = value;
    }

    /// <summary>什么都没选中。空态那一句提示绑它。</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set
        {
            if (_isEmpty == value) return;

            _isEmpty = value;
            Raise(nameof(IsEmpty));
            Raise(nameof(HasSelection));
        }
    }

    /// <summary>多选。需求要求此时只报数量与总大小，不预览其中某一个（FR-9.1.6）。</summary>
    public bool IsMultiple
    {
        get => _isMultiple;
        private set => Set(ref _isMultiple, value, nameof(IsMultiple));
    }

    /// <summary>
    /// 选中了东西。标题块（文件名 / 类型 / 路径）绑它，空态时不显示那三行。
    ///
    /// 单独出一个属性而不是在 XAML 里取反：<c>BooleanToVisibilityConverter</c> 没有取反参数，
    /// 为它写一个转换器不如让视图模型直接说清"有没有选中"。
    /// </summary>
    public bool HasSelection => !_isEmpty;

    /// <summary>
    /// 预览窗格里那三颗操作按钮（FR-9.1.5）的作用对象。
    ///
    /// 这里不另存一份"当前项"，而是从 <c>_selection</c> 现算：预览内容有 300ms 防抖，
    /// 存一份就会与选中短暂不一致——用户点了"打开"，打开的却还是上一个文件。
    /// 单选才算数；多选与空选都没有唯一目标。
    /// </summary>
    public FileEntry? Current => _selection.Count == 1 ? _selection[0] : null;

    /// <summary>三颗操作按钮是否可用。与 <see cref="Current"/> 同一判据，单独暴露是给绑定用。</summary>
    public bool CanAct => _selection.Count == 1;

    public bool HasImage
    {
        get => _hasImage;
        private set => Set(ref _hasImage, value, nameof(HasImage));
    }

    public bool HasText
    {
        get => _hasText;
        private set => Set(ref _hasText, value, nameof(HasText));
    }

    /// <summary>选中了单个条目，但取不到可视内容——显示图标 + 说明。</summary>
    public bool HasNote
    {
        get => _hasNote;
        private set => Set(ref _hasNote, value, nameof(HasNote));
    }

    /// <summary>当前这一项需要用户点一下才预览（网络路径 / 可移动设备，FR-9.2.8）。</summary>
    public bool CanRetry
    {
        get => _canRetry;
        private set => Set(ref _canRetry, value, nameof(CanRetry));
    }

    /// <summary>
    /// 这一项由系统预览处理器渲染（FR-9.1.3）。视图看到它为 true 就建宿主并交给处理器。
    ///
    /// 之所以用"路径 + CLSID"两个独立属性而不是传一个对象：视图在属性变更通知里
    /// 只拿得到属性名，看不到值；分开的话「先写路径与 CLSID、最后置 true」这个顺序
    /// 就能保证视图动手时两样都已就位。
    /// </summary>
    public bool HasHandler
    {
        get => _hasHandler;
        private set => Set(ref _hasHandler, value, nameof(HasHandler));
    }

    public string HandlerPath
    {
        get => _handlerPath;
        private set => Set(ref _handlerPath, value, nameof(HandlerPath));
    }

    public Guid HandlerClsid
    {
        get => _handlerClsid;
        private set => Set(ref _handlerClsid, value, nameof(HandlerClsid));
    }

    public string TitleText
    {
        get => _titleText;
        private set => Set(ref _titleText, value, nameof(TitleText));
    }

    /// <summary>"类型 · 大小 · 修改日期"，用中点连接。</summary>
    public string MetaText
    {
        get => _metaText;
        private set => Set(ref _metaText, value, nameof(MetaText));
    }

    public string PathText
    {
        get => _pathText;
        private set => Set(ref _pathText, value, nameof(PathText));
    }

    /// <summary>取不到内容时的说明，或"点一下预览"的提示。</summary>
    public string NoteText
    {
        get => _noteText;
        private set => Set(ref _noteText, value, nameof(NoteText));
    }

    /// <summary>内容下方的补充说明（截断提示等）。空串时不占位。</summary>
    public string FooterText
    {
        get => _footerText;
        private set => Set(ref _footerText, value, nameof(FooterText));
    }

    public string TextContent
    {
        get => _textContent;
        private set => Set(ref _textContent, value, nameof(TextContent));
    }

    public ImageSource? Image
    {
        get => _image;
        private set => Set(ref _image, value, nameof(Image));
    }

    /// <summary>文件图标。取不到缩略图时用它在空白处撑个视觉锚点。</summary>
    public ImageSource? Icon => _current is null ? null : ShellIconProvider.GetLarge(_current.IconKey);

    // ── 外部入口 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 选中项变了。空选与多选立刻结算（都不需要读盘），单选走 300ms 防抖。
    /// </summary>
    public void Update(IReadOnlyList<FileEntry> selection)
    {
        _selection = selection;
        Raise(nameof(Current));
        Raise(nameof(CanAct));

        if (selection.Count != 1)
        {
            _timer.Stop();
            CancelInFlight();
            ApplySummary(selection);
            return;
        }

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>「立即预览」按钮与右键菜单的入口（FR-9.2.8）。跳过防抖与延后判定。</summary>
    public void Retry()
    {
        _timer.Stop();
        if (_selection.Count == 1) Prepare(_selection[0], force: true);
    }

    /// <summary>
    /// 关掉预览窗格时调用（FR-9.2.6）。作废在途任务、丢掉位图引用、清空缩略图缓存——
    /// 少任何一步，关掉窗格后那几张位图仍被静态缓存按着不放，内存账面上看不出来。
    /// </summary>
    public void Release()
    {
        _timer.Stop();
        CancelInFlight();

        _selection = [];
        _current = null;
        Raise(nameof(Current));
        Raise(nameof(CanAct));
        ClearHandler();
        Image = null;
        TextContent = string.Empty;
        IsEmpty = true;
        IsMultiple = false;
        HasImage = false;
        HasText = false;
        HasNote = false;
        CanRetry = false;
        TitleText = string.Empty;
        MetaText = string.Empty;
        PathText = string.Empty;
        NoteText = string.Empty;
        FooterText = string.Empty;
        Raise(nameof(Icon));

        ShellThumbnail.ClearCache();
    }

    // ── 内部实现 ────────────────────────────────────────────────────────────

    private void CancelInFlight()
    {
        _generation++;

        if (_cts is null) return;

        // 只取消不 Dispose：在途的读盘/解码手里还握着这个 token，
        // 提前释放会让它们访问已销毁的源。这东西不含计时器与句柄，交给 GC 即可。
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts = null;
    }

    private void ApplySummary(IReadOnlyList<FileEntry> selection)
    {
        ClearHandler();
        _current = null;
        HasImage = false;
        HasText = false;
        HasNote = false;
        CanRetry = false;
        FooterText = string.Empty;
        TextContent = string.Empty;
        Image = null;
        PathText = string.Empty;
        Raise(nameof(Icon));

        if (selection.Count == 0)
        {
            IsEmpty = true;
            IsMultiple = false;
            TitleText = Strings.Preview_Title;
            MetaText = string.Empty;
            NoteText = string.Empty;
            return;
        }

        long total = 0;
        foreach (FileEntry entry in selection) total += entry.Size;

        IsEmpty = false;
        IsMultiple = true;
        TitleText = string.Format(CultureInfo.CurrentCulture, Strings.Preview_MultiTitle, selection.Count);
        MetaText = string.Format(
            CultureInfo.CurrentCulture, Strings.Preview_MultiSize, FileEntry.FormatSizeText(total));
        NoteText = string.Empty;
    }

    private void StartPreview()
    {
        if (_selection.Count != 1) return;

        FileEntry entry = _selection[0];
        ApplyHeader(entry);
        Prepare(entry, force: false);
    }

    /// <summary>
    /// 决定这一份文件怎么预览。防抖之后的唯一入口，<see cref="Retry"/> 也走这里。
    ///
    /// 顺序是刻意的：先排除根本不需要读盘的（目录、延后触发、超体积），
    /// 再问注册表——查注册表虽有缓存，但没必要为一张 2GB 的视频去问。
    /// </summary>
    private void Prepare(FileEntry entry, bool force)
    {
        if (entry.IsDirectory)
        {
            ClearHandler();
            _current = entry;
            IsEmpty = false;
            IsMultiple = false;
            HasImage = false;
            HasText = false;
            HasNote = true;
            CanRetry = false;
            NoteText = Strings.Preview_Folder;
            return;
        }

        if (!force && (!AutoPreview || IsSlowPath(entry.FullPath)))
        {
            ClearHandler();
            _current = entry;
            IsEmpty = false;
            IsMultiple = false;
            HasImage = false;
            HasText = false;
            HasNote = true;
            CanRetry = true;
            NoteText = Strings.Preview_Deferred;
            return;
        }

        PreviewKind kind = PreviewClassifier.Classify(entry.FullPath, entry.Size, entry.IsDirectory);

        // 超体积在这一层挡掉：走处理器的路不经过 PreviewLoader，那边还有一道检查，
        // 但等到那边才拦就晚了——处理器已经拉起，prevhost.exe 已经开始解析文件头
        long limit = PreviewClassifier.LimitFor(kind, entry.FullPath);
        if (limit > 0 && entry.Size > limit)
        {
            ClearHandler();
            _current = entry;
            IsEmpty = false;
            IsMultiple = false;
            HasImage = false;
            HasText = false;
            HasNote = true;
            CanRetry = false;
            NoteText = string.Format(
                CultureInfo.CurrentCulture, Strings.Preview_Note_TooLarge, PreviewLoader.DescribeLimit(limit));
            return;
        }

        Guid clsid = PreviewRouter.NeedsHandlerLookup(kind)
            ? PreviewHandlerRegistry.Find(entry.FullPath)
            : Guid.Empty;

        if (PreviewRouter.Decide(kind, clsid != Guid.Empty) == PreviewRoute.Handler)
        {
            StartHandler(entry, clsid);
            return;
        }

        _ = LoadAsync(entry);
    }

    private void StartHandler(FileEntry entry, Guid clsid)
    {
        CancelInFlight();

        // 从"另一个处理器文件"切过来时，HasHandler 本来就为 true；
        // 不先落回 false 的话视图收不到变化通知，会继续显示上一个文件
        ClearHandler();

        _current = entry;
        IsEmpty = false;
        IsMultiple = false;
        HasImage = false;
        HasText = false;
        HasNote = false;
        TextContent = string.Empty;
        Image = null;
        CanRetry = false;

        // "正在准备"写在脚注而不是内容区：处理器的窗口是 HwndHost（真实子窗口），
        // 它一出现就把内容区整块盖住（WPF 的空域问题），写在里面等于看不见
        FooterText = Strings.Preview_HandlerPreparing;

        HandlerPath = entry.FullPath;
        HandlerClsid = clsid;

        // 放最后：视图收到这个通知时，路径与 CLSID 必须已经在位
        HasHandler = true;
    }

    /// <summary>处理器把内容画出来了（FR-9.1.3）。收掉"正在准备"那一行提示。</summary>
    public void HandlerReady()
    {
        // 已经切走了：这条是上一个文件的迟到通知，别把新文件的提示也清掉
        if (!_hasHandler) return;

        HasNote = false;
        NoteText = string.Empty;
        FooterText = string.Empty;
    }

    /// <summary>
    /// 处理器没干成（没装、激活失败、渲染返回错误）。
    ///
    /// 处理方式分两步：先把这一类的处理器记成不可用，避免下次选中同类文件再走一遍
    /// 同样的失败；再退回缩略图——缩略图只是第一页，但比一片空白有用。
    /// </summary>
    public void HandlerFailed()
    {
        // 同 HandlerReady：迟到的失败不能拿来标记当前选中的另一个文件
        if (!_hasHandler || _current is null) return;

        FileEntry entry = _current;
        PreviewHandlerRegistry.MarkBroken(entry.FullPath);

        ClearHandler();
        _ = LoadAsync(entry, handlerFailed: true);
    }

    /// <summary>放弃处理器渲染：视图据此卸载处理器并拆掉宿主窗口。</summary>
    private void ClearHandler()
    {
        if (!_hasHandler) return;

        HasHandler = false;
        HandlerPath = string.Empty;
        HandlerClsid = Guid.Empty;
    }

    /// <summary>先把"选的是谁"写上去，再等内容。否则防抖那 300ms 里窗格还挂着上一个文件的标题。</summary>
    private void ApplyHeader(FileEntry entry)
    {
        IsEmpty = false;
        IsMultiple = false;

        TitleText = entry.DisplayName;
        PathText = entry.FullPath;
        Raise(nameof(Icon));

        var parts = new List<string>(3) { entry.TypeText };
        if (!entry.IsDirectory) parts.Add(entry.SizeText);
        if (!string.IsNullOrEmpty(entry.DateText)) parts.Add(entry.DateText);

        MetaText = string.Join(" · ", parts);
    }

    private async Task LoadAsync(FileEntry entry, bool handlerFailed = false)
    {
        CancelInFlight();
        ClearHandler();

        int generation = _generation;
        var cts = new CancellationTokenSource();
        _cts = cts;

        _current = entry;
        CanRetry = false;
        HasImage = false;
        HasText = false;
        HasNote = false;
        FooterText = string.Empty;
        TextContent = string.Empty;
        Image = null;
        ApplyHeader(entry);

        PreviewResult result = await PreviewLoader
            .LoadAsync(entry, DecodeWidthProvider?.Invoke() ?? 320, cts.Token)
            .ConfigureAwait(true);

        // 票号对不上 = 用户已经选了别的文件，这份结果作废（FR-9.2.4）
        if (generation != _generation) return;

        ApplyResult(result, entry, handlerFailed);
    }

    private void ApplyResult(PreviewResult result, FileEntry entry, bool handlerFailed)
    {
        bool tooHeavy = result.EstimatedBytes > BudgetBytes;

        if (result.Image is { } image && !tooHeavy)
        {
            Image = image;
            HasImage = true;

            // 缩略图是处理器的替补，得说清楚"这里只有一张图"，否则用户会以为 PDF 只有一页
            if (handlerFailed) FooterText = Strings.Preview_Note_HandlerFallback;

            return;
        }

        if (result.Text is { Length: > 0 } text && !tooHeavy)
        {
            TextContent = text;
            HasText = true;
            FooterText = result.Truncated
                ? string.Format(
                    CultureInfo.CurrentCulture, Strings.Preview_Truncated, PreviewLoader.MaxTextChars)
                : string.Empty;
            return;
        }

        HasNote = true;
        NoteText = tooHeavy ? Strings.Preview_Degraded : Describe(result.Note);

        if (result.Note == PreviewNote.TooLarge && result.LimitBytes > 0)
        {
            NoteText = string.Format(
                CultureInfo.CurrentCulture,
                Strings.Preview_Note_TooLarge,
                PreviewLoader.DescribeLimit(result.LimitBytes));
        }
    }

    private static string Describe(PreviewNote note) => note switch
    {
        PreviewNote.Binary => Strings.Preview_Note_Binary,
        PreviewNote.Unreadable => Strings.Preview_Note_Unreadable,
        PreviewNote.Timeout => Strings.Preview_Note_Timeout,
        _ => Strings.Preview_Note_NoThumbnail,
    };

    /// <summary>
    /// 网络路径与可移动设备不自动预览（FR-9.2.8）。
    ///
    /// UNC 只看前缀，不发请求。可移动设备要问 <see cref="DriveInfo"/>——那次查询对
    /// 断线的映射盘会卡几十秒，所以顺序在这里很重要：UNC 先被挡掉，
    /// 走到 DriveInfo 的一定是本地路径，不会阻塞。
    /// </summary>
    private static bool IsSlowPath(string path)
    {
        if (PathRules.IsUncPath(path)) return true;

        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;

        lock (DriveTypeCache)
        {
            if (DriveTypeCache.TryGetValue(root, out bool slow)) return slow;
        }

        bool removable;

        try
        {
            removable = new DriveInfo(root).DriveType is DriveType.Removable or DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // 盘符刚被拔掉、权限不足：不当成慢路径，让读盘那一步自己去失败
            removable = false;
        }

        lock (DriveTypeCache)
        {
            DriveTypeCache[root] = removable;
        }

        return removable;
    }

    private static readonly Dictionary<string, bool> DriveTypeCache = new(StringComparer.OrdinalIgnoreCase);

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        field = value;
        Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
