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

    /// <summary>同一张位图与同一块缓冲按档复用（尺寸对得上就清一遍重画）。
    /// 每一发都新起一张 4.6MB 的位图，连滚时会咬出 70–123ms 的 GC 尖峰（现读见过）。</summary>
    private WriteableBitmap? _gridBitmap;

    private byte[]? _gridBuffer;
    private Rect? _clipRect;
    private bool? _floorIsDark;

    private bool _inkedOnce;

    /// <summary>当前这张点阵位图是按哪一档缩放画的（0＝还没有）。缩放中途靠它算 drift。</summary>
    private double _gridPaintZoom;

    /// <summary>点阵真正重画了多少发、其中多少发是"换了尺寸所以重新 new 一张位图＋一块缓冲"。
    /// 那两个数由缩放探针报出来：LOH 上反复 new 5MB 就是几十毫秒那一抖的候选。</summary>
    internal int _gridRepaints;

    internal int _gridResized;
    private bool? _vignetteIsDark;

    /// <summary>
    /// 按"相机在这一帧的样子"画底下那两层。
    /// </summary>
    /// <param name="forensics">要不要顺手数一遍墨。那一发要 3–6ms：它把整张缓冲再扫一遍，
    /// 只在"位图到底有没有点上墨"这件事值得证的时候才付（启动后第一发、自检、以及 --perf）。
    /// 10-27 量总览那一档时它是白交的：每跨一次缩放档都要重画点阵，而每一次都数一遍墨。</param>
    private void UpdateAtmosphere(bool forensics = false) => UpdateAtmosphereAt(_view.Zoom, _view.PanX, _view.PanY, forensics);

    /// <summary>
    /// 按"相机在这一帧的样子"画底下那两层。
    /// </summary>
    /// <param name="zoom">点阵该按哪一档缩放。</param>
    /// <param name="panX">与 <paramref name="panX"/> 一起决定相位：点阵跟着相机走。</param>
    /// <param name="forensics">要不要顺手数一遍墨（那一发要 3–6ms，只有落定那一拍值这个钱）。</param>
    /// <remarks>
    /// 为什么参数化而不是直接读 <see cref="_view"/>：滚轮预览那一长串帧里，相机还没落到
    /// <c>_view</c> 上（它走的是合成变换），点阵却必须跟着走——10-24 他报"缩放时点阵忽然放大
    /// 缩小"就是这一层被冻在旧缩放档上、停手才啪地补上。中间那些帧不吃数墨那一发。
    /// </remarks>
    private void UpdateAtmosphereAt(double zoom, double panX, double panY, bool forensics)
    {
        // 每一发起来后头一次重画都要留下那行取证读数（"位图有没有墨"这件事只能在那一帧证一次）；
        // 之后的重画不再扫第二遍——缩放中途每跨一档都要重画，那 3–6ms 是白交的。
        var inked = forensics || !_inkedOnce;

        if (inked)
        {
            _inkedOnce = true;
        }
        var width = BoardArea.ActualWidth;
        var height = BoardArea.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = PixelScale();
        var period = BoardGrid.PeriodFor(zoom);

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
            _gridPaintZoom = 0;
            return;
        }

        GridLayer.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

        // Whole layout pixels, or the Image gets resampled and a 2px dot smears into a grey smear.
        // 边长再取整到 64 的倍数：缩放中途每一发只比上一发宽几个像素，取整之后同一张位图能复用
        // （配合 _gridBuffer 那一块），不必每发重新分配。多出来的那一条被 BoardArea.Clip 裁掉。
        // 位图的边长按"最宽的那一档周期"留，不按当下这一档。
        // 原来按 `width + period` 取整到 64 的倍数：缩放中途 period 跟着 zoom 走，于是每跨一格
        // 就要重新 new 一张位图＋一块缓冲（都在大对象堆上）。10-27 实测：50%↔150% 来回滚 10 秒
        // 重画 338 次、其中 84 次换了尺寸，分配 462MB，逼出 27 次 gen2 全堆回收——
        // 那一下一下的顿挫就是它。留成定长之后，缩放中途一次都不再换尺寸。
        // 乘那一下 (1 + GridDriftLimit) 是因为下面要把位图**缩小**显示（drift 可以小于 1），
        // 留出来的这一条保证缩到最狠那一档时右边和下边仍然盖得住视口＋一个周期。
        var slack = BoardGrid.PeriodFor(MaxZoom);
        var tileWidth = Math.Ceiling(((width + slack) * (1 + GridDriftLimit)) / GridTileQuantum) * GridTileQuantum;
        var tileHeight = Math.Ceiling(((height + slack) * (1 + GridDriftLimit)) / GridTileQuantum) * GridTileQuantum;
        var key = $"{width:0}|{height:0}|{scale:0.###}|{NoteStyling.IsDark}";

        // 这张位图是按"画它的那一档缩放"画的，显示时整体乘 drift = 真相机/那一档。
        // 点距与相位都跟着真相机走（相位仍按当前 period 取模），所以缩放中途点阵是**连续跟手**的，
        // 只有点的粗细跟着漂最多 GridDriftLimit；漂出那一档才重画一次。
        // 原来每一帧都重画：10-27 实测同一手势 342 次重画＝每帧把一张 5MB 纹理重传一次 GPU
        // （那一档 GPU 17.3% 就是这么来的），而其中绝大多数帧的缩放只差百分之几，重画不值。
        var drift = _gridPaintZoom > 0 ? zoom / _gridPaintZoom : 0.0;

        if (key != _gridKey || Math.Abs(drift - 1) > GridDriftLimit)
        {
            _gridKey = key;
            _gridPaintZoom = zoom;
            drift = 1.0;

            PaintGrid(tileWidth, tileHeight, scale, zoom, inked);
        }

        if (Math.Abs(GridLayer.Width - (tileWidth * drift)) > 0.5)
        {
            GridLayer.Width = tileWidth * drift;
            GridLayer.Height = tileHeight * drift;
        }

        // 平移只改这一处：点阵跟着相位走，位图不重画。
        GridOffset.X = BoardGrid.Phase(panX, period) - period;
        GridOffset.Y = BoardGrid.Phase(panY, period) - period;
    }

    /// <summary>点阵允许"靠缩放顶过去"的漂移上限（比例）。超过它才重画一次。</summary>
    private const double GridDriftLimit = 0.06;

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

    private const double GridTileQuantum = 64;

    private void PaintGrid(double tileWidth, double tileHeight, double scale, double zoom, bool forensics)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pixelsW = (int)Math.Ceiling(tileWidth * scale);
        var pixelsH = (int)Math.Ceiling(tileHeight * scale);

        WriteableBitmap bitmap;
        byte[] buffer;

        _gridRepaints++;

        if (_gridBitmap is { } cached && _gridBuffer is { } cachedBuffer
            && cached.PixelWidth == pixelsW && cached.PixelHeight == pixelsH)
        {
            bitmap = cached;
            buffer = cachedBuffer;
            Array.Clear(buffer);
        }
        else
        {
            // 换一次尺寸就 new 一张位图＋一块 5MB 缓冲（大对象堆）：缩放中途每跨一格
            // 64 像素的档就付一次。这两个计数由缩放带探针报出来，不藏。
            _gridResized++;

            bitmap = new WriteableBitmap(pixelsW, pixelsH);
            buffer = new byte[bitmap.PixelBuffer.Length];
            _gridBitmap = bitmap;
            _gridBuffer = buffer;
        }

        var ink = BoardGrid.Ink(NoteStyling.IsDark);

        // 一层一次批量盖点：核按层算一次，别把 sqrt/舍入留在每一枚点的每个像素上。
        // 逐点那一圈（StampDot）留在 Core 里给差分测试当参照，产品路径不再走它。
        foreach (var (step, levelAlpha) in BoardGrid.LevelsFor(zoom))
        {
            BoardGrid.StampLattice(
                buffer,
                pixelsW,
                pixelsH,
                step * zoom * scale,
                BoardGrid.DotRadiusPx * scale,
                levelAlpha * BoardGrid.PeakAlpha,
                ink);
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
        // 这一发要 3–6ms，所以只有落定那一拍跑它；滚轮中间那些帧只想知道"画完没有"。
        if (!forensics)
        {
            Diagnostics.Write($"grid painted {pixelsW}×{pixelsH} at {zoom:0.###} in {clock.Elapsed.TotalMilliseconds:0.#}ms（预览，不数墨）");

            return;
        }

        var paintedAt = clock.Elapsed.TotalMilliseconds;
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

        var levels = string.Join(" ", BoardGrid.LevelsFor(zoom).Select(level => level.Step + "@" + level.Alpha.ToString("0.###")));

        // 这一发要带毫秒：2026-10-24 他要"缩放丝滑"，而"能不能逐帧重画点阵"整个决定压在
        // 一次重画的代价上——没有这个数就只能猜。停手提交那 103ms 里有多少是这里，也读这一行。
        Diagnostics.Write($"grid painted {pixelsW}×{pixelsH} levels [{levels}] inked {inked} px mass {mass / 255:0.###} peak {peak}/255 buffer {buffer.Length}/expected {pixelsW * pixelsH * 4} in {clock.Elapsed.TotalMilliseconds:0.#}ms（画 {paintedAt:0.#} ＋ 数墨 {clock.Elapsed.TotalMilliseconds - paintedAt:0.#}ms）");
    }

    /// <summary>Device pixels per layout pixel — the WinUI 3 route is XamlRoot.RasterizationScale
    /// (there is no CompositionTarget.GetPixelSizeForLayoutSize in this projection).</summary>
    private double PixelScale()
    {
        var scale = BoardArea.XamlRoot?.RasterizationScale ?? 0;

        return scale <= 0 ? 1 : scale;
    }
}
