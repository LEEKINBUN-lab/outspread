namespace Outspread.Core;

/// <summary>A colour as three bytes, so the arithmetic can be tested without a window.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public double Luminance()
    {
        static double Channel(int value)
        {
            var srgb = value / 255.0;

            return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));
    }
}

/// <summary>
/// One note's three colours: the surface, the line on its edge, and the ink on that surface.
/// </summary>
/// <remarks>
/// Three, not one, because a pale square with no line and no shadow reads as a hole in the
/// board rather than as an object. The pairing rule (a big area takes the low steps, text takes
/// the high ones) is the published Radix scale rule, not an invention of ours — see the note on
/// <see cref="Presets"/>.
/// </remarks>
public readonly record struct NoteColor(Rgb Fill, Rgb Line, Rgb Ink);

/// <summary>
/// The colours a note may carry, and the ink that stays readable on each.
/// </summary>
/// <remarks>
/// A note's colour is user content, so it is a fixed hue that survives both themes —
/// "that yellow one" is how a note is recognised. What does NOT survive is the value: the same
/// hue needs a dark surface in the dark theme, so a preset is a pair of triplets and the theme
/// picks one. Ink cannot be fixed either way: an agent may write <c>#101010</c>, and dark ink on
/// it would be unreadable. So a preset carries its own measured ink, and anything else gets
/// whichever of the two inks measures higher.
///
/// The values are the steps Radix Colors publishes (MIT; light.ts / dark.ts, 12 steps per hue)
/// and the mapping is their documented rule: steps 1–3 are the large-surface backgrounds, 6–8 are
/// borders, 11–12 are text. Step 9 is deliberately NOT used as a surface — it is the most
/// saturated step in the scale (their words: "the step mixed with the least amount of white or
/// black"), which is exactly the sticker-on-a-white-page look the old flat pastels had.
/// </remarks>
public static class NotePalette
{
    /// <summary>The six colours JSON Canvas 1.0 allows as "1" to "6": red, orange, yellow, green, cyan, purple.</summary>
    public static readonly NoteColor[] LightPresets =
    [
        new(new(0xfe, 0xeb, 0xec), new(0xf4, 0xa9, 0xaa), new(0x64, 0x17, 0x23)),
        new(new(0xff, 0xef, 0xd6), new(0xf5, 0xae, 0x73), new(0x58, 0x2d, 0x1d)),
        new(new(0xff, 0xfa, 0xb8), new(0xe4, 0xc7, 0x67), new(0x47, 0x3b, 0x1f)),
        new(new(0xe6, 0xf6, 0xeb), new(0x8e, 0xce, 0xaa), new(0x19, 0x3b, 0x2d)),
        new(new(0xde, 0xf7, 0xf9), new(0x7d, 0xce, 0xdc), new(0x0d, 0x3c, 0x48)),
        new(new(0xf7, 0xed, 0xfe), new(0xd1, 0xaf, 0xec), new(0x40, 0x20, 0x60)),
    ];

    /// <summary>The same six hues at the same steps, taken from Radix's dark scale.</summary>
    public static readonly NoteColor[] DarkPresets =
    [
        new(new(0x3b, 0x12, 0x19), new(0x8c, 0x33, 0x3a), new(0xff, 0xd1, 0xd9)),
        new(new(0x33, 0x1e, 0x0b), new(0x7e, 0x45, 0x1d), new(0xff, 0xe0, 0xc2)),
        new(new(0x2d, 0x23, 0x05), new(0x66, 0x54, 0x17), new(0xf6, 0xee, 0xb4)),
        new(new(0x13, 0x2d, 0x21), new(0x28, 0x68, 0x4a), new(0xb1, 0xf1, 0xcb)),
        new(new(0x08, 0x2c, 0x36), new(0x12, 0x67, 0x7e), new(0xb6, 0xec, 0xf7)),
        new(new(0x30, 0x1c, 0x3b), new(0x66, 0x42, 0x82), new(0xec, 0xd9, 0xfa)),
    ];

    /// <summary>The preset's three colours for this theme, or null for a raw hex or no colour.</summary>
    public static NoteColor? PaletteFor(string? color, bool dark)
    {
        if (color is { Length: 1 } && color[0] is >= '1' and <= '6')
        {
            return (dark ? DarkPresets : LightPresets)[color[0] - '1'];
        }

        return null;
    }

    /// <summary>#1E1E1E — the charcoal the prototype ships as its body ink (and Figma's default
    /// text colour), so an uncoloured note reads the same in both products.</summary>
    public static readonly Rgb DarkInk = new(0x1E, 0x1E, 0x1E);

    public static readonly Rgb LightInk = new(0xF5, 0xF6, 0xF7);

    public static Rgb LightSurface = new(0xFF, 0xFF, 0xFF);

    public static Rgb DarkSurface = new(0x2B, 0x2D, 0x31);

    public static Rgb SurfaceFor(string? color, bool dark)
        => PaletteFor(color, dark) is { } preset
            ? preset.Fill
            : color is { Length: > 0 } && TryParseHex(color, out var parsed)
                ? parsed
                : (dark ? DarkSurface : LightSurface);

    /// <summary>
    /// The ink this note should be written in: the preset's own hue-matched step 12 when the
    /// colour is a preset, otherwise whichever of the two inks measures higher.
    /// </summary>
    public static Rgb InkFor(string? color, Rgb surface, bool dark)
        => PaletteFor(color, dark) is { } preset ? preset.Ink : InkFor(surface);

    /// <summary>Whichever ink gives the higher contrast ratio against this surface.</summary>
    public static Rgb InkFor(Rgb surface)
        => Contrast(DarkInk, surface) >= Contrast(LightInk, surface) ? DarkInk : LightInk;

    public static double Contrast(Rgb a, Rgb b)
    {
        var lighter = Math.Max(a.Luminance(), b.Luminance());
        var darker = Math.Min(a.Luminance(), b.Luminance());

        return (lighter + 0.05) / (darker + 0.05);
    }

    public static bool TryParseHex(string value, out Rgb color)
    {
        color = default;

        if (value.Length is not (7 or 9) || value[0] != '#')
        {
            return false;
        }

        try
        {
            if (value.Length == 9)
            {
                // The spec shows #RRGGBB; eight digits are read as #RRGGBBAA, which is what
                // several canvas tools write. The alpha is dropped on purpose: a partly
                // transparent note would show whatever is behind it, and the ink below is
                // chosen against this surface, not against the board.
                color = new Rgb(ParseByte(value, 1), ParseByte(value, 3), ParseByte(value, 5));
            }
            else
            {
                color = new Rgb(ParseByte(value, 1), ParseByte(value, 3), ParseByte(value, 5));
            }

            return true;
        }
        catch (FormatException)
        {
            color = default;
            return false;
        }
    }

    private static byte ParseByte(string value, int offset) => Convert.ToByte(value.Substring(offset, 2), 16);
}
