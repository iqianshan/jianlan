using System.IO;
using Lingpane.Core.FileSystem;

namespace Lingpane.Core.Tests;

/// <summary>
/// 路径关系判定。拖放把目录放进自己内部会造成无限递归，
/// 这组用例锁住"前缀相似但不是子目录"这类容易写错的情况。
/// </summary>
public class PathRulesTests
{
    [Theory]
    [InlineData(@"C:\a", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\a\b", true)]
    [InlineData(@"C:\a", @"C:\a\b\c", true)]
    [InlineData(@"C:\a", @"C:\ab", false)]
    [InlineData(@"C:\a", @"C:\b\a", false)]
    [InlineData(@"C:\a", @"D:\a\b", false)]
    public void IsSameOrAncestor_基本关系(string candidate, string path, bool expected)
        => Assert.Equal(expected, PathRules.IsSameOrAncestor(candidate, path));

    [Fact]
    public void IsSameOrAncestor_忽略大小写与结尾分隔符()
    {
        Assert.True(PathRules.IsSameOrAncestor(@"C:\Temp\", @"c:\temp\sub"));
        Assert.True(PathRules.IsSameOrAncestor(@"C:\Temp", @"c:\temp\"));
    }

    [Fact]
    public void IsSameOrAncestor_根目录能判出子目录()
    {
        // "C:\" 去掉分隔符会变成 "C:"，那是相对写法，不能直接比前缀
        Assert.True(PathRules.IsSameOrAncestor(@"C:\", @"C:\Windows"));
        Assert.False(PathRules.IsSameOrAncestor(@"C:\", @"D:\Windows"));
    }

    [Fact]
    public void IsSameOrAncestor_子目录反过来不成立()
        => Assert.False(PathRules.IsSameOrAncestor(@"C:\a\b", @"C:\a"));

    [Fact]
    public void IsSameOrAncestor_空值安全()
    {
        Assert.False(PathRules.IsSameOrAncestor(string.Empty, @"C:\a"));
        Assert.Throws<ArgumentNullException>(() => PathRules.IsSameOrAncestor(null!, @"C:\a"));
    }

    [Fact]
    public void IsSameOrAncestor_UNC路径按段比较()
    {
        Assert.True(PathRules.IsSameOrAncestor(@"\\server\share\a", @"\\server\share\a\b"));
        Assert.False(PathRules.IsSameOrAncestor(@"\\server\share\a", @"\\server\share\ab"));
    }

    [Theory]
    [InlineData(@"C:\Users", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"d:\data\sub", true)]
    [InlineData(@"\\server\share", false)]   // 网络路径可能因离线阻塞，不解析图标
    [InlineData(@".\relative", false)]
    [InlineData(@"C:", false)]               // 盘符相对路径不是绝对位置
    [InlineData(@"", false)]
    public void IsLocalDrivePath_只认本地盘绝对路径(string path, bool expected)
        => Assert.Equal(expected, PathRules.IsLocalDrivePath(path));
}
