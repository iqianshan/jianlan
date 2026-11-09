using Lingpane.Core.FileSystem;
using Lingpane.Core.Preview;

namespace Lingpane.Core.Tests;

/// <summary>
/// 预览分类（FR-9.1.3 / FR-9.2.5）。这一层的每个判断都直接决定"会不会去读一个不该读的文件"，
/// 因此重点覆盖边界：目录、无扩展名、体积卡在阈值上、以及二进制嗅探对多字节文本的宽容。
/// </summary>
public sealed class PreviewClassifierTests
{
    [Theory]
    [InlineData(@"C:\a\notes.txt")]
    [InlineData(@"C:\a\Program.cs")]
    [InlineData(@"C:\a\data.json")]
    [InlineData(@"C:\a\.gitignore")]
    [InlineData(@"C:\a\Main.TSX")]
    public void 文本类扩展名走内置读取(string path)
        => Assert.Equal(PreviewKind.Text, PreviewClassifier.Classify(path, 1024, isDirectory: false));

    [Theory]
    [InlineData(@"C:\a\photo.png")]
    [InlineData(@"C:\a\photo.JPG")]
    [InlineData(@"C:\a\scan.tiff")]
    public void 位图走内置解码(string path)
        => Assert.Equal(PreviewKind.Image, PreviewClassifier.Classify(path, 1024, isDirectory: false));

    [Theory]
    [InlineData(@"C:\a\manual.pdf")]
    [InlineData(@"C:\a\report.docx")]
    [InlineData(@"C:\a\clip.mp4")]
    [InlineData(@"C:\a\logo.svg")]
    [InlineData(@"C:\a\shot.webp")]
    public void 其余类型交给Shell取缩略图(string path)
        => Assert.Equal(PreviewKind.Shell, PreviewClassifier.Classify(path, 1024, isDirectory: false));

    [Fact]
    public void 目录与无扩展名文件不预览()
    {
        Assert.Equal(PreviewKind.None, PreviewClassifier.Classify(@"C:\a\folder.txt", 0, isDirectory: true));
        Assert.Equal(PreviewKind.None, PreviewClassifier.Classify(@"C:\a\README", 100, isDirectory: false));
        Assert.Equal(PreviewKind.None, PreviewClassifier.Classify(string.Empty, 100, isDirectory: false));
        Assert.Equal(PreviewKind.None, PreviewClassifier.Classify(null, 100, isDirectory: false));
    }

    [Fact]
    public void 文本阈值是含边界的整除比较()
    {
        const string path = @"C:\a\big.log";

        Assert.False(PreviewClassifier.IsTooLarge(PreviewKind.Text, path, PreviewClassifier.MaxTextBytes));
        Assert.True(PreviewClassifier.IsTooLarge(PreviewKind.Text, path, PreviewClassifier.MaxTextBytes + 1));
    }

    [Fact]
    public void 图片与PDF各有自己的阈值()
    {
        Assert.True(PreviewClassifier.IsTooLarge(
            PreviewKind.Image, @"C:\a\a.png", PreviewClassifier.MaxImageBytes + 1));
        Assert.False(PreviewClassifier.IsTooLarge(
            PreviewKind.Image, @"C:\a\a.png", PreviewClassifier.MaxImageBytes));

        Assert.True(PreviewClassifier.IsTooLarge(
            PreviewKind.Shell, @"C:\a\a.pdf", PreviewClassifier.MaxDocumentBytes + 1));
    }

    [Fact]
    public void 未设闸门的类型永不超过阈值()
    {
        // 视频动辄几个 GB，用体积挡掉等于功能失效——这类交给 2s 超时兜底
        Assert.False(PreviewClassifier.IsTooLarge(PreviewKind.Shell, @"C:\a\a.mp4", long.MaxValue));
        Assert.Equal(0, PreviewClassifier.LimitFor(PreviewKind.Shell, @"C:\a\a.docx"));
    }

    [Fact]
    public void 二进制嗅探只看NUL()
    {
        Assert.True(PreviewClassifier.LooksBinary([0x4D, 0x5A, 0x00, 0x01]));
        Assert.False(PreviewClassifier.LooksBinary("普通文本，含中文与 emoji 🙂"u8));
        Assert.False(PreviewClassifier.LooksBinary([]));
    }

    [Theory]
    [InlineData(@"\\nas\share\a.txt", true)]
    [InlineData(@"C:\a.txt", false)]
    [InlineData(@"\\", true)]
    [InlineData("", false)]
    public void 网络路径按前缀判定(string path, bool expected)
        => Assert.Equal(expected, PathRules.IsUncPath(path));
}
