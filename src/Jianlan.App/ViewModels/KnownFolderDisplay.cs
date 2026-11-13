using Jianlan.App.Shell;
using Jianlan.Core.Localization;
using Jianlan.Core.Navigation;

namespace Jianlan.App.ViewModels;

/// <summary>
/// 常用目录的显示名与图标（FR-1.1.1 / FR-1.2.1）。导航栏与主页卡片共用一套映射，
/// 否则同一个"下载"在两处图标不同，看起来像两个不同的东西。
/// </summary>
internal static class KnownFolderDisplay
{
    public static (string Name, string Glyph) Of(KnownFolderKind kind) => kind switch
    {
        KnownFolderKind.Desktop => (Strings.Nav_Desktop, IconGlyphs.Desktop),
        KnownFolderKind.Documents => (Strings.Nav_Documents, IconGlyphs.Documents),
        KnownFolderKind.Downloads => (Strings.Nav_Downloads, IconGlyphs.Download),
        KnownFolderKind.Pictures => (Strings.Nav_Pictures, IconGlyphs.Pictures),
        KnownFolderKind.Music => (Strings.Nav_Music, IconGlyphs.Music),
        KnownFolderKind.Videos => (Strings.Nav_Videos, IconGlyphs.Video),
        _ => (Strings.Nav_ThisPc, IconGlyphs.Account),
    };
}
