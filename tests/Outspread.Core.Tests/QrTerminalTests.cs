namespace Outspread.Core.Tests;

/// <summary>
/// The mapping from modules to glyphs is what a camera ends up reading off a terminal, so it
/// gets the same treatment as the merge rules: every combination named.
/// </summary>
public class QrTerminalTests
{
    private static bool[,] Grid(int size, params (int Row, int Column)[] dark)
    {
        var matrix = new bool[size, size];

        foreach (var (row, column) in dark)
        {
            matrix[row, column] = true;
        }

        return matrix;
    }

    private static string[] Lines(string rendered)
        => rendered.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Both_rows_dark_is_a_full_block()
    {
        var rendered = QrTerminal.Render(Grid(2, (0, 0), (0, 1), (1, 0), (1, 1)), quietZone: 0);

        Assert.Equal("██", Lines(rendered)[0]);
        Assert.Single(Lines(rendered));
    }

    [Fact]
    public void Only_the_upper_row_dark_is_the_top_half_block()
    {
        var rendered = QrTerminal.Render(Grid(2, (0, 0), (0, 1)), quietZone: 0);

        Assert.Equal("▀▀", Lines(rendered)[0]);
    }

    [Fact]
    public void Only_the_lower_row_dark_is_the_bottom_half_block()
    {
        var rendered = QrTerminal.Render(Grid(2, (1, 0), (1, 1)), quietZone: 0);

        Assert.Equal("▄▄", Lines(rendered)[0]);
    }

    [Fact]
    public void A_checkerboard_pairs_within_the_cell_not_across_cells()
    {
        // (0,0) dark and (1,1) dark: each text cell sees one dark half, in opposite rows of the
        // same module column-pair. Getting this wrong flips the picture's contrast for a camera.
        var rendered = QrTerminal.Render(Grid(2, (0, 0), (1, 1)), quietZone: 0);

        Assert.Equal("▀▄", Lines(rendered)[0]);
    }

    [Fact]
    public void An_odd_number_of_module_rows_does_not_lose_the_last_one()
    {
        var rendered = QrTerminal.Render(Grid(3, (2, 0), (2, 1), (2, 2)), quietZone: 0);

        // Row 2 is the third module row, so it lands in the second text line — as its TOP half,
        // because rows pair from the start (0 with 1, 2 with 3). The bottom half of that line is
        // past the grid and reads as light, so the glyph is the upper-half block, not a full one.
        Assert.Equal(2, Lines(rendered).Length);
        Assert.Equal("   ", Lines(rendered)[0]);
        Assert.Equal("▀▀▀", Lines(rendered)[1]);
    }

    [Fact]
    public void The_quiet_zone_is_light_border_and_not_more_picture()
    {
        var rendered = QrTerminal.Render(Grid(1, (0, 0)), quietZone: 1);

        // One module with a one-module border is a 3×3 field: 29 columns wide is the code, the
        // dark module sits at grid position (1,1), which is the bottom half of the first line.
        Assert.Equal(" ▄ ", Lines(rendered)[0]);
        Assert.Equal("   ", Lines(rendered)[1]);
    }

    [Fact]
    public void Four_modules_of_border_is_the_default_a_reader_expects()
    {
        var rendered = QrTerminal.Render(Grid(21));

        Assert.Equal(29, Lines(rendered)[0].Length);
        Assert.DoesNotContain('█', rendered);
        Assert.DoesNotContain('▀', rendered);
        Assert.DoesNotContain('▄', rendered);
    }

    [Fact]
    public void A_non_square_grid_is_refused_rather_than_drawn_as_something_wrong()
    {
        Assert.Throws<ArgumentException>(() => QrTerminal.Render(new bool[2, 3]));
    }

    [Fact]
    public void An_encoders_row_of_bits_draws_the_same_picture_as_the_equivalent_grid()
    {
        // The command line hands over the encoder's own shape (a list of rows of bits), so the
        // conversion has to land on exactly the picture the grid path draws — otherwise the
        // scannable thing and the tested thing are two different drawings.
        var rows = new System.Collections.BitArray[]
        {
            new([true, false]),
            new([false, true]),
        };

        var fromRows = QrTerminal.Render(rows, quietZone: 0);
        var fromGrid = QrTerminal.Render(Grid(2, (0, 0), (1, 1)), quietZone: 0);

        Assert.Equal(fromGrid, fromRows);
        Assert.Equal("▀▄", Lines(fromRows)[0]);
    }
}
