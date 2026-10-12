namespace Outspread.Core.Tests;

public class PastedImageTests
{
    [Fact]
    public void A_bitmap_whose_alpha_is_all_zero_becomes_opaque_instead_of_invisible()
    {
        // Two pixels of a GDI screenshot: colour filled in, alpha never touched.
        byte[] bgra = [10, 20, 30, 0, 40, 50, 60, 0];

        Assert.True(PastedImage.RepairBlankAlpha(bgra));

        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, bgra);
    }

    [Fact]
    public void An_image_with_any_transparency_left_is_untouched()
    {
        byte[] bgra = [10, 20, 30, 0, 40, 50, 60, 128];
        var before = (byte[])bgra.Clone();

        Assert.False(PastedImage.RepairBlankAlpha(bgra));
        Assert.Equal(before, bgra);

        // Fully opaque already: nothing to repair, and nothing may be rewritten.
        byte[] solid = [1, 2, 3, 255];
        Assert.False(PastedImage.RepairBlankAlpha(solid));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 1, 2, 3, 0, 1 })]
    public void Something_that_is_not_a_pixel_buffer_is_left_alone(byte[] bytes)
    {
        Assert.False(PastedImage.RepairBlankAlpha(bytes));
    }

    [Fact]
    public void Padded_scanlines_are_repacked_tight_instead_of_shearing_the_picture()
    {
        // Two rows of three useful bytes each, stored four bytes wide.
        byte[] padded = [1, 2, 3, 0, 4, 5, 6, 0];

        Assert.Equal(
            new byte[] { 1, 2, 3, 4, 5, 6 },
            PastedImage.RepackRows(padded, stride: 4, rowBytes: 3, rows: 2));

        // Already tight: the bytes come out in the same order, just without the padding.
        Assert.Equal(
            new byte[] { 1, 2, 3, 0, 4, 5 },
            PastedImage.RepackRows(padded, stride: 3, rowBytes: 3, rows: 2));

        // A source shorter than the row count keeps the rows that exist instead of
        // reading past the end of the array.
        var truncated = PastedImage.RepackRows(padded, stride: 4, rowBytes: 3, rows: 4);

        Assert.Equal(12, truncated.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, truncated[..6]);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0 }, truncated[6..]);

        Assert.Throws<ArgumentOutOfRangeException>(() => PastedImage.RepackRows(padded, stride: 2, rowBytes: 3, rows: 2));
    }

    [Theory]
    [InlineData(8, 3, 2, 4)] // two padded rows of a three-byte picture
    [InlineData(6, 3, 2, 3)] // already tight
    [InlineData(7, 3, 2, 3)] // does not divide into whole rows: assume tight
    [InlineData(4, 3, 2, 3)] // shorter than the image needs: assume tight
    [InlineData(8, 5, 2, 5)] // a row narrower than the tight width is not a stride
    [InlineData(0, 4, 0, 4)]
    public void The_row_length_is_measured_from_the_bytes_not_guessed(
        long length, int bytesPerRow, int rows, int expected)
    {
        Assert.Equal(expected, PastedImage.StrideFromLength(length, bytesPerRow, rows));
    }

    [Fact]
    public void Pasted_text_keeps_one_newline_form_and_reports_what_it_had_to_cut()
    {
        var (text, dropped) = PastedImage.TrimPastedText("a\r\nb\u2028c");

        Assert.Equal("a\nb\nc", text);
        Assert.Equal(0, dropped);

        var longText = new string('字', PastedImage.MaxTextCharacters + 500);
        var (kept, cut) = PastedImage.TrimPastedText(longText);

        Assert.Equal(PastedImage.MaxTextCharacters, kept.Length);
        Assert.Equal(500, cut);

        Assert.Equal(string.Empty, PastedImage.TrimPastedText(null).Text);
    }
}
