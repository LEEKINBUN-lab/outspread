namespace Outspread.Core.Tests;

/// <summary>
/// 「这一行文本到底是不是一条路径／一个链接」的那张用例表。识别面宽窄是拍过板的决定
/// （宽：盘符＋UNC＋file:///＋引号；不存在就拒绝建卡），所以两侧都要钉住：漏认是缺口，误认是把
/// 一段摘录变成一张卡。
/// </summary>
public class PathAndAnchorTests
{
    [Theory]
    [InlineData(@"D:\资料\方案.pdf", @"D:\资料\方案.pdf")]
    [InlineData(@"D:/资料/方案.pdf", @"D:\资料\方案.pdf")]
    [InlineData(@"""D:\资料\方案.pdf""", @"D:\资料\方案.pdf")]
    [InlineData(@"\\nas\共享\x.pdf", @"\\nas\共享\x.pdf")]
    [InlineData(@"D:\My Project\a.pdf", @"D:\My Project\a.pdf")]
    [InlineData(@"D:\x\", @"D:\x")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"d:\lower\case.pdf", @"d:\lower\case.pdf")]
    [InlineData("file:///D:/x%20y.pdf", @"D:\x y.pdf")]
    [InlineData("file://nas/share/x.pdf", @"\\nas\share\x.pdf")]
    public void A_line_that_is_a_windows_path_reads_as_one(string text, string expected)
    {
        Assert.True(LocalPath.TrySingle(text, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData(@"foo\bar.pdf")]
    [InlineData(@"C:")]
    [InlineData(@"看看这个 D:\x")]
    [InlineData(@"https://example.com/docs")]
    [InlineData(@"D:\a?b")]
    [InlineData(@"D:\a<b")]
    [InlineData(@"\\nas")]
    [InlineData(@"")]
    [InlineData(@"   ")]
    [InlineData("D:\\a\r\nD:\\b")]
    [InlineData("D:\\a\nD:\\b")]
    [InlineData(@"""D:\没关上的引号")]
    public void Anything_else_stays_text(string text)
        => Assert.False(LocalPath.TrySingle(text, out _));

    [Fact]
    public void A_real_path_survives_the_round_even_with_spaces_and_a_drive_root()
    {
        Assert.True(LocalPath.TrySingle(@"C:\Program Files\", out var programFiles));
        Assert.Equal(@"C:\Program Files", programFiles);

        Assert.True(LocalPath.TrySingle(@"C:\", out var root));
        Assert.Equal(@"C:\", root);
    }

    [Fact]
    public void An_anchor_is_read_when_the_text_is_one_short_line()
    {
        const string html = "<!--StartFragment--><a href=\"https://example.com/docs?a=1&amp;b=2\">这篇综述</a><!--EndFragment-->";

        Assert.True(WebAddress.TryAnchoredUrl(html, "这篇综述", out var uri));
        Assert.Equal("example.com", uri.Host);
        Assert.Contains("a=1&b=2", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void An_anchor_is_not_guessed_when_the_fragment_carries_several_targets()
    {
        const string html = "<a href=\"https://a.example/x\">一</a> 还是一条 <a href=\"https://b.example/y\">二</a>";

        Assert.False(WebAddress.TryAnchoredUrl(html, "一 还是一条 二", out _));
        Assert.False(WebAddress.TryAnchoredUrl(html, "一", out _));
    }

    [Fact]
    public void A_long_or_multi_line_selection_keeps_its_words_instead_of_becoming_a_card()
    {
        const string html = "<a href=\"https://example.com/x\">很长的一段摘录</a>";
        var longWords = new string('字', WebAddress.MaxTitleLength + 1);

        Assert.False(WebAddress.TryAnchoredUrl(html, longWords, out _));
        Assert.False(WebAddress.TryAnchoredUrl(html, "第一行\n第二行", out _));
        Assert.False(WebAddress.TryAnchoredUrl(null, "第一行", out _));
        Assert.False(WebAddress.TryAnchoredUrl("<a href=\"/relative\">相对</a>", "相对", out _));
        Assert.False(WebAddress.TryAnchoredUrl("<a href=\"#top\">锚点</a>", "锚点", out _));
    }
}
