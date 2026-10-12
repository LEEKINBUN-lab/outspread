using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Outspread.Core;
using Windows.Foundation;

namespace Outspread.Board;

/// <summary>
/// The arrows between cards: a cubic wire out of each card's chosen side, and an arrowhead on the
/// end that arrives along the curve's own tangent.
/// </summary>
/// <remarks>
/// <para>
/// Rebuilt on every arrange pass rather than cached: a dragged card whose arrow lagged behind it
/// would look like the line were a separate object on the board, and it is not — it is a relation
/// between two cards.
/// </para>
/// <para>
/// The layer sits <b>under</b> the cards (XAML order: vignette → links → items), the same way the
/// prototype's <c>#links</c> precedes <c>#items</c>. An arrow drawn over the paper hides words.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>哪根线的哪一头正被拖（松手时换成离指针最近的那一面）。</summary>
    private (string EdgeId, string End, string NodeId)? _edgeDrag;

    private void PaintLinks()
    {
        LinkLayer.Children.Clear();
        LinkHandleLayer.Children.Clear();

        var dark = NoteStyling.IsDark;
        var wire = new SolidColorBrush(NoteStyling.ToColor(EdgeGeometry.WireColour(dark)));
        var accent = new SolidColorBrush(NoteStyling.ToColor(EdgeGeometry.SelectedColour(dark)));

        foreach (var edge in _document.Edges)
        {
            if (_document.Find(edge.FromNode) is not { } from
                || _document.Find(edge.ToNode) is not { } to
                || !_realized.ContainsKey(from.Id)
                || !_realized.ContainsKey(to.Id))
            {
                // 一端不在板上（或在这发出图里被藏起来了）就不画：半根线不是"简化的线"，是脏。
                continue;
            }

            var auto = CanvasEdge.AutoSides(from, to);
            var fromSide = edge.FromSide ?? auto.From;
            var toSide = edge.ToSide ?? auto.To;

            var p1 = CanvasEdge.SidePoint(from, fromSide);
            var p2 = CanvasEdge.SidePoint(to, toSide);

            var x1 = _view.ToScreenX(p1.X);
            var y1 = _view.ToScreenY(p1.Y);
            var x2 = _view.ToScreenX(p2.X);
            var y2 = _view.ToScreenY(p2.Y);

            var (c1x, c1y, c2x, c2y) = EdgeGeometry.Controls(
                x1, y1, CanvasEdge.VectorOf(fromSide), x2, y2, CanvasEdge.VectorOf(toSide));

            var on = _selection.Contains(from.Id) || _selection.Contains(to.Id);
            var brush = on ? accent : wire;

            LinkLayer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = brush,
                StrokeThickness = EdgeGeometry.StrokePx,
                Data = new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new Point(x1, y1),
                            Segments =
                            {
                                new BezierSegment
                                {
                                    Point1 = new Point(c1x, c1y),
                                    Point2 = new Point(c2x, c2y),
                                    Point3 = new Point(x2, y2),
                                },
                            },
                        },
                    },
                },
            });

            var (tipX, tipY, leftX, leftY, rightX, rightY) = EdgeGeometry.ArrowHead(x2, y2, c2x, c2y);

            LinkLayer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Fill = brush,
                Data = new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new Point(tipX, tipY),
                            IsClosed = true,
                            Segments =
                            {
                                new LineSegment { Point = new Point(leftX, leftY) },
                                new LineSegment { Point = new Point(rightX, rightY) },
                            },
                        },
                    },
                },
            });

            AddHandle(x1, y1, edge.Id, CanvasEdge.FromEnd, from.Id);
            AddHandle(x2, y2, edge.Id, CanvasEdge.ToEnd, to.Id);
        }
    }

    /// <summary>
    /// The dot on an end whose own card is selected — drag it and the line changes which face it
    /// leaves or enters. Same rule as the prototype: the dot exists only once that end is selected, so
    /// a board full of lines is not a field of grabbable dots. Hit circle is 26px across (WCAG 2.5.8).
    /// </summary>
    private void AddHandle(double screenX, double screenY, string edgeId, string end, string nodeId)
    {
        if (!_selection.Contains(nodeId))
        {
            return;
        }

        var dark = NoteStyling.IsDark;

        var dot = new Ellipse
        {
            Width = 2 * EdgeGeometry.HandleSeenRadiusPx,
            Height = 2 * EdgeGeometry.HandleSeenRadiusPx,
            Fill = new SolidColorBrush(NoteStyling.ToColor(SelectionChrome.HandleFace(dark))),
            Stroke = new SolidColorBrush(NoteStyling.ToColor(SelectionChrome.Ring(dark))),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };

        var target = new Ellipse
        {
            Width = 2 * EdgeGeometry.HandleHitRadiusPx,
            Height = 2 * EdgeGeometry.HandleHitRadiusPx,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };

        Canvas.SetLeft(dot, screenX - EdgeGeometry.HandleSeenRadiusPx);
        Canvas.SetTop(dot, screenY - EdgeGeometry.HandleSeenRadiusPx);
        Canvas.SetLeft(target, screenX - EdgeGeometry.HandleHitRadiusPx);
        Canvas.SetTop(target, screenY - EdgeGeometry.HandleHitRadiusPx);

        ToolTipService.SetToolTip(target, "拖这一枚点＝换这根线进出哪一面");

        target.PointerPressed += (_, args) =>
        {
            args.Handled = true;

            if (_editingId is not null)
            {
                CloseEditor(commit: true);
            }

            _edgeDrag = (edgeId, end, nodeId);

            BeginGesture("换了一根线的进出面");
            BoardArea.CapturePointer(args.Pointer);
        };

        LinkHandleLayer.Children.Add(dot);
        LinkHandleLayer.Children.Add(target);
    }

    /// <summary>
    /// Alt+C — connect the selected cards into a chain in stacking order, or cut the chain if every
    /// link in it is already there (the same toggle the prototype has).
    /// </summary>
    private void ConnectSelection()
    {
        // 只有连线不退化成"全体"：把整块板子串成一条链是事故，不是效果。
        var list = _document.Nodes.Where(node => _selection.Contains(node.Id)).ToList();

        if (list.Count < 2)
        {
            Say("连线要至少 2 格（按叠放次序串成一条链）");

            return;
        }

        var pairs = new List<(string From, string To)>();

        for (var index = 0; index + 1 < list.Count; index++)
        {
            pairs.Add((list[index].Id, list[index + 1].Id));
        }

        var allOn = pairs.All(pair => _document.FindEdge(pair.From, pair.To) is not null);

        BeginGesture(allOn ? "删了连线" : "连了一串");

        var edges = _document.Edges
            .Where(edge => pairs.Any(pair => edge.FromNode == pair.From && edge.ToNode == pair.To))
            .Select(edge => edge.Id)
            .ToList();

        foreach (var id in edges)
        {
            _document.RemoveEdge(id);
        }

        if (!allOn)
        {
            foreach (var pair in pairs)
            {
                if (_document.FindEdge(pair.From, pair.To) is null)
                {
                    _document.AddEdge(pair.From, pair.To);
                }
            }
        }

        EndGesture();
        Rebuild();
        SaveSoon();

        Say(allOn
            ? $"拆掉了这 {edges.Count} 根箭头 · Ctrl＋Z 放回去"
            : $"连了 {pairs.Count} 根箭头 · 端点在选中时才可拖");
    }
}
