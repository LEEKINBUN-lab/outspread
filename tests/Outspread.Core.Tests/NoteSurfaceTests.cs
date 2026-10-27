namespace Outspread.Core.Tests;

/// <summary>
/// The paper shadow and the type triples, checked against the prototype that froze them.
/// </summary>
/// <remarks>
/// The expected numbers below were not written by hand: they were read out of
/// <c>design/feel.html</c> running in a browser (<c>makeRng(id)</c> and <c>paperShadow(id, zoom)</c>
/// for the listed ids), so this test is a cross-check between two implementations of the same
/// recipe rather than a restatement of one. If the port and the prototype ever disagree, a note
/// will sit at a different height off the paper in the two products — which is exactly the kind
/// of thing the Android golden corpus exists to catch.
/// </remarks>
public class NoteSurfaceTests
{
    private static readonly (string Id, double Lift, double Jitter)[] Golden =
    [
        ("n1", 0.859236, 0.173024),
        ("n2", 0.767567, 0.598489),
        ("n7", 0.675897, 0.023953),
        ("node-7", 1.291485, 0.343971),
        ("u1234567890ab", 1.171069, 0.733299),
        ("a0f3c9d1e2b4c4", 1.379104, 0.867996),
        ("x", 0.555444, 0.288213),
    ];

    [Theory]
    [InlineData("n1")]
    [InlineData("n2")]
    [InlineData("node-7")]
    [InlineData("u1234567890ab")]
    [InlineData("a0f3c9d1e2b4c4")]
    [InlineData("x")]
    public void The_lift_for_a_note_id_matches_the_prototype_exactly(string id)
    {
        var golden = Array.Find(Golden, entry => entry.Id == id);

        Assert.Equal(id, golden.Id);
        Assert.Equal(golden.Lift, NoteSurface.LiftFor(id), 6);
    }

    [Fact]
    public void The_three_layers_are_the_ones_the_prototype_paints()
    {
        // n1 at zoom 1: "0px 4.14px 5.00px -5.00px rgb(15 23 31 / .6), 0px 10.01px 12.01px
        // -9.16px rgb(15 23 31 / 0.39), 0px 48.00px 10.00px -10.00px inset rgb(15 23 44 / 0.023)"
        var layers = NoteSurface.LayersFor("n1", 1);

        Assert.Equal(3, layers.Count);
        Assert.Equal(4.14, layers[0].OffsetY, 2);
        Assert.Equal(5.00, layers[0].Blur, 2);
        Assert.Equal(-5.00, layers[0].Spread, 2);
        Assert.Equal(0.6, layers[0].Alpha, 3);
        Assert.False(layers[0].Inset);

        Assert.Equal(10.01, layers[1].OffsetY, 2);
        Assert.Equal(12.01, layers[1].Blur, 2);
        Assert.Equal(-9.16, layers[1].Spread, 2);
        Assert.Equal(0.39, layers[1].Alpha, 2);

        Assert.Equal(48.00, layers[2].OffsetY, 2);
        Assert.Equal(10.00, layers[2].Blur, 2);
        Assert.True(layers[2].Inset, "the third layer is the one drawn inside the top edge");
        Assert.Equal(0.023, layers[2].Alpha, 3);

        // Shadow colour: tldraw ships rgb(15 23 31), and the inset layer rgb(15 23 44).
        Assert.Equal(new Rgb(15, 23, 31), layers[0].Color);
        Assert.Equal(new Rgb(15, 23, 44), layers[2].Color);
    }

    [Fact]
    public void Zooming_scales_the_geometry_except_the_lift_which_stays_a_property_of_the_note()
    {
        // n1 at zoom 0.5 in the prototype: "0px 1.64px 2.50px -2.50px …, 0px 5.01px 6.01px
        // -4.58px …, 0px 24.00px 5.00px -5.00px inset …"
        var layers = NoteSurface.LayersFor("n1", 0.5);

        Assert.Equal(1.64, layers[0].OffsetY, 2);
        Assert.Equal(2.50, layers[0].Blur, 2);
        Assert.Equal(5.01, layers[1].OffsetY, 2);
        Assert.Equal(24.00, layers[2].OffsetY, 2);

        // The lift is 0.859 either way, which is why the first layer is 2.5 − 0.859 and not
        // 0.5 × (5 − 0.859): a note does not become better glued to the paper when you zoom out.
        Assert.Equal(5 * 0.5 - NoteSurface.LiftFor("n1"), layers[0].OffsetY, 6);
    }

    [Fact]
    public void A_card_is_painted_with_its_own_light_not_a_flat_colour()
    {
        // 平色填充读起来是贴纸而不是纸。这一格只值几级，所以任何一个采样点都量不出来——
        // 两张截图并排看才看得出来：网页那张顶 234,247,238／底 221,236,226，App 第一版上下都是 230,246,235。
        var green = new Rgb(229, 245, 234);

        Assert.Equal((0.16, 0.04), NoteSurface.PaperLight(dark: false));
        Assert.Equal((0.10, 0.22), NoteSurface.PaperLight(dark: true));
        Assert.Equal(0.42, NoteSurface.PaperMiddleOffset);

        Assert.Equal(new Rgb(233, 247, 237), NoteSurface.Over(green, new Rgb(255, 255, 255), 0.16));
        Assert.Equal(new Rgb(220, 235, 225), NoteSurface.Over(green, new Rgb(0, 0, 0), 0.04));

        // 不变量：受光只动亮度、不动色相方向；而且顶到底必须真的更亮（否则这层等于没画）。
        var lit = NoteSurface.Over(green, new Rgb(255, 255, 255), 0.16);
        var pressed = NoteSurface.Over(green, new Rgb(0, 0, 0), 0.04);
        Assert.True(lit.G > green.G && green.G > pressed.G);
    }

    [Fact]
    public void The_paper_ramp_has_exactly_one_source_of_truth()
    {
        // 卡片按这条斜坡铺底，压在它上面的渐隐也必须按同一条取色——两边同一个函数，带子才和纸同色。
        // 旧写法拿卡面本色平铺那道带子，于是它在任何一条线上都和纸差几级：放大看就是"颜色分层"。
        var green = new Rgb(229, 245, 234);

        Assert.Equal(new Rgb(233, 247, 237), NoteSurface.PaperAt(green, 0, dark: false));
        Assert.Equal(green, NoteSurface.PaperAt(green, NoteSurface.PaperMiddleOffset, dark: false));
        Assert.Equal(new Rgb(220, 235, 225), NoteSurface.PaperAt(green, 1, dark: false));

        // 暗档只是把受光的两端换成 10% 白与 22% 黑，中段仍是卡面本色。
        Assert.Equal(NoteSurface.Over(green, new Rgb(255, 255, 255), 0.10), NoteSurface.PaperAt(green, 0, dark: true));
        Assert.Equal(green, NoteSurface.PaperAt(green, NoteSurface.PaperMiddleOffset, dark: true));
        Assert.Equal(NoteSurface.Over(green, new Rgb(0, 0, 0), 0.22), NoteSurface.PaperAt(green, 1, dark: true));

        // 一路单调变暗，中间不许翻上来：翻上来就会在带子里多出一道边。
        var samples = Enumerable.Range(0, 41).Select(i => NoteSurface.PaperAt(green, i / 40.0, dark: false)).ToList();

        Assert.True(samples.Zip(samples.Skip(1)).All(pair => Luma(pair.First) >= Luma(pair.Second)));

        // 越界不许 NaN、不许回弹：卡高被压到 0 那一发，调用方给的 y 落在纸外也得停在纸边上。
        Assert.Equal(NoteSurface.PaperAt(green, 1, dark: false), NoteSurface.PaperAt(green, 9, dark: false));
        Assert.Equal(NoteSurface.PaperAt(green, 0, dark: false), NoteSurface.PaperAt(green, -3, dark: false));
    }

    private static int Luma(Rgb colour) => colour.R + colour.G + colour.B;

    [Fact]
    public void The_shadow_survives_at_the_level_of_detail_threshold_and_not_below_it()
    {
        Assert.True(NoteSurface.DrawsShadow(NoteSurface.ShadowMinZoom));
        Assert.True(NoteSurface.DrawsShadow(1));
        Assert.False(NoteSurface.DrawsShadow(0.24));
        Assert.False(NoteSurface.DrawsShadow(0.05));
    }

    [Fact]
    public void The_shadow_level_of_detail_holds_its_state_inside_the_hysteresis_band()
    {
        /* 10-27 量出来的那一发：他那块「Agent 出口」装进视口正好是 0.244×，压在阈值上。
           裸阈值比较会让来回滚轮每帧翻一次档，而每翻一次全部可见格都要重铺阴影层——
           同装置实测 104ms/帧；退开到 0.40× 只要 8.3ms。所以这一级 LOD 带一圈迟滞：
           低于阈值一定关、要回到阈值以上一整带才开，中间那一段**保持上一拍的状态**。 */
        const double line = NoteSurface.ShadowMinZoom;
        const double band = line + NoteSurface.ShadowLodBand;

        Assert.False(NoteSurface.ShadowLod(0.10, wasOn: true));      // 深处：一定要关
        Assert.False(NoteSurface.ShadowLod(line - 0.001, wasOn: true));
        Assert.True(NoteSurface.ShadowLod(1.0, wasOn: false));       // 高处：一定要开
        Assert.True(NoteSurface.ShadowLod(band, wasOn: false));

        // 中间这一段：两向都保持原样——这正是"压在阈值上来回滚轮"待的那一段。
        var middle = line + (NoteSurface.ShadowLodBand / 2);

        Assert.True(NoteSurface.ShadowLod(middle, wasOn: true), "在迟滞带里，本来画阴影的不该被关掉");
        Assert.False(NoteSurface.ShadowLod(middle, wasOn: false), "在迟滞带里，本来画底线的不该被翻开");

        // 正对照：裸阈值比较在阈值两侧给出**不同**答案——那正是"相机压在阈值上、来回滚轮
        // 每帧翻一次档"的成因（第一版我把这一条写成在阈值同侧比，闸当场把它红了）。
        Assert.NotEqual(NoteSurface.DrawsShadow(line), NoteSurface.DrawsShadow(line - 0.01));
    }

    [Fact]
    public void Different_ids_really_do_sit_at_different_heights()
    {
        // A seeded lift that never varied would make the whole recipe decoration. Seven ids is
        // enough to show the spread without depending on one lucky hash.
        var lifts = Golden.Select(entry => NoteSurface.LiftFor(entry.Id)).ToList();

        Assert.Equal(Golden.Length, lifts.Distinct().Count());
        Assert.All(lifts, lift => Assert.InRange(lift, 0.5, 1.5));
        Assert.True(lifts.Max() - lifts.Min() > 0.5, $"spread is only {lifts.Max() - lifts.Min():0.000}");
    }

    [Fact]
    public void The_bands_a_note_painted_are_derived_from_its_layers_not_invented()
    {
        // n1 at zoom 1, a 260×120 card. The layers are (4.14, 5, −5), (10.01, 12.01, −9.16)
        // and the inset (48, 10, −10).
        var bands = NoteSurface.BandsFor("n1", 1, 260, 120);

        Assert.Equal(3, bands.Count);

        // What survives an opaque card is the tail of the blur poking past its bottom edge, so
        // the band starts exactly at the edge — and it is SHORTER than offsetY + blur, because a
        // tucked shadow's own edge is already behind the card.
        Assert.Equal(120, bands[0].Y, 6);
        Assert.Equal(6.64, bands[0].Height, 2);
        Assert.Equal(120, bands[1].Y, 6);
        Assert.Equal(18.88, bands[1].Height, 2);

        // The peak is a fraction of the layer's alpha, never the layer's alpha itself. The first
        // port used .6 as the peak of a 5px band and the screenshot came back with grey planks
        // under the notes instead of a lift, which is the whole reason this assertion exists.
        var layers = NoteSurface.LayersFor("n1", 1);

        Assert.True(bands[0].Alpha < layers[0].Alpha, $"peak {bands[0].Alpha:0.###} vs layer {layers[0].Alpha}");
        Assert.True(bands[0].Alpha > 0.05, $"a peak this faint is not a shadow at all: {bands[0].Alpha:0.####}");
        Assert.True(bands[1].Alpha < layers[1].Alpha);

        // The deeper layer reaches further down, which is what makes the stack read as one soft
        // shadow instead of two hard lines.
        Assert.True(bands[1].Height > bands[0].Height);

        // The negative spread tucks the band in from the sides; blur re-widens it by half, and
        // the result must never be negative or wider than the card.
        Assert.True(bands[0].X > 0 && bands[0].X < 260 / 2, $"inset {bands[0].X}");
        Assert.True(bands[0].Width <= 260, $"band wider than the card: {bands[0].Width}");
        Assert.Equal(260 - (bands[0].X * 2), bands[0].Width, 6);

        // The third layer belongs inside the card, pressing on the top edge.
        Assert.True(bands[2].Inset);
        Assert.Equal(0, bands[2].Y, 6);
    }

    [Fact]
    public void The_shadow_peak_lands_on_the_grey_the_prototype_measures()
    {
        // 参考值不是算出来的，是从网页那张 PNG 上量的：卡片下边缘那一行灰阶 178（板底 249、影色 rgb(15,23,31)）。
        // 允许 ±6 是给截图的抗锯齿与缩放余量，不是给"差不多就行"。
        var peak = NoteSurface.BandsFor("n1", 1, 260, 120).Where(band => !band.Inset).Sum(band => band.Alpha);
        var grey = (249 * (1 - peak)) + (15 * peak);

        Assert.True(Math.Abs(grey - 178) <= 6, $"composited peak is grey {grey:0.#}; the prototype measures 178");
        Assert.True(peak < 0.35, $"the raw layer alphas sum to 0.44 — without the box-blur calibration this is the plank");
    }

    [Fact]
    public void A_band_fades_monotonically_and_ends_where_the_blur_runs_out()
    {
        var band = NoteSurface.BandsFor("n1", 1, 260, 120)[1];

        Assert.Equal(5, band.Stops.Count);
        Assert.Equal(0, band.Stops[0].Offset, 6);
        Assert.Equal(band.Height, band.Stops[^1].Offset, 6);

        for (var i = 1; i < band.Stops.Count; i++)
        {
            Assert.True(
                band.Stops[i].Alpha <= band.Stops[i - 1].Alpha,
                $"stop {i} is brighter than the one before it: {band.Stops[i].Alpha} > {band.Stops[i - 1].Alpha}");
        }

        Assert.True(band.Stops[^1].Alpha < band.Stops[0].Alpha / 10, "the far end must have faded out");
    }

    [Fact]
    public void The_gaussian_tail_is_a_half_at_the_edge_and_saturates_on_both_sides()
    {
        Assert.Equal(0.5, NoteSurface.TailPastEdge(0, 3), 6);
        Assert.Equal(1, NoteSurface.TailPastEdge(-9000, 3), 2);
        Assert.Equal(0, NoteSurface.TailPastEdge(9000, 3), 6);
        Assert.Equal(0, NoteSurface.TailPastEdge(5, 0), 6);

        // Monotone: further out can never be darker.
        var previous = 1.0;

        for (var distance = -20.0; distance <= 20; distance += 2)
        {
            var value = NoteSurface.TailPastEdge(distance, 4);

            Assert.InRange(value, 0, 1);
            Assert.True(value <= previous + 1e-9, $"the tail went back up at distance {distance}");
            previous = value;
        }
    }

    [Fact]
    public void At_the_level_of_detail_threshold_the_shadow_becomes_no_bands_and_one_line()
    {
        Assert.Empty(NoteSurface.BandsFor("n1", 0.24, 260, 120));
        Assert.NotEmpty(NoteSurface.BandsFor("n1", NoteSurface.ShadowMinZoom, 260, 120));

        // The greys are tldraw's published note borders, and the dark theme really moves.
        Assert.NotEqual(NoteSurface.FallbackLineColor(false), NoteSurface.FallbackLineColor(true));
        Assert.Equal(new Rgb(144, 144, 144), NoteSurface.FallbackLineColor(false));
        Assert.Equal(new Rgb(20, 20, 20), NoteSurface.FallbackLineColor(true));
    }

    [Fact]
    public void Zoom_scales_a_band_the_same_way_it_scales_the_card()
    {
        var atOne = NoteSurface.BandsFor("n1", 1, 260, 120);
        var atTwo = NoteSurface.BandsFor("n1", 2, 260, 120);

        Assert.Equal(240, atTwo[0].Y, 6);
        Assert.Equal(atOne[0].Height * 2 + NoteSurface.LiftFor("n1"), atTwo[0].Height, 2);

        // The lift does not scale, so a doubled zoom adds the band's own growth on top of ×2.
        Assert.True(atTwo[0].Height > atOne[0].Height * 2);
    }

    [Theory]
    [InlineData(14, 19, 16)]
    [InlineData(15, 21, 17)]
    [InlineData(16, 22, 18)]
    public void Each_size_carries_its_own_leading_and_padding(double size, double leading, double padding)
    {
        var type = NoteTypography.For(size);

        Assert.Equal(size, type.Size);
        Assert.Equal(leading, type.Leading);
        Assert.Equal(padding, type.Padding);
    }

    [Fact]
    public void The_shipped_default_is_fifteen_and_an_unknown_size_falls_back_to_it()
    {
        Assert.Equal(15, NoteTypography.DefaultSize);
        Assert.Equal(NoteType.Fifteen, NoteTypography.For(18));
        Assert.Equal(NoteType.Fifteen, NoteTypography.For(9));
    }

    [Fact]
    public void An_empty_note_is_still_a_card_and_not_a_stripe()
    {
        Assert.Equal(64, NoteType.Fifteen.MinimumHeight, 6);
        Assert.Equal(64, NoteType.Fourteen.MinimumHeight, 6);

        // 16/22/18 would want 22 + 36 = 58, still under the 64 floor.
        Assert.Equal(64, NoteType.Sixteen.MinimumHeight, 6);
    }
}
