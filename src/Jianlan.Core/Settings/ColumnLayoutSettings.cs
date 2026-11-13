namespace Jianlan.Core.Settings;

/// <summary>
/// 详细信息视图的数据列（FR-6.4）。
///
/// 四列是**固定集合**：可显隐、可换序、可改宽，但不能自定义新列。
/// 自定义列要读 Shell 属性系统（<c>IShellFolder2.GetDetailsEx</c> 那套），
/// 与"零依赖 + 万项性能"直接冲突，不在 0.2.0 的范围里（docs/m2-plan.md §2.5）。
/// </summary>
public enum PaneColumn
{
    Name = 0,
    Date = 1,
    Type = 2,
    Size = 3,
}

/// <summary>
/// 列配置的目录档位（FR-6.4 的 P1：按目录类型记忆）。
///
/// 用"已知文件夹"而不是按路径逐个记忆：逐路径记会在用户拖一次列宽之后
/// 让 settings.json 里长出一张与访问历史等长的表，而收益只在回到同一目录时体现。
/// 已知文件夹是系统给的、数量固定的分类，正好覆盖"图片目录看大小、下载目录看日期"
/// 这类真实差异。
/// </summary>
public enum ColumnProfileKind
{
    Default = 0,
    Desktop = 1,
    Documents = 2,
    Downloads = 3,
    Pictures = 4,
    Music = 5,
    Videos = 6,
}

/// <summary>
/// 一个档位的列配置。名称列不在此模型里：它恒在首位、不可隐藏、不可换序——
/// 三个数据列全关时列表就只剩文件名，那已经是可用视图的下限，
/// 允许把它关掉只会得到一片空白（属输入校验，不是偏好）。
/// </summary>
public sealed class ColumnProfileSettings
{
    /// <summary>除名称外的三个列的顺序，取值见 <see cref="PaneColumn"/> 的小写名。</summary>
    public List<string> Order { get; set; } = [];

    public bool ShowDate { get; set; } = true;

    public bool ShowType { get; set; } = true;

    public bool ShowSize { get; set; } = true;

    public double DateWidth { get; set; } = AppSettings.DefaultColumnDateWidth;

    public double TypeWidth { get; set; } = AppSettings.DefaultColumnTypeWidth;

    public double SizeWidth { get; set; } = AppSettings.DefaultColumnSizeWidth;

    /// <summary>默认顺序，也是 Order 缺失/写坏时的回落值。</summary>
    public static readonly string[] DefaultOrder = ["date", "type", "size"];

    /// <summary>可参与排序的三列（名称不在其中：它恒在首位）。</summary>
    public static readonly PaneColumn[] OrderableColumns =
        [PaneColumn.Date, PaneColumn.Type, PaneColumn.Size];

    /// <summary>
    /// 归一化：补齐缺失列、剔除未知项与重复项、宽度夹取到合法范围。
    /// 手改配置文件时最容易写坏的就是这个数组——少一项会让某列消失，
    /// 多一项（或重复）会让两个列元素落在同一个格子里重叠显示。
    /// </summary>
    public void Normalize()
    {
        Order = ColumnLayout.NormalizeOrder(Order);

        DateWidth = ColumnLayout.ClampWidth(DateWidth, AppSettings.DefaultColumnDateWidth);
        TypeWidth = ColumnLayout.ClampWidth(TypeWidth, AppSettings.DefaultColumnTypeWidth);
        SizeWidth = ColumnLayout.ClampWidth(SizeWidth, AppSettings.DefaultColumnSizeWidth);
    }

    public bool IsVisible(PaneColumn column) => column switch
    {
        PaneColumn.Date => ShowDate,
        PaneColumn.Type => ShowType,
        PaneColumn.Size => ShowSize,
        _ => true,
    };

    public void SetVisible(PaneColumn column, bool visible)
    {
        switch (column)
        {
            case PaneColumn.Date:
                ShowDate = visible;
                break;
            case PaneColumn.Type:
                ShowType = visible;
                break;
            case PaneColumn.Size:
                ShowSize = visible;
                break;
            default:
                // 名称列不可隐藏，静默忽略而不是抛异常：调用方是菜单处理器，抛出去只会变成崩溃对话框
                break;
        }
    }

    public double WidthOf(PaneColumn column) => column switch
    {
        PaneColumn.Date => DateWidth,
        PaneColumn.Type => TypeWidth,
        PaneColumn.Size => SizeWidth,
        _ => 0,
    };

    public void SetWidth(PaneColumn column, double value)
    {
        switch (column)
        {
            case PaneColumn.Date:
                DateWidth = ColumnLayout.ClampWidth(value, AppSettings.DefaultColumnDateWidth);
                break;
            case PaneColumn.Type:
                TypeWidth = ColumnLayout.ClampWidth(value, AppSettings.DefaultColumnTypeWidth);
                break;
            case PaneColumn.Size:
                SizeWidth = ColumnLayout.ClampWidth(value, AppSettings.DefaultColumnSizeWidth);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 列在列头/行模板里的槽位。前两个槽位是固定装饰（标签圆点、图标），
    /// 槽 2 恒为名称列（<c>Width="*"</c>，吃掉剩余宽度），三个数据列从槽 3 起排。
    /// </summary>
    public int SlotOf(PaneColumn column)
    {
        if (column == PaneColumn.Name) return ColumnLayout.NameSlot;

        int index = Order.IndexOf(ColumnLayout.KeyOf(column));
        return ColumnLayout.FirstDataSlot + (index < 0 ? 0 : index);
    }

    /// <summary>在顺序表里前移/后移一列。到边界时原地不动。</summary>
    public bool Move(PaneColumn column, int delta)
    {
        int from = Order.IndexOf(ColumnLayout.KeyOf(column));
        if (from < 0) return false;

        int to = from + delta;
        if (to < 0 || to >= Order.Count) return false;

        (Order[from], Order[to]) = (Order[to], Order[from]);
        return true;
    }

    /// <summary>恢复到出厂状态（FR-6.4 的"重置本目录类型"）。</summary>
    public void Reset()
    {
        Order = [.. DefaultOrder];
        ShowDate = true;
        ShowType = true;
        ShowSize = true;
        DateWidth = AppSettings.DefaultColumnDateWidth;
        TypeWidth = AppSettings.DefaultColumnTypeWidth;
        SizeWidth = AppSettings.DefaultColumnSizeWidth;
    }
}

/// <summary>列配置的公共常量与归一化工具（档位解析在 <c>ColumnProfiles</c>）。</summary>
public static class ColumnLayout
{
    /// <summary>名称列占的槽位。</summary>
    public const int NameSlot = 2;

    /// <summary>第一个数据列的槽位。</summary>
    public const int FirstDataSlot = 3;

    public static string KeyOf(PaneColumn column) => column switch
    {
        PaneColumn.Date => "date",
        PaneColumn.Type => "type",
        PaneColumn.Size => "size",
        _ => "name",
    };

    public static bool TryParseKey(string? key, out PaneColumn column)
    {
        switch (key?.Trim().ToLowerInvariant())
        {
            case "name":
                column = PaneColumn.Name;
                return true;
            case "date":
                column = PaneColumn.Date;
                return true;
            case "type":
                column = PaneColumn.Type;
                return true;
            case "size":
                column = PaneColumn.Size;
                return true;
            default:
                column = PaneColumn.Name;
                return false;
        }
    }

    /// <summary>
    /// 把顺序表归一成"三个可排序列各出现一次"。未知项与名称列被剔除（名称恒在首位，
    /// 写进顺序表只说明用户手改时没读文档），缺失项按默认顺序补在末尾。
    /// </summary>
    public static List<string> NormalizeOrder(List<string>? order)
    {
        var result = new List<string>(3);

        if (order is not null)
        {
            foreach (string raw in order)
            {
                if (!TryParseKey(raw, out PaneColumn column)) continue;
                if (column == PaneColumn.Name) continue;

                string key = KeyOf(column);
                if (!result.Contains(key)) result.Add(key);
            }
        }

        foreach (string fallback in ColumnProfileSettings.DefaultOrder)
        {
            if (!result.Contains(fallback)) result.Add(fallback);
        }

        return result;
    }

    /// <summary>非有限值（NaN/Infinity）或被写成 0 的列宽一律回落到默认值。</summary>
    public static double ClampWidth(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return fallback;
        return Math.Clamp(value, AppSettings.MinColumnWidth, AppSettings.MaxColumnWidth);
    }
}
