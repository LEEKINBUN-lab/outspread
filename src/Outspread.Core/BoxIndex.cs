using System.Collections.Generic;

namespace Outspread.Core;

/// <summary>
/// 便签的二维均匀网格索引：把"这一发点在哪一格上"和"框里圈到了哪几格"从全表扫描
/// 换成只看指针／选框压到的那几个格子。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不复用 <see cref="IntervalIndex"/>：那是一维的，回答的是"谁的边能当我这根轴的对齐基准"，
/// 候选集是 <b>X 带 ∪ Y 带</b>，问的根本不是矩形相交。命中与框选要的恰恰是相交，拿一维索引去凑
/// 会漏掉"横向不重叠、纵向重叠"的那几格——症状是框选少收一枚、点不中压在下面那张。
/// </para>
/// <para>
/// 三条约束：
/// ① <b>索引只缩候选集</b>，判据仍由调用方拿现值复核：两次提交之间格子可能被拖走，索引里的盒
/// 是建表那一刻的，命中的号要回文档再确认一次（与 <see cref="IntervalIndex"/> 同一条规矩）。
/// ② 只收正尺寸的格子；组框不进索引——组是关系，不是场地（10-22 定的）。
/// ③ <b>顺序是输入的一部分</b>：<see cref="Entry.Order"/> 就是文档序，<see cref="TopAt"/>
/// 取其中最大的那一个，等于原来"从数组末尾往前找第一个命中"。
/// </para>
/// <para>
/// 网格而不是 R 树：这里每次几何提交都是整表重建（一次 O(n)），查询要的是常数级而不是对数级，
/// 均匀网格的常数比树小一个量级；格子尺寸按这一批的中位边长现算，所以散板与密板都不会退化成
/// "所有格子挤进一个桶"。
/// </para>
/// </remarks>
public sealed class BoxIndex
{
    /// <summary>命中：格号、建表那一刻的盒、以及它在文档里的次序（越大越靠上）。</summary>
    public readonly record struct Entry(string Id, double X, double Y, double Width, double Height, int Order);

    /// <summary>一个盒子横跨超过这么多格就不再逐格登记，改放"大件"那一列，查询时always过一遍。</summary>
    /// <remarks>
    /// 组框与整块看板那种大格子会跨几百格：逐格登记的建表代价与内存都不划算，而这类格子本来
    /// 就少，查询时线性过一遍更便宜。
    /// </remarks>
    private const int MaxCellsPerBox = 96;

    private Entry[] _entries = [];

    private Dictionary<long, List<int>> _buckets = [];

    private List<int> _wide = [];

    private double _cell = 160;

    /// <summary>这一发查询打过的记号（配合 <see cref="_marks"/> 去重，不必每次清一张表）。</summary>
    private int _query;

    private int[] _marks = [];

    /// <summary>表里有多少格。调用方拿它对文档格数验"这份索引是不是这一拍的"。</summary>
    public int Count => _entries.Length;

    /// <summary>整表重建。空表与全是零尺寸的输入都得到一张空索引。</summary>
    public void Rebuild(IReadOnlyList<Grouping.Rect> boxes)
    {
        var kept = 0;
        var extents = 0;
        var extentSum = 0d;
        var staged = new Entry[boxes.Count];

        for (var index = 0; index < boxes.Count; index++)
        {
            var box = boxes[index];

            // 零尺寸与读不出的坐标不收：它们本来就点不中，收进来只会给每个桶加噪声。
            if (!(box.Width > 0) || !(box.Height > 0)
                || !double.IsFinite(box.X) || !double.IsFinite(box.Y))
            {
                continue;
            }

            staged[kept++] = new Entry(box.Id, box.X, box.Y, box.Width, box.Height, index);

            extentSum += Math.Max(box.Width, box.Height);
            extents++;
        }

        if (kept == 0)
        {
            _entries = [];
            _buckets = [];
            _wide = [];
            _marks = [];

            return;
        }

        // 格子边长取"两倍的中位尺度"：比大多数格大一点，于是每格只落进一两个桶；
        // 太小会让每个盒子横跨一堆格子，太大又退化成全表扫描。夹在 [96, 1024] 之间是为了
        // 既不让极小的格把网格铺成百万格，也不让极大的格把整板压进一格。
        _cell = Math.Clamp((extentSum / extents) * 2, 96, 1024);

        var entries = new Entry[kept];
        Array.Copy(staged, entries, kept);
        _entries = entries;

        var buckets = new Dictionary<long, List<int>>(kept);
        var wide = new List<int>();

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var x0 = CellOf(entry.X);
            var x1 = CellOf(entry.X + entry.Width);
            var y0 = CellOf(entry.Y);
            var y1 = CellOf(entry.Y + entry.Height);

            var across = (double)(x1 - x0 + 1) * (y1 - y0 + 1);

            if (across > MaxCellsPerBox)
            {
                wide.Add(index);

                continue;
            }

            for (var cx = x0; cx <= x1; cx++)
            {
                for (var cy = y0; cy <= y1; cy++)
                {
                    var key = KeyOf(cx, cy);

                    if (!buckets.TryGetValue(key, out var bucket))
                    {
                        bucket = [];
                        buckets[key] = bucket;
                    }

                    bucket.Add(index);
                }
            }
        }

        _buckets = buckets;
        _wide = wide;
        _marks = new int[entries.Length];
        _query = 0;
    }

    /// <summary>
    /// 这一发点在哪一格上（最上面那一枚）。没命中回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// "最上面"＝文档序最大的那个，与原来"从数组末尾往前找第一个命中"逐字等价：
    /// 叠成一摞时点到的永远是露在最上面的那张。
    /// </remarks>
    public string? TopAt(double x, double y)
    {
        var best = -1;

        foreach (var index in _wide)
        {
            var entry = _entries[index];

            if (Contains(entry, x, y) && (best < 0 || entry.Order > _entries[best].Order))
            {
                best = index;
            }
        }

        if (_buckets.TryGetValue(KeyOf(CellOf(x), CellOf(y)), out var bucket))
        {
            foreach (var index in bucket)
            {
                var entry = _entries[index];

                if (!Contains(entry, x, y))
                {
                    continue;
                }

                if (best < 0 || entry.Order > _entries[best].Order)
                {
                    best = index;
                }
            }
        }

        return best < 0 ? null : _entries[best].Id;
    }

    /// <summary>与这个矩形相交的格号（去重、顺序不保证）。</summary>
    /// <remarks>
    /// 相交用闭区间：边正好压在选框上的那一格要收——框选沿着一排便签的边拖过去时，
    /// 少收一枚就是"我明明框住了它"。
    /// </remarks>
    public List<string> Intersecting(double x, double y, double width, double height)
    {
        var result = new List<string>();

        if (_entries.Length == 0)
        {
            return result;
        }

        var left = Math.Min(x, x + width);
        var right = Math.Max(x, x + width);
        var top = Math.Min(y, y + height);
        var bottom = Math.Max(y, y + height);

        var x0 = CellOf(left);
        var x1 = CellOf(right);
        var y0 = CellOf(top);
        var y1 = CellOf(bottom);

        // 选框大到覆盖的格子比表还多时，逐桶去重不比全表扫便宜：直接扫一遍。
        var across = (double)(x1 - x0 + 1) * (y1 - y0 + 1);

        if (across > _entries.Length * 4.0)
        {
            foreach (var entry in _entries)
            {
                if (Overlaps(entry, left, top, right, bottom))
                {
                    result.Add(entry.Id);
                }
            }

            return result;
        }

        _query++;

        if (_query == int.MaxValue)
        {
            Array.Clear(_marks);
            _query = 1;
        }

        for (var cx = x0; cx <= x1; cx++)
        {
            for (var cy = y0; cy <= y1; cy++)
            {
                if (!_buckets.TryGetValue(KeyOf(cx, cy), out var bucket))
                {
                    continue;
                }

                foreach (var index in bucket)
                {
                    if (_marks[index] == _query)
                    {
                        continue;
                    }

                    _marks[index] = _query;

                    if (Overlaps(_entries[index], left, top, right, bottom))
                    {
                        result.Add(_entries[index].Id);
                    }
                }
            }
        }

        foreach (var index in _wide)
        {
            if (_marks[index] == _query)
            {
                continue;
            }

            _marks[index] = _query;

            if (Overlaps(_entries[index], left, top, right, bottom))
            {
                result.Add(_entries[index].Id);
            }
        }

        return result;
    }

    private long CellOf(double value)
    {
        var cell = Math.Floor(value / _cell);

        // 世界坐标可以很大（同步仓里见过六位数），夹到 int 范围再拼 key，避免移位溢出。
        if (cell > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (cell < int.MinValue)
        {
            return int.MinValue;
        }

        return (long)cell;
    }

    private static long KeyOf(long cx, long cy)
        => (cx << 32) ^ (cy & 0xffffffffL);

    private static bool Contains(Entry entry, double x, double y)
        => x >= entry.X && x <= entry.X + entry.Width
           && y >= entry.Y && y <= entry.Y + entry.Height;

    private static bool Overlaps(Entry entry, double left, double top, double right, double bottom)
        => entry.X <= right && entry.X + entry.Width >= left
           && entry.Y <= bottom && entry.Y + entry.Height >= top;
}
