// i18n-ignore-file：这里的中文是 Shell 自己给出的动作名（系统本地化词表），不是界面文案。
// 把它翻译了反而对不上——要比对的就是系统那颗按钮当时写的是什么。
namespace Jianlan.Core.FileSystem;

/// <summary>
/// 认出回收站条目上的"还原"动作。
///
/// Shell 的动词名是本地化的，还带助记符与快捷键后缀（实测简体中文是"还原(&amp;E)"）。
/// 这里原本按完整名字列候选表（"Restore"、"undelete"、"还原(&amp;R)"…），结果一个都没对上——
/// <c>InvokeVerb</c> 传错名字**不抛异常**，只是什么都不做，于是整批还原静默失败，
/// 用户看到的是"已删除，但撤不回来"。所以改成认词根，再把判定留成可单测的纯函数。
///
/// 名字认不出来不代表失败：调用方还会退回"第一项"（回收站里永远是还原），
/// 最后一道判据始终是"文件到底回没回来"。
/// </summary>
public static class RecycleVerbNames
{
    /// <summary>
    /// "还原"在各语言里的常见写法。只列词根，不带助记符与括号——
    /// 带括号的完整形式各版本各语言都不一样，列不完。
    /// </summary>
    private static readonly string[] Roots =
    [
        "还原", "复原", "復原", "restore", "undelete",
    ];

    /// <summary>
    /// 去掉 "(&amp;E)" 这类快捷键后缀与 "&amp;" 助记符，只留词根。
    /// </summary>
    public static string Normalize(string? verbName)
    {
        if (string.IsNullOrEmpty(verbName)) return string.Empty;

        string bare = CutSuffix(verbName).Replace("&", string.Empty, StringComparison.Ordinal);

        return bare.Trim();
    }

    /// <summary>这个动词名是否表示"还原"。</summary>
    public static bool IsRestore(string? verbName)
    {
        string bare = Normalize(verbName);
        if (bare.Length == 0) return false;

        foreach (string root in Roots)
        {
            if (bare.Contains(root, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// 掐掉第一个 "(" 到最后一个 ")" 之间的部分。不用正则：这里只要一个区间，
    /// 正则反而更难解释清楚为什么 "(&amp;E)" 要整段去掉。
    /// </summary>
    private static string CutSuffix(string text)
    {
        int open = text.IndexOf('(');
        if (open < 0) return text;

        int close = text.LastIndexOf(')');
        if (close <= open) return text[..open];

        return string.Concat(text.AsSpan(0, open), text.AsSpan(close + 1));
    }
}
