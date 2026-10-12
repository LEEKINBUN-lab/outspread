using Microsoft.UI.Xaml.Media;
using Outspread.Core;
using Windows.UI;

namespace Outspread.Board;

/// <summary>
/// Turns the palette arithmetic in <see cref="NotePalette"/> into WinUI colours.
/// </summary>
/// <remarks>
/// Nothing here decides anything: the contrast choice lives in Core so it can be tested
/// without a window open.
/// </remarks>
internal static class NoteStyling
{
    /// <summary>
    /// The font stack a note is set in. Segoe UI carries no Han glyphs at all, so without naming
    /// 微软雅黑 every Chinese character is resolved by per-glyph fallback. YaHei only ships
    /// 300/400/700, which is why no weight between 400 and 500 is asked for anywhere: the matching
    /// algorithm rounds down, so a requested 500 silently becomes 400.
    /// </summary>
    public static readonly FontFamily NoteFont = new("Segoe UI, Microsoft YaHei UI");

    /// <summary>Set when the window's theme is known; an uncoloured note follows it.</summary>
    public static bool IsDark { get; set; }

    public static Color SurfaceFor(string? color) => ToColor(NotePalette.SurfaceFor(color, IsDark));

    /// <summary>
    /// The 1px edge. A preset carries its own step 7; a colour an agent wrote as a raw hex gets the
    /// ink at 30% instead, because inventing a border hue for an unknown surface would put a second
    /// colour on the card that nobody chose.
    /// </summary>
    public static Color LineFor(string? color, Color surface)
        => NotePalette.PaletteFor(color, IsDark) is { } palette
            ? ToColor(palette.Line)
            : WithAlpha(InkFor(surface), 0x4D);

    /// <summary>The ink a preset ships, or for a raw hex whichever plain ink measures higher.</summary>
    public static Color InkFor(string? color, Color surface)
        => ToColor(NotePalette.InkFor(color, new Rgb(surface.R, surface.G, surface.B), IsDark));

    public static Color InkFor(Color surface)
        => ToColor(NotePalette.InkFor(new Rgb(surface.R, surface.G, surface.B)));

    /// <summary>Grey of the single bottom line drawn when zoom drops below the shadow budget.</summary>
    public static Color FallbackLine() => ToColor(NoteSurface.FallbackLineColor(IsDark));

    public static Color ToColor(Rgb rgb) => Color.FromArgb(255, rgb.R, rgb.G, rgb.B);

    /// <summary>A colour the prototype writes as <c>rgb(x y z / a)</c>.</summary>
    public static Color WithAlpha(Rgb rgb, double alpha)
        => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), rgb.R, rgb.G, rgb.B);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
