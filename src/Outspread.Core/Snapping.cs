namespace Outspread.Core;

public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public bool Intersects(Box other)
        => Right >= other.X && X <= other.Right && Bottom >= other.Y && Y <= other.Bottom;
}

public readonly record struct Guide(bool Vertical, double Position, double From, double To);

public readonly record struct SnapResult(double X, double Y, double DeltaX, double DeltaY, IReadOnlyList<Guide> Guides);

/// <summary>
/// Edge and centre snapping.
/// </summary>
/// <remarks>
/// The threshold is given in world units and is produced by the caller as
/// <c>pixels / zoom</c>, so snapping stays eight screen pixels easy to hit whether the
/// board is read at 10% or at 400%. Without that division the same drag snaps at deep
/// zoom and refuses at full size, which reads as the board being inconsistent.
/// </remarks>
public static class Snapping
{
    private static readonly double[] SourceRatiosX = [0d, 0.5d, 1d];

    private static readonly double[] SourceRatiosY = [0d, 0.5d, 1d];

    public static SnapResult Snap(
        Box moving,
        IReadOnlyList<Box> others,
        Box? viewport,
        double threshold)
    {
        if (threshold <= 0 || others.Count == 0 && viewport is null)
        {
            return new SnapResult(moving.X, moving.Y, 0, 0, []);
        }

        var targetsX = CollectTargetsX(others, viewport);
        var targetsY = CollectTargetsY(others, viewport);

        var (deltaX, guidesX) = BestAxis(
            SourceRatiosX.Select(ratio => moving.X + (moving.Width * ratio)).ToList(),
            targetsX,
            threshold);

        var (deltaY, guidesY) = BestAxis(
            SourceRatiosY.Select(ratio => moving.Y + (moving.Height * ratio)).ToList(),
            targetsY,
            threshold);

        var guides = new List<Guide>(guidesX.Count + guidesY.Count);

        guides.AddRange(guidesX.Select(position => new Guide(true, position, moving.Y + deltaY, moving.Bottom + deltaY)));
        guides.AddRange(guidesY.Select(position => new Guide(false, position, moving.X + deltaX, moving.Right + deltaX)));

        return new SnapResult(moving.X + deltaX, moving.Y + deltaY, deltaX, deltaY, guides);
    }

    private static (double Delta, List<double> HitTargets) BestAxis(
        IReadOnlyList<double> sources,
        IReadOnlyList<double> targets,
        double threshold)
    {
        double best = 0;
        var bestDistance = threshold + 1;
        var hits = new List<double>();

        foreach (var source in sources)
        {
            foreach (var target in targets)
            {
                var delta = target - source;

                if (Math.Abs(delta) > threshold)
                {
                    continue;
                }

                var distance = Math.Abs(delta);

                if (distance < bestDistance - double.Epsilon)
                {
                    bestDistance = distance;
                    best = delta;
                    hits.Clear();
                    hits.Add(target);
                }
                else if (Math.Abs(distance - bestDistance) <= double.Epsilon && !hits.Contains(target))
                {
                    hits.Add(target);
                }
            }
        }

        return (best, hits);
    }

    private static List<double> CollectTargetsX(IReadOnlyList<Box> others, Box? viewport)
    {
        var targets = new List<double>(capacity: (others.Count * 3) + 3);

        foreach (var box in others)
        {
            targets.Add(box.X);
            targets.Add(box.CenterX);
            targets.Add(box.Right);
        }

        if (viewport is { } view)
        {
            targets.Add(view.X);
            targets.Add(view.CenterX);
            targets.Add(view.Right);
        }

        return targets;
    }

    private static List<double> CollectTargetsY(IReadOnlyList<Box> others, Box? viewport)
    {
        var targets = new List<double>(capacity: (others.Count * 3) + 3);

        foreach (var box in others)
        {
            targets.Add(box.Y);
            targets.Add(box.CenterY);
            targets.Add(box.Bottom);
        }

        if (viewport is { } view)
        {
            targets.Add(view.Y);
            targets.Add(view.CenterY);
            targets.Add(view.Bottom);
        }

        return targets;
    }
}
