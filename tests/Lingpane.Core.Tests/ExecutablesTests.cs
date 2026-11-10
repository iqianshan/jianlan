using Lingpane.Core.FileSystem;

namespace Lingpane.Core.Tests;

/// <summary>
/// 可执行文件判定（FR-5.2.11）。这一项判错的代价是不对称的：
/// 多拦一个只是多一次确认，漏拦一个就可能在"看图"时执行了程序。
/// </summary>
public class ExecutablesTests
{
    [Theory]
    [InlineData(@"C:\a\setup.exe", true)]
    [InlineData(@"C:\a\SETUP.EXE", true)]
    [InlineData(@"C:\a\run.bat", true)]
    [InlineData(@"C:\a\run.cmd", true)]
    [InlineData(@"C:\a\run.ps1", true)]
    [InlineData(@"C:\a\setup.msi", true)]
    [InlineData(@"C:\a\screensaver.scr", true)]
    [InlineData(@"C:\a\script.vbs", true)]
    [InlineData(@"C:\a\link.lnk", true)]
    [InlineData(@"C:\a\tweak.reg", true)]
    [InlineData(@"C:\a\readme.txt", false)]
    [InlineData(@"C:\a\photo.jpg", false)]
    [InlineData(@"C:\a\noextension", false)]
    [InlineData(@"C:\a\dir.exe\file.txt", false)]
    [InlineData("", false)]
    public void 按扩展名判定可执行(string path, bool expected)
        => Assert.Equal(expected, Executables.IsRisky(path));
}
