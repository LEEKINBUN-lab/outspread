using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>
/// One connection between two nodes, as the file writes it.
/// </summary>
/// <remarks>
/// The raw object is kept because an edge may carry fields this program does not model (a colour, a
/// label, an arrow shape, something a newer agent invented) and dropping them on the next save would
/// turn "we preserve what we do not understand" into a lie.
/// </remarks>
/// <param name="Id">The edge's id.</param>
/// <param name="FromNode">The node the line leaves from.</param>
/// <param name="ToNode">The node it arrives at.</param>
/// <param name="FromSide">The side it leaves, or null to face the other card automatically.</param>
/// <param name="ToSide">The side it arrives at, or null for the same automatic.</param>
/// <param name="Raw">The object as parsed, for writing back.</param>
public sealed record CanvasEdge(
    string Id,
    string FromNode,
    string ToNode,
    string? FromSide,
    string? ToSide,
    JsonObject Raw)
{
    public const string FromEnd = "from";

    public const string ToEnd = "to";

    public const string Top = "top";
    public const string Bottom = "bottom";
    public const string Left = "left";
    public const string Right = "right";

    /// <summary>Unit vector pointing out of a side — the control arm runs along it.</summary>
    public static double[] VectorOf(string? side) => side switch
    {
        Top => [0, -1],
        Bottom => [0, 1],
        Left => [-1, 0],
        Right => [1, 0],
        _ => [0, -1],
    };

    /// <summary>The midpoint of one side, in world units.</summary>
    public static (double X, double Y) SidePoint(CanvasNode node, string? side)
    {
        var vector = VectorOf(side);

        return (node.X + (node.Width / 2) + (vector[0] * (node.Width / 2)),
            node.Y + (node.Height / 2) + (vector[1] * (node.Height / 2)));
    }

    /// <summary>
    /// The side of this card nearest a world point — what an endpoint is dropped onto. Distance is
    /// measured to each side's midpoint, so a card that is much wider than tall can be entered from
    /// the long side even when the pointer is nearly level with its centre.
    /// </summary>
    public static string NearestSide(CanvasNode node, double worldX, double worldY)
        // 尺只有一把：拉线态（LinkPull）与这里必须报同一条边，否则"看着吸住了、松手换面"。
        => LinkPull.SideNearest(new Grouping.Rect(node.Id, node.X, node.Y, node.Width, node.Height), worldX, worldY);

    /// <summary>The side that faces the other card: the one whose axis the other centre is furthest
    /// along. Used when the file did not write a side (and by the connect command, which records what
    /// this would have chosen so the picture stays stable once made).</summary>
    public static (string From, string To) AutoSides(CanvasNode from, CanvasNode to)
    {
        var dx = (to.X + (to.Width / 2)) - (from.X + (from.Width / 2));
        var dy = (to.Y + (to.Height / 2)) - (from.Y + (from.Height / 2));

        return Math.Abs(dx) >= Math.Abs(dy)
            ? (dx >= 0 ? Right : Left, dx >= 0 ? Left : Right)
            : (dy >= 0 ? Bottom : Top, dy >= 0 ? Top : Bottom);
    }
}

/// <summary>
/// The curve an edge is drawn as, and the arrowhead on its end. Every number here was read off
/// <c>design/feel.html</c>'s <c>paintLinks()</c> on 2026-10-11; the app draws the same shape.
/// </summary>
public static class EdgeGeometry
{
    /// <summary>The wire's thickness in screen pixels (prototype: <c>stroke-width: 1.5</c>). It does
    /// not scale with zoom — a hairline that thickens to 6px at 400% is a bar, not a line.</summary>
    public const double StrokePx = 1.5;

    /// <summary>Control-arm length: <c>max(28, min(180, distance × 0.45))</c> in screen pixels.</summary>
    public static double ControlArm(double distancePx) => Math.Max(28, Math.Min(180, distancePx * 0.45));

    /// <summary>Arrowhead: 9px along the tangent, 4.5px to each side. Measured from the tip back.</summary>
    public const double HeadLengthPx = 9;

    public const double HeadHalfWidthPx = 4.5;

    /// <summary>The wire's two control points, given both ends and their outward vectors.</summary>
    public static (double C1X, double C1Y, double C2X, double C2Y) Controls(
        double x1, double y1, double[] fromVector, double x2, double y2, double[] toVector)
    {
        var arm = ControlArm(Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1))));

        return (x1 + (fromVector[0] * arm), y1 + (fromVector[1] * arm),
            x2 + (toVector[0] * arm), y2 + (toVector[1] * arm));
    }

    /// <summary>
    /// The arrowhead's three points, aimed along the curve's <b>tangent</b> at the end
    /// (<c>c2 → p2</c>) rather than the straight line between the cards — a curve that arrives from
    /// the side would otherwise have its head sticking out at an angle nobody drew.
    /// </summary>
    public static (double TipX, double TipY, double LeftX, double LeftY, double RightX, double RightY) ArrowHead(
        double tipX, double tipY, double controlX, double controlY, double scale = 1)
    {
        var tx = tipX - controlX;
        var ty = tipY - controlY;
        var length = Math.Sqrt((tx * tx) + (ty * ty));

        if (length <= 0)
        {
            return (tipX, tipY, tipX, tipY, tipX, tipY);
        }

        var ux = tx / length;
        var uy = ty / length;
        var head = HeadLengthPx * scale;
        var half = HeadHalfWidthPx * scale;

        var baseX = tipX - (ux * head);
        var baseY = tipY - (uy * head);

        return (tipX, tipY, baseX - (uy * half), baseY + (ux * half), baseX + (uy * half), baseY - (ux * half));
    }

    /// <summary>Prototype <c>--link</c>: <c>rgb(144 144 144)</c> light, <c>rgb(150 157 168)</c> dark.</summary>
    public static Rgb WireColour(bool dark) => dark ? new Rgb(150, 157, 168) : new Rgb(144, 144, 144);

    /// <summary>Prototype <c>.edge.on</c>: an edge touching a selected card goes to the accent.</summary>
    public static Rgb SelectedColour(bool dark) => SelectionChrome.Ring(dark);

    /// <summary>The invisible hit ring on an endpoint is 13px in radius (26 across) so it clears
    /// WCAG 2.5.8's 24px door; the visible dot is 4.5px.</summary>
    public const double HandleHitRadiusPx = 13;

    public const double HandleSeenRadiusPx = 4.5;
}
