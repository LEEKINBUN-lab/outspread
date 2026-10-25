using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Outspread.Core;
using Windows.Foundation;

namespace Outspread.Board;

/// <summary>
/// 拉线：双击一格的边缘把那根线的起点钉在那一面上，指针带着自由端走，
/// 下一发按在另一格（或另一格的边缘）上就接上，按在空白上或 Esc 就是没画过。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是"双击边缘"而不是"从边缘拖出去"：边缘那一发的按下已经归了缩放（压边＝改那条边，
/// 6 屏幕像素的命中带），同一发手势不能有两个主人。双击是那块面上唯一还没被占用的动作——
/// 双击空白＝新建、双击格内＝编辑，都在别的地方，三条互不打架。
/// </para>
/// <para>
/// 起点带"哪一面"一起钉住（w/e/n/s），落点取目标格离起点最近的那一面：拉线的人心里想的是
/// "从这格的右边出去，进那格的左边"，把这两半都存进 <c>fromSide/toSide</c> 才落得回他想要的那条路。
/// </para>
/// <para>
/// 未落定之前不落盘：整条路上文档一个字节都不动，所以 Esc 是纯放弃（不变量 5），
/// 撤销栈里也不会多出"我拉了一下没接上"的空格子（不变量 2）。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private (string FromId, string Side)? _pulling;

    /// <summary>这一次拉线的"选中"是双击替它开出来的：取消时要一起退回去。
    /// 从菜单/键位起线时选中是用户自己的，取消不该把它清掉。</summary>
    private bool _pullOwnsSelection;

    private Microsoft.UI.Xaml.Shapes.Path? _pullWire;

    private Microsoft.UI.Xaml.Shapes.Path? _pullHead;

    private Ellipse? _pullDot;

    /// <summary>指针这点正吸着谁（null＝还没吸住任何东西）。</summary>
    private (string NodeId, string Side, double X, double Y)? _pullSnap;

    /// <summary>正在拉线＝这一发按键/按下归这条线，不归板子。</summary>
    private bool Pulling => _pulling is not null;

    /// <summary>从这一格的这一面起头。</summary>
    private void StartPull(string id, string side)
    {
        if (NodeOf(id) is not { } node)
        {
            _pullOwnsSelection = false;

            return;
        }

        ClearPullGhost();

        _pulling = (id, SpecSide(side));

        var brush = new SolidColorBrush(NoteStyling.ToColor(SelectionChrome.Ring(NoteStyling.IsDark)));

        // 拉线中途那根就得是成品那根：同一套三次贝塞尔、同一个沿切线的箭头。
        // 原来这里是一根两点直线（Polyline），于是"拉的时候不像、松手才像"——他退回原型时
        // 点的"线段是死板的直线"就是这一格，实物上同样成立。
        _pullWire = new Microsoft.UI.Xaml.Shapes.Path
        {
            Stroke = brush,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
        };

        _pullHead = new Microsoft.UI.Xaml.Shapes.Path { Fill = brush, IsHitTestVisible = false };

        _pullDot = new Ellipse
        {
            Width = 12,
            Height = 12,
            Stroke = brush,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(NoteStyling.ToColor(SelectionChrome.HandleFace(NoteStyling.IsDark))),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        GuideLayer.Children.Add(_pullWire);
        GuideLayer.Children.Add(_pullHead);
        GuideLayer.Children.Add(_pullDot);


        Say("拉线中：点另一格接上，点空白或 Esc 取消");
    }

    /// <summary>菜单/键位那一条路：从选中那一格靠视野中心的那一面起头。</summary>
    private void StartPullFromSelection()
    {
        if (_pulling is not null)
        {
            CancelPull();

            return;
        }

        var members = SelectedNotes();

        if (members.Count != 1)
        {
            Say($"拉线要先选中一格，现在选了 {members.Count} 格");

            return;
        }

        // 菜单那条路：选中是用户自己的，取消时留着。
        _pullOwnsSelection = false;
        StartPull(members[0].Id, SideTowardViewCentre(members[0]));
    }

    /// <summary>
    /// 指针在走：自由端跟着走，并且<b>吸到四条边中心最近的那一条</b>（半径按缩放归一，见 LinkPull）。
    /// 曲线与箭头用 EdgeGeometry 那一套——和松手之后的成品同一把尺，拉的时候就能看出它会长成什么样。
    /// </summary>
    private void TrackPull(Point screen)
    {
        if (_pulling is not { } pull || _pullWire is null || _pullHead is null || NodeOf(pull.FromId) is not { } from)
        {
            return;
        }

        var wx = _view.ToWorldX(screen.X);
        var wy = _view.ToWorldY(screen.Y);

        _pullSnap = LinkPull.Nearest(
            BoardNotes().ConvertAll(RectOf),
            wx,
            wy,
            pull.FromId,
            LinkPull.Reach(_view.Zoom));

        var leave = CanvasEdge.SidePoint(from, pull.Side);
        var x1 = _view.ToScreenX(leave.X);
        var y1 = _view.ToScreenY(leave.Y);
        var x2 = _pullSnap is { } hit ? _view.ToScreenX(hit.X) : screen.X;
        var y2 = _pullSnap is { } gone ? _view.ToScreenY(gone.Y) : screen.Y;

        var v1 = CanvasEdge.VectorOf(pull.Side);
        var v2 = _pullSnap is { } aimed ? CanvasEdge.VectorOf(aimed.Side) : [-v1[0], -v1[1]];

        var (c1x, c1y, c2x, c2y) = EdgeGeometry.Controls(x1, y1, v1, x2, y2, v2);

        _pullWire.Data = Curve(x1, y1, c1x, c1y, c2x, c2y, x2, y2);

        var (tipX, tipY, leftX, leftY, rightX, rightY) = EdgeGeometry.ArrowHead(x2, y2, c2x, c2y);

        _pullHead.Data = Head(tipX, tipY, leftX, leftY, rightX, rightY);

        if (_pullDot is not null)
        {
            _pullDot.Visibility = _pullSnap is null ? Visibility.Collapsed : Visibility.Visible;
            Canvas.SetLeft(_pullDot, x2 - 6);
            Canvas.SetTop(_pullDot, y2 - 6);
        }
    }

    private static Microsoft.UI.Xaml.Media.PathGeometry Curve(
        double x1, double y1, double c1x, double c1y, double c2x, double c2y, double x2, double y2)
        => new()
        {
            Figures =
            {
                new PathFigure
                {
                    StartPoint = new Point(x1, y1),
                    Segments = { new BezierSegment { Point1 = new Point(c1x, c1y), Point2 = new Point(c2x, c2y), Point3 = new Point(x2, y2) } },
                },
            },
        };

    private static Microsoft.UI.Xaml.Media.PathGeometry Head(
        double tipX, double tipY, double leftX, double leftY, double rightX, double rightY)
        => new()
        {
            Figures =
            {
                new PathFigure
                {
                    StartPoint = new Point(tipX, tipY),
                    IsClosed = true,
                    Segments = { new LineSegment { Point = new Point(leftX, leftY) }, new LineSegment { Point = new Point(rightX, rightY) } },
                },
            },
        };

    /// <summary>
    /// 落定：命中另一格就画一条边（一个事务），命中空白或还是原来那一格就放弃。
    /// </summary>
    /// <returns>是否消费了这一发按下。</returns>
    private bool FinishPull(Point screen)
    {
        if (_pulling is not { } pull)
        {
            return false;
        }

        // 吸住了就连那一枚（指针在空白上但边已经吸住，也算他想连这条）；没吸住才按"点在谁身上"。
        var target = (_pullSnap is { } snap ? NodeOf(snap.NodeId) : null) ?? NodeAt(screen);

        if (target is null || target.Id == pull.FromId)
        {
            CancelPull();
            Say("没有接到任何一格，这根线没画");

            return true;
        }

        if (NodeOf(pull.FromId) is not { } from)
        {
            CancelPull();

            return true;
        }

        var leave = CanvasEdge.SidePoint(from, pull.Side);

        // 落点那面按指针挑，不按起点挑：拉线的人心里指的就是他点的那一下附近那条边。
        var toSide = _pullSnap is { } aimedSide && aimedSide.NodeId == target.Id
            ? aimedSide.Side
            : CanvasEdge.NearestSide(target, _view.ToWorldX(screen.X), _view.ToWorldY(screen.Y));

        CancelPull();

        BeginGesture("连了一根线");

        var edge = _document.AddEdge(pull.FromId, target.Id, pull.Side, toSide);

        EndGesture();

        Rebuild();
        SaveSoon();

        Say("已连线");

        return true;
    }

    private void CancelPull()
    {
        _pulling = null;

        ClearPullGhost();

        // 双击起线时选中是替这一发开的：取消（右键/点空白/Esc）就退回去；接上那一路
        // 由 Rebuild 清选中，同样不残留。从菜单起线时不动用户原有的选中。
        if (_pullOwnsSelection)
        {
            _pullOwnsSelection = false;
            ClearSelection();
            ProjectSelection();
        }
    }

    private void ClearPullGhost()
    {
        _pullSnap = null;

        foreach (var ghost in new UIElement?[] { _pullWire, _pullHead, _pullDot })
        {
            if (ghost is not null)
            {
                GuideLayer.Children.Remove(ghost);
            }
        }

        _pullWire = null;
        _pullHead = null;
        _pullDot = null;
    }

    /// <summary>最靠视野中心的那一面——从那儿起头，线一出去就在看得见的地方。</summary>
    private string SideTowardViewCentre(CanvasNode node)
    {
        var centreX = BoardArea.ActualWidth / 2;
        var centreY = BoardArea.ActualHeight / 2;

        var candidates = new (string Side, double Away)[]
        {
            (CanvasEdge.Left, Math.Abs(centreX - _view.ToScreenX(node.X))),
            (CanvasEdge.Right, Math.Abs(centreX - _view.ToScreenX(node.Right))),
            (CanvasEdge.Top, Math.Abs(centreY - _view.ToScreenY(node.Y))),
            (CanvasEdge.Bottom, Math.Abs(centreY - _view.ToScreenY(node.Bottom))),
        };

        return candidates.MinBy(candidate => candidate.Away).Side;
    }

    /// <summary>边缘命中带回的是 w/e/n/s（缩放那一套的坐标轴），文档里写的是格式那四个名字。</summary>
    /// <summary>
    /// 两套写法都收：单字母（w/e/n/s，改尺寸那一条与老调用）和 spec 词（left/right/top/bottom，
    /// 双击起线走 EdgeNear）。10-24 只收单字母、其余一律落到 Bottom，于是"在哪双击都从下边起线"。
    /// </summary>
    private static string SpecSide(string side) => side switch
    {
        "w" or CanvasEdge.Left => CanvasEdge.Left,
        "e" or CanvasEdge.Right => CanvasEdge.Right,
        "n" or CanvasEdge.Top => CanvasEdge.Top,
        "s" or CanvasEdge.Bottom => CanvasEdge.Bottom,
        _ => CanvasEdge.Bottom,
    };

    /// <summary>
    /// 这一发落在哪一格上。叠在上面的先命中，但<b>组整体不算落点</b>：10-22 起组是一个关系，
    /// 不是一块场地——双击框内空白＝在那儿新建一格（落在框里就自动进组），拉线落到框内空白
    /// ＝没接上任何一格。"点名牌/虚线边＝选整组"是单击的事，长在 NodeVisual 的环带与
    /// OnNodePressed 里，不归这里管。早期"组让给格子里面那一枚、剩下再兜底给组"的形状会让
    /// 双击框内走进"编辑一个不能编辑的东西"，那正是他这次报的语义错位。
    /// </summary>
    private CanvasNode? NodeAt(Point screen)
    {
        var world = new Point(_view.ToWorldX(screen.X), _view.ToWorldY(screen.Y));

        for (var index = _document.Nodes.Count - 1; index >= 0; index--)
        {
            var node = _document.Nodes[index];

            if (node.Width <= 0 || node.Height <= 0)
            {
                continue;
            }

            if (world.X < node.X || world.X > node.Right || world.Y < node.Y || world.Y > node.Bottom)
            {
                continue;
            }

            if (node.Kind == CanvasNodeKind.Group)
            {
                continue;
            }

            return node;
        }

        return null;
    }
}
