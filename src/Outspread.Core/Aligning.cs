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

        // 已经齐在同一条线上时，"再对齐一次"不该把所有格子摞到同一点上（会完全盖住）：
        // 退成堆叠的错位（10-25 他：同高选左/右对齐要像堆叠，别直接重合）。
        if (items.Count >= 2 && FlushOnAxis(items, side))
        {
            return Stack(items);
        }

        var moves = items.Select(item => side switch
        {
            Side.Left => new Move(item.Id, bounds.X, item.Y),
            Side.HCenter => new Move(item.Id, bounds.X + ((bounds.Width - item.Width) / 2), item.Y),
            Side.Right => new Move(item.Id, bounds.Right - item.Width, item.Y),
            Side.Top => new Move(item.Id, item.X, bounds.Y),
            Side.VCenter => new Move(item.Id, item.X, bounds.Y + ((bounds.Height - item.Height) / 2)),
            _ => new Move(item.Id, item.X, bounds.Bottom - item.Height),
        }).ToList();

        // 不齐的格子也可能在"另一轴"上本来就几乎同一条线：同高两枚、x 不同，左对齐后
        // 就被搬到同一点上了。量的是**结果**的重合，不是"两条线贴没贴"——只要任何一对
        // 压住对方 80% 以上，就按堆叠错开（10-25 他：重合度超过 80% 的就要自动规避）。
        if (items.Count >= 2 && HeavilyOverlaps(items, moves))
        {
            return Stack(items);
        }

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

    /// <summary>一对格子重合到"小的那一格被盖住八成以上"就算摞在一起了。</summary>
    public const double OverlapLimit = 0.8;

    private static bool HeavilyOverlaps(IReadOnlyList<Placed> items, IReadOnlyList<Move> moves)
    {
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                if (OverlapShare(items[i], moves[i], items[j], moves[j]) >= OverlapLimit - 1e-9)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>两格按给定位置落下后，交集面积占**较小那一格**的比例（0–1）。</summary>
    private static double OverlapShare(Placed a, Move ma, Placed b, Move mb)
    {
        var width = Math.Min(ma.X + a.Width, mb.X + b.Width) - Math.Max(ma.X, mb.X);
        var height = Math.Min(ma.Y + a.Height, mb.Y + b.Height) - Math.Max(ma.Y, mb.Y);

        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);

        return smaller <= 0 ? 0 : (width * height) / smaller;
    }

    /// <summary>这一轴是不是已经贴在同一条线上了（0.5px 以内算同一条）。</summary>
    private static bool FlushOnAxis(IReadOnlyList<Placed> items, Side side)
    {
        var values = items.Select(item => side switch
        {
            Side.Left => item.X,
            Side.Right => item.X + item.Width,
            Side.HCenter => item.X + (item.Width / 2),
            Side.Top => item.Y,
            Side.Bottom => item.Y + item.Height,
            _ => item.Y + (item.Height / 2),
        }).ToList();

        return values.Max() - values.Min() <= 0.5;
    }

    /// <summary>
    /// A pile: a top-left staircase that leaves <see cref="MinGapPx"/> of each note showing.
    /// </summary>
    /// <remarks>
    /// NOT FOUND in any shipped product — tldraw's <c>stackShapes</c> lays a row, not a pile — so
    /// 24 is our own value, chosen as "enough to see the edge of the one behind". 大格子在 24px 的
    /// 错位下仍会压住九成（10-25 他定的线是"重合不许超过 80%"），所以步长至少取短边的两成：
    /// 竖向错开这么多，交集高度就不超过那短边的八成，面积比自然也在八成以内。
    /// </remarks>
    public static Outcome Stack(IReadOnlyList<Placed> items)
    {
        if (items.Count < 2)
        {
            return new Outcome(false, [], Needed: 2, Have: items.Count);
        }

        var bounds = Bounds(items);
        var step = Math.Ceiling(Math.Max(
            MinGapPx,
            (1 - OverlapLimit) * items.Max(item => Math.Min(item.Width, item.Height))));

        // The order is z-order as given: whoever wrote the list owns what ends up on top.
        var moves = items.Select((item, index) => new Move(
            item.Id,
            bounds.X + (index * step),
            bounds.Y + (index * step))).ToList();

        return new Outcome(true, moves, Needed: 2, Have: items.Count);
    }

    /// <summary>
    /// 排列（原“聚拢”）：同 key（颜色 / 第一个标签）的合成一块，每块排成矩阵而不是一条长龙；
    /// 块与块之间留得比块内更大；块内按到期优先级（逾期 → 今天 → 将来 → 读不出 → 无日期），
    /// 同一档里按连线把有关系的排在一起；块与块之间也按连线多少排序，免得线横穿整块板。
    /// </summary>
    /// <param name="keys">每件东西的归类键；空字符串也算一个键（“无标签”是一种标签）。</param>
    /// <param name="priorities">每件东西的到期优先级，越小越靠前。</param>
    /// <param name="links">东西之间的连线（A→B 与 B→A 等价）。</param>
    /// <param name="lastKey">哪一档排到最后（按标签排时把“无标签”放底部，读起来才顺）。</param>
    /// <remarks>
    /// 矩阵的列数取 ⌈√n⌉：5 格排成 3+2，而不是一条 5 格的长行。列宽/行高逐列逐行取最大，
    /// 所以块内既不重叠也不留出大洞。块从 (40, 40) 起、自上而下叠，块间距 <see cref="BucketGapPx"/>。
    /// </remarks>
    public static Outcome Arrange(
        IReadOnlyList<Placed> items,
        IReadOnlyList<string> keys,
        IReadOnlyList<int> priorities,
        IReadOnlyList<(string A, string B)> links,
        string? lastKey = null)
    {
        if (items.Count < 2 || keys.Count != items.Count || priorities.Count != items.Count)
        {
            return new Outcome(false, [], Needed: 2, Have: items.Count);
        }

        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < items.Count; i++)
        {
            indexOf[items[i].Id] = i;
        }

        var neighbours = new List<HashSet<int>>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            neighbours.Add([]);
        }

        var bucketLinks = new Dictionary<(string A, string B), int>();

        foreach (var (a, b) in links)
        {
            if (!indexOf.TryGetValue(a, out var ia) || !indexOf.TryGetValue(b, out var ib) || ia == ib)
            {
                continue;
            }

            neighbours[ia].Add(ib);
            neighbours[ib].Add(ia);

            var ka = keys[ia];
            var kb = keys[ib];

            if (ka != kb)
            {
                var pair = string.CompareOrdinal(ka, kb) <= 0 ? (ka, kb) : (kb, ka);
                bucketLinks[pair] = bucketLinks.GetValueOrDefault(pair) + 1;
            }
        }

        var buckets = new List<(string Key, List<int> Members)>();
        var bucketOfKey = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < items.Count; i++)
        {
            if (bucketOfKey.TryGetValue(keys[i], out var found))
            {
                buckets[found].Members.Add(i);
            }
            else
            {
                bucketOfKey[keys[i]] = buckets.Count;
                buckets.Add((keys[i], [i]));
            }
        }

        foreach (var (_, members) in buckets)
        {
            var ordered = new List<int>();

            foreach (var tier in members.GroupBy(index => priorities[index]).OrderBy(group => group.Key))
            {
                var left = new SortedSet<int>(tier);

                while (left.Count > 0)
                {
                    var seed = left.Min;
                    left.Remove(seed);
                    ordered.Add(seed);

                    while (true)
                    {
                        int? next = null;

                        foreach (var candidate in left)
                        {
                            if (neighbours[seed].Contains(candidate))
                            {
                                next = candidate;
                                break;
                            }
                        }

                        if (next is not { } picked)
                        {
                            break;
                        }

                        left.Remove(picked);
                        ordered.Add(picked);
                        seed = picked;
                    }
                }
            }

            members.Clear();
            members.AddRange(ordered);
        }

        var order = new List<int>();
        var taken = new bool[buckets.Count];

        if (buckets.Count > 0)
        {
            var start = Enumerable.Range(0, buckets.Count)
                .OrderByDescending(index => buckets[index].Members.Count)
                .ThenBy(index => index)
                .First();

            order.Add(start);
            taken[start] = true;

            while (order.Count < buckets.Count)
            {
                var next = -1;
                var best = -1;

                for (var index = 0; index < buckets.Count; index++)
                {
                    if (taken[index])
                    {
                        continue;
                    }

                    var linked = 0;

                    foreach (var placed in order)
                    {
                        var a = buckets[index].Key;
                        var b = buckets[placed].Key;
                        var pair = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
                        linked += bucketLinks.GetValueOrDefault(pair);
                    }

                    if (linked > best
                        || (linked == best && next >= 0 && buckets[index].Members.Count > buckets[next].Members.Count))
                    {
                        best = linked;
                        next = index;
                    }
                }

                if (next < 0)
                {
                    next = Enumerable.Range(0, buckets.Count).First(index => !taken[index]);
                }

                order.Add(next);
                taken[next] = true;
            }
        }

        // 有标签的在上、没标签的垫底（10-25 他：这样才像人读的顺序）。
        if (lastKey is not null)
        {
            var tail = buckets.FindIndex(bucket => bucket.Key == lastKey);

            if (tail >= 0)
            {
                var bucket = order.IndexOf(tail);

                if (bucket >= 0)
                {
                    order.RemoveAt(bucket);
                    order.Add(tail);
                }
            }
        }

        const double origin = 40;
        var moves = new List<Move>(items.Count);
        var blockTop = origin;

        foreach (var bucketIndex in order)
        {
            var members = buckets[bucketIndex].Members;

            if (members.Count == 0)
            {
                continue;
            }

            var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(members.Count)));
            var rows = (members.Count + columns - 1) / columns;
            var columnWidths = new double[columns];
            var rowHeights = new double[rows];

            for (var position = 0; position < members.Count; position++)
            {
                var item = items[members[position]];
                var column = position % columns;
                var row = position / columns;

                columnWidths[column] = Math.Max(columnWidths[column], item.Width);
                rowHeights[row] = Math.Max(rowHeights[row], item.Height);
            }

            var lefts = new double[columns];

            for (var column = 0; column < columns; column++)
            {
                lefts[column] = column == 0 ? origin : lefts[column - 1] + columnWidths[column - 1] + MinGapPx;
            }

            var tops = new double[rows];
            var top = blockTop;

            for (var row = 0; row < rows; row++)
            {
                tops[row] = top;
                top += rowHeights[row] + MinGapPx;
            }

            for (var position = 0; position < members.Count; position++)
            {
                moves.Add(new Move(items[members[position]].Id, lefts[position % columns], tops[position / columns]));
            }

            blockTop = top - MinGapPx + BucketGapPx;
        }

        return new Outcome(true, moves, Needed: 2, Have: items.Count);
    }

    /// <summary>
    /// 把排好的一块挪到离锚点最近、又不压到别人的地方（“就近找空地”）。
    /// 候选是绕锚点的方格环（步长 <paramref name="step"/>，先近后远），第一个与障碍物
    /// 保持 <paramref name="margin"/> 空隙的位置胜出；一圈都没找到就退回锚点（至少不乱跑）。
    /// </summary>
    /// <param name="block">已经排好的那些格子（坐标未平移，用来量这一块的宽高）。</param>
    /// <param name="obstacles">不能被压住的东西：没参与排列的格子、组框。</param>
    /// <param name="anchorX">块的左上角从哪找起（一般用选中那块原来的包围盒左上角）。</param>
    public static (double X, double Y) PlaceNear(
        IReadOnlyList<Placed> block,
        IReadOnlyList<Placed> obstacles,
        double anchorX,
        double anchorY,
        double margin = 12,
        double step = 32,
        int rings = 24)
    {
        if (block.Count == 0)
        {
            return (anchorX, anchorY);
        }

        var left = block.Min(item => item.X);
        var top = block.Min(item => item.Y);
        var width = block.Max(item => item.X + item.Width) - left;
        var height = block.Max(item => item.Y + item.Height) - top;

        for (var ring = 0; ring <= rings; ring++)
        {
            for (var dx = -ring; dx <= ring; dx++)
            {
                for (var dy = -ring; dy <= ring; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                    {
                        continue;
                    }

                    var x = anchorX + (dx * step);
                    var y = anchorY + (dy * step);

                    if (obstacles.All(obstacle =>
                        x - margin >= obstacle.X + obstacle.Width
                        || x + width + margin <= obstacle.X
                        || y - margin >= obstacle.Y + obstacle.Height
                        || y + height + margin <= obstacle.Y))
                    {
                        return (x, y);
                    }
                }
            }
        }

        return (anchorX, anchorY);
    }

    /// <summary>不同颜色 / 不同标签之间留的空：比块内的 <see cref="MinGapPx"/> 宽一倍半以上，才看得出分组。</summary>
    public const double BucketGapPx = 96;

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
