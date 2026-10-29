namespace Outspread.Core.Tests;

/// <summary>
/// 区间索引必须给出与"暴力扫全表"完全相同的候选集——它只负责缩小候选集，不许改判据。
/// </summary>
/// <remarks>
/// 这条差异测试钉的是拖一格那一帧的对齐基准来源。它一旦少给一格，症状是"少一根参考线、位置差几像素"，
/// 是最难归因的一类；所以这里不测"结果看着像"，逐格核集合。
///
/// 特别要钉住的是<b>并集</b>那一笔：一格可以竖直方向离得很远、却正好和移动盒的左边对齐，
/// 它仍然提供一根 X 基准（<c>Snapping.CollectTargetsX</c> 收每格的 X／CenterX／Right）。
/// 谁要是把候选集改成"矩形相交"，只有这一条会红。
/// </remarks>
public class IntervalIndexTests
{
    private static readonly Func<Grouping.Rect, double> LeftX = box => box.X;

    private static readonly Func<Grouping.Rect, double> RightX = box => box.X + box.Width;

    private static readonly Func<Grouping.Rect, double> TopY = box => box.Y;

    private static readonly Func<Grouping.Rect, double> BottomY = box => box.Y + box.Height;

    [Fact]
    public void The_two_axis_union_matches_a_brute_force_scan_over_every_cell()
    {
        var boxes = Scattered(600);
        var x = new IntervalIndex();
        var y = new IntervalIndex();

        x.Rebuild(boxes, LeftX, RightX);
        y.Rebuild(boxes, TopY, BottomY);

        var threshold = 8;

        for (var i = 0; i < 80; i++)
        {
            var movingX = -2000 + i * 53.5;
            var movingY = -1500 + i * 41.25;
            var moving = new Grouping.Rect("m", movingX, movingY, 360, 100);

            var throughIndex = IntervalIndex
                .OverlappingEither(
                    x,
                    y,
                    moving.X - threshold,
                    moving.X + moving.Width + threshold,
                    moving.Y - threshold,
                    moving.Y + moving.Height + threshold)
                .Select(hit => hit.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            // 压线也算相交（闭区间）：Right 恰好压在带下沿的那一格，它的 Right 边正好给出
            // ±threshold 的增量，BestAxis 收 |delta| ≤ threshold——全表那一发的判据必须收下它，
            // 索引才谈得上"不改判据"（开区间会把这条边界上的吸附悄悄弄丢）。
            var byScan = boxes
                .Where(box => !(box.X > moving.X + moving.Width + threshold || box.X + box.Width < moving.X - threshold)
                              || !(box.Y > moving.Y + moving.Height + threshold || box.Y + box.Height < moving.Y - threshold))
                .Select(box => box.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(byScan, throughIndex);
        }
    }

    [Fact]
    public void A_cell_far_below_but_aligned_in_x_is_still_a_candidate()
    {
        // 这一条是"不许把候选集改成矩形相交"的那把尺：它竖直方向差着十万八千里，横向正好对得上。
        var far = new Grouping.Rect("far", 0, 90_000, 360, 100);
        var x = new IntervalIndex();
        var y = new IntervalIndex();

        x.Rebuild([far], LeftX, RightX);
        y.Rebuild([far], TopY, BottomY);

        var hits = IntervalIndex.OverlappingEither(x, y, -8, 368, 200 - 8, 300 + 8);

        Assert.Contains(hits, hit => hit.Id == "far");
    }

    [Fact]
    public void The_reported_edges_are_the_ones_the_axis_was_built_with()
    {
        var box = new Grouping.Rect("u1", 100, 200, 40, 30);
        var x = new IntervalIndex();

        x.Rebuild([box], LeftX, RightX);

        var hit = Assert.Single(x.Overlapping(0, 500));

        Assert.Equal("u1", hit.Id);
        Assert.Equal(100, hit.Low);
        Assert.Equal(140, hit.High);
    }

    [Fact]
    public void Nothing_is_reported_for_a_band_that_reaches_no_one()
    {
        var x = new IntervalIndex();

        x.Rebuild([new Grouping.Rect("u1", 0, 0, 40, 30)], LeftX, RightX);

        Assert.Empty(x.Overlapping(10_000, 10_100));
    }

    [Fact]
    public void An_empty_index_answers_empty_instead_of_throwing()
    {
        var x = new IntervalIndex();

        x.Rebuild([], LeftX, RightX);

        Assert.Empty(x.Overlapping(-1000, 1000));
    }

    [Fact]
    public void Rebuild_forgets_the_cells_that_left_the_document()
    {
        var x = new IntervalIndex();

        x.Rebuild([new Grouping.Rect("a", 0, 0, 40, 30), new Grouping.Rect("b", 500, 0, 40, 30)], LeftX, RightX);
        x.Rebuild([new Grouping.Rect("a", 0, 0, 40, 30)], LeftX, RightX);

        Assert.Equal(["a"], x.Overlapping(-1000, 4000).Select(hit => hit.Id));
    }

    /// <summary>散一板：负坐标、跨块边界、窄条都要有，剪枝写错才会在这里露出来。</summary>
    private static List<Grouping.Rect> Scattered(int count)
    {
        var state = 977;

        int Next()
        {
            state = state * 1103515245 + 12345;

            return (state >> 8) & 0xFFFFFF;
        }

        var boxes = new List<Grouping.Rect>(count);

        for (var i = 0; i < count; i++)
        {
            boxes.Add(new Grouping.Rect(
                $"u{i}",
                Next() % 9000 - 3000,
                Next() % 6000 - 2000,
                1 + Next() % 500,
                1 + Next() % 220));
        }

        return boxes;
    }
}
