using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using Jianlan.App.Services;
using Jianlan.App.Shell;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Localization;
using Jianlan.Core.Preview;

namespace Jianlan.App.ViewModels;

/// <summary>
/// 预览窗格的状态（FR-9.1）。
///
/// 一次预览要跨过三道异步：等上一次加载让位、读盘/解码、以及随时可能到来的下一次选中。
/// 因此这里有一份 <c>_generation</c> 递增票据——结果回来时票号对不上就整份丢掉。
/// 只靠 <see cref="CancellationToken"/> 不够：取消是协作式的，已经算完的结果照样会回来。
/// </summary>
public sealed class PreviewViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// 预览内容的内存预算（FR-9.2.7）。超了就不放内容，只留基本信息。
    ///
    /// 这里的口径是"这一次预览自己占了多少"，不是进程工作集：
    /// 工作集里混着框架、列表虚拟化缓存等与本功能无关的部分，拿它做判据会让
    /// 降级在别的模块吃内存时莫名其妙地触发。解码出来的像素数 + 文本字符数是可核算的。
    /// </summary>
    public const long BudgetBytes = 40L * 1024 * 1024;

    /// <summary>
    /// 预览链路的耗时诊断（<c>JIANLAN_PREVIEWDEBUG=1</c>）。
    ///
    /// 只在排查"按了方向键半天不出图"时开：写的是 <c>data/logs/app.log</c>，
    /// 每行带进程启动以来的毫秒数，能直接看出卡在读盘、解码、还是排队。
    /// 默认关闭——这条路径每次选中都要走，同步写日志会污染它自己要测的指标。
    /// </summary>
    private static readonly bool PreviewDebug =
        Environment.GetEnvironmentVariable("JIANLAN_PREVIEWDEBUG") == "1";

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private static void Trace(string message)
    {
        if (PreviewDebug) Diagnostics.AppLog.Info("preview.dbg", $"{Clock.ElapsedMilliseconds}ms {message}");
    }

    private long _selectionStamp;

    /// <summary>
    /// 在途加载数。大于 0 时新的选中只记 <see cref="_dirty"/>，不另起一次加载。
    ///
    /// 这里是"有活就接着干"而不是"定时器驱动"，走过两个错版本：
    /// 先是 300ms 防抖（连按时被一路推后，一次都不触发），再是 120ms 节流
    /// （每 120ms 跑一张，可一张解码就要 50–100ms，加上窗口延迟仍落后手指三四张）。
    /// 真正的约束不是"多久允许跑一次"，而是"同时只跑一份"：串行之后，
    /// 一张解码完成就立刻上屏，下一张紧接着开始，画面更新率等于解码速度本身。
    ///
    /// 用计数而不是布尔：作废一次在途后可能马上又起一次，先结束的那个若直接
    /// 把标志清掉，后来的就会被误判成"空闲"而再起第三份。
    /// </summary>
    private int _inflight;

    /// <summary>在途期间选中又变了：这次回来之后要拿最新那一项再跑一次。</summary>
    private bool _dirty;

    /// <summary>浮层盖在窗格上时暂停：看不见的结果不该花钱（见 <see cref="Suspend"/>）。</summary>
    private bool _suspended;

    private IReadOnlyList<FileEntry> _selection = [];
    private FileEntry? _current;
    private CancellationTokenSource? _cts;
    private int _generation;

    private bool _autoPreview = true;

    private bool _isEmpty = true;
    private bool _isMultiple;
    private bool _isLoading;
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

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 解码尺寸（物理像素）。由视图在每次请求前提供——窗格宽度可以拖动，
    /// 浮层的内容区也更矮，拖窄/变小之后还按满宽满高解码等于白占内存与时间。
    ///
    /// 两个维度都要给：手机截图是 720×1600 这类竖图，只限宽度时它照样解满高，
    /// 而屏幕上受高度限制最多显示一半——这一项在实测里把单张解码从 50–100ms 压到十几毫秒。
    /// </summary>
    public Func<(int Width, int Height)>? DecodeSizeProvider { get; set; }

    /// <summary>选中后是否自动预览（FR-5.2.10 的"触发方式"）。</summary>
    public bool AutoPreview
    {
        get => _autoPreview;
        set => _autoPreview = value;
    }

    /// <summary>
    /// 是否允许拉起系统预览处理器（FR-9.2.1）。默认允许；空格浮层设 <c>false</c>。
    ///
    /// 处理器把自己画在一个真实子窗口里（HwndHost），而浮层是不激活、鼠标穿透的窗口：
    /// 子窗口在那种宿主里显示会压不住层级，也没法接收点击。浮层对 PDF/Office 就给一张
    /// Shell 缩略图——反正它本来就是"按一下看一眼"，可翻页可播放的场合仍归预览窗格。
    /// </summary>
    public bool UseShellHandler { get; set; } = true;

    /// <summary>
    /// 这次预览是用户显式要的（空格、点"立即预览"），不是自动触发。
    ///
    /// 只影响一件事：网络盘与可移动设备上，自动预览会退成"点击立即预览"（FR-9.2.8）；
    /// 显式请求必须真的去取，否则用户在 U 盘上按空格只得到一句"要不要预览"，
    /// 等于把动作又还给了他。
    /// </summary>
    public bool ExplicitRequest { get; set; }

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
    /// 正在读下一个文件的内容，画面里还挂着上一个文件的那一帧。
    ///
    /// 这个标记存在的唯一理由是让视图把它画淡一点：既不清空内容（清空就是一闪白，
    /// 连按时每张都闪），也不让旧内容顶着新标题冒充结果。降权是最省事的说法——
    /// "这张是旧的，马上换"。
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value, nameof(IsLoading));
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
    /// 这里不另存一份"当前项"，而是从 <c>_selection</c> 现算：预览内容是异步落地的，
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
    /// 选中项变了。空选与多选立刻结算（都不需要读盘）；单选交给"一次一张"的队列
    /// （见 <see cref="_inflight"/>）：没有在途就立刻加载，有在途就只记一笔、它回来再追。
    /// </summary>
    public void Update(IReadOnlyList<FileEntry> selection)
    {
        // 浮层盖在窗格上：看不见的内容不取。这里连 _selection 都不动——
        // 恢复时调用方会再 Update 一次当前选中，而"同一实例直接返回"那条早退
        // 会让它误以为已经加载过了。
        if (_suspended) return;

        // 同一个条目被重复通知（列表刷新、焦点来回、切回标签）不重读。
        // 没有缓冲层之后，重复通知就是重复读盘加一次画面降权的闪烁。
        // 比实例而不是比路径：刷新后拿到的是新实例，可能是文件被改过。
        if (selection.Count == 1 && _selection.Count == 1 && ReferenceEquals(selection[0], _selection[0]))
        {
            _selection = selection;
            Raise(nameof(Current));
            Raise(nameof(CanAct));
            Trace($"update \"{selection[0].DisplayName}\" -> same");
            return;
        }

        _selection = selection;
        _selectionStamp = Clock.ElapsedMilliseconds;
        Raise(nameof(Current));
        Raise(nameof(CanAct));

        if (selection.Count != 1)
        {
            Trace($"update n={selection.Count} -> summary");
            _dirty = false;
            CancelInFlight();
            ApplySummary(selection);
            return;
        }

        // 有一张在途：只记一笔，它回来立刻追最新这张。不在这里取消重开——
        // 取消重开会让"解码比按键慢"的那些图一张也出不来，正是之前两版的老毛病。
        if (_inflight > 0)
        {
            _dirty = true;
            Trace($"update \"{selection[0].DisplayName}\" -> queued");
            return;
        }

        StartPreview();
        Trace($"update \"{selection[0].DisplayName}\" -> preview started");
    }

    /// <summary>「立即预览」按钮与右键菜单的入口（FR-9.2.8）。跳过延后判定。</summary>
    public void Retry()
    {
        _dirty = false;

        // 在途时不能另起一次：并发两份加载，先回来的那个会让位，排队状态就乱了。
        // 让它回来再追最新选中，效果一样。
        if (_inflight > 0)
        {
            _dirty = true;
            return;
        }

        if (_selection.Count == 1) Prepare(_selection[0], force: true);
    }

    /// <summary>
    /// 暂停（空格浮层盖上来时调用）。
    ///
    /// 浮层与窗格各有一份视图模型，同一个文件会被解码两遍——而浮层通常正盖在窗格上，
    /// 那一份谁也看不见。实测两遍并发时各要 95–104ms，也就是说连翻时有一半的解码
    /// 与磁盘带宽花在了看不见的地方。
    /// </summary>
    public void Suspend()
    {
        _suspended = true;
        _dirty = false;
        CancelInFlight();
    }

    /// <summary>恢复（浮层收起时调用）。调用方要紧接着 <see cref="Update"/> 一次当前选中，
    /// 否则窗格停在浮层打开之前那张。</summary>
    public void Resume() => _suspended = false;

    /// <summary>
    /// 关掉预览窗格时调用（FR-9.2.6）。作废在途任务、丢掉位图引用、清空缩略图缓存——
    /// 少任何一步，关掉窗格后那几张位图仍被静态缓存按着不放，内存账面上看不出来。
    /// </summary>
    public void Release()
    {
        _suspended = false;
        _dirty = false;
        CancelInFlight();

        _selection = [];
        _current = null;
        Raise(nameof(Current));
        Raise(nameof(CanAct));
        ClearHandler();
        Image = null;
        TextContent = string.Empty;
        IsLoading = false;
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
        IsLoading = false;
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
        Trace($"preview \"{entry.DisplayName}\" {Clock.ElapsedMilliseconds - _selectionStamp}ms after selection");
        ApplyHeader(entry);
        Prepare(entry, force: false);

        // Prepare 有三条同步分支（目录、超大、延后）根本不加载，_inflight 仍为 0；
        // 若在途期间又来过选中，这里立刻把它追掉，不必等下一次选中
        Pump();
    }

    /// <summary>在途的回来了：如果期间选中变过，拿最新那一项接着跑。</summary>
    private void Pump()
    {
        if (_inflight > 0 || !_dirty) return;

        _dirty = false;
        StartPreview();
    }

    /// <summary>
    /// 决定这一份文件怎么预览。防抖之后的唯一入口，<see cref="Retry"/> 也走这里。
    ///
    /// 顺序是刻意的：先排除根本不需要读盘的（目录、延后触发、超体积），
    /// 再问注册表——查注册表虽有缓存，但没必要为一张 2GB 的视频去问。
    /// </summary>
    private void Prepare(FileEntry entry, bool force)
    {
        // 下面三条提前返回都不会去读盘，也就没有"还挂着上一帧"这回事；
        // 真正要读的两条（LoadAsync / StartHandler）会自己把它重新置上。
        // 同一个同步块里改两次不会闪——WPF 只在一帧渲染时取最终值。
        IsLoading = false;

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

        if (!force && !ExplicitRequest && (!AutoPreview || IsSlowPath(entry.FullPath)))
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

        Guid clsid = UseShellHandler && PreviewRouter.NeedsHandlerLookup(kind)
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
        IsLoading = true;

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

        IsLoading = false;
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

    /// <summary>先把"选的是谁"写上去，再等内容。标题与内容必须同步改，不能只改其中一个。</summary>
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

        (int width, int height) = DecodeSizeProvider?.Invoke() ?? (320, 320);

        Trace($"load start \"{entry.DisplayName}\" size={entry.Size} {width}x{height}");

        int generation = _generation;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _inflight++;

        try
        {
            _current = entry;
            CanRetry = false;
            HasNote = false;
            NoteText = string.Empty;
            FooterText = string.Empty;

            // ★ 上一帧的 Image / TextContent 刻意不清。
            //
            // 早先这里把 Image 置 null、TextContent 置空，等于每选中一次就先闪一下白：
            // 连按时几乎全程是白的，"看不到预览"的感觉有一半来自这一步。
            // 现在改为留着旧内容、只置 IsLoading——视图据此把内容层画淡，
            // 语义是"这张是上一个文件的，马上换"，既不闪白也不冒充结果。
            IsLoading = true;

            ApplyHeader(entry);

            PreviewResult result = await PreviewLoader
                .LoadAsync(entry, width, height, cts.Token)
                .ConfigureAwait(true);

            Trace($"load done \"{entry.DisplayName}\" " +
                  $"{Clock.ElapsedMilliseconds - _selectionStamp}ms from selection, " +
                  $"img={(result.Image is null ? "null" : $"{result.Image.PixelWidth}x{result.Image.PixelHeight}")}");

            // 票号对不上 = 用户已经选了别的文件，这份结果作废（FR-9.2.4）
            if (generation != _generation) return;

            ApplyResult(result, entry, handlerFailed);
        }
        finally
        {
            // 回到 UI 线程（上面 ConfigureAwait(true)）：这里可以直接接着开下一张
            _inflight--;
            if (_inflight == 0) Pump();
        }
    }

    private void ApplyResult(PreviewResult result, FileEntry entry, bool handlerFailed)
    {
        // 内容已落地，卸掉"上一帧"的降权标记
        IsLoading = false;

        bool tooHeavy = result.EstimatedBytes > BudgetBytes;

        if (result.Image is { } image && !tooHeavy)
        {
            Image = image;
            HasImage = true;

            // LoadAsync 不再预先清空，成功分支要自己把另一类内容收干净——
            // 否则上一张是文本时，那截文本仍挂在 HasText=true 上跟图片抢同一块区域
            HasText = false;
            TextContent = string.Empty;
            HasNote = false;
            NoteText = string.Empty;

            // 缩略图是处理器的替补，得说清楚"这里只有一张图"，否则用户会以为 PDF 只有一页
            FooterText = handlerFailed ? Strings.Preview_Note_HandlerFallback : string.Empty;

            return;
        }

        if (result.Text is { Length: > 0 } text && !tooHeavy)
        {
            Image = null;
            HasImage = false;
            TextContent = text;
            HasText = true;
            HasNote = false;
            NoteText = string.Empty;
            FooterText = result.Truncated
                ? string.Format(
                    CultureInfo.CurrentCulture, Strings.Preview_Truncated, PreviewLoader.MaxTextChars)
                : string.Empty;
            return;
        }

        Image = null;
        HasImage = false;
        TextContent = string.Empty;
        HasText = false;
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
