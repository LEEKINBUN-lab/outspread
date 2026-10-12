using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>
/// Align / distribute / stack, pinned to the prototype's decisions — including the one case where
/// copying the shipped products' arithmetic produced a visible disaster.
/// </summary>
public class AligningTests
{
    private static Aligning.Placed Note(string id, double x, double y, double w = 100, double h = 60)
        => new(id, x, y, w, h);

    private static readonly Aligning.Placed[] Row =
    [
        Note("a", 0, 0),
        Note("b", 200, 40),
        Note("c", 500, 10),
    ];

    [Fact]
    public void Aligning_to_a_side_pins_that_edge_and_leaves_the_other_axis_alone()
    {
        var left = Aligning.Align(Row, Aligning.Side.Left).Moves;

        Assert.All(left, move => Assert.Equal(0, move.X));
        Assert.Equal(40, left[1].Y);   // b keeps its vertical position

        var right = Aligning.Align(Row, Aligning.Side.Right).Moves;
        Assert.All(right, move => Assert.Equal(500 + 100, move.X + 100));
    }

    [Fact]
    public void Centring_uses_the_box_not_the_biggest_note()
    {
        var items = new[] { Note("tall", 0, 0, 100, 200), Note("short", 300, 0, 100, 60) };

        var moves = Aligning.Align(items, Aligning.Side.VCenter).Moves;

        // Box height is 200: the short one lands at (200−60)/2 = 70, the tall one does not move.
        Assert.Equal(70, moves.Single(m => m.Id == "short").Y);
        Assert.Equal(0, moves.Single(m => m.Id == "tall").Y);
    }

    [Fact]
    public void Distribute_needs_three_because_two_notes_have_only_one_gap()
    {
        var two = Aligning.Distribute(new[] { Note("a", 0, 0), Note("b", 300, 0) }, horizontal: true);

        Assert.False(two.Ok);
        Assert.Equal(3, two.Needed);
        Assert.Equal(2, two.Have);
    }

    [Fact]
    public void Distribute_makes_every_gap_equal_and_pins_the_two_ends()
    {
        var items = new[] { Note("a", 0, 0), Note("b", 130, 0), Note("c", 500, 0) };

        var moves = Aligning.Distribute(items, horizontal: true).Moves.OrderBy(m => m.X).ToList();

        var gaps = new[] { moves[1].X - (moves[0].X + 100), moves[2].X - (moves[1].X + 100) };
        Assert.Equal(gaps[0], gaps[1], 6);
        Assert.Equal(0, moves[0].X);                       // 起点仍钉在原包围盒的左边
        Assert.Equal(500, moves[^1].X);                    // 包围盒够宽 ⇒ 末点不动
    }

    [Fact]
    public void Distribute_after_align_left_spreads_instead_of_crushing()
    {
        // 这就是照抄 Excalidraw/tldraw 踩到的那一发：刚左对齐过的四格，包围盒只有一格宽，
        // "在当前盒子里分间隔"算出 −216 的间隔，四格被压成一格。下限 24 与"只往右撑"是修法。
        var aligned = Aligning.Align(new[] { Note("a", 0, 0), Note("b", 10, 0), Note("c", 20, 0), Note("d", 30, 0) }, Aligning.Side.Left).Moves;
        var stackedUp = aligned.Select(m => Note(m.Id, m.X, 0)).ToList();

        var spread = Aligning.Distribute(stackedUp, horizontal: true).Moves.OrderBy(m => m.X).ToList();

        Assert.Equal(0, spread[0].X);
        for (var i = 1; i < spread.Count; i++)
        {
            var gap = spread[i].X - (spread[i - 1].X + 100);
            Assert.True(gap >= Aligning.MinGapPx - 0.0001, $"gap {gap} fell under the floor");
        }

        // 撑开是往右往下长，不许把整排往左搬。
        Assert.True(spread[^1].X > 30);
    }

    [Fact]
    public void Distribute_sorts_by_position_so_the_row_order_survives()
    {
        var items = new[] { Note("right", 900, 0), Note("left", 0, 0), Note("mid", 450, 0) };

        var spread = Aligning.Distribute(items, horizontal: true).Moves;

        Assert.Equal("left", spread.OrderBy(m => m.X).First().Id);
        Assert.Equal("right", spread.OrderBy(m => m.X).Last().Id);
    }

    [Fact]
    public void Stacking_leaves_an_edge_of_every_note_showing()
    {
        var moves = Aligning.Stack(new[] { Note("a", 100, 100), Note("b", 0, 0), Note("c", 40, 90) }).Moves;

        // 堆从**包围盒**的左上角起，不是从列表第一格起；给出的顺序就是叠放顺序（a 在最底）。
        Assert.Equal(0, moves[0].X);
        Assert.Equal(0, moves[0].Y);
        Assert.Equal(Aligning.MinGapPx, moves[1].X);
        Assert.Equal(2 * Aligning.MinGapPx, moves[2].Y);
    }

    [Fact]
    public void A_single_note_is_refused_with_the_count_that_was_needed()
    {
        var one = Aligning.Align(new[] { Note("alone", 0, 0) }, Aligning.Side.Left);
        Assert.False(one.Ok);
        Assert.True(one.TooFew);

        var stack = Aligning.Stack(new[] { Note("alone", 0, 0) });
        Assert.False(stack.Ok);
        Assert.Equal(2, stack.Needed);
    }

    [Fact]
    public void Arranging_moves_exactly_the_notes_it_was_given_and_nothing_else()
    {
        // 排布只搬位置（Move 结构里没有尺寸），而且不许吞格、不许凭空造格。
        foreach (var outcome in new[]
        {
            Aligning.Align(Row, Aligning.Side.Left),
            Aligning.Distribute(Row, horizontal: true),
            Aligning.Stack(Row),
        })
        {
            Assert.True(outcome.Ok);
            Assert.Equal(new[] { "a", "b", "c" }, outcome.Moves.Select(m => m.Id).OrderBy(id => id));
            Assert.All(outcome.Moves, move => Assert.True(double.IsFinite(move.X) && double.IsFinite(move.Y)));
        }
    }
}
