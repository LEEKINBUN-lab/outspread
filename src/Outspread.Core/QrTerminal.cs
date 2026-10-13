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
    /// The row-of-bits shape an encoder hands over, so the conversion to a grid is here under a
    /// test rather than inside the command line that draws it.
    /// </summary>
    public static string Render(IList<BitArray> rows, int quietZone = QuietZoneModules)
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

        return Render(matrix, quietZone);
    }

    public static string Render(bool[,] matrix, int quietZone = QuietZoneModules)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        var size = matrix.GetLength(0);
        var columns = matrix.GetLength(1);

        if (matrix.GetLength(1) != size)
        {
            throw new ArgumentException($"a QR grid must be square; this one is {size}×{columns}", nameof(matrix));
        }

        var side = size + (2 * quietZone);

        var builder = new StringBuilder(side * ((side / 2) + 1));

        for (var row = 0; row < side; row += 2)
        {
            for (var column = 0; column < side; column++)
            {
                var top = IsDark(matrix, quietZone, size, row, column);
                var bottom = row + 1 < side && IsDark(matrix, quietZone, size, row + 1, column);

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

    private static bool IsDark(bool[,] matrix, int quietZone, int size, int row, int column)
    {
        var innerRow = row - quietZone;
        var innerColumn = column - quietZone;

        return innerRow >= 0 && innerColumn >= 0
            && innerRow < size && innerColumn < size
            && matrix[innerRow, innerColumn];
    }
}
