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
/// 常用目录（FR-1.1）。只解析路径，显示名与图标由 UI 层按 <see cref="KnownFolderKind"/> 映射，
/// Core 不持有任何界面文案。
/// </summary>
public static class KnownFolders
{
    public static IReadOnlyList<KnownFolder> All { get; } = Build();

    private static List<KnownFolder> Build()
    {
        var list = new List<KnownFolder>(7);
        Add(list, KnownFolderKind.Desktop, Environment.SpecialFolder.DesktopDirectory);
        Add(list, KnownFolderKind.Documents, Environment.SpecialFolder.MyDocuments);
        Add(list, KnownFolderKind.Pictures, Environment.SpecialFolder.MyPictures);
        Add(list, KnownFolderKind.Music, Environment.SpecialFolder.MyMusic);
        Add(list, KnownFolderKind.Videos, Environment.SpecialFolder.MyVideos);

        // Downloads 无 SpecialFolder 枚举值，按用户目录推导；
        // 后续版本改用 SHGetKnownFolderPath 以支持用户重定向过的下载目录。
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddPath(list, KnownFolderKind.Downloads, System.IO.Path.Combine(profile, "Downloads"));
        AddPath(list, KnownFolderKind.UserProfile, profile);

        return list;
    }

    private static void Add(List<KnownFolder> list, KnownFolderKind kind, Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        AddPath(list, kind, path);
    }

    private static void AddPath(List<KnownFolder> list, KnownFolderKind kind, string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        list.Add(new KnownFolder(kind, path));
    }
}
