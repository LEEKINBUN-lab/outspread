namespace Outspread.Core.Tests;

public class SnappingTests
{
    private static readonly Box Other = new(400, 200, 200, 100);

    [Fact]
    public void A_near_edge_snaps_to_it_and_reports_the_line_that_was_hit()
    {
        // Narrow enough that only its left edge is anywhere near a target, so exactly
        // one guide line is drawn rather than a tie between left and right.
        var moving = new Box(396, 0, 50, 40);

        var result = Snapping.Snap(moving, [Other], viewport: null, threshold: 8);

        Assert.Equal(400, result.X, 9);
        Assert.Equal(4, result.DeltaX, 9);
        Assert.Single(result.Guides);
        Assert.True(result.Guides[0].Vertical);
        Assert.Equal(400, result.Guides[0].Position, 9);
    }

    [Fact]
    public void Outside_the_threshold_nothing_moves_and_no_line_is_drawn()
    {
        var moving = new Box(380, 0, 100, 40);

        var result = Snapping.Snap(moving, [Other], viewport: null, threshold: 8);

        Assert.Equal(380, result.X);
        Assert.Equal(0, result.DeltaX);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void The_nearest_of_several_candidates_wins()
    {
        var moving = new Box(100, 100, 50, 50);

        // A's left edge is three away from the moving left edge, B's left edge four away
        // from the moving centre: three is nearer, so it is the one that gets picked.
        var result = Snapping.Snap(
            moving,
            [new Box(103, 0, 10, 10), new Box(129, 500, 10, 10)],
            viewport: null,
            threshold: 20);

        Assert.Equal(103, result.X, 9);
        Assert.Equal(3, result.DeltaX, 9);
    }

    [Fact]
    public void A_moving_box_centre_snaps_to_another_centre()
    {
        // moving centre 50; other centre 52.
        var moving = new Box(0, 0, 100, 20);

        var result = Snapping.Snap(moving, [new Box(2, 500, 100, 20)], viewport: null, threshold: 8);

        Assert.Equal(2, result.X, 9);
    }

    [Fact]
    public void X_and_y_snap_independently()
    {
        var moving = new Box(396, 196, 50, 50);

        var result = Snapping.Snap(moving, [Other], viewport: null, threshold: 8);

        Assert.Equal(400, result.X, 9);
        Assert.Equal(200, result.Y, 9);

        // Three lines, not two: the moving box's top is four from the other's top and its
        // bottom is four from the other's middle, both inside the threshold, and a board
        // that drew only one of them would be hiding a second alignment the user is on.
        Assert.Equal(3, result.Guides.Count);
    }

    [Fact]
    public void The_viewport_centre_is_a_target_so_a_note_can_be_parked_in_the_middle()
    {
        var viewport = new Box(0, 0, 1000, 600);

        // Its own centre sits at 500, the viewport's centre line.
        var moving = new Box(478, 20, 44, 40);

        var result = Snapping.Snap(moving, [], viewport, threshold: 8);

        Assert.Equal(478, result.X, 9);
        Assert.Equal(0, result.DeltaX, 9);
        Assert.Contains(result.Guides, guide => guide.Vertical && Math.Abs(guide.Position - 500) < 0.001);
    }

    [Fact]
    public void The_nearest_target_wins_even_when_it_is_an_edge_rather_than_the_centre()
    {
        var viewport = new Box(0, 0, 1000, 600);

        // Left edge four from the centre line, centre sixteen from it: the edge is nearer,
        // and that is what every board does — parking is achieved by aiming the centre.
        var moving = new Box(496, 20, 40, 40);

        var result = Snapping.Snap(moving, [], viewport, threshold: 8);

        Assert.Equal(500, result.X, 9);
    }

    [Fact]
    public void The_threshold_is_in_world_units_which_is_why_the_caller_divides_by_zoom()
    {
        var moving = new Box(392, 0, 100, 40);

        // Eight screen pixels at 4× zoom is two world pixels; at 0.2× it is forty.
        Assert.NotEqual(
            Snapping.Snap(moving, [Other], null, 8 / 4.0).DeltaX,
            Snapping.Snap(moving, [Other], null, 8 / 0.2).DeltaX);

        Assert.Equal(0, Snapping.Snap(moving, [Other], null, threshold: 8 / 4.0).DeltaX);
        Assert.Equal(8, Snapping.Snap(moving, [Other], null, threshold: 8 / 0.2).DeltaX);
    }

    [Fact]
    public void An_empty_target_set_is_not_an_error_and_moves_nothing()
    {
        var result = Snapping.Snap(new Box(10, 10, 10, 10), [], null, threshold: 8);

        Assert.Equal(10, result.X);
        Assert.Empty(result.Guides);
    }
}

public class HistoryTests
{
    [Fact]
    public void An_action_that_changed_nothing_does_not_eat_an_undo_step()
    {
        var history = new History();

        Assert.False(history.Record("移动", "same", "same"));
        Assert.False(history.CanUndo);

        Assert.True(history.Record("移动", "a", "b"));
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void Undo_then_redo_returns_the_same_two_states()
    {
        var history = new History();

        history.Record("贴了一格", "before", "after");

        var back = history.TakeUndo("after");

        Assert.NotNull(back);
        Assert.Equal("before", back!.Json);
        Assert.Equal("贴了一格", back.Label);
        Assert.True(history.CanRedo);

        var forward = history.TakeRedo("before");

        Assert.Equal("after", forward!.Json);
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void A_new_action_forgets_the_redo_it_would_have_collided_with()
    {
        var history = new History();

        history.Record("一", "a", "b");
        history.TakeUndo("b");

        Assert.True(history.CanRedo);

        history.Record("二", "b", "c");

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void The_history_is_capped_and_the_oldest_steps_are_the_ones_dropped()
    {
        var history = new History(capacity: 3);

        for (var index = 0; index < 10; index++)
        {
            history.Record($"step{index}", $"json{index}", $"json{index + 1}");
        }

        Assert.Equal(3, history.UndoCount);

        var last = history.TakeUndo("json10");

        Assert.Equal("step9", last!.Label);

        history.TakeUndo("json9");
        history.TakeUndo("json8");

        Assert.False(history.CanUndo);
        Assert.Null(history.TakeUndo("anything"));
    }

    [Fact]
    public void The_menu_can_say_what_would_be_undone_before_it_is_done()
    {
        var history = new History();

        Assert.Null(history.NextUndoLabel);

        history.Record("删除 3 格", "a", "b");
        history.Record("缩放", "b", "c");

        Assert.Equal("缩放", history.NextUndoLabel);
    }

    [Fact]
    public void Clearing_is_what_a_reconnect_to_the_disk_has_to_do()
    {
        var history = new History();

        history.Record("x", "a", "b");
        history.Clear();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }
}
