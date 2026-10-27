namespace Outspread.Core.Tests;

/// <summary>
/// The dot grid's levels, brightness and phase.
/// </summary>
/// <remarks>
/// The numbers here are the prototype's, and two of the cases are scars: an early version keyed the
/// fade to the zoom instead of to the on-screen spacing, which at 100% left only the 4px step on
/// screen — a grey fog, not a grid. These tests are written so that mistake cannot come back.
/// </remarks>
public class BoardGridTests
{
    [Theory]
    [InlineData(64, 1, 1.0)]        // 100%：64px 间距落在满亮带（24–72）
    [InlineData(16, 1, 0.3333)]     // 16px 间距：往 12px 那一端淡出
    [InlineData(4, 1, 0.0)]         // 4px 已经低于 12 ⇒ 不画
    [InlineData(1, 1, 0.0)]
    [InlineData(16, 5, 0.6667)]     // 500%：16 的世界步长＝80px，往 96px 那一端淡出
    [InlineData(64, 5, 0.0)]        // 320px 间距早已超出 96 ⇒ 不画
    public void A_levels_brightness_is_decided_by_its_spacing_on_screen(double step, double zoom, double expected)
    {
        Assert.Equal(expected, BoardGrid.Alpha(step, zoom), 4);
    }

    [Fact]
    public void At_100_percent_the_grid_shows_the_64_and_16_steps_and_nothing_finer()
    {
        var levels = BoardGrid.LevelsFor(1);

        Assert.Equal(2, levels.Count);
        Assert.Equal(64, levels[0].Step);
        Assert.Equal(16, levels[1].Step);

        // 这一条就是"灰雾"回归：任何一档的屏幕间距都不许低于淡出下限。
        Assert.All(levels, level => Assert.True(level.Step * 1 >= BoardGrid.FadedOutBelowPx));
        Assert.Equal(64, BoardGrid.PeriodFor(1));
    }

    [Fact]
    public void Every_step_divides_the_period_so_one_tile_repeats_without_a_seam()
    {
        foreach (var zoom in new[] { 0.05, 0.2, 0.5, 1, 1.4, 2, 5, 8 })
        {
            var period = BoardGrid.PeriodFor(zoom);

            if (period <= 0)
            {
                continue;
            }

            foreach (var (step, _) in BoardGrid.LevelsFor(zoom))
            {
                var ratio = period / (step * zoom);

                Assert.True(
                    Math.Abs(ratio - Math.Round(ratio)) < 1e-9,
                    $"zoom {zoom}: period {period} is not a multiple of step {step * zoom}");
            }
        }
    }

    [Fact]
    public void Panning_only_moves_the_phase_and_it_stays_inside_one_period()
    {
        Assert.Equal(0, BoardGrid.Phase(0, 64));
        Assert.Equal(6, BoardGrid.Phase(70, 64));
        Assert.Equal(54, BoardGrid.Phase(-10, 64));
        Assert.Equal(0, BoardGrid.Phase(128, 64));

        // 周期为 0（一档都不画）时不许除零。
        Assert.Equal(0, BoardGrid.Phase(12, 0));
    }

    [Fact]
    public void The_grid_stays_a_grid_and_never_becomes_a_wash()
    {
        // 覆盖率＝每档「一个点的面积 / 一个格子的面积」之和。100% 时原型量到 1.56% 的墨，
        // 超过 2% 就说明有人在把点画大、或把不该亮的档点亮了。
        var coverage = 0.0;

        foreach (var (step, alpha) in BoardGrid.LevelsFor(1))
        {
            var spacing = step * 1;
            var dotArea = Math.PI * BoardGrid.DotRadiusPx * BoardGrid.DotRadiusPx;

            coverage += (dotArea / (spacing * spacing)) * (alpha * BoardGrid.PeakAlpha);
        }

        Assert.InRange(coverage, 0.001, 0.02);
    }

    [Fact]
    public void The_two_themes_get_different_dot_colour()
    {
        Assert.NotEqual(BoardGrid.Ink(dark: false), BoardGrid.Ink(dark: true));
        Assert.Equal(new Rgb(110, 110, 110), BoardGrid.Ink(dark: false));
        Assert.Equal(new Rgb(102, 102, 102), BoardGrid.Ink(dark: true));
    }

    [Fact]
    public void A_dot_on_an_empty_pixel_reaches_its_full_alpha()
    {
        var buffer = Stamp(0.85);

        Assert.Equal(217, AlphaAt(buffer, 8, 8));
    }

    [Fact]
    public void A_fainter_dot_landing_on_a_bright_one_never_makes_it_fainter()
    {
        // 100% 时 64 档与 16 档每 64 像素重合一次。第一版把 source-over 写成了
        // 「dst·(1−dstα)」而不是「dst·(1−srcα)」——空像素上两者相等，所以冒烟看不出来，
        // 而重合点上 0.85 被 0.283 压成了 0.41：亮档整档消失（PNG 上量到的就是这件事）。
        var bgra = new byte[16 * 16 * 4];

        BoardGrid.StampDot(bgra, 16, 16, 8, 8, 1, 0.85, BoardGrid.Ink(dark: false));
        BoardGrid.StampDot(bgra, 16, 16, 8, 8, 1, 0.283, BoardGrid.Ink(dark: false));

        Assert.Equal(228, AlphaAt(bgra, 8, 8));
    }

    [Theory]
    [InlineData(37, 23, 4.0, 1.0)]      // 密档：核 5×5 会压到邻点的边环，落点顺序参与成色
    [InlineData(64, 64, 16.0, 1.0)]     // 整档
    [InlineData(41, 17, 6.5, 2.0)]      // 非整数 spacing ＋ 大核：四舍五入的落点最容易被改动碰坏
    public void The_batched_lattice_paints_the_exact_same_bitmap_as_dot_by_dot(int width, int height, double spacing, double radius)
    {
        /* 这一发改的是点阵的**画法**，不是长相（缩放卡顿那 8.5ms 全在这一圈点上）。
           所以断言必须是逐字节相同，而不是"看着一样"——"看着一样"这一类里能装下好几种画错：
           落点顺序换了、边上一列没画、重合点用了查表那条快路。 */
        var ink = BoardGrid.Ink(dark: false);

        var reference = new byte[width * height * 4];
        var batched = new byte[width * height * 4];

        for (var x = 0.0; x < width; x += spacing)
        {
            for (var y = 0.0; y < height; y += spacing)
            {
                BoardGrid.StampDot(reference, width, height, x, y, radius, 0.31, ink);
            }
        }

        BoardGrid.StampLattice(batched, width, height, spacing, radius, 0.31, ink);

        Assert.True(
            reference.AsSpan().SequenceEqual(batched),
            $"{width}×{height} @ spacing {spacing}：批量入口画出了和逐点入口不同的位图 —— 第一处差异在像素 " +
            DescribeFirstDiff(reference, batched, width));

        // 正对照：两枚全 0 的缓冲也"逐字节相同"，所以必须证明这一发确实落了墨。
        Assert.Contains(batched, b => b != 0);
    }

    [Fact]
    public void Two_batched_levels_overlap_the_same_way_two_dot_by_dot_levels_do()
    {
        // 100% 那一档真的有两层点（16 与 4 重合），重合的像素走的是"目的地非空"那条原式。
        // 只测单层等于没测快路的分支条件——那正是第一版把亮档压暗的那一格。
        var ink = BoardGrid.Ink(dark: false);
        const int width = 72;
        const int height = 40;

        var reference = new byte[width * height * 4];
        var batched = new byte[width * height * 4];

        foreach (var (spacing, alpha) in new[] { (16.0, 0.85), (4.0, 0.283) })
        {
            for (var x = 0.0; x < width; x += spacing)
            {
                for (var y = 0.0; y < height; y += spacing)
                {
                    BoardGrid.StampDot(reference, width, height, x, y, 1, alpha, ink);
                }
            }

            BoardGrid.StampLattice(batched, width, height, spacing, 1, alpha, ink);
        }

        Assert.True(reference.AsSpan().SequenceEqual(batched), "两层点阵重合处画出了不一样的位图");

        // 正对照：重合点（0,0）必须还是亮档那颗的亮度，不能被淡档压回去。
        Assert.Equal(AlphaAt(reference, 0, 0), AlphaAt(batched, 0, 0));
        Assert.True(AlphaAt(batched, 0, 0) > 150, $"重合点亮度读回 {AlphaAt(batched, 0, 0)}，两档都没画坏也不该这么暗");
    }

    /// <summary>第一处不同的像素直接写进红灯——"两张位图不同"要再下一发调试才知道差在哪一格。</summary>
    private static string DescribeFirstDiff(byte[] expected, byte[] actual, int width)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] == actual[i])
            {
                continue;
            }

            var pixel = i / 4;

            return $"({pixel % width},{pixel / width}) 通道 {i % 4}：逐点 {expected[i]}，批量 {actual[i]}";
        }

        return "（找不到差异）";
    }

    [Fact]
    public void The_stamped_ink_mass_is_the_disc_area_times_the_alpha()
    {
        var buffer = Stamp(0.85);
        double mass = 0;

        for (var i = 3; i < buffer.Length; i += 4)
        {
            mass += buffer[i] / 255.0;
        }

        // 边缘用的是线性坡度而不是圆的积分，所以质量比 πr² 多 6.5%（半径 1px 时差一圈半像化的像素）。
        // 钉住这两件事：别漂成方点（覆盖率＝质量/α 只能略大于 πr²），也别被画小。
        var rampIntegral = 1 + (4 * 0.5) + (4 * (0.5 - (Math.Sqrt(2) - 1)));
        Assert.Equal(rampIntegral * 0.85, mass, 2);
        Assert.InRange(mass / 0.85 / (Math.PI * BoardGrid.DotRadiusPx * BoardGrid.DotRadiusPx), 1.0, 1.1);
    }

    [Fact]
    public void The_buffer_stays_premultiplied_so_the_ink_colour_survives()
    {
        var buffer = Stamp(0.85);
        var index = ((8 * 16) + 8) * 4;
        var alpha = buffer[index + 3] / 255.0;

        // 反解出来还是那支墨色；写成非预乘的话，点会在合成时发白。
        Assert.Equal(110, buffer[index + 2] / alpha, 0);
        Assert.Equal(110, buffer[index + 1] / alpha, 0);
        Assert.Equal(110, buffer[index] / alpha, 0);
    }

    [Fact]
    public void The_board_floor_is_the_prototype_bg_not_the_winui_theme_brush()
    {
        // WinUI 的 SolidBackgroundFillColorBase 浅档是 #F3F3F3（243），原型是 hsl(210 20% 98%)（250）；
        // 深色档差得更多：主题刷 #272727（39）对原型 16。整块板底会灰两级。
        Assert.Equal(new Rgb(249, 250, 251), BoardGrid.BoardBackground(dark: false));
        Assert.Equal(new Rgb(16, 16, 17), BoardGrid.BoardBackground(dark: true));
        Assert.NotEqual(243, BoardGrid.BoardBackground(dark: false).R);
    }

    [Fact]
    public void The_vignette_is_the_css_ellipse_not_half_of_it()
    {
        // 网页那一侧现读：radial-gradient(120% 90% at 50% 42%, transparent 45%, rgba(0,0,0,.055))
        // 120%/90% 是半径。PNG 上 (200,430)/1500×940 处实测还是 249,250,251（一点没压暗），
        // 而按 0.6/0.45 那版会压到 245.8 ⇒ 半径读成直径，暗角强了三倍。
        Assert.Equal(1.2, BoardGrid.VignetteRadiusX);
        Assert.Equal(0.9, BoardGrid.VignetteRadiusY);
        Assert.Equal(0, BoardGrid.VignetteAt(200 / 1500.0, 430 / 940.0, dark: false), 3);
        Assert.Equal(0.0175, BoardGrid.VignetteAt(0, 0, dark: false), 3);
        Assert.Equal(0, BoardGrid.VignetteAt(0.5, 0.42, dark: false));

        // 不变量：把标度放大十倍，角上仍是最暗、中心仍是最亮。
        Assert.True(BoardGrid.VignetteAt(0, 0, true) > BoardGrid.VignetteAt(0.5, 0.42, true));
    }

    private static byte[] Stamp(double alpha)
    {
        var bgra = new byte[16 * 16 * 4];
        BoardGrid.StampDot(bgra, 16, 16, 8, 8, BoardGrid.DotRadiusPx, alpha, BoardGrid.Ink(dark: false));

        return bgra;
    }

    private static int AlphaAt(byte[] bgra, int x, int y) => bgra[(((y * 16) + x) * 4) + 3];

}
