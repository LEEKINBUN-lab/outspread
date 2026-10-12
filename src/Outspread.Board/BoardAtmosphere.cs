using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Outspread.Core;
using Windows.Foundation;

namespace Outspread.Board;

/// <summary>
/// The two layers under the notes: the dot grid and the board's vignette.
/// </summary>
/// <remarks>
/// <para>
/// WinUI has no tile brush, so the grid is one bitmap — exactly the route the prototype's recipe
/// table says the port has to take. The bitmap is one period larger than the viewport and is
/// <b>moved</b> while panning instead of repainted: the pattern is periodic and every step divides
/// the widest visible one, so a translation is all a pan needs. Repainting happens when the zoom,
/// the viewport size, the theme or the rasterisation scale changes.
/// </para>
/// <para>
/// Both layers sit in the XAML <b>before</b> <c>ItemLayer</c>. That ordering is the fix for a real
/// bug: on the web the darkening was written as <c>#board::after</c>, and a pseudo-element is the
/// last child — so it lay over every note and he read it as "the board bottom is above the cards".
/// </para>
/// </remarks>
partial class MainWindow
{
    private string _gridKey = string.Empty;
    private Rect? _clipRect;
    private bool? _floorIsDark;
    private bool? _vignetteIsDark;

    private void UpdateAtmosphere()
    {
        var width = BoardArea.ActualWidth;
        var height = BoardArea.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = PixelScale();
        var period = BoardGrid.PeriodFor(_view.Zoom);

        // The board's own bounds, not the union of what's inside it: the grid tile is deliberately
        // a period larger than the viewport so panning needs no repaint, and without a clip that
        // overflow — and any node dragged past an edge — paints outside the surface, over the
        // toolbar. It also made the shot's frame disagree with the viewport by tens of pixels,
        // which is how a working grid got read back as "no dots".
        var clip = new Rect(0, 0, width, height);

        if (_clipRect is not Rect current || current.Width != clip.Width || current.Height != clip.Height)
        {
            _clipRect = clip;
            BoardArea.Clip = new RectangleGeometry { Rect = clip };
        }

        ApplyBoardFloor();
        ApplyVignette();

        if (period <= 0)
        {
            GridLayer.Source = null;
            GridLayer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            _gridKey = string.Empty;
            return;
        }

        GridLayer.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

        // Whole layout pixels, or the Image gets resampled and a 2px dot smears into a grey smear.
        var tileWidth = Math.Ceiling(width + period);
        var tileHeight = Math.Ceiling(height + period);
        var key = $"{_view.Zoom:0.####}|{width:0}|{height:0}|{scale:0.###}|{NoteStyling.IsDark}";

        if (key != _gridKey)
        {
            _gridKey = key;
            PaintGrid(tileWidth, tileHeight, scale);
        }

        // 平移只改这一处：点阵跟着相位走，位图不重画。
        GridOffset.X = BoardGrid.Phase(_view.PanX, period) - period;
        GridOffset.Y = BoardGrid.Phase(_view.PanY, period) - period;
    }

    private void ApplyBoardFloor()
    {
        var dark = NoteStyling.IsDark;

        if (_floorIsDark == dark)
        {
            return;
        }

        _floorIsDark = dark;
        BoardArea.Background = new SolidColorBrush(NoteStyling.ToColor(BoardGrid.BoardBackground(dark)));
    }

    private void ApplyVignette()
    {
        var dark = NoteStyling.IsDark;

        if (_vignetteIsDark == dark)
        {
            return;
        }

        _vignetteIsDark = dark;
        var alpha = BoardGrid.VignetteAlpha(dark);

        var brush = new RadialGradientBrush
        {
            Center = new Point(BoardGrid.VignetteCenterX, BoardGrid.VignetteCenterY),
            GradientOrigin = new Point(BoardGrid.VignetteCenterX, BoardGrid.VignetteCenterY),
            RadiusX = BoardGrid.VignetteRadiusX,
            RadiusY = BoardGrid.VignetteRadiusY,
        };

        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = BoardGrid.VignetteClearUntil });
        brush.GradientStops.Add(new GradientStop
        {
            Color = Windows.UI.Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0),
            Offset = 1,
        });

        VignetteLayer.Fill = brush;
    }

    private void PaintGrid(double tileWidth, double tileHeight, double scale)
    {
        var pixelsW = (int)Math.Ceiling(tileWidth * scale);
        var pixelsH = (int)Math.Ceiling(tileHeight * scale);

        var bitmap = new WriteableBitmap(pixelsW, pixelsH);
        var buffer = new byte[bitmap.PixelBuffer.Length];

        var ink = BoardGrid.Ink(NoteStyling.IsDark);

        foreach (var (step, levelAlpha) in BoardGrid.LevelsFor(_view.Zoom))
        {
            var spacing = step * _view.Zoom * scale;
            var alpha = levelAlpha * BoardGrid.PeakAlpha;

            for (var x = 0.0; x < pixelsW; x += spacing)
            {
                for (var y = 0.0; y < pixelsH; y += spacing)
                {
                    BoardGrid.StampDot(buffer, pixelsW, pixelsH, x, y, BoardGrid.DotRadiusPx * scale, alpha, ink);
                }
            }
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(buffer, 0, buffer.Length);
        }

        // Without this the buffer above is only picked up when the compositor happens to look:
        // two captures of the same board came back with and without the dots.
        bitmap.Invalidate();

        GridLayer.Source = bitmap;
        GridLayer.Width = tileWidth;
        GridLayer.Height = tileHeight;
        GridLayer.Stretch = Stretch.Fill;

        // 取证：位图自己有没有墨点。没有就是 PaintGrid 的数学问题，有就是合成/显示的问题。
        var inked = 0;
        var peak = 0;
        double mass = 0;

        for (var i = 3; i < buffer.Length; i += 4)
        {
            if (buffer[i] == 0)
            {
                continue;
            }

            inked++;
            mass += buffer[i];
            peak = Math.Max(peak, buffer[i]);
        }

        var levels = string.Join(" ", BoardGrid.LevelsFor(_view.Zoom).Select(level => level.Step + "@" + level.Alpha.ToString("0.###")));

        Diagnostics.Write($"grid painted {pixelsW}×{pixelsH} levels [{levels}] inked {inked} px mass {mass / 255:0.###} peak {peak}/255 buffer {buffer.Length}/expected {pixelsW * pixelsH * 4}");
    }

    /// <summary>Device pixels per layout pixel — the WinUI 3 route is XamlRoot.RasterizationScale
    /// (there is no CompositionTarget.GetPixelSizeForLayoutSize in this projection).</summary>
    private double PixelScale()
    {
        var scale = BoardArea.XamlRoot?.RasterizationScale ?? 0;

        return scale <= 0 ? 1 : scale;
    }
}
