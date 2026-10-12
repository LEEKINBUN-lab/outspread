namespace Outspread.Core;

/// <summary>
/// The dot grid: which levels are drawn, how bright each is, and where the pattern sits.
/// </summary>
/// <remarks>
/// Ported from the prototype's <c>drawGrid</c>/<c>gridAlpha</c> line for line. Two things there are
/// the load-bearing decisions, and both came from being wrong once: the visibility test runs on
/// <b>screen</b> spacing (not on the zoom level — writing it against zoom left only the 4px step on
/// screen at 100% and the board looked like grey fog), and the whole bitmap is redrawn only when the
/// camera or the viewport actually changed (a per-frame repaint of the viewport is what made the
/// selection chrome flicker).
/// </remarks>
public static class BoardGrid
{
    /// <summary>World-space spacings, tldraw's published steps.</summary>
    public static readonly double[] Steps = [64, 16, 4, 1];

    /// <summary>Dot radius in screen pixels.</summary>
    public const double DotRadiusPx = 1;

    /// <summary>The brightest a dot ever gets — the prototype's peak, not 1.</summary>
    public const double PeakAlpha = 0.85;

    /// <summary>A level below this alpha is skipped entirely (the prototype's cut-off).</summary>
    public const double SkipBelowAlpha = 0.02;

    public const double FullBrightFromPx = 24;
    public const double FullBrightToPx = 72;
    public const double FadedOutBelowPx = 12;
    public const double FadedOutAbovePx = 96;

    /// <summary>0–1 for one level, from its spacing in screen pixels.</summary>
    public static double Alpha(double step, double zoom)
    {
        var spacing = step * zoom;

        if (spacing is >= FullBrightFromPx and <= FullBrightToPx)
        {
            return 1;
        }

        return spacing < FullBrightFromPx
            ? Math.Max(0, (spacing - FadedOutBelowPx) / (FullBrightFromPx - FadedOutBelowPx))
            : Math.Max(0, (FadedOutAbovePx - spacing) / (FadedOutAbovePx - FullBrightToPx));
    }

    /// <summary>The levels to paint, brightest first, already carrying their alpha.</summary>
    public static IReadOnlyList<(double Step, double Alpha)> LevelsFor(double zoom)
    {
        var levels = new List<(double, double)>(Steps.Length);

        foreach (var step in Steps)
        {
            var alpha = Alpha(step, zoom);

            if (alpha > SkipBelowAlpha)
            {
                levels.Add((step, alpha));
            }
        }

        return levels;
    }

    /// <summary>
    /// The pattern's period on screen: the widest visible spacing. Every other step divides it
    /// (64/16/4/1), so one bitmap of this size repeats without a seam.
    /// </summary>
    public static double PeriodFor(double zoom)
    {
        var levels = LevelsFor(zoom);

        return levels.Count == 0 ? 0 : levels[0].Step * zoom;
    }

    /// <summary>
    /// Where the pattern starts on screen for a given camera pan. Panning moves the bitmap by this
    /// phase instead of repainting it, which is what keeps a drag at frame rate.
    /// </summary>
    public static double Phase(double panPx, double periodPx)
    {
        if (periodPx <= 0)
        {
            return 0;
        }

        return ((panPx % periodPx) + periodPx) % periodPx;
    }

    /// <summary>
    /// The board darkening: centre bright, edges a little darker, so the colour blocks have
    /// something to sit on. Values are the prototype&apos;s own radial gradient —
    /// <c>radial-gradient(120% 90% at 50% 42%, transparent 45%, rgba(0,0,0,.055))</c>.
    /// </summary>
    /// <remarks>
    /// The two radii are <b>radii</b>: CSS resolves <c>120%</c> against the box&apos;s width, so the
    /// ellipse spans 2.4 board widths. An earlier port read those numbers as diameters and wrote
    /// 0.6/0.45, which put the corner at the full .055 where the prototype is only .0175 — a
    /// vignette three times too strong, measured on the two PNGs.
    /// </remarks>
    public const double VignetteCenterX = 0.5;
    public const double VignetteCenterY = 0.42;
    public const double VignetteRadiusX = 1.2;
    public const double VignetteRadiusY = 0.9;
    public const double VignetteClearUntil = 0.45;
    public const double VignetteAlphaLight = 0.055;
    public const double VignetteAlphaDark = 0.35;

    /// <summary>How dark a given board-relative point gets, 0–1 of the vignette alpha.</summary>
    public static double VignetteAt(double x, double y, bool dark)
    {
        var dx = (x - VignetteCenterX) / VignetteRadiusX;
        var dy = (y - VignetteCenterY) / VignetteRadiusY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var ramp = Math.Clamp((distance - VignetteClearUntil) / (1 - VignetteClearUntil), 0, 1);

        return ramp * VignetteAlpha(dark);
    }

    public static double VignetteAlpha(bool dark) => dark ? VignetteAlphaDark : VignetteAlphaLight;

    /// <summary>Dot colour: the prototype's <c>hsl(0 0% 43%)</c> light and <c>40%</c> dark.</summary>
    public static Rgb Ink(bool dark) => dark ? new Rgb(102, 102, 102) : new Rgb(110, 110, 110);

    /// <summary>
    /// The board's own fill — the prototype&apos;s <c>--bg</c>: <c>hsl(210 20% 98%)</c> light,
    /// <c>hsl(240 5% 6.5%)</c> dark.
    /// </summary>
    /// <remarks>
    /// Not the WinUI theme brush. <c>SolidBackgroundFillColorBase</c> measures 243 where the
    /// prototype measures 250, and the dark theme&apos;s is #272727 (39) against a prototype 16 —
    /// i.e. the whole board would sit two steps too grey, and in dark the notes would float on
    /// a lighter surface than the design intends.
    /// </remarks>
    public static Rgb BoardBackground(bool dark) => dark ? new Rgb(16, 16, 17) : new Rgb(249, 250, 251);

    /// <summary>
    /// Stamp one dot into a premultiplied BGRA buffer, source-over.
    /// </summary>
    /// <remarks>
    /// The <c>(1 - srcAlpha)</c> in the destination term is the whole point of this method: the
    /// first port wrote <c>(1 - dstAlpha)</c> there, which is only equal when the pixel is empty.
    /// Since every 64-spacing dot sits on a 16-spacing one, the fainter pass then <b>lowered</b>
    /// the bright dot&apos;s alpha from 0.85 to 0.41 — measured, not theorised — and the grid lost
    /// its bright level. The coverage ramp is linear across the rim rather than a true circle
    /// integral: at a 1px radius that is one antialiased ring, the same size the prototype draws.
    /// </remarks>
    public static void StampDot(
        byte[] bgra,
        int width,
        int height,
        double centreX,
        double centreY,
        double radius,
        double alpha,
        Rgb ink)
    {
        var reach = (int)Math.Ceiling(radius) + 1;

        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var x = ((int)Math.Round(centreX)) + dx;
                var y = ((int)Math.Round(centreY)) + dy;

                if (x < 0 || y < 0 || x >= width || y >= height)
                {
                    continue;
                }

                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                var coverage = Math.Clamp(0.5 + (radius - distance), 0, 1);

                if (coverage <= 0)
                {
                    continue;
                }

                var source = alpha * coverage;
                var oneMinusSource = 1 - source;
                var index = (x + (y * width)) * 4;

                bgra[index] = (byte)Math.Round((ink.B * source) + (bgra[index] * oneMinusSource));
                bgra[index + 1] = (byte)Math.Round((ink.G * source) + (bgra[index + 1] * oneMinusSource));
                bgra[index + 2] = (byte)Math.Round((ink.R * source) + (bgra[index + 2] * oneMinusSource));
                bgra[index + 3] = (byte)Math.Round((source * 255) + (bgra[index + 3] * oneMinusSource));
            }
        }
    }
}
