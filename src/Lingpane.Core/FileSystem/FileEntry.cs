using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;

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
    private string? _typeText;

    public FileEntry(string name, string parentPath, bool isDirectory, long size, DateTime lastWriteTime,
        bool isHidden = false)
    {
        Name = name;
        _parentPath = parentPath;
        IsDirectory = isDirectory;
        Size = size;
        LastWriteTime = lastWriteTime;
        IsHidden = isHidden;
        IconKey = ResolveIconKey(name, isDirectory);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>隐藏属性。用于"显示隐藏文件"开关的过滤，过滤在内存中做，不重新枚举磁盘。</summary>
    public bool IsHidden { get; }

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

    /// <summary>完整路径。首次访问时拼接并缓存，之后不再重复分配。</summary>
    public string FullPath => _fullPath ??= Path.Join(_parentPath, Name);

    public string SizeText
    {
        get
        {
            if (IsDirectory) return string.Empty;
            return _sizeText ??= FormatSize(Size);
        }
    }

    public string DateText => _dateText ??= FormatDate(LastWriteTime, DateTime.Now.Year);

    /// <summary>
    /// 类型列文本。按需计算：只有真正显示出来的行才会分配这个字符串，
    /// 未滚动到的条目一个字节都不占（万项目录的内存口径要求）。
    /// </summary>
    public string TypeText => _typeText ??= ResolveTypeText();

    private string ResolveTypeText()
    {
        if (IsDirectory) return "文件夹";

        int dot = Name.LastIndexOf('.');
        if (dot <= 0 || dot == Name.Length - 1) return "文件";

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

    private static string ResolveIconKey(string name, bool isDirectory)
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
