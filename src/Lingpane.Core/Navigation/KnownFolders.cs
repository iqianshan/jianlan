namespace Lingpane.Core.Navigation;

public enum KnownFolderKind
{
    Desktop,
    Documents,
    Downloads,
    Pictures,
    Music,
    Videos,
    UserProfile,
}

public sealed record KnownFolder(KnownFolderKind Kind, string Path);

/// <summary>
/// 常用目录的**次序与兜底**（FR-1.1）。真正的解析在 App 层的 KnownFolderService：
/// 那条路径要调 <c>SHGetKnownFolderPath</c>，属于 P/Invoke 与缓存策略，Core 不该背。
///
/// Core 只保留两件事：
///   1. <see cref="DisplayOrder"/> —— 界面顺序（FR-1.1.1 写死"桌面、文档、下载、图片、音乐、视频"）；
///   2. <see cref="FallbackPath"/> —— 外壳解析不出来时的兜底，保证导航栏不会整块空掉。
/// </summary>
public static class KnownFolders
{
    /// <summary>
    /// 展示顺序。用户目录（UserProfile）不在主页卡片里，只在导航栏兜底时用。
    /// 顺序即需求里的排列，不按字母排——"下载"是高频入口，排在"文档"之后而不是最后。
    /// </summary>
    public static IReadOnlyList<KnownFolderKind> DisplayOrder { get; } =
    [
        KnownFolderKind.Desktop,
        KnownFolderKind.Documents,
        KnownFolderKind.Downloads,
        KnownFolderKind.Pictures,
        KnownFolderKind.Music,
        KnownFolderKind.Videos,
    ];

    /// <summary>设置文件里缓存用的键。用枚举名而不是数字：settings.json 是给人看的。</summary>
    public static string CacheKey(KnownFolderKind kind) => kind.ToString();

    /// <summary>
    /// 外壳调用不可用时的兜底路径。会校验存在性，不存在返回空串（由调用方隐藏该项）。
    /// 注意 Downloads 在这里只能靠"用户目录 + Downloads"推导——重定向过的下载夹
    /// 只有外壳知道，这正是必须优先走 <c>SHGetKnownFolderPath</c> 的原因。
    /// </summary>
    public static string FallbackPath(KnownFolderKind kind)
    {
        string path = kind switch
        {
            KnownFolderKind.Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            KnownFolderKind.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            KnownFolderKind.Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            KnownFolderKind.Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            KnownFolderKind.Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            KnownFolderKind.Downloads => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            _ => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        return Directory.Exists(path) ? path : string.Empty;
    }
}
