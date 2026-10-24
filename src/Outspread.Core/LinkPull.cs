namespace Outspread.Core;

/// <summary>
/// 拉线那一发的吸附几何：指针这点该吸到哪一枚的哪条边中心。纯算术——界面只负责把这里给的点画出来，
/// 所以"吸得准不准"能在这一层量，不用开窗口。
/// </summary>
/// <remarks>
/// 10-24 他把连线原型交回来时点了四格要改：箭头方向、按钮画错、<b>吸附效果</b>、<b>"线段是死板的直线"</b>。
/// 后两格在实物里也成立：拉线中途那根 ghost 是一根两点直线（<c>Polyline</c>），松手之后才变成贝塞尔——
/// 也就是"拉的时候不像、松手才像"。这一枚补的是吸附那半：四条边的中心要吸得住，
/// 而且半径要按缩放归一，否则放大以后同样移动 44 个屏幕像素、世界坐标里只走了 25 像素，就"粘不住"。
/// </remarks>
public static class LinkPull
{
    /// <summary>屏幕上给多远的吸附距离（世界半径＝这个数除以缩放）。</summary>
    public const double ScreenReachPx = 44;

    /// <summary>吸附半径按缩放归一：缩放拿不到（≤0）时退回屏幕值，不除零。</summary>
    public static double Reach(double zoom) => zoom > 0 ? ScreenReachPx / zoom : ScreenReachPx;

    /// <summary>四条边的中心里离这点最近的那一条。平方欧氏距离——与 <see cref="CanvasEdge.NearestSide"/> 同一条尺。</summary>
    public static string SideNearest(Grouping.Rect note, double px, double py)
    {
        var best = CanvasEdge.Right;
        var bestDistance = double.MaxValue;

        foreach (var side in new[] { CanvasEdge.Top, CanvasEdge.Bottom, CanvasEdge.Left, CanvasEdge.Right })
        {
            var (x, y) = AnchorOf(note, side);
            var dx = x - px;
            var dy = y - py;
            var distance = (dx * dx) + (dy * dy);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = side;
            }
        }

        return best;
    }

    /// <summary>某条边的中心（世界坐标）。</summary>
    public static (double X, double Y) AnchorOf(Grouping.Rect note, string side)
    {
        var vector = CanvasEdge.VectorOf(side);

        return (note.X + (note.Width / 2) + (vector[0] * (note.Width / 2)),
            note.Y + (note.Height / 2) + (vector[1] * (note.Height / 2)));
    }

    /// <summary>
    /// 指针这点吸谁：落在谁里面就一定吸它；格子外要最近的锚点不超过 <paramref name="reach"/> 才算。
    /// 起点自己永远不参与（不能跟自己连）；一个都不合条件返回 null——线继续跟着指针走，不硬吸。
    /// </summary>
    public static (string NodeId, string Side, double X, double Y)? Nearest(
        IReadOnlyList<Grouping.Rect> notes,
        double px,
        double py,
        string? excludeId,
        double reach)
    {
        (string NodeId, string Side, double X, double Y)? best = null;
        var bestScore = double.MaxValue;

        foreach (var note in notes)
        {
            if (note.Id == excludeId || note.Width <= 0 || note.Height <= 0)
            {
                continue;
            }

            var side = SideNearest(note, px, py);
            var (x, y) = AnchorOf(note, side);
            var dx = x - px;
            var dy = y - py;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            var inside = px >= note.X && px <= note.Right && py >= note.Y && py <= note.Bottom;

            if (!inside && distance > reach)
            {
                continue;
            }

            // "落在格子里"整体排在"够近"前面：指针压在谁身上就吸谁，哪怕它在正中心、离四条边都远。
            var score = (inside ? 0 : InsideBand) + distance;

            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            best = (note.Id, side, x, y);
        }

        return best;
    }

    /// <summary>给"落在格子里"那一档让路用的常数：比任何屏幕半径都大，所以只在同为 inside 时比锚点远近。</summary>
    private const double InsideBand = 1_000_000;
}
