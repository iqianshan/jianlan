# ADR-0003：目录列表整体替换而非增量追加

- 状态：**已接受**（2026-11-03）
- 影响：`MainViewModel.Entries`、所有列表数据源、排序与过滤实现

## 背景

遍历目录时天然是"逐条得到"的：`FileSystemEnumerable` 每读到一个条目就交出一个。
最直观的绑定方式是 `ObservableCollection<FileEntry>`，读到就 `Add`。
本文记录为什么**不**这么做。

## 决定

`Entries` 的类型固定为 `IReadOnlyList<FileEntry>`，**每次导航整表替换**：

```csharp
private IReadOnlyList<FileEntry> _entries = [];
public IReadOnlyList<FileEntry> Entries
{
    get => _entries;
    private set => Set(ref _entries, value);   // 一次 PropertyChanged
}
```

枚举在后台线程完成后一次性赋值（`Entries = result.List`），UI 线程只收到**一次**变更通知。

## 依据

1. **`ObservableCollection.Add` 每次都触发 `CollectionChanged`。** 一万条就是一万次通知，
   每次通知都会让 `ItemsControl` 走一遍项容器维护逻辑。这条路径的开销与"通知次数"成正比，
   与最终条目数无关，属于纯粹浪费。
2. **增量追加期间列表处于中间状态。** 用户会看到列表"边加载边长"，排序视图在追加过程
   逐次重排，滚动条长度持续跳动。整体替换没有中间态。
3. **排序与过滤必须整表重算。** 按名称/大小/日期排序、按关键字过滤，都需要全部数据。
   增量追加的中间状态既不能排也不能过滤，最终还是要重建一次——两次工作。
4. **本项目的枚举已经足够快。** 10000 项目录枚举 66 ms，一次性拿到 `List<FileEntry>`
   再赋值，用户感知不到差异，没有必要为了"渐进显示"付出上面的代价。

由此带来的约束（已知并接受）：

- 枚举完成前列表是空的，大目录（10 万项）会有可见等待。R6 已规划"分批枚举 + 简化模式"，
  届时若改为分批，方案是**分批替换**（累积到阈值后整体赋值）而非逐条 `Add`，
  避免推翻本决定。
- 单条更新（重命名、删除）需要重建或局部替换，不能依赖 `ObservableCollection` 的便利。
  M1 做文件操作时按"重建受影响区间"处理。

## 备选方案与不选的理由

**`ObservableCollection<T>` 逐条 `Add`。** 通知次数与条目数同阶，且产生中间态，见上。

**`VirtualizingCollection` / 按需分页加载。** 对万项级别没有收益（枚举 66 ms），
却引入"滚动到未加载区域触发 IO"的复杂度与卡顿风险。10 万项以上再考虑。

**`BindingList<T>`。** 同样逐条通知，且 `RaiseListChangedEvents` 语义更重，无优势。

## 后果

- 列表性能与条目数近似解耦：空目录 93.0 MB → 10000 项 102.6 MB，只增 9.6 MB（数据本身）
- 排序、过滤、文件操作都按"整表/整区间替换"实现，代码里不出现逐条 `Add` 到绑定集合
- 若将来引入增量更新，必须先证明通知成本已不是瓶颈，并更新本 ADR
