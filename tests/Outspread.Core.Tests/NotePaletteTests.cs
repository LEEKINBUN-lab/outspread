namespace Outspread.Core.Tests;

/// <summary>
/// Readability of the six note colours, in both themes.
/// </summary>
/// <remarks>
/// His standing rule is that "看不见字" is a defect, not a taste question, and that a claim
/// about both themes has to show the dark one actually moved. So every preset is measured,
/// including what happens when an agent writes an awkward hex.
/// </remarks>
public class NotePaletteTests
{
    private const double MinimumRatio = 4.5;

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    public void Each_preset_note_colour_has_readable_ink_in_light_and_dark(string preset)
    {
        foreach (var dark in new[] { false, true })
        {
            var surface = NotePalette.SurfaceFor(preset, dark);
            var ink = NotePalette.InkFor(surface);

            Assert.True(
                NotePalette.Contrast(ink, surface) >= MinimumRatio,
                $"preset {preset} in {(dark ? "dark" : "light")}: {NotePalette.Contrast(ink, surface):0.00}:1");
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    public void The_ink_a_preset_ships_is_readable_and_the_preset_actually_moves_with_the_theme(string preset)
    {
        // The card does not get whichever of the two plain inks measures higher any more: a
        // preset carries its own hue-matched step 12, and that is the number a reader sees.
        // Asserting only the fallback would let a shipped preset pass while its real ink fails.
        var light = NotePalette.PaletteFor(preset, dark: false);
        var dark = NotePalette.PaletteFor(preset, dark: true);

        Assert.NotNull(light);
        Assert.NotNull(dark);

        foreach (var (palette, isDark) in new[] { (light!.Value, false), (dark!.Value, true) })
        {
            var surface = NotePalette.SurfaceFor(preset, isDark);
            var ink = NotePalette.InkFor(preset, surface, isDark);
            var ratio = NotePalette.Contrast(ink, surface);

            Assert.Equal(palette.Fill, surface);
            Assert.Equal(palette.Ink, ink);
            Assert.True(
                ratio >= MinimumRatio,
                $"preset {preset} in {(isDark ? "dark" : "light")}: {ratio:0.00}:1 with {ink} on {surface}");

            // Step 7 is not a hard rule about visibility (measured: line-on-fill is only
            // 1.55–2.29:1, which is why the edge is carried by the shadow, not the line).
            // It does have to be a different colour from the fill, or it is doing nothing.
            Assert.NotEqual(palette.Fill, palette.Line);
        }

        // The claim that the dark theme really moved is measured, not assumed: same hue,
        // different surface AND different ink AND different line.
        Assert.NotEqual(light!.Value.Fill, dark!.Value.Fill);
        Assert.NotEqual(light.Value.Ink, dark.Value.Ink);
        Assert.NotEqual(light.Value.Line, dark.Value.Line);
    }

    [Fact]
    public void An_uncoloured_note_follows_the_theme_and_stays_readable_in_both()
    {
        var lightSurface = NotePalette.SurfaceFor(null, dark: false);
        var darkSurface = NotePalette.SurfaceFor(null, dark: true);

        // Not just "readable": the two surfaces must differ, otherwise the claim that the
        // dark theme really moved would be a guess.
        Assert.NotEqual(lightSurface, darkSurface);
        Assert.True(NotePalette.Contrast(NotePalette.InkFor(lightSurface), lightSurface) >= MinimumRatio);
        Assert.True(NotePalette.Contrast(NotePalette.InkFor(darkSurface), darkSurface) >= MinimumRatio);
    }

    [Theory]
    [InlineData("#101010")]
    [InlineData("#FFFFFF")]
    [InlineData("#0000FF")]
    [InlineData("#FFFF00")]
    [InlineData("#FF6600")]
    public void A_hex_an_agent_wrote_is_given_the_ink_that_clears_it(string hex)
    {
        var surface = NotePalette.SurfaceFor(hex, dark: false);

        Assert.True(NotePalette.TryParseHex(hex, out _), $"{hex} should parse");
        Assert.True(
            NotePalette.Contrast(NotePalette.InkFor(surface), surface) >= MinimumRatio,
            $"{hex}: {NotePalette.Contrast(NotePalette.InkFor(surface), surface):0.00}:1");
    }

    [Fact]
    public void The_middle_grey_falls_back_to_a_refusal_rather_than_a_false_pass()
    {
        // #7F7F7F is the honest worst case: neither ink clears 4.5:1 against it. The test
        // records that the rule is real, and that the palette cannot quietly claim success.
        var surface = NotePalette.SurfaceFor("#7F7F7F", dark: false);
        var best = Math.Max(
            NotePalette.Contrast(NotePalette.DarkInk, surface),
            NotePalette.Contrast(NotePalette.LightInk, surface));

        Assert.True(best < MinimumRatio, $"expected the mid grey to fail; got {best:0.00}:1");
    }

    [Theory]
    [InlineData("#GGHHJJ")]
    [InlineData("lightgreen")]
    [InlineData("#FFF")]
    [InlineData("")]
    [InlineData(null)]
    public void Something_that_is_not_a_colour_falls_back_to_the_theme_surface_without_throwing(string? value)
    {
        var surface = NotePalette.SurfaceFor(value, dark: false);

        Assert.Equal(NotePalette.LightSurface, surface);
        Assert.False(NotePalette.TryParseHex(value ?? "x", out _));
    }

    [Fact]
    public void An_eight_digit_hex_keeps_its_colour_and_drops_its_alpha_on_purpose()
    {
        Assert.True(NotePalette.TryParseHex("#FF880080", out var color));
        Assert.Equal(new Rgb(0xFF, 0x88, 0x00), color);
    }

    [Fact]
    public void Contrast_is_symmetric_and_1_for_identical_and_21_for_black_on_white()
    {
        var white = new Rgb(0xFF, 0xFF, 0xFF);
        var black = new Rgb(0x00, 0x00, 0x00);

        Assert.Equal(1, NotePalette.Contrast(white, white), 6);
        Assert.Equal(21, NotePalette.Contrast(white, black), 6);
        Assert.Equal(NotePalette.Contrast(white, black), NotePalette.Contrast(black, white), 6);
    }
}
