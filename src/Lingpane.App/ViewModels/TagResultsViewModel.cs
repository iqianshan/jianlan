using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Lingpane.Core.Localization;
using Lingpane.Core.Tagging;

namespace Lingpane.App.ViewModels;

/// <summary>
/// 标签结果列表里的一行。
///
/// 只带"零 IO 就能拿到"的三样东西：文件名、所在目录、颜色。
/// 刻意不显示修改时间——那要对每条记录各发一次 IO，而这里可能是几千条，
/// 网络盘上足够让一次点击变成几秒的空白。用户要看时间，双击跳过去就有。
/// </summary>
public sealed class TagResultItem
{
    public TagResultItem(TagRecord record, string colorHex)
    {
        Path = record.Path;
        Hex = colorHex;

        // 路径末段就是名字；末段为空（"D:\" 这种根）时退回完整路径，
        // 否则整行只剩一个色点，看不出是哪一项
        string name = System.IO.Path.GetFileName(record.Path);
        Name = string.IsNullOrEmpty(name) ? record.Path : name;

        string? dir = System.IO.Path.GetDirectoryName(record.Path);
        Directory = dir ?? record.Path;
    }

    public string Name { get; }

    /// <summary>所在目录。列表里显示它，用来区分"同名但不在一个地方"的两项。</summary>
    public string Directory { get; }

    public string Path { get; }

    public string Hex { get; }

    public string Tooltip => Path;
}

/// <summary>
/// 某一色标签下的全部条目（FR-4.1.5）。
///
/// 结果页是**一种标签形态**（见 <see cref="TabKind.TagResults"/>），不是独立窗口：
/// 这样它能和目录标签共存、切走再切回、关掉就没了，全都不用另写一套生命周期。
/// </summary>
public sealed class TagResultsViewModel : INotifyPropertyChanged
{
    private TagColors _color = TagColors.None;
    private string _colorName = string.Empty;

    /// <summary>上次填充时的标签库版本（<see cref="TagStore.Revision"/>）。判重用它，不用"列表非空"。</summary>
    private long _loadedRevision = -1;

    /// <summary>当前结果页的颜色（<c>#RRGGBB</c>），头部那个大色点用它。</summary>
    public string ColorHex => TagPalette.Hex(_color);

    public string TitleText => _colorName;

    public string HintText => string.Format(CultureInfo.CurrentCulture, Strings.Tag_Results_Hint, Items.Count);

    public bool IsEmpty => Items.Count == 0;

    public ObservableCollection<TagResultItem> Items { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 重新填一遍列表。
    ///
    /// 判重看两样：颜色与**标签库版本**。只比颜色（原来还要求列表非空）会漏掉最要紧的一种变化——
    /// 同一颜色下新打了一项，再点开这一色，页面停在旧列表上：侧栏数字变了、内容没变。
    /// 这比"每次重建"划算：版本没变时连 <see cref="Items"/> 都不用碰，滚动位置与选中态都留着。
    ///
    /// <paramref name="force"/> 用于"这一色刚被改动过、但版本号可能因批量操作只加了一次"的场合，
    /// 以及导入之后要立刻看到结果的场合。
    /// </summary>
    public void Load(TagStore tags, TagColors color, string colorName, bool force = false)
    {
        long revision = tags.Revision;

        if (!force && _color == color && _loadedRevision == revision) return;

        _color = color;
        _colorName = colorName;
        _loadedRevision = revision;

        string hex = TagPalette.Hex(color);

        Items.Clear();
        foreach (TagRecord record in tags.OfColor(color))
        {
            Items.Add(new TagResultItem(record, hex));
        }

        Raise(nameof(ColorHex));
        Raise(nameof(TitleText));
        Raise(nameof(HintText));
        Raise(nameof(IsEmpty));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
