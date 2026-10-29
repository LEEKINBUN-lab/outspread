namespace Outspread.Core.Tests;

/// <summary>
/// "看不清那一档不画正文"的判据：量的是<b>屏幕字高</b>，不是缩放数。
/// </summary>
/// <remarks>
/// 三条最容易写错的：① 拿缩放数当尺，高分屏上会提前把字藏掉（2.0 缩放的屏上 25% 还有 7.5 像素）；
/// ② 不带迟滞的裸阈值——压在阈值上来回滚轮，一翻档就是全部可见格重铺文字层（那本账实测过是
/// 104ms/帧那个量级）；③ 把迟滞带做成对称的，结果 50% 那一档"看不看得到字取决于你怎么进来的"——
/// 而 50% 是标签栏上按得到的预设档。第三条由 <see cref="Fifty_percent_is_not_left_to_how_you_got_there"/> 钉住。
/// </remarks>
public class TextLodTests
{
    [Theory]
    [InlineData(0.25, 1.0, false)]   // 总览那一档：15 世界像素只剩 3.75，笔画并掉了
    [InlineData(0.4, 1.0, false)]    // 6 像素：还没到可辨那一线
    [InlineData(0.5, 1.0, true)]     // 7.5 像素：过线
    [InlineData(1.0, 1.0, true)]
    [InlineData(0.25, 2.0, true)]    // 同一档在 2.0 缩放的屏上是 7.5 像素——判据必须是屏幕像素
    public void The_gate_is_measured_in_screen_pixels(double zoom, double scale, bool expected)
        => Assert.Equal(expected, NoteSurface.DrawsText(zoom, scale));

    [Fact]
    public void Between_the_two_lines_the_previous_state_decides()
    {
        // 6.3 像素：在"藏"与"画"两条线之间，所以两种都合法，看上一拍——这一档存在的意义就是别让
        // 来回滚轮每帧把全部可见格的文字层重铺一遍。
        var zoom = 6.3 / NoteTypography.DefaultSize;

        Assert.False(NoteSurface.DrawsText(zoom, 1.0));
        Assert.False(NoteSurface.TextLod(zoom, 1.0, wasOn: false));
        Assert.True(NoteSurface.TextLod(zoom, 1.0, wasOn: true));
    }

    [Fact]
    public void Fifty_percent_is_not_left_to_how_you_got_there()
    {
        // 50% 是标签栏上的预设档：它必须落在确定的一边，不许取决于上一拍。
        Assert.True(NoteSurface.TextLod(0.5, 1.0, wasOn: false));
        Assert.True(NoteSurface.TextLod(0.5, 1.0, wasOn: true));

        // 25%（总览）同样确定：两路都是不画。
        Assert.False(NoteSurface.TextLod(0.25, 1.0, wasOn: true));
        Assert.False(NoteSurface.TextLod(0.25, 1.0, wasOn: false));
    }

    [Fact]
    public void Well_above_and_below_the_band_the_hysteresis_stops_mattering()
    {
        Assert.True(NoteSurface.TextLod(1.0, 1.0, wasOn: false));
        Assert.False(NoteSurface.TextLod(0.2, 1.0, wasOn: true));
    }

    [Fact]
    public void The_function_can_actually_switch_both_ways()
    {
        // 正对照：一条恒真或恒假的判据会让上面那几条全绿。这里钉"存在一档翻起来、存在一档翻下去"。
        Assert.True(NoteSurface.TextLod(2.0, 1.0, wasOn: false));
        Assert.False(NoteSurface.TextLod(0.1, 1.0, wasOn: true));
    }

    [Fact]
    public void The_two_lines_are_one_pixel_apart_and_ordered()
    {
        Assert.True(NoteSurface.TextHideBelowPx < NoteSurface.TextLegiblePx);
        Assert.Equal(1.0, NoteSurface.TextLegiblePx - NoteSurface.TextHideBelowPx, 3);
    }

    [Fact]
    public void The_legibility_floor_follows_the_body_size_not_a_second_number()
    {
        // 判据由字号推出来：改字号时这档自己跟着走，不留第二个刻死的数。
        var zoom = NoteSurface.TextLegiblePx / NoteTypography.DefaultSize;

        Assert.True(NoteSurface.DrawsText(zoom, 1.0));
        Assert.False(NoteSurface.DrawsText(zoom * 0.99, 1.0));
    }
}
