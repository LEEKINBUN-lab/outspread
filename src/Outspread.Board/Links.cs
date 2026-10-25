using Microsoft.UI.Xaml;
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

    /// <summary>单独选中了哪根线。10-24 他点的：点线体就该选中线、两端一起出把手，
    /// 而不是先选中两端的便签才摸得到线头。线和格子不同时选：选中线＝清掉格子。</summary>
    private string? _selectedEdgeId;

    /// <summary>
    /// 拖端点那一发的实时反馈：自由端现在指到哪、吸住了谁的哪一面。
    /// 原来按下只记一个状态、抬起才改面——中间屏幕上什么都不动，所以"没法确定自己是否成功抓住了线条"
    /// （他 10-24 点的）。现在这一头跟着指针走、虚线半透明，吸住哪面就停在哪面上。
    /// </summary>
    private (double X, double Y)? _edgePreview;

    private (string NodeId, string Side)? _edgeSnap;

    /// <summary>松手就会删线：指针离所有格子都超过 <see cref="EdgeDeleteReachPx"/>（屏幕像素）。
    /// 吸附半径是 44，留出这段空白再判删除，才不至于把"只是没吸上"当成拖到空地。</summary>
    private bool _edgeDeleteArmed;

    private const double EdgeDeleteReachPx = 96;

    /// <summary>指针在走：把被拖的那一头挪到指针（或它吸住的边中心）上，并重画。</summary>
    private void TrackEdgeDrag(double worldX, double worldY)
    {
        if (_edgeDrag is null)
        {
            return;
        }

        var notes = BoardNotes();
        var snap = LinkPull.Nearest(notes.ConvertAll(RectOf), worldX, worldY, null, LinkPull.Reach(_view.Zoom));

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

        _edgeDeleteArmed = snap is null
            && notes.Count > 0
            && notes.All(note => LinkPull.GapToRect(RectOf(note), worldX, worldY) * _view.Zoom > EdgeDeleteReachPx);

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
    /// 线头松手：拖到没有便签的空地（删除区）＝删掉这根线；吸在别的格上＝换归属；
    /// 否则只换进出哪一面。删除区要比吸附半径大一圈，才不会把"只是没吸上"当成拖到空地。
    /// </summary>
    private bool FinishEdgeDrag(string edgeId, string end, string nodeId, double worldX, double worldY)
    {
        if (_edgeDeleteArmed)
        {
            _edgePreview = null;
            _edgeSnap = null;
            _edgeDeleteArmed = false;

            if (!_document.Edges.Any(edge => edge.Id == edgeId))
            {
                EndGesture();
                Rebuild();

                return false;
            }

            // 换掉开手那条"换面"标签：撤销里要说得出这一步删的是一根线。
            BeginGesture("删掉一根连线");
            _document.RemoveEdge(edgeId);
            EndGesture();
            Rebuild();
            SaveSoon();
            Say("已删除连线 · 可按 Ctrl＋Z 撤销");

            return true;
        }

        var changed = _document.Edges.Any(edge => edge.Id == edgeId)
            && (CommitEdgeDrag(edgeId, end)
                || (_edgeSnap is null && _document.Find(nodeId) is { } card
                    && _document.SetEdgeSide(edgeId, end == CanvasEdge.FromEnd, CanvasEdge.NearestSide(card, worldX, worldY))));

        _edgePreview = null;
        _edgeSnap = null;
        _edgeDeleteArmed = false;

        EndGesture();
        Rebuild();

        if (changed)
        {
            SaveSoon();
        }

        return changed;
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

        // 删除区：离所有格子都远（吸附半径 44 之外还有一圈空当）＝预览转红、松手删线。
        var doomed = _document.AddEdge(pair[0].Id, pair[1].Id);

        ArrangeAll();

        _edgeDrag = (doomed.Id, CanvasEdge.ToEnd, pair[1].Id);

        var farX = BoardNotes().Max(node => node.Right) + 2000;
        var farY = BoardNotes().Max(node => node.Bottom) + 2000;

        TrackEdgeDrag(farX, farY);

        if (!_edgeDeleteArmed)
        {
            problems.Add("自检线头: 拖到没有格子的空地没进入删除区");
        }
        else
        {
            var danger = NoteStyling.ToColor(EdgeGeometry.DeleteColour(NoteStyling.IsDark));
            var previewPath = LinkLayer.Children
                .OfType<Microsoft.UI.Xaml.Shapes.Path>()
                .FirstOrDefault(path => path.StrokeDashArray is { Count: > 0 });

            if (previewPath?.Stroke is not SolidColorBrush brush || brush.Color != danger)
            {
                problems.Add("自检线头: 删除区里那根线没变红——松手删不删看不出来");
            }
        }

        var removed = FinishEdgeDrag(doomed.Id, CanvasEdge.ToEnd, pair[1].Id, farX, farY);

        if (!removed || _document.Edges.Any(edge => edge.Id == doomed.Id))
        {
            problems.Add("自检线头: 拖到空地松手没删掉这根线");
        }
        else if (!_sayRow.Text.Contains("已删除", StringComparison.Ordinal))
        {
            problems.Add($"自检线头: 删线没说：回执『{_sayRow.Text}』");
        }

        Undo();
        Rebuild();

        if (_document.Edges.All(edge => edge.Id != doomed.Id))
        {
            problems.Add("自检线头: 删线以后撤销没把线带回来");
        }

        _document.RemoveEdge(doomed.Id);
        ArrangeAll();

        // 只是没吸上：离边 60px（吸附 44、删除 96 之间）不该判成删除。
        var near = _document.AddEdge(pair[0].Id, pair[1].Id);

        ArrangeAll();

        _edgeDrag = (near.Id, CanvasEdge.ToEnd, pair[1].Id);

        var topmost = BoardNotes().OrderBy(node => node.Y).First();
        var justMissX = topmost.X + (topmost.Width / 2);
        var justMissY = topmost.Y - 60;

        TrackEdgeDrag(justMissX, justMissY);

        if (_edgeSnap is not null)
        {
            problems.Add($"自检线头: 离边 60px 仍吸住了 {_edgeSnap.Value.NodeId}——没测到“只是没吸上”那一档");
        }

        if (_edgeDeleteArmed)
        {
            problems.Add("自检线头: 离边 60px 只是没吸上，却被判成删除");
        }

        _edgeDrag = null;
        _edgePreview = null;
        _edgeSnap = null;
        _edgeDeleteArmed = false;

        _document.RemoveEdge(near.Id);
        ArrangeAll();

        if (_document.ToJson() != before)
        {
            problems.Add("自检线头: 删除区那一发收尾没把文档带回原样");
        }

        Diagnostics.Write(
            "selftest 线头: 拖端点时那一头跟指针走（虚线预览）、吸住最近那条边、松手换归属；" +
            "拖到空地预览转红、松手删线且可撤销；离边 60px 只是没吸上时不删");
    }

    /// <summary>
    /// 线体自己可选中：屏幕上离曲线 6px 以内的左键落点判给这根线，不是框选。
    /// 判定按几何最短距离算，不用 Shape 命中测试——LinkLayer 里的 Path 布局盒是 0×0
    /// （它们画在布局盒之外），10-24 实测连填色箭头都不进 FindElementsInHostCoordinates。
    /// 选中以后两端各长一枚把手、线本身加粗到 2.5px；清空后两样都收回去。
    /// </summary>
    private void SelfTestEdgeCanBeSelectedByItsWire(List<string> problems)
    {
        var pair = BoardNotes().Take(2).ToList();

        if (pair.Count < 2)
        {
            problems.Add("自检选线: 板上不足两格，这一发不适用");

            return;
        }

        var before = _document.ToJson();

        BeginGesture("自检选线");

        var edge = _document.AddEdge(pair[0].Id, pair[1].Id);

        ArrangeAll();

        // 用屏幕上真正画出来的那条线自己的几何取中点，不另算一遍账。
        var wire = LinkLayer.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>()
            .FirstOrDefault(path => path.Data is PathGeometry { Figures.Count: 1 });

        if (wire?.Data is PathGeometry geometry
            && geometry.Figures[0].Segments.OfType<BezierSegment>().FirstOrDefault() is { } bezier)
        {
            var start = geometry.Figures[0].StartPoint;
            var midX = (start.X + (3 * bezier.Point1.X) + (3 * bezier.Point2.X) + bezier.Point3.X) / 8;
            var midY = (start.Y + (3 * bezier.Point1.Y) + (3 * bezier.Point2.Y) + bezier.Point3.Y) / 8;
            var onWire = EdgeUnder(midX, midY);

            // 负对照取"这根线外接盒再向外 40px"：曲线自己可能拐回来，单纯上下偏 18px 未必真的离线远。
            var farX = new[] { start.X, bezier.Point1.X, bezier.Point2.X, bezier.Point3.X }.Max() + 40;
            var farY = new[] { start.Y, bezier.Point1.Y, bezier.Point2.Y, bezier.Point3.Y }.Max() + 40;
            var offWire = EdgeUnder(farX, farY);

            if (onWire != edge.Id)
            {
                problems.Add($"自检选线: 线中点 ({midX:0.#},{midY:0.#}) 判成了 {onWire ?? "谁都不是"}，应选中这根线");
            }

            // 带内也要够宽：离曲线 4px 的落点一定在 6px 带里（到曲线的距离不可能大于竖向偏移），
            // 不然手一抖就点空——这一发同时把"命中带的宽度"钉住。
            if (EdgeUnder(midX, midY - 4) != edge.Id)
            {
                problems.Add($"自检选线: 中线旁 4px 的落点没判成线——命中带窄得像没有");
            }

            if (offWire == edge.Id)
            {
                problems.Add($"自检选线: 线外接盒外 40px 的落点 ({farX:0.#},{farY:0.#}) 也算这根线——命中判定跑偏了");
            }
        }
        else
        {
            problems.Add("自检选线: 读不到线的几何，没法验线体点不点得中");
        }

        SelectEdge(edge.Id);

        var handles = LinkHandleLayer.Children.OfType<Ellipse>()
            .Count(ellipse => Math.Abs(ellipse.Width - (2 * EdgeGeometry.HandleHitRadiusPx)) < 0.01);

        if (handles != 2)
        {
            problems.Add($"自检选线: 选中一根线以后读到 {handles} 枚端点把手，要两端各一枚");
        }

        var thick = LinkLayer.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>()
            .Any(path => !path.IsHitTestVisible && Math.Abs(path.StrokeThickness - EdgeGeometry.SelectedStrokePx) < 0.01);

        if (!thick)
        {
            problems.Add($"自检选线: 选中的线没有加粗到 {EdgeGeometry.SelectedStrokePx:0.#}px——屏幕上认不出哪根被选中");
        }

        ClearSelection();

        handles = LinkHandleLayer.Children.OfType<Ellipse>()
            .Count(ellipse => Math.Abs(ellipse.Width - (2 * EdgeGeometry.HandleHitRadiusPx)) < 0.01);

        if (handles != 0)
        {
            problems.Add($"自检选线: 清空选中以后还剩 {handles} 枚端点把手");
        }

        EndGesture();

        Undo();
        Rebuild();

        if (_document.ToJson() != before)
        {
            problems.Add("自检选线: 撤销一发以后文档没回到原样");
        }
        else
        {
            Diagnostics.Write(
                "selftest 选线: 线中点在 6px 命中带内判给这根线、远处不误判；选中后两端各一枚把手、线加粗到 2.5px；清空全收回、撤销回原样");
        }
    }

    /// <summary>
    /// 指针落点属于哪根线：6px 以内的最近一根，没有就 null。和绘制读同一套边点与控制点，
    /// 所以"屏幕上看见的那条线"与"点得中的那条线"是同一个东西。
    /// </summary>
    private string? EdgeUnder(double screenX, double screenY)
    {
        string? best = null;
        var bestDistance = EdgeGeometry.HitReachPx;

        foreach (var edge in _document.Edges)
        {
            if (_document.Find(edge.FromNode) is not { } from
                || _document.Find(edge.ToNode) is not { } to
                || !_realized.ContainsKey(from.Id)
                || !_realized.ContainsKey(to.Id))
            {
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

            var distance = EdgeGeometry.DistanceToCurve(screenX, screenY, x1, y1, c1x, c1y, c2x, c2y, x2, y2);

            // <=：同样近时后画的（在上层的）赢，和眼睛看到的一致。
            if (distance <= bestDistance)
            {
                best = edge.Id;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void PaintLinks()
    {
        LinkLayer.Children.Clear();
        LinkHandleLayer.Children.Clear();

        var dark = NoteStyling.IsDark;
        var wire = new SolidColorBrush(NoteStyling.ToColor(EdgeGeometry.WireColour(dark)));
        var accent = new SolidColorBrush(NoteStyling.ToColor(EdgeGeometry.SelectedColour(dark)));
        var danger = new SolidColorBrush(NoteStyling.ToColor(EdgeGeometry.DeleteColour(dark)));

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

            var selected = _selectedEdgeId == edge.Id;
            var on = selected || _selection.Contains(from.Id) || _selection.Contains(to.Id);
            var deleting = (draggingFrom || draggingTo) && _edgeDeleteArmed;
            var brush = deleting ? danger : on ? accent : wire;

            // 命中线也画一份自己的几何：Geometry 是依赖对象，不要两枚 Path 共用一个实例。
            PathGeometry Wire()
                => new()
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
                };

            LinkLayer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = brush,
                StrokeThickness = selected ? EdgeGeometry.SelectedStrokePx : EdgeGeometry.StrokePx,
                StrokeDashArray = draggingFrom || draggingTo ? [4, 3] : null,
                Opacity = draggingFrom || draggingTo ? (deleting ? 1 : 0.72) : 1,
                IsHitTestVisible = false,
                Data = Wire(),
            });

            var (tipX, tipY, leftX, leftY, rightX, rightY) = EdgeGeometry.ArrowHead(x2, y2, c2x, c2y);

            LinkLayer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Fill = brush,
                IsHitTestVisible = false,
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

    /// <summary>线被别的选中物顶掉：收起两端的把手，线回到普通粗细。</summary>
    private void ClearEdgeSelection()
    {
        if (_selectedEdgeId is null)
        {
            return;
        }

        _selectedEdgeId = null;
        PaintLinks();
    }

    /// <summary>
    /// 点线体：这根线成为选中物，格子全部退出（选择是单一的，不混着放）。
    /// 选中之后两端各长出一枚把手，拖哪一端就改哪一端——不再要求先点两端的便签。
    /// </summary>
    private void SelectEdge(string edgeId)
    {
        if (_document.Edges.All(edge => edge.Id != edgeId))
        {
            return;
        }

        ClearSelection();
        _selectedEdgeId = edgeId;

        ProjectSelection();
        PaintLinks();

        Say("已选中连线 · 拖动两端圆点可调整连接面或归属，按 Esc 取消");
    }

    /// <summary>
    /// 端点的把手：那一端的便签被选中、或这根线自己被选中时出现。
    /// 同一个规矩的另一半：别的线的另一端没有把手，板上就不会是一片抓不住的小圆点。
    /// 命中圈 26px（WCAG 2.5.8），可见点 4.5px。
    /// </summary>
    private void AddHandle(double screenX, double screenY, string edgeId, string end, string nodeId, bool dragging = false)
    {
        if (!_selection.Contains(nodeId) && _selectedEdgeId != edgeId)
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
            Say("连线至少需要 2 个便签（按叠放次序连接为链）");

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
            ? $"已移除 {edges.Count} 条连线 · 可按 Ctrl＋Z 撤销"
            : $"已创建 {pairs.Count} 条连线 · 选中后可拖动端点");
    }
}
