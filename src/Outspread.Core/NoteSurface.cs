namespace Outspread.Core;

/// <summary>
/// The paper shadow a note carries instead of a border, and the type triple its text is set in.
/// </summary>
/// <remarks>
/// Both come from shipped products rather than from taste. The shadow is tldraw's note recipe
/// (<c>NoteShapeUtil.tsx</c>, the three layers <c>getNoteShadow</c> returns); the type triple is
/// the one the frozen prototype measures — a Chinese body at 15px wants 21px of leading and 17px
/// of inset, and the three numbers have to move together or the card looks "somehow unbalanced"
/// without anyone being able to say which number is wrong.
/// </remarks>
public static class NoteSurface
{
    /// <summary>
    /// The card's own light: <c>--paper-top</c> and <c>--paper-bottom</c>, composited over the
    /// fill as a vertical ramp whose middle stop is the untouched colour at 42%.
    /// </summary>
    /// <remarks>
    /// Flat-filled, a card reads as a sticker rather than as a piece of paper — that is the whole
    /// difference the eye catches between the prototype and a first port, and it is invisible to
    /// any single sample point because it is worth only a few levels.
    /// </remarks>
    public static (double Top, double Bottom) PaperLight(bool dark) => dark ? (0.10, 0.22) : (0.16, 0.04);

    /// <summary>Where the ramp's middle (untouched) stop sits.</summary>
    public const double PaperMiddleOffset = 0.42;

    /// <summary><paramref name="front"/> at <paramref name="alpha"/> over <paramref name="back"/>.</summary>
    public static Rgb Over(Rgb back, Rgb front, double alpha)
        => new(
            (byte)Math.Round((front.R * alpha) + (back.R * (1 - alpha))),
            (byte)Math.Round((front.G * alpha) + (back.G * (1 - alpha))),
            (byte)Math.Round((front.B * alpha) + (back.B * (1 - alpha))));

    /// <summary>
    /// The card's own paper colour at one height: <paramref name="y"/> is 0 at the card's top edge
    /// and 1 at its bottom, and the ramp is the one <see cref="PaperLight"/> describes.
    /// </summary>
    /// <remarks>
    /// This is the single definition of "what colour is the card at this line" — the card's fill and
    /// anything painted on top of it (the overflow fade) have to read it from here, or the two
    /// disagree by a few levels and the overlay reads as a second sheet of paper laid on the first.
    /// Interpolating the overlay's <i>alpha</i> is the same thing as interpolating the composited
    /// colour, because <see cref="Over"/> is affine in it — so this reproduces the three-stop brush
    /// exactly, kink at 42% included.
    /// </remarks>
    public static Rgb PaperAt(Rgb surface, double y, bool dark)
    {
        var (top, bottom) = PaperLight(dark);
        var k = Math.Clamp(y, 0, 1);

        // 1 - PaperMiddleOffset is exact for a double at 0.42, but guard the divide anyway: a
        // future token edit that parks the middle stop on the bottom edge must not yield NaN.
        var span = 1 - PaperMiddleOffset;

        return k <= PaperMiddleOffset
            ? Over(surface, new Rgb(255, 255, 255), top * (1 - (k / PaperMiddleOffset)))
            : Over(surface, new Rgb(0, 0, 0), span <= 0 ? 0 : bottom * ((k - PaperMiddleOffset) / span));
    }

    /// <summary>Below this zoom the shadow is dropped and one bottom line is drawn instead.</summary>
    /// <remarks>
    /// tldraw's own level-of-detail budget: a blurred edge costs a redraw per frame, and at 25%
    /// nobody can see it anyway. The threshold is published in its <c>options.ts</c>.
    /// </remarks>
    public const double ShadowMinZoom = 0.25;

    /// <summary>The bottom line's width in world units when the shadow is off.</summary>
    public const double FallbackLineWidth = 2;

    /// <summary>迟滞带的宽度（缩放档）：阈值上方这一段里，阴影保持它原来的状态不翻。</summary>
    public const double ShadowLodBand = 0.05;

    /// <summary>
    /// "阴影 ↔ 一条底线"这一级 LOD 的<b>下一状态</b>——带一圈迟滞，所以它要看上一拍是什么。
    /// </summary>
    /// <remarks>
    /// 为什么不是裸阈值比较：板子缩到正好压在 <see cref="ShadowMinZoom"/> 上时（他那块「Agent 出口」
    /// 装进视口正好是 0.244×），来回滚轮等于每帧翻一次档，而每翻一次全部可见格都要重铺阴影层——
    /// 同装置实测 <b>104ms/帧</b>，把相机退开到 0.40× 只要 8.3ms。那 0.05 档里两种画法在屏幕上
    /// 差不到一个像素（阴影在世界数下只有 5–16 像素），代价却差一个数量级。
    /// </remarks>
    /// <param name="wasOn">这一格上一拍是画阴影还是画底线。</param>
    public static bool ShadowLod(double zoom, bool wasOn)
        => zoom < ShadowMinZoom ? false
            : zoom >= ShadowMinZoom + ShadowLodBand ? true
            : wasOn;

    /// <summary>One layer of a shadow: how far it drops, how soft, how tucked in, how dark.</summary>
    /// <param name="OffsetY">Downward shift in world units. tldraw never shifts sideways.</param>
    /// <param name="Blur">Softness radius in world units.</param>
    /// <param name="Spread">Negative pulls the shadow inside the silhouette, which is what keeps a
    /// square card from looking round at the corners.</param>
    /// <param name="Inset">True for the layer drawn inside the top edge instead of under the card.</param>
    public readonly record struct Layer(double OffsetY, double Blur, double Spread, bool Inset, Rgb Color, double Alpha);

    /// <summary>
    /// The per-note lift, seeded from the node id: 0.5–1.5.
    /// </summary>
    /// <remarks>
    /// Seeded rather than random so the same note lifts the same amount on every redraw, on every
    /// machine, and in the Android port. Two notes with different ids sit at different heights off
    /// the paper — that is the whole trick that keeps a board of identical cards from looking flat.
    /// </remarks>
    public static double LiftFor(string id) => DrawsFor(id).Lift;

    /// <summary>
    /// The three layers, already multiplied by <paramref name="zoom"/> so a caller only paints.
    /// </summary>
    public static IReadOnlyList<Layer> LayersFor(string id, double zoom)
    {
        var (lift, jitter) = DrawsFor(id);

        return LayersAt(lift, jitter, zoom);
    }

    private static IReadOnlyList<Layer> LayersAt(double lift, double jitter, double zoom)
    {
        // The letters a/b/c/d are tldraw's own names in getNoteShadow, kept here so the arithmetic
        // can be checked against its source line by line.
        var a = 5 * zoom;
        var b = 4 * zoom;
        var c = 6 * zoom;
        var d = 7 * zoom;

        // rgb(15 23 31) is the ink-blue shadow colour tldraw ships; the alphas are its .6,
        // .3+.1·lift and the 48:10 inset layer at .022+·jitter.
        var ink = new Rgb(15, 23, 31);
        var inkDark = new Rgb(15, 23, 44);

        return
        [
            new(Math.Max(0, a - lift), a, -a, false, ink, 0.6),
            new(b + lift * d, c + lift * d, -(b + lift * c), false, ink, 0.3 + lift * 0.1),
            new(48 * zoom, 10 * zoom, -10 * zoom, true, inkDark, 0.022 + jitter * 0.005),
        ];
    }

    /// <summary>Whether this note draws a shadow at all, or the single line that replaces it.</summary>
    public static bool DrawsShadow(double zoom) => zoom >= ShadowMinZoom;

    /// <summary>
    /// 汉字在屏幕上高到这一像素就一律画正文。<b>这一档是"噪声地板"，不是"可辨线"</b>：
    /// 11-03 之前它叫 <c>TextLegiblePx = 7</c>，按"看不清就别画"办事，结果他日常那一档（23%，字高 3.4 像素）
    /// 整板卡面只剩色条与牌子，他报「不显示有些影响观感」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 改判据的账（11-03 现测，同一把尺两臂背靠背各两遍，临时构建跑副本，产品板没动）：永久画字相对藏字，
    /// 他那块板每帧 82→93ms（+14%），300 格整屏那种 24→40ms（+66%），停手那一发 heavy 544→632ms；
    /// 而牌子那一层摘掉能省 ~50ms、跨档整格重跑那一发是 518ms——都比文字层大一到两个数量级。
    /// 所以观感优先，只把"笔画并进一个像素、画了只剩灰噪"那一档留着不画。
    /// </para>
    /// <para>
    /// 判据仍是<b>屏幕字高</b>（<c>zoom × scale × 字号</c>），不是缩放数：同一档 8% 在 1.0 缩放的屏上是
    /// 1.2 像素（噪声），在 2.0 缩放的屏上是 2.4 像素（画）。拿缩放数当尺会在高分屏上提前把字藏掉。
    /// 8% 那一档画出来是什么样子看过图（均匀灰带、不闪、无摩尔纹），仍判为不值得画，故地板留在 2 像素。
    /// 要挪这个数就一起挪下面那条带宽，并把那一档的截图附在提交里。
    /// </para>
    /// </remarks>
    public const double TextDrawAtOrAbovePx = 2;

    /// <summary>
    /// 迟滞带的下沿（屏幕像素）：<b>2 以上一定画、1.6 以下一定不画、1.6–2 之间看上一拍</b>。
    /// 带宽放在阈值<b>下面</b>而不是两边对称，是为了让常用档（50% 是 7.5 像素、他日常的 23% 是 3.4 像素）
    /// 都落在"一定画"那一侧；对称带会把阈值上下各半档变成"看你怎么进来的"。
    /// </summary>
    public const double TextHideBelowPx = 1.6;

    /// <summary>这一档屏幕上画的还是不是字（而不是并进一像素的噪声）。</summary>
    public static bool DrawsText(double zoom, double scale) => ScreenTextPx(zoom, scale) >= TextDrawAtOrAbovePx;

    /// <summary>
    /// 文字 LOD 的<b>下一状态</b>——带 0.4 像素带宽，所以它要看上一拍是什么，与 <see cref="ShadowLod"/> 同一形状。
    /// </summary>
    public static bool TextLod(double zoom, double scale, bool wasOn)
    {
        var px = ScreenTextPx(zoom, scale);

        return px < TextHideBelowPx ? false
            : px >= TextDrawAtOrAbovePx ? true
            : wasOn;
    }

    private static double ScreenTextPx(double zoom, double scale)
        => zoom * scale * NoteTypography.DefaultSize;

    /// <summary>
    /// 这一格的阴影最远伸到盒底下方多少（世界单位）。遮挡判定拿它当外扩，比盒子本身宽。
    /// </summary>
    public static double ShadowReachWorld(string id)
    {
        var (lift, _) = DrawsFor(id);

        return ReachAt(lift);
    }

    /// <summary>lift 走到哪一档都盖得住的那个数（0.5–1.5 的最大端）；逐格算不如用它一次。</summary>
    public static double MaxShadowReachWorld => ReachAt(1.5);

    /// <summary>
    /// 非内缩那两层各自"伸出盒底的距离"（<c>edge + 3σ</c>，与 <see cref="BandsFor"/> 同一个式子）取最远的一层。
    /// </summary>
    /// <remarks>
    /// 为什么阴影算不出"被盖住"却能吃掉判定：一层阴影带画在卡盒<b>外面</b>，所以"这一格的纸被压住了"
    /// 不等于"这一格什么都不剩"。摘早了就是 10-22 那排"拖完还留着一排阴影"的反面版本——少了一排。
    /// 底线那一档（阴影关掉时画的 2 宽那条）也一并兜住。
    /// </remarks>
    private static double ReachAt(double lift)
    {
        var reach = FallbackLineWidth;

        foreach (var layer in LayersAt(lift, 0, 1.0))
        {
            if (layer.Inset)
            {
                continue;
            }

            var edge = layer.OffsetY + layer.Spread;
            var candidate = edge + 3 * (layer.Blur / 2);

            if (candidate > reach)
            {
                reach = candidate;
            }
        }

        return reach;
    }

    /// <summary>A rectangle to paint, in screen pixels, relative to the card's top-left.</summary>
    /// <param name="Alpha">How dark the band is right where it meets the card — NOT the layer's own
    /// alpha, which belongs to the whole blurred shape. See <paramref name="Stops"/>.</param>
    /// <param name="Inset">True when the band belongs inside the card's own box (the layer that
    /// presses down on the top edge) rather than under it.</param>
    public readonly record struct Band(
        double X,
        double Y,
        double Width,
        double Height,
        double Alpha,
        Rgb Color,
        bool Inset,
        IReadOnlyList<(double Offset, double Alpha)> Stops);

    /// <summary>
    /// The tail of a Gaussian: the fraction of a shadow's alpha that survives past an edge.
    /// </summary>
    /// <remarks>
    /// A CSS box-shadow is a blurred rectangle, so what is visible below a card is only the part of
    /// that blur which pokes past the card's own opaque background. Telling the difference matters:
    /// the first port used the layer's full alpha (.6) as the peak of the band, and the screenshot
    /// came back with a grey plank under every note instead of a lift. Abramowitz &amp; Stegun
    /// 7.1.26 is the usual hand-rolled erfc — accurate to ~1.5e-7, far below one alpha byte.
    /// </remarks>
    public static double TailPastEdge(double distance, double sigma)
    {
        if (sigma <= 0)
        {
            return distance <= 0 ? 1 : 0;
        }

        var z = distance / (sigma * Math.Sqrt(2));

        if (z < 0)
        {
            return 1 - TailPastEdge(-distance, sigma);
        }

        var t = 1 / (1 + (0.3275911 * z));

        var polynomial = t * (0.254829592
            + t * (-0.284496736
            + t * (1.421413741
            + t * (-1.453152027
            + t * 1.061405429))));

        // Q(x) = ½·erfc(x/√2): the half is what makes the value at the edge 0.5, not 1.
        return 0.5 * polynomial * Math.Exp(-z * z);
    }

    /// <summary>
    /// Chrome does not blur a box-shadow with a true Gaussian — it runs the three-pass box blur,
    /// whose peak at an edge is lower and whose tail is flatter.
    /// </summary>
    /// <remarks>
    /// 0.695 is measured, not derived: the app's own PNG put the pixel right under a card's bottom
    /// edge at grey 151 while the prototype put the same layer at 178 (board 249, shadow rgb(15,23,31)).
    /// Solving each for alpha gives 0.437 against 0.304, and their ratio is this number. Without it the
    /// notes sit on dark planks — the exact complaint the first screenshot of this port caught.
    /// </remarks>
    public const double BoxBlurPeakFactor = 0.695;

    /// <summary>Five samples of one blurred edge, taken from the card's boundary outward.</summary>
    private static IReadOnlyList<(double Offset, double Alpha)> SampleStops(double alpha, double edge, double sigma)
    {
        var stops = new List<(double, double)>(5);

        var reach = Math.Max(0.001, edge + (3 * sigma));

        for (var i = 0; i < 5; i++)
        {
            var offset = reach * i / 4;

            stops.Add((offset, alpha * BoxBlurPeakFactor * TailPastEdge(offset - edge, sigma)));
        }

        return stops;
    }

    /// <summary>
    /// Where each shadow layer actually lands on screen.
    /// </summary>
    /// <remarks>
    /// A CSS box-shadow is drawn behind an opaque background, so what survives to the eye is the
    /// part that pokes out past the bottom edge: a band starting at the edge and fading out after
    /// <c>offsetY + blur</c>, narrowed on the sides by however much the negative spread tucked it
    /// in (less half the blur, since blur re-widens it). Turning the layers into rectangles here
    /// keeps the arithmetic testable and leaves the WinUI side with nothing to decide.
    /// </remarks>
    public static IReadOnlyList<Band> BandsFor(string id, double zoom, double widthWorld, double heightWorld)
    {
        if (!DrawsShadow(zoom))
        {
            return [];
        }

        var bands = new List<Band>(3);

        foreach (var layer in LayersFor(id, zoom))
        {
            // LayersFor already multiplied by the zoom, so these are screen pixels from here on.
            var offsetY = layer.OffsetY;
            var blur = layer.Blur;
            var sigma = blur / 2;
            var tuck = Math.Max(0, -layer.Spread - (blur / 2));
            var width = Math.Max(0, (widthWorld * zoom) - (tuck * 2));

            if (layer.Inset)
            {
                // The third layer is 48 down and 10 tucked, so what lands on the paper is the
                // faint press along the inside of the top edge, not a band under the card.
                var press = SampleStops(layer.Alpha, -layer.OffsetY, sigma);

                bands.Add(new Band(tuck, 0, width, blur, press[0].Alpha, layer.Color, true, press));
                continue;
            }

            // Where the tucked shadow's own bottom edge lies, relative to the card's bottom edge.
            // Negative means it is still behind the card and only its blur escapes.
            var edge = offsetY + layer.Spread;
            var reach = Math.Max(0, edge + (3 * sigma));

            if (reach <= 0)
            {
                continue;
            }

            var stops = SampleStops(layer.Alpha, edge, sigma);

            bands.Add(new Band(
                tuck,
                heightWorld * zoom,
                width,
                reach,
                stops[0].Alpha,
                layer.Color,
                false,
                stops));
        }

        return bands;
    }

    /// <summary>The colour of the single bottom line drawn when the shadow is off.</summary>
    /// <remarks>
    /// These are tldraw's published note-border greys (<c>rgb(144,144,144)</c> light and
    /// <c>rgb(20,20,20)</c> dark) — the point of the level-of-detail rule is that a 1px line costs
    /// nothing at 5% zoom while a blurred band costs a redraw every frame.
    /// </remarks>
    public static Rgb FallbackLineColor(bool dark)
        => dark ? new Rgb(20, 20, 20) : new Rgb(144, 144, 144);

    /// <summary>
    /// The two draws behind the lift and the third layer's alpha: FNV-1a feeding an LCG, drawn in
    /// the same order as the prototype so an id means the same numbers in both products.
    /// </summary>
    private static (double Lift, double Jitter) DrawsFor(string id)
    {
        var state = 2166136261u;

        foreach (var character in id)
        {
            state ^= character;
            state *= 16777619u;
        }

        double Next()
        {
            // uint arithmetic already wraps at 2^32, which is what the prototype's ">>> 0" does.
            state = (state * 1103515245u) + 12345u;

            return state / 4294967296.0;
        }

        return (0.5 + Next(), Next());
    }
}

/// <summary>
/// A note's text size and the two numbers that have to move with it.
/// </summary>
/// <param name="Size">Font size in world units.</param>
/// <param name="Leading">Line height in world units — an integer, because a fractional one jitters
/// at fractional zoom levels.</param>
/// <param name="Padding">Inset from the card edge to the text.</param>
public readonly record struct NoteType(double Size, double Leading, double Padding)
{
    public static readonly NoteType Fourteen = new(14, 19, 16);

    public static readonly NoteType Fifteen = new(15, 21, 17);

    public static readonly NoteType Sixteen = new(16, 22, 18);

    /// <summary>
    /// The chip's font size in <b>screen</b> pixels, measured off the prototype's specimen sheet
    /// (all four variants: <c>font-size: 12px</c>). A chip is chrome, not paper type: it does not
    /// shrink when the board zooms out, because a mark nobody can read is the same as no mark.
    /// </summary>
    public const double ChipFontPx = 12;

    /// <summary>The chip's own height (3 + 16 + 3), and therefore the band it reserves on the card.</summary>
    public const double ChipBandPx = 22;

    /// <summary>
    /// The band the chip row takes, at this zoom. The fit-measure and the paint both read it, so a
    /// card can never be sized without the row it draws — the old defect class.
    /// </summary>
    public static double ChipBand(double zoom) => ChipBandPx * Math.Max(0.05, zoom);

    public static double ChipFont(double zoom) => ChipFontPx * Math.Max(0.05, zoom);

    /// <summary>
    /// 牌子的形状（2026-10-12 他打回：「一个方框看起来很廉价」，并要我先调研）。
    /// 调研回来的四条一手事实，每一条都对着我们当时的那一发：
    /// ① **没有一家给标记用直角**——Material 3 chip 的 `container_shape` 是 cornerSmall（8dp）、
    /// Fluent 2 Tag 是 `borderRadiusMedium`（4px）、Carbon Tag 是 `border-radius: 16px`、
    /// Ant Design Tag 是 `borderRadiusSM`（4px）、GitHub 的 `.Label` 是 `border-radius: 2em`。
    /// 我们是 `CornerRadius(0)`。
    /// ② **日期与标签在正经产品里是两种东西**：Trello 的到期是一枚整块换色的状态徽牌、标签是色块；
    /// GitLab / Microsoft Planner / Asana 的到期是"文字自己变红"，根本没有容器；
    /// Todoist 把颜色挂在标签名上而不是盒子上。我们把两者装进同一种方框。
    /// ③ **描边那一格是留白不画**：Fluent 的默认边是 `colorTransparentStroke`（槽位在、色透明）、
    /// Carbon 每条非可选的 tag 都写死 `border: 0`、Ant 的文档直接建议弱层级时用 `bordered={false}`。
    /// 我们当时是 1px、墨 34% 的实线框——那正是"廉价"的形状。
    /// ④ 状态用**点 + 文字**表达是成文模式（Ant Design 的 Badge status 就是 6px 点 + 文案）。
    /// ⇒ 落成：日期＝点＋字，无容器；标签＝胶囊（半径＝半高），底色一档、无描边；
    /// 两档都保留同一带高、同一字号、同一字重，所以它们看着是"一套里的两种"，不是两套。
    /// 底色压在 8%：调研里最实在的一条风险是"便签自己是淡彩，再叠一层色调会撞"，各家把标记底色
    /// 都留在 18% 以下。
    /// </summary>
    public const double ChipDotPx = 6;

    public const double ChipPadX = 7;
    public const double ChipGapPx = 5;
    public const double ChipFillAlpha = 0.08;
    public const double ChipHoverAlpha = 0.16;
    public const double ChipInkAlpha = 0.82;

    /// <summary>× 那枚圆钮的直径（也就是它的命中区）。它**不参与牌宽**：牌子宽度＝字的自然宽，
    /// 悬停时这枚钮扣在牌尾上（他 10-26 的第二条：占位让每枚都比字宽，一排全是空档）。
    /// 11-02：先改成圆钮、再按他的眼睛收到 14（16 那档偏大，牌尾被吞掉一截）。</summary>
    public const double ChipCrossHitPx = 14;

    /// <summary>胶囊：半径＝带高的一半（Primer 的 2em、Carbon 的 16px 都是这个意思）。</summary>
    public static double ChipRadiusPx => ChipBandPx / 2;

    /// <summary>
    /// The shortest a note this size can be: two insets and one line, with a 64 floor so an empty
    /// note is still a card and not a stripe.
    /// </summary>
    public double MinimumHeight => Math.Max(64, Math.Ceiling((Padding * 2) + Leading));
}

/// <summary>Whether a note's height follows its text or stays where it was left.</summary>
public enum NoteSizeMode
{
    /// <summary>Editing a note measures its content and sets the height. This is the shipped
    /// default: the prototype measured six demo cards holding text in 39.7%–50.1% of their own
    /// height, which is a small block of words floating in the middle of a coloured square.</summary>
    Automatic,

    /// <summary>Editing changes the text only; the frame is sized by the resize handles alone.</summary>
    Fixed,
}

/// <summary>The sizes a note can be set in, and which one ships as the default.</summary>
public static class NoteTypography
{
    /// <summary>15 — he judged 14 too small and 16 too big on Chinese glyphs; the old default of 18
    /// was larger still, and the prototype measured why (a Han glyph fills nearly its whole em box,
    /// so a pixel size copied from a Latin-first product reads as oversized).</summary>
    public const double DefaultSize = 15;

    /// <summary>The triples keyed by their point size, so a stored size finds its own leading and
    /// padding instead of letting the three numbers drift apart.</summary>
    public static NoteType For(double size) => size switch
    {
        14 => NoteType.Fourteen,
        16 => NoteType.Sixteen,
        _ => NoteType.Fifteen,
    };
}
