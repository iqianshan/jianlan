using System.IO;
using Jianlan.Core.FileSystem;
using Jianlan.Core.Localization;

namespace Jianlan.Core.Tests;

/// <summary>
/// 异常 → 用户可见原因的映射（docs/logging.md §6）。
///
/// 这几条的意义：运行时异常消息跟随 **系统语言**，与应用内切换的界面语言无关。
/// 直接把它拼进状态栏，中文界面就会出现 "新建文件夹失败：Access to the path ... is denied."。
/// 所以界面文案必须来自资源键，异常原文只进 app.log。
/// </summary>
[Collection(LocalizationCollection.Name)]
public class FsErrorsTests : IDisposable
{
    // 单例会改进程级 CultureInfo，跑完必须复位，否则同进程其他测试拿到中文
    public FsErrorsTests() => LocalizationService.Instance.Apply(LocalizationService.English);

    public void Dispose() => LocalizationService.Instance.Apply(LocalizationService.English);

    [Fact]
    public void 权限异常归到E1002()
    {
        Assert.Equal(FsErrors.AccessDenied, FsErrors.CodeOf(new UnauthorizedAccessException("x")));
    }

    [Fact]
    public void 文件被占用归到E1005()
    {
        // ERROR_SHARING_VIOLATION：File.Copy / Directory.Move 遇到别人占着文件时的真实码
        var ex = new IOException("sharing violation", unchecked((int)0x80070020));
        Assert.Equal(FsErrors.InUse, FsErrors.CodeOf(ex));
    }

    [Fact]
    public void 磁盘满归到E1006()
    {
        var ex = new IOException("disk full", unchecked((int)0x80070070));
        Assert.Equal(FsErrors.DiskFull, FsErrors.CodeOf(ex));
    }

    [Fact]
    public void 认不出的异常不硬猜更细的码()
    {
        Assert.Equal(FsErrors.PathNotFound, FsErrors.CodeOf(new IOException("谁也不知道这是什么")));
    }

    [Fact]
    public void 文案跟随界面语言而不是系统语言()
    {
        var service = LocalizationService.Instance;

        service.Apply(LocalizationService.SimplifiedChinese);
        Assert.Equal("没有访问权限", FsErrors.DescribeForOperation(FsErrors.AccessDenied));

        service.Apply(LocalizationService.English);
        Assert.Equal("Access denied", FsErrors.DescribeForOperation(FsErrors.AccessDenied));
    }

    [Fact]
    public void 中文界面下不出现英文异常原文()
    {
        LocalizationService.Instance.Apply(LocalizationService.SimplifiedChinese);

        // 这是修复前的实际输出：整句里只有前缀是中文
        const string raw = "Access to the path 'D:\\ac05\\aclnowrite\\新建文件夹' is denied.";
        string shown = string.Format(LocalizationService.Instance.Culture,
            LocalizationService.Instance.Get("Status.NewFolderFailed"),
            FsErrors.DescribeForOperation(FsErrors.CodeOf(new UnauthorizedAccessException(raw))));

        Assert.Equal("新建文件夹失败：没有访问权限", shown);
    }

    [Fact]
    public void 按异常文本也能归类()
    {
        // 目录枚举把原因当字符串返回，拿不到异常对象
        Assert.Equal(FsErrors.AccessDenied, FsErrors.CodeOf("对路径“D:\\x”的访问被拒绝。"));
        Assert.Equal(FsErrors.AccessDenied, FsErrors.CodeOf("Access to the path 'D:\\x' is denied."));
        Assert.Equal(FsErrors.DeviceNotReady, FsErrors.CodeOf("设备未就绪。"));
        Assert.Equal(FsErrors.InUse, FsErrors.CodeOf("The process cannot access the file because it is being used by another process."));
    }
}
