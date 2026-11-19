namespace Outspread.Core.Tests;

/// <summary>
/// 二维索引必须与"扫全表"逐字等价——它只缩候选集，不改判据。
/// </summary>
/// <remarks>
/// 这一条比区间索引那条更要紧：命中与框选是"点错一格就选错对象"的路，索引少给一格或多给一格
/// 都会直接变成他手上的错。所以这里两件事逐条核：点位取的是不是文档序最大的那一枚、
/// 矩形相交收的是不是与暴力扫完全相同的那一批。
/// </remarks>
public class BoxIndexTests
{
    [Fact]
    public void Point_queries_return_the_same_topmost_cell_as_a_full_scan()
    {
        var boxes = Scattered(600);
        var index = new BoxIndex();
        index.Rebuild(boxes);

        for (var i = 0; i < 400; i++)
        {
            // 采样点故意落在负坐标、零坐标与大坐标三档上：均匀网格最容易在这里翻车。
            var x = -3000 + (i * 17.31) + ((i % 7) * 0.5);
            var y = -2400 + (i * 13.77);

            var expected = TopByScan(boxes, x, y);
            var actual = index.TopAt(x, y);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Rectangle_queries_return_the_same_set_as_a_full_scan()
    {
        var boxes = Scattered(500);
        var index = new BoxIndex();
        index.Rebuild(boxes);

        for (var i = 0; i < 120; i++)
        {
            var x = -2600 + (i * 41.5);
            var y = -1800 + (i * 33.25);
            var width = 60 + ((i * 37) % 900);
            var height = 40 + ((i * 53) % 700);

            var expected = boxes
                .Where(box => Overlaps(box, x, y, x + width, y + height))
                .Select(box => box.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            var actual = index
                .Intersecting(x, y, width, height)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Cells_with_no_size_are_left_out_because_nothing_can_land_on_them()
    {
        var boxes = new List<Grouping.Rect>
        {
            new("a", 0, 0, 100, 100),
            new("zero", 50, 50, 0, 60),
            new("flat", 50, 50, 60, 0),
        };

        var index = new BoxIndex();
        index.Rebuild(boxes);

        Assert.Equal(1, index.Count);
        Assert.Equal("a", index.TopAt(40, 40));
        Assert.Null(index.TopAt(5000, 5000));
    }

    [Fact]
    public void A_box_too_wide_to_register_cell_by_cell_is_still_found()
    {
        // 一整块看板那么大的格：跨的格子远超逐格登记的上限，走"大件"那一列。
        var boxes = new List<Grouping.Rect>
        {
            new("huge", -5000, -5000, 12000, 12000),
            new("small", 10, 10, 40, 40),
        };

        var index = new BoxIndex();
        index.Rebuild(boxes);

        Assert.Equal("huge", index.TopAt(-4000, 3000));
        Assert.Equal("small", index.TopAt(20, 20));

        var hits = index.Intersecting(100, 100, 10, 10);

        Assert.Contains("huge", hits);
        Assert.DoesNotContain("small", hits);
    }

    [Fact]
    public void An_empty_board_answers_without_allocating_a_result_set()
    {
        var index = new BoxIndex();
        index.Rebuild([]);

        Assert.Equal(0, index.Count);
        Assert.Null(index.TopAt(0, 0));
        Assert.Empty(index.Intersecting(-10, -10, 20, 20));
    }

    private static string? TopByScan(IReadOnlyList<Grouping.Rect> boxes, double x, double y)
    {
        for (var index = boxes.Count - 1; index >= 0; index--)
        {
            var box = boxes[index];

            if (!(box.Width > 0) || !(box.Height > 0))
            {
                continue;
            }

            if (x >= box.X && x <= box.X + box.Width && y >= box.Y && y <= box.Y + box.Height)
            {
                return box.Id;
            }
        }

        return null;
    }

    private static bool Overlaps(Grouping.Rect box, double left, double top, double right, double bottom)
        => box.X <= right && box.X + box.Width >= left && box.Y <= bottom && box.Y + box.Height >= top;

    /// <summary>确定性的散板：同样的种子在任何机器上都排成同一张表（与区间索引那条测试同一把尺）。</summary>
    private static List<Grouping.Rect> Scattered(int count)
    {
        var boxes = new List<Grouping.Rect>(count);
        var state = 12345u;

        double Next()
        {
            state = unchecked((state * 1103515245u) + 12345u);

            return state / 4294967296.0;
        }

        for (var i = 0; i < count; i++)
        {
            var x = Math.Round(-2800 + (Next() * 5600));
            var y = Math.Round(-2000 + (Next() * 4200));
            var width = Math.Round(60 + (Next() * 340));
            var height = Math.Round(40 + (Next() * 260));

            boxes.Add(new Grouping.Rect($"n{i:0000}", x, y, width, height));
        }

        return boxes;
    }
}
