using System.Collections;
using System.Text;

namespace Outspread.Core;

/// <summary>
/// Draws a QR module grid with block characters, for a terminal.
/// </summary>
/// <remarks>
/// Pure and dependency-free on purpose: this class turns a grid into text and knows nothing
/// about who produced the grid, so the mapping is testable here while the encoder itself stays
/// a third-party package in the command line project. <see cref="Outspread.Core"/> takes no
/// NuGet packages, and a renderer is the kind of thing worth having under a test.
///
/// Two module rows are folded into one text row because a terminal cell is roughly twice as
/// tall as it is wide: without that fold the picture is a tall rectangle a camera cannot read
/// comfortably. A scanner reads the rendered pixels, so the quiet zone matters — the trailing
/// blanks on each line are background either way, and the leading ones are what a reader uses
/// to find the edge.
/// </remarks>
public static class QrTerminal
{
    /// <summary>Modules of light border a QR code must have to be readable (ISO/IEC 18004 says four).</summary>
    public const int QuietZoneModules = 4;

    /// <summary>
    /// The grid with its light border already around it, square, side = size + 2·quietZone.
    /// Both drawers start here: the terminal folds this into block characters and the window
    /// paints it into pixels. Two implementations of "add four modules of white" is how one of
    /// them ends up scannable and the other not.
    /// </summary>
    public static bool[,] Padded(bool[,] matrix, int quietZone = QuietZoneModules)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        var size = matrix.GetLength(0);

        if (matrix.GetLength(1) != size)
        {
            throw new ArgumentException($"a QR grid must be square; this one is {size}×{matrix.GetLength(1)}", nameof(matrix));
        }

        var side = size + (2 * quietZone);
        var padded = new bool[side, side];

        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                padded[row + quietZone, column + quietZone] = matrix[row, column];
            }
        }

        return padded;
    }

    /// <summary>The row-of-bits shape an encoder hands over, so the conversion to a grid is here under a
    /// test rather than inside the command line that draws it.</summary>
    public static bool[,] ToGrid(IList<BitArray> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var size = rows.Count;
        var matrix = new bool[size, size];

        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                matrix[row, column] = rows[row][column];
            }
        }

        return matrix;
    }

    public static string Render(IList<BitArray> rows, int quietZone = QuietZoneModules)
        => Render(ToGrid(rows), quietZone);

    public static string Render(bool[,] matrix, int quietZone = QuietZoneModules)
    {
        var padded = Padded(matrix, quietZone);
        var side = padded.GetLength(0);

        var builder = new StringBuilder(side * ((side / 2) + 1));

        for (var row = 0; row < side; row += 2)
        {
            for (var column = 0; column < side; column++)
            {
                var top = padded[row, column];
                var bottom = row + 1 < side && padded[row + 1, column];

                builder.Append((top, bottom) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    (false, false) => ' ',
                });
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
