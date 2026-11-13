using System.Diagnostics;
using System.Text;
using Jianlan.Core.FileSystem;

namespace FileIdSpike;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  dump <path> [n]     打印前 n 项，自检结构体布局");
            Console.WriteLine("  bench <path> [runs] 对比两种枚举的耗时");
            Console.WriteLine("  verify <path>       条目数、FileId 两条路径一致性");
            Console.WriteLine("  id <path>           单个路径的 (VolumeSerial, FileId)");
            Console.WriteLine("  stability <dir>     重命名 / 同卷移动后的 FileId 稳定性");
            return 1;
        }

        string command = args[0];
        try
        {
            switch (command)
            {
                case "dump": Dump(args[1], args.Length > 2 ? int.Parse(args[2]) : 5); break;
                case "bench": Bench(args[1], args.Length > 2 ? int.Parse(args[2]) : 5); break;
                case "verify": Verify(args[1]); break;
                case "id": PrintId(args[1]); break;
                case "stability": Stability(args[1]); break;
                default:
                    Console.WriteLine($"未知命令：{command}");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"失败：{ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        return 0;
    }

    private static void Dump(string path, int count)
    {
        List<FileIdEntry> entries = FileIdEnumerator.Read(path, out EnumStats stats);

        Console.WriteLine($"目录：{path}");
        Console.WriteLine($"统计：{stats}");
        Console.WriteLine();
        Console.WriteLine($"{"名称",-40} {"大小",12} {"修改时间",-20} {"属性",-8} {"FileId",-22}");

        foreach (FileIdEntry e in entries.Take(count))
        {
            Console.WriteLine(
                $"{Truncate(e.Name, 40),-40} {e.Size,12} {e.LastWriteLocal:yyyy-MM-dd HH:mm:ss} " +
                $"{(e.IsDirectory ? "目录" : "文件"),-8} {e.FileId,-22}");
        }

        Console.WriteLine();
        Console.WriteLine($"共 {entries.Count} 项；前 {Math.Min(count, entries.Count)} 项如上。");
        Console.WriteLine("自检要点：文件名应是正常文字（不是乱码），修改时间应是近期合理值，");
        Console.WriteLine("         FileId 应各不相同且为较大整数。任一不符即说明结构体布局或偏移有误。");
    }

    private static void Bench(string path, int runs)
    {
        Console.WriteLine($"基准目录：{path}");
        Console.WriteLine($"轮次：{runs}（各轮交替执行，避免缓存与调度偏置）");
        Console.WriteLine();

        var baseline = new List<double>();
        var fileIdEnum = new List<double>();
        var baselineAll = new List<double>();
        var fileIdAll = new List<double>();
        int baselineCount = 0, fileIdCount = 0;

        // 预热：首轮要建目录缓存、JIT 两套路径
        _ = DirectoryEnumerator.ReadSorted(path);
        _ = FileIdEnumerator.Read(path, out _);

        for (int i = 0; i < runs; i++)
        {
            // ── A：现有实现（纯枚举，不含排序） ──
            var sw = Stopwatch.StartNew();
            var listA = new List<FileEntry>(10000);
            foreach (FileEntry entry in DirectoryEnumerator.Enumerate(path)) listA.Add(entry);
            sw.Stop();
            baseline.Add(sw.Elapsed.TotalMilliseconds);
            baselineCount = listA.Count;

            // ── A：现有实现（枚举 + 排序），这是真实调用路径 ──
            sw.Restart();
            List<FileEntry> sortedA = DirectoryEnumerator.ReadSorted(path);
            sw.Stop();
            baselineAll.Add(sw.Elapsed.TotalMilliseconds);

            // ── B：FileId 枚举（纯枚举） ──
            sw.Restart();
            List<FileIdEntry> listB = FileIdEnumerator.Read(path, out EnumStats stats);
            sw.Stop();
            fileIdEnum.Add(sw.Elapsed.TotalMilliseconds);
            fileIdCount = listB.Count;

            // ── B：FileId 枚举 + 转 FileEntry + 排序 ──
            sw.Restart();
            var converted = new List<FileEntry>(listB.Count);
            foreach (FileIdEntry e in listB)
            {
                converted.Add(new FileEntry(e.Name, path, e.IsDirectory, e.Size, e.LastWriteLocal,
                    (e.Attributes & FileAttributes.Hidden) != 0));
            }

            converted.Sort(EntrySort.Default.ToComparison());
            sw.Stop();
            fileIdAll.Add(sw.Elapsed.TotalMilliseconds);

            Console.WriteLine(
                $"第 {i + 1} 轮：现有 枚举 {baseline[^1]:0.0} / 枚举+排序 {baselineAll[^1]:0.0} ms" +
                $"    FileId 枚举 {fileIdEnum[^1]:0.0} / 枚举+排序 {fileIdAll[^1]:0.0} ms" +
                $"    [{stats}]");
        }

        Console.WriteLine();
        Console.WriteLine("| 指标 | 现有 (FileSystemEnumerable) | FileId (FileIdBothDirectoryInfo) | 倍数 |");
        Console.WriteLine("|---|---|---|---|");
        PrintRow("纯枚举 P50", baseline, fileIdEnum);
        PrintRow("枚举+排序 P50", baselineAll, fileIdAll);
        Console.WriteLine();
        Console.WriteLine($"条目数：现有 {baselineCount}，FileId {fileIdCount}" +
                          (baselineCount == fileIdCount ? "  ✅ 一致" : "  ❌ 不一致，说明有项被漏掉或重复"));
        Console.WriteLine($"NF-03 参照：首屏 ≤ 800ms，当前实测 233ms；枚举只是其中一段。");
    }

    private static void PrintRow(string label, List<double> a, List<double> b)
    {
        double pa = Median(a), pb = Median(b);
        Console.WriteLine($"| {label} | {pa:0.0} ms | {pb:0.0} ms | x{pb / pa:0.00} |");
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    private static void Verify(string path)
    {
        List<FileIdEntry> viaEnum = FileIdEnumerator.Read(path, out EnumStats stats);
        List<FileEntry> viaBaseline = DirectoryEnumerator.ReadSorted(path);

        Console.WriteLine($"目录：{path}");
        Console.WriteLine($"FileId 枚举 {viaEnum.Count} 项；现有枚举 {viaBaseline.Count} 项" +
                          (viaEnum.Count == viaBaseline.Count ? "  ✅" : "  ❌"));
        Console.WriteLine($"统计：{stats}");

        var baselineNames = new HashSet<string>(viaBaseline.Select(e => e.Name), StringComparer.Ordinal);
        var fileIdNames = new HashSet<string>(viaEnum.Select(e => e.Name), StringComparer.Ordinal);

        var onlyBaseline = baselineNames.Except(fileIdNames).ToList();
        var onlyFileId = fileIdNames.Except(baselineNames).ToList();
        Console.WriteLine($"仅现有枚举有：{onlyBaseline.Count} 项 {(onlyBaseline.Count > 0 ? string.Join(", ", onlyBaseline.Take(5)) : "")}");
        Console.WriteLine($"仅 FileId 枚举有：{onlyFileId.Count} 项 {(onlyFileId.Count > 0 ? string.Join(", ", onlyFileId.Take(5)) : "")}");

        // 重复 FileId 会让"两个文件共用一个标签"，是危险的静默错误
        var duplicated = viaEnum.GroupBy(e => e.FileId).Where(g => g.Count() > 1).ToList();
        Console.WriteLine($"重复 FileId：{duplicated.Count} 组" +
                          (duplicated.Count > 0 ? $"  ❌ 例：{duplicated[0].Key} -> {string.Join(", ", duplicated[0].Take(3).Select(e => e.Name))}" : "  ✅"));

        // FILE_ID_BOTH_DIR_INFO 本身**不带卷序列号**，只能对目录句柄单独取一次、目录内共用。
        // 这里顺带验证"目录的卷序列号 == 目录内文件的卷序列号"，否则组合键会一半对一半错。
        (ulong dirSerial, ulong dirId) = FileIdEnumerator.Identify(path);
        Console.WriteLine($"目录卷序列号：{dirSerial}（0x{dirSerial:X16}），目录 FileId：{dirId}");
        Console.WriteLine();

        // 交叉校验：枚举拿到的 FileId 与 FileIdInfo 单独取到的必须一致
        int checkedCount = 0, mismatch = 0, serialMismatch = 0;
        foreach (FileIdEntry e in viaEnum.Take(20))
        {
            string full = Path.Combine(path, e.Name);
            if (!File.Exists(full) && !Directory.Exists(full)) continue;

            try
            {
                (ulong serial, ulong id) = FileIdEnumerator.Identify(full);
                checkedCount++;
                if (id != e.FileId)
                {
                    mismatch++;
                    if (mismatch <= 3)
                    {
                        Console.WriteLine($"  ❌ {e.Name}：枚举 {e.FileId} vs 句柄 {id}");
                    }
                }

                if (serial != dirSerial)
                {
                    serialMismatch++;
                    if (serialMismatch <= 3)
                    {
                        Console.WriteLine($"  ❌ {e.Name}：卷序列号 {serial} 与目录 {dirSerial} 不同");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! {e.Name}：{ex.Message}");
            }
        }

        Console.WriteLine($"FileId 交叉校验：抽查 {checkedCount} 项，不一致 {mismatch} 项" +
                          (mismatch == 0 ? "  ✅ 两条路径一致" : "  ❌ 两条路径不一致，标签索引会对不上"));
        Console.WriteLine($"卷序列号一致性：不一致 {serialMismatch} 项" +
                          (serialMismatch == 0 ? "  ✅ 目录内共用目录的卷序列号成立" : "  ❌ 不能用目录的卷序列号代替"));
    }

    private static void PrintId(string path)
    {
        (ulong serial, ulong id) = FileIdEnumerator.Identify(path);
        (uint legacySerial, ulong legacyIndex) = FileIdEnumerator.IdentifyByHandleIndex(path);

        Console.WriteLine($"路径：{path}");
        Console.WriteLine();
        Console.WriteLine("FileIdInfo（推荐口径，与目录枚举同源）");
        Console.WriteLine($"  VolumeSerial：{serial}（0x{serial:X16}）");
        Console.WriteLine($"  FileId：      {id}（0x{id:X16}）");
        Console.WriteLine($"  标签键：      ({serial}, {id})");
        Console.WriteLine();
        Console.WriteLine("BY_HANDLE_FILE_INFORMATION（旧口径，仅供对照）");
        Console.WriteLine($"  VolumeSerial：{legacySerial}（0x{legacySerial:X8}）");
        Console.WriteLine($"  FileIndex：   {legacyIndex}（0x{legacyIndex:X16}）");
        Console.WriteLine($"  标签键：      ({legacySerial}, {legacyIndex})");
        Console.WriteLine();
        Console.WriteLine(legacyIndex == id
            ? "FileIndex 与 FileId 的低位一致（本文件恰好没有序列号差异）"
            : "⚠ FileIndex 与 FileId 不一致 —— 旧口径缺序列号，会把「删掉旧文件后复用同一 MFT 记录的新文件」认成同一个");
    }

    private static void Stability(string dir)
    {
        string root = Path.Combine(dir, "fileid-stability");
        Directory.CreateDirectory(root);

        string original = Path.Combine(root, "spike-original.txt");
        File.WriteAllText(original, "fileid spike");
        Console.WriteLine($"测试目录：{root}");
        Console.WriteLine();

        (ulong serial0, ulong id0) = FileIdEnumerator.Identify(original);
        Console.WriteLine($"1) 新建      {Path.GetFileName(original),-30} FileId={id0}");

        string renamed = Path.Combine(root, "spike-renamed.txt");
        File.Move(original, renamed);
        (ulong serial1, ulong id1) = FileIdEnumerator.Identify(renamed);
        Console.WriteLine($"2) 同目录重命名 {Path.GetFileName(renamed),-27} FileId={id1}  " +
                          (id0 == id1 ? "✅ 不变" : "❌ 变了"));

        string sub = Path.Combine(root, "spike-sub");
        Directory.CreateDirectory(sub);
        string moved = Path.Combine(sub, "spike-renamed.txt");
        File.Move(renamed, moved);
        (ulong serial2, ulong id2) = FileIdEnumerator.Identify(moved);
        Console.WriteLine($"3) 同卷子目录移动 {Path.GetFileName(moved),-25} FileId={id2}  " +
                          (id0 == id2 ? "✅ 不变" : "❌ 变了（同卷移动必须稳定，否则标签会丢）"));

        // 目录枚举路径给出的 FileId 也要和句柄一致
        List<FileIdEntry> entries = FileIdEnumerator.Read(sub, out _);
        FileIdEntry? fromEnum = entries.FirstOrDefault(e => e.Name == "spike-renamed.txt");
        Console.WriteLine($"4) 枚举路径      {(fromEnum is null ? "❌ 没找到条目" : $"FileId={fromEnum.FileId}  " + (fromEnum.FileId == id0 ? "✅ 与句柄一致" : "❌ 与句柄不一致"))}");

        // 跨卷：目标卷不同则应该变，这正是"跨卷移动丢标签"的来源
        string otherRoot = Path.GetPathRoot(root)?.TrimEnd('\\') == "D:"
            ? Path.Combine(Path.GetTempPath(), "fileid-cross-volume")
            : Path.Combine("D:\\", "ac05", "fileid-cross-volume");

        try
        {
            Directory.CreateDirectory(otherRoot);
            string cross = Path.Combine(otherRoot, "spike-renamed.txt");
            if (File.Exists(cross)) File.Delete(cross);
            File.Move(moved, cross);
            (ulong serial3, ulong id3) = FileIdEnumerator.Identify(cross);
            Console.WriteLine($"5) 跨卷移动      {cross}");
            Console.WriteLine($"   FileId={id3}  卷={serial3}（原卷 {serial0}）  " +
                              (id0 == id3 ? "同卷（本次没跨成，结论不可用）" : "✅ 变了，印证跨卷移动会丢标签"));
            File.Delete(cross);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"5) 跨卷移动      跳过：{ex.Message}");
        }

        Directory.Delete(root, recursive: true);
        Console.WriteLine();
        Console.WriteLine("清理完成。");
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
