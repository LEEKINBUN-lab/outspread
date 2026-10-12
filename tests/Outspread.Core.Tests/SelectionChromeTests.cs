namespace Outspread.Core.Tests;

/// <summary>
/// The selection chrome's numbers, straight off the prototype's tokens.
/// </summary>
/// <remarks>
/// The load-bearing pair here is "ring blue" vs "fill blue": <c>hsl(214 84% 56%)</c> and
/// <c>hsl(210 100% 56%)</c> are within a few degrees of each other, and writing one where the other
/// belongs is invisible until a PNG is measured. So both are pinned as rgb triples, not as a
/// relationship between them.
/// </remarks>
public class SelectionChromeTests
{
    [Fact]
    public void The_two_blues_are_two_different_blues()
    {
        Assert.Equal(new Rgb(49, 130, 237), SelectionChrome.Ring(dark: false));
        Assert.Equal(new Rgb(31, 143, 255), SelectionChrome.Fill(dark: false));
        Assert.NotEqual(SelectionChrome.Ring(dark: false), SelectionChrome.Fill(dark: false));

        Assert.Equal(new Rgb(100, 164, 247), SelectionChrome.Ring(dark: true));
        Assert.Equal(new Rgb(51, 153, 255), SelectionChrome.Fill(dark: true));
    }

    [Theory]
    [InlineData(false, 0.24)]
    [InlineData(true, 0.20)]
    public void The_selection_fill_is_translucent_not_painted(bool dark, double expected)
    {
        Assert.Equal(expected, SelectionChrome.SelectionFillAlpha(dark), 2);
        Assert.True(SelectionChrome.SelectionFillAlpha(dark) < 0.5, "a solid fill would hide the note under it");
    }

    [Theory]
    [InlineData(false, 0.12)]
    [InlineData(true, 0.10)]
    public void The_marquee_is_lighter_than_the_selection(bool dark, double expected)
    {
        Assert.Equal(expected, SelectionChrome.MarqueeFillAlpha(dark), 2);
        Assert.True(SelectionChrome.MarqueeFillAlpha(dark) < SelectionChrome.SelectionFillAlpha(dark));
    }

    [Fact]
    public void The_ring_carries_an_inner_contrast_line_on_both_sides()
    {
        // 浅档白线、深档黑线：深色便签上纯蓝描边会糊进卡面（Figma 的做法）。
        Assert.Equal(new Rgb(255, 255, 255), SelectionChrome.InnerContrast(dark: false));
        Assert.Equal(0.85, SelectionChrome.InnerContrastAlpha(dark: false), 2);
        Assert.Equal(new Rgb(0, 0, 0), SelectionChrome.InnerContrast(dark: true));
        Assert.Equal(0.55, SelectionChrome.InnerContrastAlpha(dark: true), 2);
    }

    [Fact]
    public void A_handle_is_big_enough_to_hit_and_small_enough_to_see()
    {
        Assert.Equal(8, SelectionChrome.HandleSeenPx);
        Assert.Equal(10, SelectionChrome.HandleHoverPx);

        // WCAG 2.5.8 的那扇 24px 的门：命中区不许缩到画出来的那一格。
        Assert.True(SelectionChrome.HandleHitPx >= 24);
        Assert.True(SelectionChrome.HandleHoverPx > SelectionChrome.HandleSeenPx);
    }

    [Fact]
    public void The_zoom_clamp_is_the_prototype_s_not_the_first_guess()
    {
        // 第一版是 10%–400%，两端都比网页窄。
        Assert.Equal(0.05, SelectionChrome.MinZoom);
        Assert.Equal(8.0, SelectionChrome.MaxZoom);
    }

    [Fact]
    public void The_snap_line_is_the_red_the_prototype_measures()
    {
        Assert.Equal(new Rgb(255, 107, 107), SelectionChrome.Snap(dark: false));   // #ff6b6b
        Assert.Equal(new Rgb(255, 144, 144), SelectionChrome.Snap(dark: true));    // #ff9090
    }

    [Fact]
    public void Both_themes_actually_look_different()
    {
        Assert.NotEqual(SelectionChrome.HandleFace(dark: false), SelectionChrome.HandleFace(dark: true));
        Assert.NotEqual(SelectionChrome.HandleEdgeAlpha(dark: false), SelectionChrome.HandleEdgeAlpha(dark: true));
        Assert.Equal(0.22, SelectionChrome.HandleEdgeAlpha(dark: false), 2);
        Assert.Equal(0.50, SelectionChrome.HandleEdgeAlpha(dark: true), 2);
    }
}
