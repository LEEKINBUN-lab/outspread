namespace Outspread.Core.Tests;

/// <summary>
/// "字小到并进一个像素就只剩噪声，别画"的判据：量的是<b>屏幕字高</b>，不是缩放数。
/// </summary>
/// <remarks>
/// 11-03 改过一次判据：原来是可辨线（7 画／6 藏），他日常那一档（23%，字高 3.4 像素）整板只剩色条与牌子，
/// 报「缩小后不显示文字有些影响观感」。实测藏字省下的钱（他那块板每帧 +11~12ms、300 格整屏 +16ms）
/// 比牌子那一层（~50ms）和跨档整格重跑（518ms）小一到两个数量级，于是地板降到亚像素那一档（2 画／1.6 藏）。
/// 三条最容易写错的照旧：① 拿缩放数当尺，高分屏上会提前把字藏掉；② 不带迟滞的裸阈值——压在阈值上来回滚轮，
/// 一翻档就是全部可见格重铺文字层；③ 把迟滞带做成对称的，阈值上下各半档就变成"取决于你怎么进来的"。
/// </remarks>
public class TextLodTests
{
    [Theory]
    [InlineData(0.25, 1.0, true)]    // 3.75 像素：总览那一档，字照画（11-03 之前这一档是藏的）
    [InlineData(0.229, 1.0, true)]   // 3.44 像素：他日常那一档
    [InlineData(0.5, 1.0, true)]     // 7.5 像素
    [InlineData(1.0, 1.0, true)]
    [InlineData(0.08, 1.0, false)]   // 1.2 像素：笔画全并进一像素，画了是灰噪
    [InlineData(0.08, 2.0, true)]    // 同一档在 2.0 缩放的屏上是 2.4 像素——判据必须是屏幕像素
    [InlineData(0.14, 1.0, true)]    // 2.1 像素：过线
    [InlineData(0.1, 1.0, false)]    // 1.5 像素：在藏那一侧
    public void The_gate_is_measured_in_screen_pixels(double zoom, double scale, bool expected)
        => Assert.Equal(expected, NoteSurface.DrawsText(zoom, scale));

    [Fact]
    public void Between_the_two_lines_the_previous_state_decides()
    {
        // 1.8 像素：在"藏"与"画"两条线之间，所以两种都合法，看上一拍——这一档存在的意义就是别让
        // 来回滚轮每帧把全部可见格的文字层重铺一遍。
        var zoom = 1.8 / NoteTypography.DefaultSize;

        Assert.False(NoteSurface.DrawsText(zoom, 1.0));
        Assert.False(NoteSurface.TextLod(zoom, 1.0, wasOn: false));
        Assert.True(NoteSurface.TextLod(zoom, 1.0, wasOn: true));
    }

    [Fact]
    public void The_zooms_he_can_reach_by_click_are_not_left_to_how_you_got_there()
    {
        // 50%（标签栏预设档）与他日常的 23% 必须落在确定的一边：两路都画。
        Assert.True(NoteSurface.TextLod(0.5, 1.0, wasOn: false));
        Assert.True(NoteSurface.TextLod(0.5, 1.0, wasOn: true));
        Assert.True(NoteSurface.TextLod(0.229, 1.0, wasOn: false));
        Assert.True(NoteSurface.TextLod(0.229, 1.0, wasOn: true));

        // 8%（整块板塞进视口那种）同样确定：两路都不画。
        Assert.False(NoteSurface.TextLod(0.08, 1.0, wasOn: true));
        Assert.False(NoteSurface.TextLod(0.08, 1.0, wasOn: false));
    }

    [Fact]
    public void Well_above_and_below_the_band_the_hysteresis_stops_mattering()
    {
        Assert.True(NoteSurface.TextLod(1.0, 1.0, wasOn: false));
        Assert.False(NoteSurface.TextLod(0.05, 1.0, wasOn: true));
    }

    [Fact]
    public void The_function_can_actually_switch_both_ways()
    {
        // 正对照：一条恒真或恒假的判据会让上面那几条全绿。这里钉"存在一档翻起来、存在一档翻下去"。
        Assert.True(NoteSurface.TextLod(2.0, 1.0, wasOn: false));
        Assert.False(NoteSurface.TextLod(0.05, 1.0, wasOn: true));
    }

    [Fact]
    public void The_two_lines_are_ordered_and_the_band_is_subpixel()
    {
        Assert.True(NoteSurface.TextHideBelowPx < NoteSurface.TextDrawAtOrAbovePx);
        Assert.Equal(0.4, NoteSurface.TextDrawAtOrAbovePx - NoteSurface.TextHideBelowPx, 3);
    }

    [Fact]
    public void The_floor_follows_the_body_size_not_a_second_number()
    {
        // 判据由字号推出来：改字号时这档自己跟着走，不留第二个刻死的数。
        var zoom = NoteSurface.TextDrawAtOrAbovePx / NoteTypography.DefaultSize;

        Assert.True(NoteSurface.DrawsText(zoom, 1.0));
        Assert.False(NoteSurface.DrawsText(zoom * 0.99, 1.0));
    }

    [Fact]
    public void Text_stays_painted_at_the_overview_he_reads_daily()
    {
        // 缺陷复现锁（他 11-03 原话：「不显示有些影响观感」）。当年按"可辨线"办事，
        // 他日常那一档整板卡面是空的；这一发钉住"日常那一档必须有字"，谁再把地板抬回可辨线就红。
        Assert.True(NoteSurface.DrawsText(0.229, 1.0));

        // 地板不许高过他日常那一档的字高：留出的余量是给别的屏幕密度用的，不是给观感讨价还价的。
        Assert.True(NoteSurface.TextDrawAtOrAbovePx <= 0.229 * NoteTypography.DefaultSize);
    }
}
