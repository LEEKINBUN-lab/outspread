using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// Draws a QR grid as rectangles, for the pairing sheet.
/// </summary>
/// <remarks>
/// Shapes rather than a bitmap: a QR code is axis-aligned squares, so a vector drawing is
/// sharp at any window scale while a bitmap has to guess a pixel size and then blurs when the
/// user's display is 150%. It also keeps this window free of any imaging dependency.
///
/// Dark modules are merged into horizontal runs, so a 73×73 grid becomes a few hundred
/// rectangles instead of thousands of 1-pixel ones.
///
/// The quiet zone comes from <see cref="QrTerminal.Padded"/>, the same function the terminal
/// drawing uses: a code that a phone can read off one screen and cannot read off the other is
/// not a difference in encoders, it is a difference in white border.
/// </remarks>
internal static class QrImage
{
    /// <summary>Pixels per module at the sheet's default size; the picture is about 260px square.</summary>
    public const double ModulePixels = 3.5;

    public static FrameworkElement Build(IList<BitArray> rows)
    {
        var grid = QrTerminal.Padded(QrTerminal.ToGrid(rows));
        var side = grid.GetLength(0);

        var canvas = new Canvas
        {
            Width = side * ModulePixels,
            Height = side * ModulePixels,
        };

        // Light field, dark modules, and neither follows the theme. A camera reads contrast,
        // not colours: an inverted code on a dark sheet scans worse than a white card that
        // looks slightly out of place, and the sheet is only ever on screen to be photographed.
        canvas.Children.Add(new Rectangle
        {
            Width = canvas.Width,
            Height = canvas.Height,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
        });

        var ink = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 16, 20));

        for (var row = 0; row < side; row++)
        {
            var column = 0;

            while (column < side)
            {
                if (!grid[row, column])
                {
                    column++;

                    continue;
                }

                var start = column;

                while (column < side && grid[row, column])
                {
                    column++;
                }

                var run = new Rectangle
                {
                    Width = (column - start) * ModulePixels,
                    Height = ModulePixels,
                    Fill = ink,
                };

                // Attached properties, so they are set through Canvas: a Rectangle has no
                // CanvasLeft of its own.
                Canvas.SetLeft(run, start * ModulePixels);
                Canvas.SetTop(run, row * ModulePixels);

                canvas.Children.Add(run);
            }
        }

        return canvas;
    }
}
