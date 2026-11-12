using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;

using Lingpane.Core.Localization;
using Lingpane.Core.Tagging;

namespace Lingpane.Core.FileSystem;

/// <summary>
/// 列表行的数据模型。
///
/// 内存口径（NF-05）：万项目录下每个条目都会常驻，因此字段按"必须常驻"和"按需计算"分开：
///   · 常驻：名称、所在目录（**所有条目共享同一个字符串实例**）、是否为目录、大小、修改时间
///   · 按需：完整路径、大小文本、日期文本、图标键
///
/// 这样做的理由：列表只显示名称/日期/大小，完整路径在打开、复制路径之前根本用不到；
/// 而一万个条目各存一份完整路径，绝大部分内容是重复的父目录前缀。
///
/// 唯一例外是 <see cref="IconKey"/>：它参与绑定且每行首个可见项就会取用，
/// 但相同扩展名共用一个驻留实例，避免一万个 ".txt" 各占一份。
/// </summary>
public sealed class FileEntry : INotifyPropertyChanged
{
    private static readonly ConcurrentDictionary<string, string> IconKeyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 是否显示扩展名。做成静态开关而不是构造参数：
    /// 切换时要"即时生效且不重新枚举目录"（FR-5.2.4），逐条重建一万个对象反而更慢。
    /// 切换后调用 <see cref="RefreshDisplayName"/> 通知已实现的容器。
    /// </summary>
    private static bool _showExtensions = true;

    private readonly string _parentPath;

    private string? _fullPath;
    private string? _sizeText;
    private string? _dateText;
    // 类型文本曾经缓存在 _typeText，因支持运行时切换语言已去掉缓存（见 TypeText）

    // 文件夹大小（FR-6.16）：只在用户显式触发后才有值。
    // 每个条目固定多占 9 字节，万项目录约 90KB，相对 NF-05H 的 12MB 上限可以忽略；
    // 换来的是"结果直接挂在行上"，不必让列表去查一张按路径索引的字典。
    private long _folderSize;
    private FolderSizeState _folderSizeState;

    public FileEntry(string name, string parentPath, bool isDirectory, long size, DateTime lastWriteTime,
        bool isHidden = false, bool isSystem = false, ulong fileId = 0)
    {
        Name = name;
        _parentPath = parentPath;
        IsDirectory = isDirectory;
        Size = size;
        LastWriteTime = lastWriteTime;
        IsHidden = isHidden;
        IsSystem = isSystem;
        FileId = fileId;
        IconKey = ResolveIconKey(name, isDirectory);
    }

    /// <summary>
    /// 文件的稳定标识（FR-4.2.2，标签主键的一半）。0 表示"这次枚举没取到"——
    /// 标签库为空时走的是不取 FileId 的快枚举，这种情况下每个条目都是 0；
    /// 用户真要给某一项打标签时再单独查一次（见 <c>PaneViewModel.ResolveTargets</c>）。
    ///
    /// 不做成构造后不可变：同一个 <see cref="FileEntry"/> 在"打标签"那一刻才拿到 id，
    /// 为它重建整个列表（几千项）代价远大于事后补一个字段。
    /// </summary>
    public ulong FileId { get; private set; }

    /// <summary>打标签时补上 id，并通知界面（圆点要立刻画出来）。</summary>
    public void SetFileId(ulong fileId)
    {
        if (fileId == 0 || FileId == fileId) return;

        FileId = fileId;
        PropertyChanged?.Invoke(this, FileIdChanged);
    }

    private static readonly PropertyChangedEventArgs FileIdChanged = new(nameof(FileId));

    /// <summary>
    /// 颜色标签（FR-4.1.4）。库未就绪时一律是 <see cref="TagColors.None"/>，
    /// 库就绪后由窗格统一补一次（FR-4.2.5）——列表不必等标签库才开始渲染。
    /// </summary>
    public TagColors Tags => _tags;

    private TagColors _tags;

    /// <summary>有没有标签。行模板用它决定要不要画那圈描边——
    /// 只靠 <see cref="Tags"/> 的话，没有标签的行会留下一个空心小圆环，
    /// 看起来像一个没勾的复选框。</summary>
    public bool HasTags => _tags != TagColors.None;

    /// <summary>
    /// 行内要画的那几个色点（FR-4.1.2）。按 <see cref="TagPalette.All"/> 的位序取前几个，
    /// 最多 <see cref="TagPalette.MaxDots"/> 个——行高只有 28px，色点塞不满一行就只能省略，
    /// 而省略哪个必须稳定（按位序），否则每次重画顺序都可能变。
    ///
    /// 缓存成列表：绑定会在每次行重绘时取它，而 SetTags 之后才需要重算。
    /// </summary>
    public IReadOnlyList<TagColors> ColorDots => _dots ??= BuildDots();

    private List<TagColors>? _dots;

    private List<TagColors> BuildDots()
    {
        var dots = new List<TagColors>(TagPalette.MaxDots);

        foreach (TagColors color in TagPalette.All)
        {
            if ((_tags & color) == 0) continue;

            // 已删除（仅隐藏）的标签不画点：记录还在，但侧栏没有对应入口，
            // 行上留一个点不进去的标记只会让人以为程序坏了
            if (!TagPalette.IsActive(color)) continue;

            dots.Add(color);
            if (dots.Count >= TagPalette.MaxDots) break;
        }

        return dots;
    }

    private static readonly PropertyChangedEventArgs TagsChanged = new(nameof(Tags));
    private static readonly PropertyChangedEventArgs HasTagsChanged = new(nameof(HasTags));
    private static readonly PropertyChangedEventArgs ColorDotsChanged = new(nameof(ColorDots));

    /// <summary>
    /// 设置颜色并通知。颜色相同也要重算色点：启用掩码会随"删除/新增标签"变化，
    /// 位值没动的行同样要少画或多画一个点。重算代价是每行最多 32 次位与，可以忽略。
    /// </summary>
    public void SetTags(TagColors colors)
    {
        if (_tags == colors)
        {
            _dots = null;
            PropertyChanged?.Invoke(this, ColorDotsChanged);
            return;
        }

        _tags = colors;
        _dots = null;
        PropertyChanged?.Invoke(this, TagsChanged);
        PropertyChanged?.Invoke(this, HasTagsChanged);
        PropertyChanged?.Invoke(this, ColorDotsChanged);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>隐藏属性。用于"显示隐藏文件"开关的过滤，过滤在内存中做，不重新枚举磁盘。</summary>
    public bool IsHidden { get; }

    /// <summary>
    /// 系统属性（FR-5.2.4）。与隐藏属性分开两个开关：Windows 里两者常同时出现
    /// （<c>desktop.ini</c>、<c>pagefile.sys</c> 都是 H+S），但用户的意图不同——
    /// 想看见以点开头的配置目录，不代表想看页面文件。两个属性都满足某开关才显示。
    /// </summary>
    public bool IsSystem { get; }

    public long Size { get; }

    public DateTime LastWriteTime { get; }

    /// <summary>实际显示的名称：关闭"显示扩展名"时去掉扩展名（目录名原样）。</summary>
    public string DisplayName
    {
        get
        {
            if (_showExtensions || IsDirectory) return Name;

            int dot = Name.LastIndexOf('.');
            return dot > 0 ? Name[..dot] : Name;
        }
    }

    /// <summary>全局切换"显示扩展名"并通知所有已实现的容器（绑定只存在于可视行，开销可忽略）。</summary>
    public static void SetShowExtensions(IReadOnlyList<FileEntry> entries, bool show)
    {
        if (_showExtensions == show) return;

        _showExtensions = show;
        foreach (var entry in entries)
        {
            entry.PropertyChanged?.Invoke(entry, DisplayNameChanged);
        }
    }

    private static readonly PropertyChangedEventArgs DisplayNameChanged = new(nameof(DisplayName));

    private static readonly PropertyChangedEventArgs TypeTextChanged = new(nameof(TypeText));

    /// <summary>
    /// 语言切换后重算类型列。属性本身不缓存，但绑定只在收到通知时才重取——
    /// 不通知的话，已渲染的行会一直停在切换前的语言上（伪语言走查发现的问题）。
    /// 与 <see cref="SetShowExtensions"/> 同一套路：批量通知，只有可视行真有订阅者。
    /// </summary>
    public static void RefreshTypeText(IReadOnlyList<FileEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.PropertyChanged?.Invoke(entry, TypeTextChanged);
        }
    }

    /// <summary>完整路径。首次访问时拼接并缓存，之后不再重复分配。</summary>
    public string FullPath => _fullPath ??= Path.Join(_parentPath, Name);

    public string SizeText
    {
        get
        {
            if (IsDirectory)
            {
                return _folderSizeState switch
                {
                    FolderSizeState.Computing => Strings.Size_Computing,
                    FolderSizeState.Done => FormatSize(_folderSize),
                    // 未完成标注 "+"：这个数只多不少，不加标记用户会以为是精确值
                    FolderSizeState.Partial => FormatSize(_folderSize) + "+",
                    _ => string.Empty,
                };
            }

            return _sizeText ??= FormatSize(Size);
        }
    }

    /// <summary>
    /// 文件夹大小的计算状态（FR-6.16）。默认 <see cref="FolderSizeState.None"/>，
    /// 只有用户显式在右键菜单里点了「计算大小」才会离开这个状态——列表滚动、
    /// 目录切换都不会触发，这是需求里"严禁自动递归"的落点。
    /// </summary>
    public FolderSizeState FolderSizeState => _folderSizeState;

    /// <summary>标记为计算中，让行内先显示占位而不是空白（否则点了没反应）。</summary>
    public void MarkFolderSizeComputing()
    {
        if (!IsDirectory || _folderSizeState == FolderSizeState.Computing) return;

        _folderSizeState = FolderSizeState.Computing;
        RaiseSizeChanged();
    }

    /// <summary>写入结果。<paramref name="completed"/> 为 false 时显示成"至少这么多"。</summary>
    public void SetFolderSize(long bytes, bool completed)
    {
        if (!IsDirectory) return;

        _folderSize = bytes;
        _folderSizeState = completed ? FolderSizeState.Done : FolderSizeState.Partial;
        RaiseSizeChanged();
    }

    /// <summary>丢弃结果（被新的计算顶掉时用），回到与刚枚举出来时一致的状态。</summary>
    public void ClearFolderSize()
    {
        if (!IsDirectory || _folderSizeState == FolderSizeState.None) return;

        _folderSize = 0;
        _folderSizeState = FolderSizeState.None;
        RaiseSizeChanged();
    }

    private void RaiseSizeChanged()
    {
        PropertyChanged?.Invoke(this, SizeTextChanged);
        PropertyChanged?.Invoke(this, FolderSizeStateChanged);
    }

    // 事件参数对象按静态复用：每行每次计算都会触发，新建 12 个对象没有意义
    private static readonly PropertyChangedEventArgs SizeTextChanged = new(nameof(SizeText));
    private static readonly PropertyChangedEventArgs FolderSizeStateChanged = new(nameof(FolderSizeState));

    public string DateText => _dateText ??= FormatDate(LastWriteTime, DateTime.Now.Year);

    /// <summary>
    /// 类型列文本。**不缓存**：文案随界面语言变化，缓存会让切换语言后已显示过的行停在旧语言。
    /// 代价可忽略——只有真正渲染出来的行才会取这个属性（虚拟化，通常 30 行内），
    /// 未滚动到的条目一次都不会调用。
    /// </summary>
    public string TypeText => ResolveTypeText();

    private string ResolveTypeText()
    {
        if (IsDirectory) return Strings.Type_Folder;

        int dot = Name.LastIndexOf('.');
        if (dot <= 0 || dot == Name.Length - 1) return Strings.Type_File;

        // 扩展名大写更符合 Windows 习惯（.TXT 而非 .txt）
        return Name[dot..].ToUpperInvariant();
    }

    /// <summary>图标缓存键：目录统一，文件按扩展名（FONT-06 图标不随字号变化）。</summary>
    public string IconKey { get; }

    /// <summary>
    /// 实际参与绑定的图标键。默认等于 <see cref="IconKey"/>；
    /// 目录在后台解析出真实图标后会被改写（形如 <c>#12</c>），并通知界面换图。
    ///
    /// 为什么不在 Core 里直接存 ImageSource：Core 不引用 WPF，
    /// 这里只保存一个可解释的键，取图仍由 App 层的图标服务完成。
    /// </summary>
    public string IconToken => _iconToken ?? IconKey;

    private string? _iconToken;

    private static readonly PropertyChangedEventArgs IconTokenChanged = new(nameof(IconToken));

    /// <summary>设置真实图标键。相同值不触发通知，避免整批刷新时的无谓布局。</summary>
    public void SetIconToken(string? token)
    {
        if (string.Equals(_iconToken, token, StringComparison.Ordinal)) return;

        _iconToken = token;
        PropertyChanged?.Invoke(this, IconTokenChanged);
    }

    /// <summary>
    /// 图标键规则：目录固定 <c>.folder</c>，文件按扩展名（相同扩展名共用驻留实例）。
    ///
    /// 公开是为了让"不在列表里、但也要显示文件图标"的地方复用同一套规则——
    /// 地址栏补全候选（FR-6.1）就是这种场景。各写一套的后果是同名文件在列表里
    /// 是一种图标、在补全里是另一种。
    /// </summary>
    public static string ResolveIconKey(string name, bool isDirectory)
    {
        if (isDirectory) return ".folder";

        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return ".file";

        // 相同扩展名共用驻留实例：一万个文件通常只有十几种扩展名
        return IconKeyCache.GetOrAdd(name[dot..], static key => key.ToLowerInvariant());
    }

    private static string FormatSize(long bytes) => FormatSizeText(bytes);

    /// <summary>把字节数格式化为展示文本。状态栏统计选中项总大小时复用。</summary>
    public static string FormatSizeText(long bytes) => bytes switch
    {
        < 0 => string.Empty,
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024L * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024d * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
    };

    /// <summary>
    /// 当年条目省略年份（界面规则 4.2）：<c>10-28 14:32</c>；跨年显示 <c>2025-09-12</c>。
    /// <paramref name="currentYear"/> 由调用方传入，避免每行都读一次系统时间。
    /// </summary>
    private static string FormatDate(DateTime value, int currentYear) =>
        value.Year == currentYear
            ? value.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
