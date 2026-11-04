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
}
