using Lingpane.Core.Settings;

namespace Lingpane.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lingpane-settings-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 文件不存在时返回默认值且不写盘()
    {
        var result = SettingsStore.Load(File_);

        Assert.Equal(1, result.Settings.SchemaVersion);
        Assert.Null(result.Warning);
        Assert.False(File.Exists(File_));   // 首次运行不创建文件
        Assert.False(File.Exists(File_ + ".bak"));
    }

    [Fact]
    public void 文件损坏时备份为bak并返回默认值()
    {
        File.WriteAllText(File_, "{ this is not json ");

        var result = SettingsStore.Load(File_);

        Assert.True(result.IsCorrupt);
        Assert.NotNull(result.Warning);
        Assert.False(File.Exists(File_));
        Assert.True(File.Exists(File_ + ".bak"));
        Assert.Equal(1100, result.Settings.Session.WindowWidth);   // 默认值
    }

    [Fact]
    public void 越界值被夹取到边界()
    {
        File.WriteAllText(File_, """
        {
          "schemaVersion": 1,
          "appearance": { "fontSizePt": 99, "density": "comfortable" },
          "privacy": { "historyLimit": -5, "cacheLimitMb": 99999 },
          "session": { "windowWidth": 100, "windowHeight": 100 }
        }
        """);

        var s = SettingsStore.Load(File_).Settings;

        Assert.Equal(24, s.Appearance.FontSizePt);           // 上限 24
        Assert.Equal(0, s.Privacy.HistoryLimit);             // 下限 0
        Assert.Equal(4096, s.Privacy.CacheLimitMb);          // 上限 4096
        Assert.Equal(AppSettings.MinWindowWidth, s.Session.WindowWidth);
        Assert.Equal(AppSettings.MinWindowHeight, s.Session.WindowHeight);
    }

    [Fact]
    public void 非法枚举值回落默认而不是解析失败()
    {
        File.WriteAllText(File_, """
        {
          "schemaVersion": 1,
          "appearance": { "theme": "purple", "density": "tiny" },
          "behavior": { "defaultView": "timeline", "sortBy": "colour" }
        }
        """);

        var result = SettingsStore.Load(File_);

        Assert.False(result.IsCorrupt);                       // 整个文件仍可用
        Assert.Equal(ThemeKind.System, result.Settings.Appearance.Theme);
        Assert.Equal(DensityKind.Comfortable, result.Settings.Appearance.Density);
        Assert.Equal(DefaultViewKind.Details, result.Settings.Behavior.DefaultView);
        Assert.Equal(SortFieldKind.Name, result.Settings.Behavior.SortBy);
    }

    [Fact]
    public void 未知字段在保存后仍然保留()
    {
        File.WriteAllText(File_, """
        {
          "schemaVersion": 1,
          "appearance": { "theme": "dark", "futureFlag": true },
          "futureSection": { "a": 1 }
        }
        """);

        var loaded = SettingsStore.Load(File_);
        Assert.True(SettingsStore.Save(loaded.Settings, File_, loaded.IsReadOnly));

        string written = File.ReadAllText(File_);
        Assert.Contains("futureFlag", written);
        Assert.Contains("futureSection", written);

        // 且已知字段确实写对了
        var again = SettingsStore.Load(File_);
        Assert.Equal(ThemeKind.Dark, again.Settings.Appearance.Theme);
    }

    [Fact]
    public void 保存后读取得到相同值且无临时文件残留()
    {
        var s = new AppSettings();
        s.Appearance.Theme = ThemeKind.Dark;
        s.Appearance.FontSizePt = 12;
        s.Appearance.FontFamily = "Microsoft YaHei UI";
        s.Behavior.SortBy = SortFieldKind.Size;
        s.Session.LastPath = @"C:\Windows";

        Assert.True(SettingsStore.Save(s, File_));

        var back = SettingsStore.Load(File_).Settings;
        Assert.Equal(ThemeKind.Dark, back.Appearance.Theme);
        Assert.Equal(12, back.Appearance.FontSizePt);
        Assert.Equal("Microsoft YaHei UI", back.Appearance.FontFamily);
        Assert.Equal(SortFieldKind.Size, back.Behavior.SortBy);
        Assert.Equal(@"C:\Windows", back.Session.LastPath);

        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Fact]
    public void 第二次保存会生成bak备份()
    {
        var s = new AppSettings();
        SettingsStore.Save(s, File_);

        s.Appearance.Theme = ThemeKind.Light;
        SettingsStore.Save(s, File_);

        Assert.True(File.Exists(File_ + ".bak"));
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Fact]
    public void 高版本文件为只读且保存不覆盖()
    {
        string original = """{ "schemaVersion": 7, "appearance": { "theme": "dark" } }""";
        File.WriteAllText(File_, original);

        var result = SettingsStore.Load(File_);

        Assert.True(result.IsReadOnly);
        Assert.NotNull(result.Warning);
        Assert.False(SettingsStore.Save(result.Settings, File_, result.IsReadOnly));

        Assert.Equal(original, File.ReadAllText(File_));   // 原文件未被改写
    }

    [Theory]
    [InlineData(10, AppSettings.MinColumnWidth)]        // 小于下限 → 夹到下限
    [InlineData(9999, AppSettings.MaxColumnWidth)]      // 大于上限 → 夹到上限
    [InlineData(180, 180)]                              // 合法值原样保留
    [InlineData(0, AppSettings.DefaultColumnDateWidth)] // 0 视为未设置
    [InlineData(-5, AppSettings.DefaultColumnDateWidth)]
    public void 列宽越界被夹取或回落默认(double stored, double expected)
    {
        var settings = new AppSettings();
        settings.Layout.ColumnDateWidth = stored;

        settings.Normalize();

        Assert.Equal(expected, settings.Layout.ColumnDateWidth, 3);
    }

    [Fact]
    public void 列宽为NaN时回落默认而不抛异常()
    {
        var settings = new AppSettings();

        // 手改配置文件可以写出 1e999（解析为 Infinity），不能让界面拿到 NaN 宽度
        settings.Layout.ColumnSizeWidth = double.NaN;

        settings.Normalize();

        Assert.Equal(AppSettings.DefaultColumnSizeWidth, settings.Layout.ColumnSizeWidth, 3);
    }

    [Theory]
    [InlineData(0.01, AppSettings.MinSplitRatio)]       // 太窄 → 夹到下限
    [InlineData(0.99, AppSettings.MaxSplitRatio)]       // 太宽 → 夹到上限
    [InlineData(0.35, 0.35)]                            // 合法值原样保留
    public void 分屏比例越界被夹取(double stored, double expected)
    {
        var settings = new AppSettings();
        settings.Layout.SplitRatio = stored;

        settings.Normalize();

        Assert.Equal(expected, settings.Layout.SplitRatio, 3);
    }

    [Fact]
    public void 分屏比例为NaN时回落默认()
    {
        var settings = new AppSettings();
        settings.Layout.SplitRatio = double.NaN;

        settings.Normalize();

        Assert.Equal(AppSettings.DefaultSplitRatio, settings.Layout.SplitRatio, 3);
    }

    [Theory]
    [InlineData(120, AppSettings.MinPreviewWidth)]      // 比"看得清一张缩略图"还窄 → 抬到下限
    [InlineData(2000, AppSettings.MaxPreviewWidth)]     // 把列表挤没了 → 压到上限
    [InlineData(0, AppSettings.DefaultPreviewWidth)]    // 手改文件留下的 0 → 回默认
    [InlineData(-40, AppSettings.DefaultPreviewWidth)]  // 负数同理
    [InlineData(360, 360)]                              // 合法值原样保留
    public void 预览宽度越界被夹取或回落默认(double stored, double expected)
    {
        var settings = new AppSettings();
        settings.Layout.PreviewWidth = stored;

        settings.Normalize();

        Assert.Equal(expected, settings.Layout.PreviewWidth, 3);
    }

    [Fact]
    public void 恢复布局默认会把预览宽度一起复位()
    {
        var settings = new AppSettings();
        settings.Layout.PreviewWidth = 500;

        settings.ResetLayout();

        Assert.Equal(AppSettings.DefaultPreviewWidth, settings.Layout.PreviewWidth, 3);
    }

    [Fact]
    public void 第二窗格路径与标签数量对齐()
    {
        var settings = new AppSettings();
        settings.Session.Tabs = [@"C:\a", @"C:\b", @"C:\c"];
        settings.Session.TabSecondPaths = [@"D:\x"];   // 手改文件时少写了几条

        settings.Normalize();

        // 平行数组必须等长，否则读的人要自己判错位；缺的位置补空串表示"没开分屏"
        Assert.Equal(3, settings.Session.TabSecondPaths.Count);
        Assert.Equal(@"D:\x", settings.Session.TabSecondPaths[0]);
        Assert.Equal(string.Empty, settings.Session.TabSecondPaths[2]);
    }

    [Fact]
    public void 第二窗格路径多出的部分被截断()
    {
        var settings = new AppSettings();
        settings.Session.Tabs = [@"C:\a"];
        settings.Session.TabSecondPaths = [@"D:\x", @"D:\y"];

        settings.Normalize();

        Assert.Single(settings.Session.TabSecondPaths);
    }

    [Fact]
    public void 固定标记与标签数量对齐且缺少的位置默认不固定()
    {
        var settings = new AppSettings();
        settings.Session.Tabs = [@"C:\a", @"C:\b", @"C:\c"];
        settings.Session.TabPinned = [true];          // 手改文件时少写了几条

        settings.Normalize();

        // 第三份平行数组的约束与第二窗格路径完全一致（FR-2.9）
        Assert.Equal(3, settings.Session.TabPinned.Count);
        Assert.True(settings.Session.TabPinned[0]);
        Assert.False(settings.Session.TabPinned[2]);
    }

    [Theory]
    [InlineData("sideways")]     // 手改坏了
    [InlineData("")]
    [InlineData(null)]
    public void 标签栏位置不认识时回落顶部(string? stored)
    {
        var settings = new AppSettings();
        settings.Appearance.TabStripPosition = stored!;

        settings.Normalize();

        // 宁可回到顶部，也不要让标签栏落在一个不存在的位置上（会直接看不见标签）
        Assert.Equal(AppearanceSettings.TabTop, settings.Appearance.TabStripPosition);
    }

    [Fact]
    public void 标签栏位置为底部时保持不变()
    {
        var settings = new AppSettings();
        settings.Appearance.TabStripPosition = AppearanceSettings.TabBottom;

        settings.Normalize();

        Assert.Equal(AppearanceSettings.TabBottom, settings.Appearance.TabStripPosition);
    }

    [Theory]
    [InlineData("  D:\\Files  ", "D:\\Files")]   // 两侧空白去掉
    [InlineData("", null)]                        // 空串 = 跟随系统「下载」
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void 新标签页默认目录归一到去空白的路径或空值(string? stored, string? expected)
    {
        var settings = new AppSettings();
        settings.General.NewTabPath = stored;

        settings.Normalize();

        // 空值有独立含义（用系统下载目录），不能留一个 "" 让下游再判一次
        Assert.Equal(expected, settings.General.NewTabPath);
    }

    [Fact]
    public void 已知文件夹缓存里的空值被清掉()
    {
        var settings = new AppSettings();
        settings.General.KnownFolderPaths["Downloads"] = @"C:\Users\Me\Downloads";
        settings.General.KnownFolderPaths["Music"] = "   ";

        settings.Normalize();

        Assert.True(settings.General.KnownFolderPaths.ContainsKey("Downloads"));
        Assert.False(settings.General.KnownFolderPaths.ContainsKey("Music"));
    }

    [Fact]
    public void 已知文件夹缓存的键不区分大小写()
    {
        var settings = new AppSettings();
        // 手改配置文件常见的两种写法：小写、以及同名的另一份大小写
        settings.General.KnownFolderPaths["downloads"] = @"C:\Old";
        settings.General.KnownFolderPaths["Downloads"] = @"C:\Users\Me\Downloads";

        settings.Normalize();

        // 合并成一条、留下最后一次写入的值，且按枚举名的大小写也能取到
        Assert.Single(settings.General.KnownFolderPaths);
        Assert.Equal(@"C:\Users\Me\Downloads", settings.General.KnownFolderPaths["Downloads"]);
        Assert.Equal(@"C:\Users\Me\Downloads", settings.General.KnownFolderPaths["DOWNLOADS"]);
    }

    [Fact]
    public void 已知文件夹缓存为空值节点时回落空字典()
    {
        var settings = new AppSettings();
        settings.General.KnownFolderPaths = null!;

        settings.Normalize();

        Assert.NotNull(settings.General.KnownFolderPaths);
        Assert.Empty(settings.General.KnownFolderPaths);
    }

    [Fact]
    public void 历史按时间倒序去重并截断到上限()
    {
        var settings = new AppSettings();
        var older = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var newer = older.AddHours(2);
        settings.History.Entries =
        [
            new HistoryEntry { Path = @"C:\a", VisitedAt = older },
            new HistoryEntry { Path = @"C:\b", VisitedAt = newer },
            new HistoryEntry { Path = @"c:\A", VisitedAt = newer.AddHours(1) },   // 与 C:\a 同一个目录
            new HistoryEntry { Path = "  ", VisitedAt = newer },
        ];

        settings.Normalize();

        // 去重留最新一次访问、按时间倒序、空白项丢掉
        Assert.Equal([@"c:\A", @"C:\b"], settings.History.Entries.Select(e => e.Path));
    }

    [Fact]
    public void 历史条数被隐私设置夹取()
    {
        var settings = new AppSettings();
        settings.Privacy.HistoryLimit = 2;
        settings.History.Entries = Enumerable.Range(0, 10)
            .Select(i => new HistoryEntry
            {
                Path = $@"C:\d{i}",
                VisitedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
            })
            .ToList();

        settings.Normalize();

        Assert.Equal(2, settings.History.Entries.Count);
    }

    [Fact]
    public void 收藏去重去空并清理空白名字()
    {
        var settings = new AppSettings();
        settings.Bookmarks.Items =
        [
            new BookmarkEntry { Path = @"C:\a", Name = "   " },
            new BookmarkEntry { Path = @"c:\A" },
            new BookmarkEntry { Path = "" },
        ];

        settings.Normalize();

        // 空白名字归一成 null（显示时回落路径末段），重复路径只留第一条
        Assert.Single(settings.Bookmarks.Items);
        Assert.Null(settings.Bookmarks.Items[0].Name);
        Assert.Equal(@"C:\a", settings.Bookmarks.Items[0].Path);
    }

    [Theory]
    [InlineData("#2F6BE0", "#2F6BE0")]
    [InlineData("#abcdef", "#ABCDEF")]
    [InlineData("  #0F7B8A  ", "#0F7B8A")]
    [InlineData("red", null)]
    [InlineData("#F00", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("", null)]
    public void 强调色只接受六位十六进制(string input, string? expected)
    {
        // FR-5.1.3：手改成颜色名或三位写法一律当"跟随系统"，
        // 让界面去猜颜色名只会得到一块读不出来的黑洞。
        var settings = new AppSettings();
        settings.Appearance.Accent = input;

        settings.Normalize();

        Assert.Equal(expected, settings.Appearance.Accent);
    }

    [Fact]
    public void 重置外观不动用户数据()
    {
        var settings = new AppSettings();
        settings.History.Entries = [new HistoryEntry { Path = @"C:\a", VisitedAt = DateTimeOffset.UtcNow }];
        settings.Bookmarks.Items = [new BookmarkEntry { Path = @"C:\b" }];
        settings.Appearance.Theme = ThemeKind.Dark;
        settings.Appearance.FontSizePt = 18;

        settings.ResetAppearance();

        Assert.Equal(ThemeKind.System, settings.Appearance.Theme);
        Assert.Null(settings.Appearance.FontSizePt);

        // 历史与收藏是用户攒下来的东西，"恢复默认"不该动它们
        Assert.Single(settings.History.Entries);
        Assert.Single(settings.Bookmarks.Items);
    }

    [Fact]
    public void 重置隐私保留历史条目与收藏()
    {
        var settings = new AppSettings();
        settings.History.Entries = [new HistoryEntry { Path = @"C:\a", VisitedAt = DateTimeOffset.UtcNow }];
        settings.Bookmarks.Items = [new BookmarkEntry { Path = @"C:\b" }];
        settings.Privacy.HistoryLimit = 500;
        settings.Privacy.HistoryEnabled = false;

        settings.ResetPrivacy();

        Assert.Equal(100, settings.Privacy.HistoryLimit);
        Assert.True(settings.Privacy.HistoryEnabled);

        // 复用同一段对象（VisitHistory / BookmarkStore 持有它的引用），
        // 换对象会让它们继续读写旧实例
        Assert.Single(settings.History.Entries);
        Assert.Single(settings.Bookmarks.Items);
    }
}
