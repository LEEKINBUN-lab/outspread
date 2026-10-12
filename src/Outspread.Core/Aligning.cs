namespace Outspread.Core;

/// <summary>
/// Arranging a set of notes: align, distribute, stack.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the prototype's <c>cmdAlign</c>/<c>cmdDistribute</c>/<c>cmdStack</c>, including the
/// one place where the shipped products had to be corrected rather than copied: both Excalidraw's
/// <c>space:"between"</c> and tldraw's <c>distributeShapes</c> divide up <b>the current bounding
/// box</b>, and that is wrong the moment the box is smaller than the notes inside it. Aligning four
/// notes left then distributing them measured a gap of −216px and crushed them into one column, so
/// the spread gets a floor (<see cref="MinGapPx"/>) and is allowed to widen the box — the start stays
/// pinned at the old left/top edge, because "this row is still here" is what the user just did.
/// </para>
/// <para>
/// Nothing here returns a silent no-op: a command that cannot run says which of its preconditions
/// failed, so the surface can say it out loud (invariant ④ of docs/interaction-machine.md).
/// </para>
/// </remarks>
public static class Aligning
{
    /// <summary>The gap floor, and the stack step. One number for both, as in the prototype.</summary>
    public const double MinGapPx = 24;

    /// <summary>One note's geometry, as far as arranging cares.</summary>
    public readonly record struct Placed(string Id, double X, double Y, double Width, double Height);

    /// <summary>Where to put one note. Sizes never change here.</summary>
    public readonly record struct Move(string Id, double X, double Y);

    /// <param name="Ok">False when the counts explain the refusal.</param>
    /// <param name="Needed">The precondition in notes, so the message can say "要至少 3 格（现在 2 格）".</param>
    /// <param name="Have">How many were offered.</param>
    public readonly record struct Outcome(bool Ok, IReadOnlyList<Move> Moves, int Needed, int Have)
    {
        public bool TooFew => !Ok && Have < Needed;
    }

    public enum Side
    {
        Left,
        HCenter,
        Right,
        Top,
        VCenter,
        Bottom,
    }

    public static Outcome Align(IReadOnlyList<Placed> items, Side side)
    {
        var bounds = Bounds(items);

        var moves = items.Select(item => side switch
        {
            Side.Left => new Move(item.Id, bounds.X, item.Y),
            Side.HCenter => new Move(item.Id, bounds.X + ((bounds.Width - item.Width) / 2), item.Y),
            Side.Right => new Move(item.Id, bounds.Right - item.Width, item.Y),
            Side.Top => new Move(item.Id, item.X, bounds.Y),
            Side.VCenter => new Move(item.Id, item.X, bounds.Y + ((bounds.Height - item.Height) / 2)),
            _ => new Move(item.Id, item.X, bounds.Bottom - item.Height),
        }).ToList();

        return new Outcome(items.Count >= 2, moves, Needed: 2, Have: items.Count);
    }

    /// <summary>
    /// Equal gaps between neighbours, first and last pinned, box widened rather than crushed if it
    /// has to be. Two notes cannot be "evenly spread" — the gap is whatever it is — hence ≥3.
    /// </summary>
    public static Outcome Distribute(IReadOnlyList<Placed> items, bool horizontal)
    {
        if (items.Count < 3)
        {
            return new Outcome(false, [], Needed: 3, Have: items.Count);
        }

        var bounds = Bounds(items);
        var start = horizontal ? bounds.X : bounds.Y;
        var span = horizontal ? bounds.Width : bounds.Height;
        var sum = items.Sum(item => horizontal ? item.Width : item.Height);

        var usable = Math.Max(span, sum + (MinGapPx * (items.Count - 1)));
        var step = (usable - sum) / (items.Count - 1);

        var ordered = items.OrderBy(item => horizontal ? item.X : item.Y).ToList();
        var cursor = start;
        var moves = new List<Move>(ordered.Count);

        foreach (var item in ordered)
        {
            moves.Add(horizontal
                ? new Move(item.Id, cursor, item.Y)
                : new Move(item.Id, item.X, cursor));

            cursor += (horizontal ? item.Width : item.Height) + step;
        }

        return new Outcome(true, moves, Needed: 3, Have: items.Count);
    }

    /// <summary>
    /// A pile: a top-left staircase that leaves <see cref="MinGapPx"/> of each note showing.
    /// </summary>
    /// <remarks>
    /// NOT FOUND in any shipped product — tldraw's <c>stackShapes</c> lays a row, not a pile — so
    /// 24 is our own value, chosen as "enough to see the edge of the one behind".
    /// </remarks>
    public static Outcome Stack(IReadOnlyList<Placed> items)
    {
        if (items.Count < 2)
        {
            return new Outcome(false, [], Needed: 2, Have: items.Count);
        }

        var bounds = Bounds(items);

        // The order is z-order as given: whoever wrote the list owns what ends up on top.
        var moves = items.Select((item, index) => new Move(
            item.Id,
            bounds.X + (index * MinGapPx),
            bounds.Y + (index * MinGapPx))).ToList();

        return new Outcome(true, moves, Needed: 2, Have: items.Count);
    }

    /// <summary>
    /// Re-flow a set into one row per key — colour or first tag — rows stacked top to bottom in the
    /// order each key first appears (which is z-order, so the pile reads the way the board did).
    /// </summary>
    /// <remarks>
    /// The prototype clusters the <b>whole board</b>, not the selection, and so does this: the
    /// command's promise is "put like with like", which is meaningless over a subset. Row and
    /// column gaps are 48 and <see cref="MinGapPx"/> — the same floor the other commands use.
    /// </remarks>
    public static Outcome Cluster(IReadOnlyList<Placed> items, IReadOnlyList<string> keys)
    {
        if (items.Count < 2 || keys.Count != items.Count)
        {
            return new Outcome(false, [], Needed: 2, Have: items.Count);
        }

        var clusters = new List<(string Key, List<Placed> Items)>();

        for (var i = 0; i < items.Count; i++)
        {
            var key = keys[i];
            var found = clusters.FindIndex(cluster => cluster.Key == key);

            if (found < 0)
            {
                clusters.Add((key, [items[i]]));
            }
            else
            {
                clusters[found].Items.Add(items[i]);
            }
        }

        const double origin = 40;
        const double rowGap = 48;

        var moves = new List<Move>(items.Count);
        var y = origin;

        foreach (var (_, members) in clusters)
        {
            var x = origin;
            var tallest = members.Max(member => member.Height);

            foreach (var member in members)
            {
                moves.Add(new Move(member.Id, x, y));
                x += member.Width + MinGapPx;
            }

            y += tallest + rowGap;
        }

        return new Outcome(true, moves, Needed: 2, Have: items.Count);
    }

    public static Box Bounds(IReadOnlyList<Placed> items)
    {
        if (items.Count == 0)
        {
            return new Box(0, 0, 0, 0);
        }

        var left = items.Min(item => item.X);
        var top = items.Min(item => item.Y);
        var right = items.Max(item => item.X + item.Width);
        var bottom = items.Max(item => item.Y + item.Height);

        return new Box(left, top, right - left, bottom - top);
    }
}
