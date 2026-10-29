using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Outspread.Board;
using Outspread.Core;
using System.Numerics;
using Windows.UI;

namespace Outspread.Spike;

/// <summary>
/// 另一臂：整块板面画进一枚合成表面。
/// </summary>
/// <remarks>
/// 与产品的对法刻意保持一致，否则比的不是同一件事：
/// ① 相机＝缩放＋世界中心，两臂都按同一组数摆（跑之前由测量脚本写进参数，不各算各的）；
/// ② 卡面几何全用<b>世界单位</b>，缩放在表面变换里乘一次——与产品的
///    "世界单位排版＋<c>_paintZoom</c> 那一层变换"同构，文字因此不是"每帧重排一遍"；
/// ③ 阴影带与纸色一律现读 <see cref="NoteSurface"/>，屏外那几格照旧不画（对齐产品的剔除）。
///
/// 这一枚工程不进 check.cmd、不进 run-board.cmd：它只为回答"每格一枚 XAML 视觉"这一项
/// 到底值多少 GPU／CPU／帧间隔。读数回来就判生死，不许它长成第二个产品。
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly record struct Cell(CanvasNode Node, Grouping.Rect Box);

    private readonly List<Cell> _cells = [];

    private readonly Dictionary<string, CanvasTextLayout> _layouts = new(StringComparer.Ordinal);

    private double _zoom = 1.0;

    private double _centerX;

    private double _centerY;

    private int _panMilliseconds;

    private int _frame;

    private bool _moving;

    private Stopwatch? _clock;

    private double _drawTotalMs;

    private readonly List<double> _gaps = [];

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();

        var board = Value(args, "--board");
        _zoom = double.Parse(Value(args, "--zoom") ?? "1.0");
        _centerX = double.Parse(Value(args, "--cx") ?? "0");
        _centerY = double.Parse(Value(args, "--cy") ?? "0");
        _panMilliseconds = int.Parse(Value(args, "--pan-ms") ?? "0");

        NoteStyling.IsDark = string.Equals(Value(args, "--theme"), "dark", StringComparison.OrdinalIgnoreCase);

        if (board is null || !File.Exists(board))
        {
            Log.Write("缺 --board 或那枚文件不在——这一发起不来");

            Environment.Exit(2);

            return;
        }

        Load(board);

        Board.ResourceLost += (_, _) => _layouts.Clear();

        if (_panMilliseconds > 0)
        {
            StartPanProbe();
        }
    }

    private static string? Value(string[] args, string flag)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private void Load(string path)
    {
        var started = Stopwatch.StartNew();

        var document = CanvasDocument.Parse(File.ReadAllText(path), path);

        foreach (var node in document.Nodes)
        {
            // 与产品同档：只画普通便签那两型，组框/连线/表格卡这轮不比（那些不是每像素的账）。
            if (node.Kind is not (CanvasNodeKind.Note or CanvasNodeKind.Text) || node.Width <= 0 || node.Height <= 0)
            {
                continue;
            }

            _cells.Add(new Cell(node, new Grouping.Rect(node.Id, node.X, node.Y, node.Width, node.Height)));
        }

        Log.Write($"载入 {path}：{_cells.Count} 枚可画 · 解析 {started.Elapsed.TotalMilliseconds:0.0}ms · 缩放 {_zoom:0.###} · 中心 ({_centerX:0}, {_centerY:0})");

        Title = $"「铺陈」spike · {_cells.Count} 格";
    }

    private void StartPanProbe()
    {
        _startCenterX = _centerX;
        _startCenterY = _centerY;
        _clock = Stopwatch.StartNew();

        CompositionTarget.Rendering += OnBeat;
    }

    /// <summary>节拍挂在渲染那一拍（与产品的探针同一把尺，不挂被量化到 31ms 的计时器）。</summary>
    private void OnBeat(object? sender, object e)
    {
        if (_clock!.Elapsed.TotalMilliseconds >= _panMilliseconds)
        {
            Finish();

            return;
        }

        // 与产品拖一格那档同形的连续移动：X 乒乓、Y 也走一小段，别一路漂到空地上。
        var amplitude = 60.0 / _zoom;

        _centerX = _startCenterX + Math.Sin(_frame / 8.0) * amplitude;
        _centerY = _startCenterY + Math.Cos(_frame / 8.0) * (amplitude * 0.66);

        _frame++;

        Board.Invalidate();
    }

    private double _startCenterX;

    private double _startCenterY;

    private void Finish()
    {
        CompositionTarget.Rendering -= OnBeat;

        var over25 = _gaps.Count(gap => gap > 25);
        var worst = _gaps.OrderByDescending(gap => gap).Take(5).ToList();

        Log.Write(
            $"spike pan {_panMilliseconds}ms: {_frame} 帧 · 每帧均 {(_clock!.Elapsed.TotalMilliseconds / Math.Max(1, _frame)):0.###}ms" +
            $" 其中我们画 {(_drawTotalMs / Math.Max(1, _frame)):0.###}ms · 最慢 {(_gaps.Count > 0 ? _gaps.Max() : 0):0.###}ms");
        Log.Write(
            $"spike 帧间隔: 全发中位 {Median(_gaps):0.##}ms · 超 20ms 的 {_gaps.Count(gap => gap > 20)} 帧 · 超 25ms 的 {over25} 帧" +
            $" · 最坏 {string.Join('/', worst.Select(gap => gap.ToString("0.##")))}");
        Log.Write(
            $"spike 常驻: 工作集 {Process.GetCurrentProcess().WorkingSet64 / 1048576:0}MB · 托管 {GC.GetTotalMemory(false) / 1048576:0}MB" +
            $" · 文字版面缓存 {_layouts.Count} 枚 · 表面 1 枚 · 正对照：走了 {_frame} 帧、画了 {_drawnLastFrame} 枚");

        Environment.Exit(0);
    }

    private int _drawnLastFrame;

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();

        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var started = Stopwatch.GetTimestamp();

        var surface = sender.Size;
        var session = args.DrawingSession;

        session.Clear(NoteStyling.ToColor(BoardGrid.BoardBackground(NoteStyling.IsDark)));

        // 世界 → 屏幕那一枚变换：缩放在这里乘一次，卡内一切还是世界单位（与产品的 _paintZoom 同构）。
        var transform = Matrix3x2.CreateScale((float)_zoom, (float)_zoom)
            * Matrix3x2.CreateTranslation((float)(surface.Width / 2 - _centerX * _zoom), (float)(surface.Height / 2 - _centerY * _zoom));

        session.Transform = transform;

        var world = new Windows.Foundation.Rect(
            -surface.Width / 2 / _zoom + _centerX,
            -surface.Height / 2 / _zoom + _centerY,
            surface.Width / _zoom,
            surface.Height / _zoom);

        var drawn = 0;

        foreach (var cell in _cells)
        {
            if (cell.Box.X > world.Right || cell.Box.X + cell.Box.Width < world.X
                || cell.Box.Y > world.Bottom || cell.Box.Y + cell.Box.Height < world.Y)
            {
                continue;
            }

            DrawCell(session, cell.Node);

            drawn++;
        }

        _drawnLastFrame = drawn;

        session.Transform = Matrix3x2.Identity;

        _drawTotalMs += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

        if (_moving)
        {
            var gap = (Stopwatch.GetTimestamp() - _lastBeat) * 1000.0 / Stopwatch.Frequency;

            _gaps.Add(gap);
        }

        _lastBeat = Stopwatch.GetTimestamp();
        _moving = true;
    }

    private long _lastBeat;

    private void DrawCell(CanvasDrawingSession session, CanvasNode node)
    {
        var surface = NoteStyling.SurfaceFor(node.Color);
        var back = new Rgb(surface.R, surface.G, surface.B);

        foreach (var band in NoteSurface.BandsFor(node.Id, 1.0, node.Width, node.Height))
        {
            if (band.Inset || band.Width <= 0 || band.Height <= 0)
            {
                continue;
            }

            var stops = new CanvasGradientStop[band.Stops.Count];
            var index = 0;

            foreach (var (offset, alpha) in band.Stops)
            {
                // Band.Stops 的位置是**沿带的像素数**，D2D 要 0–1（与产品那段同一个归一）。
                stops[index++] = new CanvasGradientStop(
                    (float)Math.Clamp(offset / band.Height, 0, 1),
                    NoteStyling.WithAlpha(band.Color, alpha));
            }

            var brush = session.CreateLinearGradientBrush(
                stops,
                new Vector2(0, band.Y),
                new Vector2(0, band.Y + band.Height));

            session.FillRectangle(new Windows.Foundation.Rect(band.X, band.Y, band.Width, band.Height), brush);
        }

        var paper = session.CreateLinearGradientBrush(
        [
            new CanvasGradientStop(0, NoteStyling.ToColor(NoteSurface.PaperAt(back, 0, NoteStyling.IsDark))),
            new CanvasGradientStop((float)NoteSurface.PaperMiddleOffset, surface),
            new CanvasGradientStop(1, NoteStyling.ToColor(NoteSurface.PaperAt(back, 1, NoteStyling.IsDark))),
        ],
            new Vector2(0, (float)node.Y),
            new Vector2(0, (float)(node.Y + node.Height)));

        session.FillRectangle(new Windows.Foundation.Rect(node.X, node.Y, node.Width, node.Height), paper);

        session.DrawRectangle(
            new Windows.Foundation.Rect(node.X, node.Y, node.Width, node.Height),
            NoteStyling.LineFor(node.Color, surface),
            1f);

        var type = NoteTypography.For(NoteTypography.DefaultSize);
        var text = node.Text ?? string.Empty;

        if (text.Length == 0)
        {
            return;
        }

        var layout = LayoutFor(session, node.Id, text, node.Width - (2 * type.Padding));

        session.DrawTextLayout(layout, (float)(node.X + type.Padding), (float)(node.Y + type.Padding), NoteStyling.InkFor(node.Color, surface));
    }

    private CanvasTextLayout LayoutFor(CanvasDrawingSession session, string id, string text, double width)
    {
        if (_layouts.TryGetValue(id, out var cached) && cached.Text == text && cached.LayoutWidth >= width - 0.5 && cached.LayoutWidth <= width + 0.5)
        {
            return cached;
        }

        var type = NoteTypography.For(NoteTypography.DefaultSize);

        var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI, Microsoft YaHei UI",
            FontSize = (float)type.Size,
            WordWrapping = CanvasWordWrapping.Wrap,
            ParagraphMinimumLineSpacing = (float)type.Leading,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            Trimming = CanvasLineTrimming.Trim,
        };

        var layout = new CanvasTextLayout(session, text, format, (float)Math.Max(1, width), float.MaxValue);

        _layouts[id] = layout;

        return layout;
    }
}
