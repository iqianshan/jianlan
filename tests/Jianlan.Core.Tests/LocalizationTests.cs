using System.Globalization;
using Jianlan.Core.Localization;

namespace Jianlan.Core.Tests;

/// <summary>
/// 多语言基建的单元测试（FR-10.4 / docs/i18n.md §十一）。
/// 注意：<see cref="LocalizationService"/> 是单例且会改进程级 CultureInfo，
/// 每个用例结束都恢复成英文，避免影响同进程内的其它测试。
/// </summary>
[Collection(LocalizationCollection.Name)]
public sealed class LocalizationTests : IDisposable
{
    public LocalizationTests() => LocalizationService.Instance.Apply(LocalizationService.English);

    public void Dispose() => LocalizationService.Instance.Apply(LocalizationService.English);

    [Theory]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("en", "en")]
    [InlineData(null, "en")]           // 未知 = auto，系统语言为英文
    [InlineData("zh_CN", "en")]        // 写坏的值按 auto 处理，不抛异常
    public void Resolve_显式值与兜底(string? setting, string expected)
    {
        CultureInfo installed = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal(expected, LocalizationService.Resolve(setting, installed).Name);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("zh-Hans")]
    public void Resolve_auto跟随系统语言(string installedName)
    {
        var installed = CultureInfo.GetCultureInfo(installedName);
        Assert.Equal("zh-Hans", LocalizationService.Resolve("auto", installed).Name);
    }

    [Fact]
    public void Resolve_auto在英文系统下选英文()
    {
        var installed = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("en", LocalizationService.Resolve("auto", installed).Name);
    }

    [Fact]
    public void 切换语言后取到对应文案()
    {
        var service = LocalizationService.Instance;

        service.Apply(LocalizationService.English);
        Assert.Equal("Open", service.Get("Menu.Open"));

        service.Apply(LocalizationService.SimplifiedChinese);
        Assert.Equal("打开", service.Get("Menu.Open"));
    }

    [Fact]
    public void 未知键返回醒目占位而不是空串()
    {
        Assert.Equal("!No.Such.Key!", LocalizationService.Instance.Get("No.Such.Key"));
        Assert.Equal(string.Empty, LocalizationService.Instance.Get(""));
    }

    [Fact]
    public void 占位符按当前语言格式化()
    {
        var service = LocalizationService.Instance;

        service.Apply(LocalizationService.SimplifiedChinese);
        Assert.Equal("无法打开：没有访问权限",
            service.Format("Error.CannotOpen", service.Get("Error.AccessDenied")));

        service.Apply(LocalizationService.English);
        Assert.Equal("Cannot open: Access denied",
            service.Format("Error.CannotOpen", service.Get("Error.AccessDenied")));
    }

    [Fact]
    public void 中英文资源键完全一致()
    {
        // 与 tools/i18n/check-keys.py 同一条约束，放进单测是为了让 IDE 里也能立刻发现
        var en = Keys("Strings.resx");
        var zh = Keys("Strings.zh-Hans.resx");
        Assert.Equal(en.OrderBy(k => k), zh.OrderBy(k => k));
    }

    private static IEnumerable<string> Keys(string fileName)
    {
        // 直接读源文件：构建产物里的卫星程序集在测试输出目录不一定齐全
        string path = Path.Combine(RepoRoot(), "src", "Jianlan.Core", "Localization", fileName);
        Assert.True(File.Exists(path), $"找不到资源文件：{path}");

        var doc = System.Xml.Linq.XDocument.Load(path);
        return doc.Root!.Elements("data")
            .Select(e => (string?)e.Attribute("name"))
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jianlan.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录（Jianlan.sln）");
    }
}
