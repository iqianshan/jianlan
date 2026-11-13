using Jianlan.Core.FileSystem;

namespace Jianlan.Core.Tests;

/// <summary>
/// 当前目录搜索的过滤口径（FR-6.13）。这里断言的是**列表里会出现哪些条目**，
/// 而不是界面长什么样：搜索一旦多给或少给一条，用户看到的就是错误的结果集。
/// </summary>
public sealed class EntryFilterTests
{
    private static FileEntry Make(string name, bool hidden = false, bool system = false, bool directory = false)
        => new(name, @"C:\work", directory, 0, new DateTime(2026, 11, 9), hidden, system);

    private static List<FileEntry> Items(params FileEntry[] entries) => [.. entries];

    [Fact]
    public void 空查询不做名称过滤()
    {
        var source = Items(Make("alpha.txt"), Make("beta.log"));

        var sink = new List<FileEntry>();
        int total = EntryFilter.Fill(source, showHidden: true, showSystem: true, query: "   ", sink, 100);

        Assert.Equal(2, total);
        Assert.Equal(2, sink.Count);
    }

    [Fact]
    public void 子串匹配且忽略大小写()
    {
        Assert.True(EntryFilter.Matches("Annual-REPORT.docx", "port"));
        Assert.True(EntryFilter.Matches("Annual-REPORT.docx", "annual"));
        Assert.False(EntryFilter.Matches("Annual-REPORT.docx", "invoice"));
    }

    [Fact]
    public void 中文按子串命中()
    {
        Assert.True(EntryFilter.Matches("项目验收报告.docx", "验收"));
        Assert.True(EntryFilter.Matches("项目验收报告.docx", "报告"));
        Assert.False(EntryFilter.Matches("项目验收报告.docx", "预算"));
    }

    [Fact]
    public void 多个关键词之间是与的关系且不计次序()
    {
        Assert.True(EntryFilter.Matches("2026-11 项目验收报告.docx", "报告 2026"));
        Assert.True(EntryFilter.Matches("2026-11 项目验收报告.docx", "2026   报告"));
        Assert.False(EntryFilter.Matches("2026-11 项目验收报告.docx", "2026 预算"));
    }

    [Fact]
    public void 全角空格同样分词()
    {
        // 中文输入法与文档粘贴常带全角空格，不拆开就变成"一个含全角空格的词"，永远匹配不上
        Assert.True(EntryFilter.Matches("项目 验收 报告.docx", "项目　报告"));
    }

    [Fact]
    public void 搜索遵守隐藏与系统文件开关()
    {
        var source = Items(
            Make("visible.txt"),
            Make("hidden.txt", hidden: true),
            Make("system.txt", system: true));

        var sink = new List<FileEntry>();
        int total = EntryFilter.Fill(source, showHidden: false, showSystem: false, query: "txt", sink, 100);

        Assert.Equal(1, total);
        Assert.Equal("visible.txt", sink[0].Name);
    }

    [Fact]
    public void 带隐藏与系统双标记的条目需要两个开关都打开()
    {
        // desktop.ini 这类条目同时带 H 与 S。写成"任一位被允许就显示"会让它漏出来。
        var desktopIni = Make("desktop.ini", hidden: true, system: true);
        var source = Items(desktopIni);

        var sink = new List<FileEntry>();
        Assert.Equal(0, EntryFilter.Fill(source, showHidden: true, showSystem: false, query: "", sink, 100));
        Assert.Equal(0, EntryFilter.Fill(source, showHidden: false, showSystem: true, query: "", sink, 100));
        Assert.Equal(1, EntryFilter.Fill(source, showHidden: true, showSystem: true, query: "", sink, 100));
    }

    [Fact]
    public void 超出上限时只收前N项但总数照实统计()
    {
        var source = new List<FileEntry>();
        for (int i = 0; i < 12; i++) source.Add(Make($"report-{i:D2}.txt"));

        var sink = new List<FileEntry>();
        int total = EntryFilter.Fill(source, true, true, "report", sink, limit: 5);

        Assert.Equal(12, total);
        Assert.Equal(5, sink.Count);
        Assert.Equal("report-00.txt", sink[0].Name);
    }

    [Fact]
    public void 过滤保持源顺序()
    {
        // 排序在枚举阶段已完成，过滤只做取舍。重排会让"按大小排序后搜索"变成另一种顺序。
        var source = Items(Make("c.txt"), Make("a.txt"), Make("b.txt"));

        var sink = new List<FileEntry>();
        EntryFilter.Fill(source, true, true, "txt", sink, 100);

        Assert.Equal(["c.txt", "a.txt", "b.txt"], sink.Select(e => e.Name));
    }

    [Fact]
    public void 上限常量与文档口径一致()
    {
        Assert.Equal(1000, EntryFilter.MaxSearchResults);
    }
}
