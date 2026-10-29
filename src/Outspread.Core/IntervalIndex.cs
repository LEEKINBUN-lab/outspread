using System.Collections.Generic;

namespace Outspread.Core;

/// <summary>
/// 一维区间索引（按低边排序 ＋ 子树高边最大值剪枝）：拖一格那一帧要问"谁的边能当我这根轴的对齐基准"，
/// 而这个问题只有压在 <c>移动盒 ± 阈值</c> 那一条带上的格有答案。
/// </summary>
/// <remarks>
/// 为什么不是二维网格：对齐候选集是 <b>X 带 ∪ Y 带</b>，不是"矩形相交"。一格可以在竖直方向离得很远、
/// 却正好和移动盒左边对齐——那种格照样提供一根 X 基准线（<c>Snapping.CollectTargetsX</c> 收每格的
/// X／CenterX／Right），用矩形相交去筛会把它漏掉，症状是"少一根对齐参考线、位置差几像素"。
/// 所以这里两轴各建一枚，查询取并集。
///
/// 几何的真相仍在文档里：索引只用来缩小候选集，返回的是当时的边；调用方要拿现值复核再用。
/// 下一次提交整表重建，任何漏改都活不过一拍——而一次手势中间只有"正被拖着的那几格"在动，
/// 它们本来就被排除在基准之外（与 <c>SnapDrag</c> 原来的 <c>!selected.Contains(id)</c> 同一件事）。
/// </remarks>
public sealed class IntervalIndex
{
    /// <summary>命中：格的号、建索引时那一份盒，以及这一轴上的两条边。</summary>
    /// <remarks>盒一起带着，调用方就不必为了拿另外两个数再回文档读一次（每读一个约 1.3µs）。</remarks>
    public readonly record struct Hit(string Id, Grouping.Rect Box, double Low, double High);

    private readonly List<Hit> _hits = [];

    private Hit[] _items = [];

    private double[] _blockMaxHigh = [];

    /// <summary>把一批盒按这一轴投影后整表重建。<paramref name="pickLow"/> 取该轴的低边，<paramref name="pickHigh"/> 取高边。</summary>
    public void Rebuild(IReadOnlyList<Grouping.Rect> boxes, System.Func<Grouping.Rect, double> pickLow, System.Func<Grouping.Rect, double> pickHigh)
    {
        _hits.Clear();

        if (boxes.Count == 0)
        {
            _items = [];
            _blockMaxHigh = [];

            return;
        }

        var items = new Hit[boxes.Count];

        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];

            items[i] = new Hit(box.Id, box, pickLow(box), pickHigh(box));
        }

        System.Array.Sort(items, (left, right) => left.Low.CompareTo(right.Low));

        _items = items;

        var blocks = (items.Length + Block - 1) / Block;

        _blockMaxHigh = new double[blocks];

        for (var b = 0; b < blocks; b++)
        {
            var max = double.MinValue;
            var end = System.Math.Min((b + 1) * Block, items.Length);

            for (var i = b * Block; i < end; i++)
            {
                if (items[i].High > max)
                {
                    max = items[i].High;
                }
            }

            _blockMaxHigh[b] = max;
        }
    }

    /// <summary>与 [<paramref name="low"/>, <paramref name="high"/>] 相交的那些区间（顺序不保证）。</summary>
    public IReadOnlyList<Hit> Overlapping(double low, double high)
    {
        _hits.Clear();

        if (_items.Length == 0)
        {
            return _hits;
        }

        // 剪枝只有一刀，而且跳得掉的一定确实不相交：这一整块里最高的边都够不着 low。
        // 另一刀靠"按低边排序"：最后一个低边不超过 high 的位置之后全都可以不看——那一刀用二分找位置，
        // 不许按块跳（按块跳会把"块里前半段还够得着"的那些区间整块丢掉，症状是少一根对齐参考线）。
        var from = 0;
        var to = LastLowAtOrBelow(high);

        if (to < 0)
        {
            return _hits;
        }

        for (var start = from; start <= to; start += Block)
        {
            var end = System.Math.Min(start + Block - 1, to);

            if (_blockMaxHigh[start / Block] < low)
            {
                continue;
            }

            for (var i = start; i <= end; i++)
            {
                var item = _items[i];

                if (item.Low > high)
                {
                    break;
                }

                if (item.High >= low)
                {
                    _hits.Add(item);
                }
            }
        }

        return _hits;
    }

    /// <summary>按低边排序之后，最后一个 <c>Low &lt;= high</c> 的下标；一个都没有时回 -1。</summary>
    private int LastLowAtOrBelow(double high)
    {
        var lo = 0;
        var hi = _items.Length - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;

            if (_items[mid].Low <= high)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>一块多少个区间：够小所以跳一块就省一大截扫描，够大所以每块的建块开销不显著。</summary>
    private const int Block = 64;

    /// <summary>
    /// 两轴各查一次取<b>并集</b>——这就是"候选集是 X 带 ∪ Y 带，不是矩形相交"那句话的落点。
    /// 同号只留一次（它可能在两轴上都命中）。
    /// </summary>
    public static List<Hit> OverlappingEither(
        IntervalIndex onX,
        IntervalIndex onY,
        double lowX,
        double highX,
        double lowY,
        double highY)
    {
        var merged = new List<Hit>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var hit in onX.Overlapping(lowX, highX))
        {
            if (seen.Add(hit.Id))
            {
                merged.Add(hit);
            }
        }

        foreach (var hit in onY.Overlapping(lowY, highY))
        {
            if (seen.Add(hit.Id))
            {
                merged.Add(hit);
            }
        }

        return merged;
    }
}
