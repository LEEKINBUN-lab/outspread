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

    /// <summary>
    /// 拖端点那一发的实时反馈：自由端现在指到哪、吸住了谁的哪一面。
    /// 原来按下只记一个状态、抬起才改面——中间屏幕上什么都不动，所以"没法确定自己是否成功抓住了线条"
    /// （他 10-24 点的）。现在这一头跟着指针走、虚线半透明，吸住哪面就停在哪面上。
    /// </summary>
    private (double X, double Y)? _edgePreview;

    private (string NodeId, string Side)? _edgeSnap;

    /// <summary>指针在走：把被拖的那一头挪到指针（或它吸住的边中心）上，并重画。</summary>
    private void TrackEdgeDrag(double worldX, double worldY)
    {
        if (_edgeDrag is null)
        {
            return;
        }

        var snap = LinkPull.Nearest(BoardNotes().ConvertAll(RectOf), worldX, worldY, null, LinkPull.Reach(_view.Zoom));

        if (snap is { } hit)
        {
            _edgeSnap = (hit.NodeId, hit.Side);
            _edgePreview = (hit.X, hit.Y);
        }
        else
        {
            _edgeSnap = null;
            _edgePreview = (worldX, worldY);
        }

        PaintLinks();
    }

    /// <summary>松手：吸在别的格上就换归属，否则只换进出哪一面。返回是否真的动了文档。</summary>
    private bool CommitEdgeDrag(string edgeId, string end)
    {
        var fromEnd = end == CanvasEdge.FromEnd;

        if (_edgeSnap is { } snap && _document.Find(snap.NodeId) is { } card)
        {
            var current = _document.Edges.FirstOrDefault(edge => edge.Id == edgeId);
            var owner = fromEnd ? current?.FromNode : current?.ToNode;

            if (current is not null && owner != snap.NodeId && _document.MoveEdgeEnd(edgeId, fromEnd, snap.NodeId, snap.Side))
            {
                return true;
            }

            return _document.SetEdgeSide(edgeId, fromEnd, snap.Side);
        }

        return false;
    }

    /// <summary>
    /// 拖一根线的端点时，屏幕上必须看得见"抓住了"：那一头跟着指针走、画成虚线半透明，
    /// 并且吸到离指针最近的那条边中心；松手按停下的地方结算（换归属或换面）。
    /// </summary>
    private void SelfTestEdgeHandleFeedback(List<string> problems)
    {
        var pair = BoardNotes().Take(2).ToList();

        if (pair.Count < 2)
        {
            problems.Add("自检线头: 板上不足两格，这一发不适用");

            return;
        }

        var before = _document.ToJson();

        BeginGesture("自检线头");

        var edge = _document.AddEdge(pair[0].Id, pair[1].Id);

        ArrangeAll();

        _edgeDrag = (edge.Id, CanvasEdge.ToEnd, pair[1].Id);

        // 把终点那一头拖到起点那枚的上边中心上方 6 像素：该吸住它的上边。
        var target = pair[0];
        var wx = target.X + (target.Width / 2);
        var wy = target.Y - 6;

        TrackEdgeDrag(wx, wy);

        if (_edgeSnap is not { } snap || snap.NodeId != target.Id || snap.Side != CanvasEdge.Top)
        {
            problems.Add($"自检线头: 拖到那枚上边附近该吸住它的上边，实得 {(_edgeSnap is null ? "谁都没吸" : $"{_edgeSnap.Value.NodeId}/{_edgeSnap.Value.Side}")}");
        }

        if (!LinkLayer.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Any(path => path.StrokeDashArray is { Count: > 0 }))
        {
            problems.Add("自检线头: 拖动中那根线没有虚线预览——屏幕上看不出自己是否抓住了它");
        }

        var committed = CommitEdgeDrag(edge.Id, CanvasEdge.ToEnd);

        _edgeDrag = null;
        _edgePreview = null;
        _edgeSnap = null;

        EndGesture();

        var moved = _document.Edges.FirstOrDefault(item => item.Id == edge.Id);

        if (!committed || moved?.ToNode != target.Id)
        {
            problems.Add($"自检线头: 松手没把这一头挪到另一枚上（现在 toNode={moved?.ToNode ?? "线没了"}）");
        }

        Undo();
        Rebuild();

        if (_document.ToJson() != before)
        {
            problems.Add("自检线头: 撤销一发以后文档没回到原样");

            return;
        }

        Diagnostics.Write("selftest 线头: 拖端点时那一头跟指针走（虚线预览）、吸住离得最近的那条边、松手换归属，撤销回原样");
    }

    private void PaintLinks()
    {
        LinkLayer.Children.Clear();
        LinkHandleLayer.Children.Clear();

        var dark = NoteStyling.IsDark;
        var wire = new SolidColorBrush(NoteStyling.ToColor(EdgeGeometry.WireColour(dark)));
        var accent = new SolidColorBrush(NoteStyling.ToColor(EdgeGeometry.SelectedColour(dark)));

        foreach (var edge in _document.Edges)
        {
            if (_document.Find(edge.FromNode) is not { } found
                || _document.Find(edge.ToNode) is not { } aimed2
                || !_realized.ContainsKey(found.Id)
                || !_realized.ContainsKey(aimed2.Id))
            {
                // 一端不在板上（或在这发出图里被藏起来了）就不画：半根线不是"简化的线"，是脏。
                continue;
            }

            var from = found;
            var to = aimed2;

            var auto = CanvasEdge.AutoSides(from, to);
            var fromSide = edge.FromSide ?? auto.From;
            var toSide = edge.ToSide ?? auto.To;
            (double X, double Y)? p1Override = null;
            (double X, double Y)? p2Override = null;

            var draggingFrom = _edgeDrag is { } drag && drag.EdgeId == edge.Id && drag.End == CanvasEdge.FromEnd;
            var draggingTo = _edgeDrag is { } drag2 && drag2.EdgeId == edge.Id && drag2.End == CanvasEdge.ToEnd;

            if ((draggingFrom || draggingTo) && _edgePreview is { } preview)
            {
                var aimed = _edgeSnap;

                if (draggingFrom)
                {
                    if (aimed is { } a && _document.Find(a.NodeId) is { } moved)
                    {
                        from = moved;
                    }

                    fromSide = aimed?.Side ?? fromSide;
                }
                else
                {
                    if (aimed is { } b && _document.Find(b.NodeId) is { } moved)
                    {
                        to = moved;
                    }

                    toSide = aimed?.Side ?? toSide;
                }

                p1Override = draggingFrom ? preview : p1Override;
                p2Override = draggingTo ? preview : p2Override;
            }

            var p1 = p1Override ?? CanvasEdge.SidePoint(from, fromSide);
            var p2 = p2Override ?? CanvasEdge.SidePoint(to, toSide);

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
                StrokeDashArray = draggingFrom || draggingTo ? [4, 3] : null,
                Opacity = draggingFrom || draggingTo ? 0.72 : 1,
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

            AddHandle(x1, y1, edge.Id, CanvasEdge.FromEnd, from.Id, draggingFrom);
            AddHandle(x2, y2, edge.Id, CanvasEdge.ToEnd, to.Id, draggingTo);
        }
    }

    /// <summary>
    /// The dot on an end whose own card is selected — drag it and the line changes which face it
    /// leaves or enters. Same rule as the prototype: the dot exists only once that end is selected, so
    /// a board full of lines is not a field of grabbable dots. Hit circle is 26px across (WCAG 2.5.8).
    /// </summary>
    private void AddHandle(double screenX, double screenY, string edgeId, string end, string nodeId, bool dragging = false)
    {
        if (!_selection.Contains(nodeId))
        {
            return;
        }

        var dark = NoteStyling.IsDark;

        var radius = dragging ? EdgeGeometry.HandleSeenRadiusPx + 2 : EdgeGeometry.HandleSeenRadiusPx;

        var dot = new Ellipse
        {
            Width = 2 * radius,
            Height = 2 * radius,
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
