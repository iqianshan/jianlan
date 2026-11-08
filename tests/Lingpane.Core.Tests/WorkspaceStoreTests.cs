using Lingpane.Core.Settings;
using Lingpane.Core.Workspaces;

namespace Lingpane.Core.Tests;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lingpane-ws-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_dir, "workspaces.json");

    public WorkspaceStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static WorkspaceTab Tab(string path, string? second = null, bool pinned = false)
        => new() { Path = path, Second = second, Pinned = pinned };

    [Fact]
    public void 文件不存在时是空集合且不写盘()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);

        Assert.Empty(store.Items);
        Assert.Null(store.LoadWarning);
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void 保存后能读回来且保留双窗格与固定标记()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);
        Assert.Equal(WorkspaceSaveResult.Saved,
            store.SaveAs("开发", [Tab(@"C:\a", @"C:\b"), Tab(@"C:\c", pinned: true)], activeIndex: 1));

        WorkspaceStore reloaded = WorkspaceStore.Load(File_);
        Workspace ws = Assert.Single(reloaded.Items);
        Assert.Equal("开发", ws.Name);
        Assert.Equal(2, ws.Tabs.Count);
        Assert.Equal(@"C:\b", ws.Tabs[0].Second);
        Assert.True(ws.Tabs[1].Pinned);
        Assert.Null(ws.Tabs[1].Second);
        Assert.Equal(1, ws.ActiveTabIndex);
    }

    [Fact]
    public void 保存耗时在百毫秒内()
    {
        // FR-8.1 验收：<100ms。这里给足余量，测的是"没有意外做 O(n²) 或同步 IO 灾难"
        WorkspaceStore store = WorkspaceStore.Load(File_);
        List<WorkspaceTab> tabs = [.. Enumerable.Range(0, 12).Select(i => Tab($@"C:\dir{i}"))];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        store.SaveAs("大工作区", tabs);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100, $"保存用了 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void 同名保存返回重复且不覆盖已有内容()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);
        store.SaveAs("开发", [Tab(@"C:\old")]);

        Assert.Equal(WorkspaceSaveResult.Duplicate, store.SaveAs("开发", [Tab(@"C:\new")]));
        Assert.Equal(@"C:\old", Assert.Single(store.Items[0].Tabs).Path);
    }

    [Fact]
    public void 名字忽略大小写与首尾空格()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);
        store.SaveAs("Dev", [Tab(@"C:\a")]);

        Assert.True(store.Contains("dev"));
        Assert.True(store.Contains("  DEV  "));
        Assert.Equal(WorkspaceSaveResult.Duplicate, store.SaveAs(" dev ", [Tab(@"C:\b")]));
    }

    [Fact]
    public void 覆盖更新保留原名并替换标签()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);
        store.SaveAs("开发", [Tab(@"C:\a")]);

        Assert.True(store.Overwrite("开发", [Tab(@"C:\x"), Tab(@"C:\y")], activeIndex: 1));

        WorkspaceStore reloaded = WorkspaceStore.Load(File_);
        Workspace ws = Assert.Single(reloaded.Items);
        Assert.Equal("开发", ws.Name);
        Assert.Equal([@"C:\x", @"C:\y"], ws.Tabs.Select(t => t.Path));
        Assert.Equal(1, ws.ActiveTabIndex);
    }

    [Fact]
    public void 覆盖不存在的名字等同于新建()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);

        Assert.True(store.Overwrite("新的", [Tab(@"C:\a")]));
        Assert.Single(store.Items);
    }

    [Fact]
    public void 空名或没有标签时拒绝保存()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);

        Assert.Equal(WorkspaceSaveResult.Invalid, store.SaveAs("", [Tab(@"C:\a")]));
        Assert.Equal(WorkspaceSaveResult.Invalid, store.SaveAs("  ", [Tab(@"C:\a")]));
        Assert.Equal(WorkspaceSaveResult.Invalid, store.SaveAs("空", []));
        Assert.Equal(WorkspaceSaveResult.Invalid, store.SaveAs("空", [Tab("   ")]));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void 重命名成功且撞名时拒绝()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);
        store.SaveAs("甲", [Tab(@"C:\a")]);
        store.SaveAs("乙", [Tab(@"C:\b")]);

        Assert.True(store.Rename("甲", "甲2"));
        Assert.True(store.Contains("甲2"));

        // 撞上已有的名字：拒绝，不能静默合并两份工作区
        Assert.False(store.Rename("甲2", "乙"));
        Assert.True(store.Contains("甲2"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void 删除后不再出现在文件里()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);
        store.SaveAs("甲", [Tab(@"C:\a")]);

        Assert.True(store.Remove("甲"));
        Assert.False(store.Remove("甲"));
        Assert.Empty(WorkspaceStore.Load(File_).Items);
    }

    [Fact]
    public void 数量到上限时拒绝新增()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);
        for (int i = 0; i < WorkspacesDocument.MaxWorkspaces; i++)
        {
            Assert.Equal(WorkspaceSaveResult.Saved, store.SaveAs($"ws{i}", [Tab($@"C:\{i}")]));
        }

        Assert.True(store.IsFull);
        Assert.Equal(WorkspaceSaveResult.Full, store.SaveAs("多余", [Tab(@"C:\x")]));
    }

    [Fact]
    public void 单个工作区的标签按上限截断()
    {
        WorkspaceStore store = WorkspaceStore.Load(File_);
        List<WorkspaceTab> many = [.. Enumerable.Range(0, WorkspacesDocument.MaxTabsPerWorkspace + 8)
            .Select(i => Tab($@"C:\{i}"))];

        store.SaveAs("超长", many);

        Assert.Equal(WorkspacesDocument.MaxTabsPerWorkspace, store.Items[0].Tabs.Count);
    }

    [Fact]
    public void 手改文件里的脏数据在读取时被洗掉()
    {
        File.WriteAllText(File_, """
        {
          "schemaVersion": 1,
          "items": [
            { "name": "  ", "tabs": [{ "path": "C:\\a" }] },
            { "name": "开发", "tabs": [{ "path": "C:\\a" }, { "path": "  " }, { "path": "C:\\b", "second": " C:\\c " }] },
            { "name": "开发", "tabs": [{ "path": "C:\\dup" }] },
            { "name": "空工作区", "tabs": [] }
          ]
        }
        """);

        WorkspaceStore store = WorkspaceStore.Load(File_);

        // 空名去掉、重名只留第一条、"空工作区"保留（它只是没有标签，不是坏数据）
        Assert.Equal(["开发", "空工作区"], store.Items.Select(w => w.Name));
        Assert.Equal([@"C:\a", @"C:\b"], store.Items[0].Tabs.Select(t => t.Path));
        Assert.Equal(@"C:\c", store.Items[0].Tabs[1].Second);   // 首尾空格已修剪
    }

    [Fact]
    public void 文件损坏时备份为bak并返回空集合()
    {
        File.WriteAllText(File_, "{ 这不是 JSON");

        WorkspaceStore store = WorkspaceStore.Load(File_);

        Assert.Empty(store.Items);
        Assert.NotNull(store.LoadWarning);
        Assert.True(File.Exists(File_ + ".bak"));
    }

    [Fact]
    public void 高版本文件只读不写()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 99, "items": [{ "name": "甲", "tabs": [{ "path": "C:\\a" }] }] }""");

        WorkspaceStore store = WorkspaceStore.Load(File_);
        Assert.True(store.IsReadOnly);
        Assert.Single(store.Items);

        Assert.Equal(WorkspaceSaveResult.Saved, store.SaveAs("乙", [Tab(@"C:\b")]));
        Assert.False(File.Exists(File_ + ".tmp") && new FileInfo(File_ + ".tmp").Length > 0);
        Assert.DoesNotContain("乙", WorkspaceStore.Load(File_).Items.Select(w => w.Name));
    }
}
