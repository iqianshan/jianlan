// i18n-ignore-file：本文件的中文字符串只进日志/诊断输出，不进界面，不参与翻译。
using System.Text;
using System.Text.Json;
using Lingpane.Core.Diagnostics;
using Lingpane.Core.FileSystem;

namespace Lingpane.Core.Tagging;

/// <summary>
/// 彩色标签的读写（FR-4）。
///
/// 存储是**单文件追加日志**（<c>data/tags.jsonl</c>）：每行一条 <c>set</c>/<c>del</c> 操作，
/// 打一次标签只追加一行、不重写整个文件。内存侧维护
/// <c>Dictionary&lt;(卷, FileId), TagRecord&gt;</c>，所以列表渲染时的查询是一次哈希查找，
/// 不存在"批量查还是逐项查"的问题（FR-4.2.4 那条约束在字典下自动消失）。
///
/// 为什么不用 SQLite：实测 5000 条时 SQLite 多占 12 MB 私有提交且释放后不归还，
/// 而本项目内存余量只有二十来 MB；查询还慢 47 倍。完整对比见
/// <c>docs/tags-storage.md</c>，不要重新论证。
///
/// **懒加载**（FR-4.2.3）：构造函数什么都不读，首次 <see cref="EnsureLoaded"/> 才解析文件。
/// 启动路径上不碰它，冷启动不为标签多付几十毫秒。
///
/// 读永不抛异常（FR-4.2.8）：坏行跳过，整份读不了就备份 <c>.bak</c> 并以空库继续——
/// 标签库坏了不该让程序打不开。
/// </summary>
public sealed class TagStore
{
    /// <summary>
    /// 记录数上限。到顶后**显式拒绝新增**并在状态栏说明，不静默丢弃、也不自动清理旧记录：
    /// 5 万条已经不是"标记"，是拿它当目录索引用；自动淘汰会让用户莫名丢标记。
    /// </summary>
    public const int MaxRecords = 50_000;

    /// <summary>
    /// 压缩的下限条件之一：行数低于这个数就不必费事（重写几百行没有收益）。
    /// 做成可注入是为了让单测能在几十次写入之内走完压缩分支——真按 2000 行测，
    /// 光 <c>Flush(true)</c> 就要跑好几秒。
    /// </summary>
    private const int DefaultCompactMinLines = 2_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = TagJsonContext.Default,
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly Dictionary<(ulong Volume, ulong FileId), TagRecord> _byKey = [];
    private readonly Dictionary<string, (ulong Volume, ulong FileId)> _byPath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly int _maxRecords;
    private readonly int _compactMinLines;

    private int _lines;
    private bool _loaded;

    /// <summary>
    /// 内容版本号：每次真正改动记录就加一。
    ///
    /// 存在的理由只有一个——让读方（标签结果页）能区分"我这次拿到的还是上次那份"。
    /// 原来那里按"颜色相同且列表非空"判重，结果给某一色新打了一项之后再点这一色，
    /// 页面停在旧列表上：数量变了、内容没变，看起来就像"点的标签和我标的文件夹对不上"。
    /// 比较版本号比"比较内容"便宜，也比"每次都重建"稳（重建会丢滚动位置与选中态）。
    /// </summary>
    private long _revision;

    /// <summary>整份文件不可用（读不了 / 目录不可写）：置位后只读，不再尝试追加。</summary>
    private bool _readOnly;

    /// <param name="path">用于测试注入；默认走 <see cref="AppPaths.TagsFile"/>。</param>
    /// <param name="maxRecords">上限。测试用它把 5 万条缩到几条。</param>
    /// <param name="compactMinLines">压缩下限行数。测试用它把阈值压到几条。</param>
    public TagStore(string? path = null, int maxRecords = MaxRecords, int compactMinLines = DefaultCompactMinLines)
    {
        _path = path ?? AppPaths.TagsFile;
        _maxRecords = Math.Max(1, maxRecords);
        _compactMinLines = Math.Max(2, compactMinLines);
    }

    /// <summary>首次加载完成（成功或降级）。宿主据此给已经在屏幕上的列表补标记（FR-4.2.5）。</summary>
    public event Action? Loaded;

    /// <summary>记录发生变化。目前只有宿主关心"要不要重画圆点"，所以不给更细的粒度。</summary>
    public event Action? Changed;

    public string FilePath => _path;

    public bool IsLoaded
    {
        get
        {
            lock (_gate) return _loaded;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _byKey.Count;
        }
    }

    /// <summary>
    /// 内容版本（见 <see cref="_revision"/>）。读方拿它判断"要不要重新取一遍"。
    /// </summary>
    public long Revision
    {
        get
        {
            lock (_gate) return _revision;
        }
    }

    public bool IsFull
    {
        get
        {
            lock (_gate) return _byKey.Count >= _maxRecords;
        }
    }

    /// <summary>
    /// 解析 <c>tags.jsonl</c>。可重复调用，只有第一次真正读盘。
    ///
    /// 由宿主在首帧之后的线程池里调一次；也可以由任何"马上要查标签"的路径顺手调，
    /// 代价是一次文件读取（几千行不到 1 ms），换来的是调用方不必关心加载时机。
    /// </summary>
    public void EnsureLoaded()
    {
        bool raise;
        lock (_gate)
        {
            if (_loaded) return;
            Parse();
            _loaded = true;
            raise = true;
        }

        if (raise) Loaded?.Invoke();
    }

    /// <summary>
    /// 查一项的标签。列表渲染走这个（可视区几十项，每项一次哈希查找）。
    /// </summary>
    public bool TryGet(ulong volume, ulong fileId, out TagColors colors)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue((volume, fileId), out TagRecord? record))
            {
                colors = record.Colors;
                return colors != TagColors.None;
            }
        }

        colors = TagColors.None;
        return false;
    }

    public TagColors Get(ulong volume, ulong fileId)
        => TryGet(volume, fileId, out TagColors colors) ? colors : TagColors.None;

    /// <summary>给一项设置标签（覆盖原有的颜色组合）。</summary>
    public TagSetResult Set(TagTarget target, TagColors colors, DateTimeOffset? at = null)
        => SetMany([target], colors, at);

    /// <summary>
    /// 批量设置标签——**多选打标签走这一个**，不是循环调 <see cref="Set"/>：
    /// 每次 <see cref="Set"/> 都要开一次文件并 <c>Flush(true)</c>，选 200 个文件就是 200 次落盘。
    /// </summary>
    public TagSetResult SetMany(IReadOnlyList<TagTarget> targets, TagColors colors, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        // 传 None 进来当作"移除"，免得调用方在两处各写一遍分支
        if (colors == TagColors.None) return RemoveMany(targets) > 0 ? TagSetResult.Applied : TagSetResult.Invalid;
        if (targets.Count == 0) return TagSetResult.Invalid;

        TagSetResult result = TagSetResult.Applied;
        var lines = new List<TagOpLine>(targets.Count);
        DateTimeOffset stamp = at ?? DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (!_loaded) Parse();

            foreach (TagTarget target in targets)
            {
                if (string.IsNullOrWhiteSpace(target.Path))
                {
                    result = TagSetResult.Invalid;
                    continue;
                }

                // FileId 为 0 说明这卷拿不到稳定标识。**必须拒绝**：所有条目的键都会变成
                // (卷, 0)，给一个文件打标签会让整个目录一起变色——最难解释的一类 bug。
                if (target.FileId == 0)
                {
                    result = TagSetResult.NoFileId;
                    continue;
                }

                (ulong, ulong) key = (target.Volume, target.FileId);
                bool exists = _byKey.TryGetValue(key, out TagRecord? record);

                if (!exists && _byKey.Count >= _maxRecords)
                {
                    result = TagSetResult.Full;
                    continue;
                }

                if (exists && record is not null)
                {
                    record.Colors = colors;
                    record.Path = target.Path;
                    record.UpdatedAt = stamp;
                }
                else
                {
                    _byKey[key] = new TagRecord(target.Volume, target.FileId, target.Path, colors, stamp);
                }

                _byPath[target.Path] = key;
                lines.Add(new TagOpLine
                {
                    Op = "set",
                    V = target.Volume,
                    F = target.FileId,
                    P = target.Path,
                    C = (uint)colors,
                    T = stamp.ToUnixTimeSeconds(),
                });
            }
        }

        if (lines.Count > 0)
        {
            Append(lines);

            lock (_gate) _revision++;

            Changed?.Invoke();
        }

        return result;
    }

    public bool Remove(ulong volume, ulong fileId)
    {
        lock (_gate)
        {
            if (!_loaded) Parse();
        }

        return RemoveMany([new TagTarget(volume, fileId, string.Empty)]) > 0;
    }

    /// <summary>
    /// 批量移除。<paramref name="targets"/> 只用到前两个字段（路径可空）。
    /// 返回实际删掉的条数——本来就没有标签的项不算。
    /// </summary>
    public int RemoveMany(IReadOnlyList<TagTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var lines = new List<TagOpLine>(targets.Count);
        int removed = 0;

        lock (_gate)
        {
            if (!_loaded) Parse();

            foreach (TagTarget target in targets)
            {
                (ulong, ulong) key = (target.Volume, target.FileId);
                if (!_byKey.Remove(key, out TagRecord? record)) continue;

                _byPath.Remove(record.Path);
                lines.Add(new TagOpLine { Op = "del", V = target.Volume, F = target.FileId });
                removed++;
            }
        }

        if (lines.Count > 0)
        {
            Append(lines);

            lock (_gate) _revision++;

            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>清空整个标签库（FR-4.1.3 / FR-5.3.6）。返回清掉的条数。</summary>
    public int Clear()
    {
        int count;

        lock (_gate)
        {
            if (!_loaded) Parse();

            count = _byKey.Count;
            _byKey.Clear();
            _byPath.Clear();
        }

        if (count == 0) return 0;

        // 清空不是"追加一堆 del"：那会让文件变成"死记录 + 一堆删除行"，
        // 下次加载还要先把它们折叠一遍。直接重写成空文件更干净。
        Rewrite([]);

        lock (_gate) _revision++;

        Changed?.Invoke();
        return count;
    }

    /// <summary>当前全部记录（导出、清理失效项、按颜色归类都用它）。</summary>
    public IReadOnlyList<TagRecord> Snapshot()
    {
        lock (_gate)
        {
            if (!_loaded) Parse();
            return [.. _byKey.Values];
        }
    }

    /// <summary>
    /// 清理失效项（FR-4.2.6 / FR-5.3.6）：路径已打不开，或者那个路径现在指向了另一个文件
    /// （同卷移动后原位置被新文件占用）。返回清掉的条数。
    ///
    /// 每条一次文件系统查询，所以**调用方负责放到后台线程**，并且要事先告知用户：
    /// 离线磁盘（拔掉的 U 盘、断开的网络盘）上的标记同样会被算成失效——
    /// 这是这个操作真正的破坏面，不能藏在"清理垃圾"的说法后面。
    /// </summary>
    public int PruneStale()
    {
        List<TagRecord> records = [.. Snapshot()];
        if (records.Count == 0) return 0;

        var stale = new List<TagTarget>();
        foreach (TagRecord record in records)
        {
            bool alive = FileIdEnumerator.TryIdentify(record.Path, out ulong volume, out ulong fileId)
                         && volume == record.Volume
                         && fileId == record.FileId;

            if (!alive) stale.Add(new TagTarget(record.Volume, record.FileId, record.Path));
        }

        return stale.Count == 0 ? 0 : RemoveMany(stale);
    }

    /// <summary>某一色下的全部记录（FR-4.1.5 导航栏"标签"节点用）。</summary>
    public IReadOnlyList<TagRecord> OfColor(TagColors color)
    {
        if (color == TagColors.None) return [];
        return [.. Snapshot().Where(r => (r.Colors & color) != 0)];
    }

    /// <summary>
    /// 合并一批外部记录（FR-4.2.7 导入）。返回新增 / 更新 / 跳过的条数。
    ///
    /// 合并而不是替换，理由：导出文件往往来自另一台机器或另一段时间，替换等于把
    /// 本机后来打的标签全清掉——那不是"导入"，是"恢复出厂"。逐条按主键判：
    /// <list type="bullet">
    ///   <item>本机没有 → 新增（受 <see cref="MaxRecords"/> 限制，满了算跳过）</item>
    ///   <item>本机有、导入的那份更新 → 覆盖颜色</item>
    ///   <item>本机有、本机那份一样新或更新 → 跳过（本机是后来改的，不能被旧文件盖掉）</item>
    /// </list>
    /// 路径会跟着更新：导入之后路径指向的是"导出时它在哪"，比本机那份旧值更有用。
    /// </summary>
    public TagImportResult Import(IReadOnlyList<TagRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        int added = 0, updated = 0, skipped = 0;
        var lines = new List<TagOpLine>(records.Count);

        lock (_gate)
        {
            if (!_loaded) Parse();

            foreach (TagRecord incoming in records)
            {
                if (incoming.FileId == 0)
                {
                    skipped++;
                    continue;
                }

                (ulong, ulong) key = incoming.Key;

                if (_byKey.TryGetValue(key, out TagRecord? existing) && existing is not null)
                {
                    // 同一条记录、同样的颜色：不必重写，也不必算"更新"
                    if (existing.Colors == incoming.Colors
                        && string.Equals(existing.Path, incoming.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    if (incoming.UpdatedAt <= existing.UpdatedAt)
                    {
                        skipped++;
                        continue;
                    }

                    existing.Colors = incoming.Colors;
                    existing.Path = incoming.Path;
                    existing.UpdatedAt = incoming.UpdatedAt;
                    updated++;
                }
                else
                {
                    if (_byKey.Count >= _maxRecords)
                    {
                        skipped++;
                        continue;
                    }

                    _byKey[key] = new TagRecord(
                        incoming.Volume, incoming.FileId, incoming.Path, incoming.Colors, incoming.UpdatedAt);
                    added++;
                }

                if (!string.IsNullOrWhiteSpace(incoming.Path)) _byPath[incoming.Path] = key;

                lines.Add(new TagOpLine
                {
                    Op = "set",
                    V = incoming.Volume,
                    F = incoming.FileId,
                    P = incoming.Path,
                    C = (uint)incoming.Colors,
                    T = incoming.UpdatedAt.ToUnixTimeSeconds(),
                });
            }
        }

        if (lines.Count > 0)
        {
            Append(lines);

            lock (_gate) _revision++;

            Changed?.Invoke();
        }

        return new TagImportResult(added, updated, skipped);
    }

    /// <summary>
    /// 八色各有多少条，一条记录可同时计入多色（一条记录可以是红+蓝）。
    ///
    /// 侧栏要给八个颜色节点标数量。用八次 <see cref="OfColor"/> 也能算出来，
    /// 但每次都要 <see cref="Snapshot"/> 一份新列表——五万条上限下就是复制八遍，
    /// 而这里只需要一遍遍历、一次加锁。
    /// </summary>
    public IReadOnlyDictionary<TagColors, int> CountByColor()
    {
        var counts = new Dictionary<TagColors, int>(TagPalette.All.Count);
        foreach (TagColors color in TagPalette.All) counts[color] = 0;

        lock (_gate)
        {
            if (!_loaded) Parse();

            foreach (TagRecord record in _byKey.Values)
            {
                foreach (TagColors color in TagPalette.All)
                {
                    if ((record.Colors & color) != 0) counts[color]++;
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// 把追加日志重写成"一条记录一行"的快照，返回重写后的行数。
    /// 记录数不变，只是文件变小、加载变快。
    /// </summary>
    public int Compact()
    {
        List<TagRecord> records;
        lock (_gate)
        {
            if (!_loaded) Parse();
            records = [.. _byKey.Values];
        }

        return Rewrite(records);
    }

    // ── 落盘 ────────────────────────────────────────────────────────────────

    private void Append(List<TagOpLine> lines)
    {
        lock (_gate)
        {
            if (_readOnly) return;

            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // FileShare.Read：别的进程（用户拿编辑器打开看）不会让这里失败
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                WriteLines(stream, lines);

                _lines += lines.Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _readOnly = true;
                LogSink.Warn("tags.write", "E3003", $"标签日志写入失败，本次改为只读：{ex.Message}");
                return;
            }

            if (ShouldCompact()) RewriteLocked();
        }
    }

    private static void WriteLines(FileStream stream, List<TagOpLine> lines)
    {
        foreach (TagOpLine line in lines)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(line, TagJsonContext.Default.TagOpLine);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte((byte)'\n');
        }

        // Flush(true)：断电最多丢最后一条，不会出现"写了半行"的坏文件
        stream.Flush(flushToDisk: true);
    }

    private bool ShouldCompact() => _lines > _compactMinLines && _lines > _byKey.Count * 3;

    /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
    private int RewriteLocked() => RewriteCore([.. _byKey.Values]);

    /// <summary>原子重写：先写 <c>.tmp</c>，再 <c>File.Replace</c>（与 settings.json 同一套）。</summary>
    private int Rewrite(List<TagRecord> records)
    {
        lock (_gate) return RewriteCore(records);
    }

    private int RewriteCore(List<TagRecord> records)
    {
        if (_readOnly) return _lines;

        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var lines = records
                .Select(r => new TagOpLine
                {
                    Op = "set",
                    V = r.Volume,
                    F = r.FileId,
                    P = r.Path,
                    C = (uint)r.Colors,
                    T = r.UpdatedAt.ToUnixTimeSeconds(),
                })
                .ToList();

            string tmp = _path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                WriteLines(stream, lines);
            }

            if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, _path);

            _lines = lines.Count;
            return _lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _readOnly = true;
            LogSink.Warn("tags.compact", "E3004", $"标签日志压缩失败，本次改为只读：{ex.Message}");
            return _lines;
        }
    }

    // ── 读取 ────────────────────────────────────────────────────────────────

    /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
    private void Parse()
    {
        _byKey.Clear();
        _byPath.Clear();
        _lines = 0;

        if (!File.Exists(_path)) return;

        string[] raw;
        try
        {
            raw = File.ReadAllLines(_path, Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // **不置只读**：读不出来的常见原因是"另一个程序正独占着这个文件"，
            // 那是暂时的。置了只读等于让用户本次会话永远打不上标签，
            // 而重试的代价只是这一次多读一个文件。
            Backup($"标签库无法读取，已备份为 .bak：{ex.Message}");
            return;
        }

        int bad = 0;
        foreach (string line in raw)
        {
            if (line.Length == 0) continue;

            TagOpLine? op;
            try
            {
                op = JsonSerializer.Deserialize(line, TagJsonContext.Default.TagOpLine);
            }
            catch (JsonException)
            {
                bad++;
                continue;
            }

            // FileId 为 0 的行没有意义（见 SetMany 的说明），当作坏行跳过
            if (op is null || op.F == 0)
            {
                bad++;
                continue;
            }

            _lines++;

            if (string.Equals(op.Op, "del", StringComparison.OrdinalIgnoreCase))
            {
                if (_byKey.Remove((op.V, op.F), out TagRecord? removed)) _byPath.Remove(removed.Path);
                continue;
            }

            if (!string.Equals(op.Op, "set", StringComparison.OrdinalIgnoreCase)) continue;

            var colors = (TagColors)(op.C ?? 0);
            if (colors == TagColors.None) continue;

            string path = op.P ?? string.Empty;
            _byKey[(op.V, op.F)] = new TagRecord(
                op.V, op.F, path, colors, DateTimeOffset.FromUnixTimeSeconds(op.T ?? 0));

            if (path.Length > 0) _byPath[path] = (op.V, op.F);
        }

        if (bad > 0)
        {
            LogSink.Warn("tags.load", "E3002", $"{_path} 有 {bad} 行无法解析，已跳过");
        }
    }

    private void Backup(string message)
    {
        try
        {
            File.Copy(_path, _path + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSink.Warn("tags.backup", "E3005", $"备份失败：{ex.Message}");
        }

        LogSink.Warn("tags.load", "E3001", message);
    }
}
