using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>
/// The transition table in docs/interaction-machine.md, executed.
/// </summary>
/// <remarks>
/// Each case is one row of that table, and the row it is named after is a bug this port already
/// paid for once. A press that never moved must not become a transaction; a static right-click
/// must become the menu and not a pan; a click on a note must not also be a resize.
/// </remarks>
public class BoardMachineTests
{
    private static readonly BoardMachine.PressFacts EmptyLeft = new(Right: false, Left: true, Space: false, Additive: false, OnNode: false);
    private static readonly BoardMachine.PressFacts EmptyRight = new(Right: true, Left: false, Space: false, Additive: false, OnNode: false);
    private static readonly BoardMachine.PressFacts EmptySpace = new(Right: false, Left: true, Space: true, Additive: false, OnNode: false);
    private static readonly BoardMachine.PressFacts NoteLeft = new(Right: false, Left: true, Space: false, Additive: false, OnNode: true);
    private static readonly BoardMachine.PressFacts NoteEdgeLeft = new(Right: false, Left: true, Space: false, Additive: false, OnNode: true, Edge: "e");
    private static readonly BoardMachine.PressFacts NoteRight = new(Right: true, Left: false, Space: false, Additive: false, OnNode: true);

    [Fact]
    public void A_press_on_empty_board_with_the_left_button_draws_a_marquee()
    {
        var (next, effect) = BoardMachine.OnPress(BoardMachine.Interaction.Selected, EmptyLeft);

        Assert.Equal(BoardMachine.BeginEffect.StartMarquee, effect);
        Assert.Equal(BoardMachine.Phase.Pressed, next.At);
        Assert.Equal(BoardMachine.PointerOp.Marquee, next.Op);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Right_drag_and_space_drag_both_pan(bool right, bool space)
    {
        var facts = right ? EmptyRight : EmptySpace;

        var (next, effect) = BoardMachine.OnPress(BoardMachine.Interaction.Idle, facts);

        Assert.Equal(BoardMachine.BeginEffect.StartPan, effect);
        Assert.Equal(BoardMachine.PointerOp.Pan, next.Op);
    }

    [Fact]
    public void Both_buttons_at_once_is_nobody_s_turn()
    {
        var both = new BoardMachine.PressFacts(Right: true, Left: true, Space: false, Additive: false, OnNode: false);

        var (next, effect) = BoardMachine.OnPress(BoardMachine.Interaction.Selected, both);

        Assert.Equal(BoardMachine.BeginEffect.Nothing, effect);
        Assert.Equal(BoardMachine.Phase.Selected, next.At);
    }

    [Fact]
    public void Nothing_becomes_a_gesture_until_the_pointer_has_travelled()
    {
        var (pressed, _) = BoardMachine.OnPress(BoardMachine.Interaction.Idle, EmptyLeft);

        Assert.Equal(BoardMachine.Phase.Pressed, BoardMachine.OnMove(pressed, travelPx: 2).At);
        Assert.Equal(BoardMachine.Phase.Dragging, BoardMachine.OnMove(pressed, travelPx: 9).At);
    }

    [Fact]
    public void A_right_click_that_never_moved_is_the_menu_not_a_pan()
    {
        var (pressed, _) = BoardMachine.OnPress(BoardMachine.Interaction.Idle, EmptyRight);

        var (next, effect) = BoardMachine.OnRelease(pressed, travelPx: 1);

        Assert.Equal(BoardMachine.ReleaseEffect.OpenMenu, effect);
        Assert.Equal(BoardMachine.Phase.Menu, next.At);
    }

    [Fact]
    public void A_right_drag_that_moved_is_a_pan_and_commits_without_a_menu()
    {
        var (pressed, _) = BoardMachine.OnPress(BoardMachine.Interaction.Idle, EmptyRight);
        var dragging = BoardMachine.OnMove(pressed, travelPx: 40);

        var (next, effect) = BoardMachine.OnRelease(dragging, travelPx: 40);

        Assert.Equal(BoardMachine.ReleaseEffect.Commit, effect);
        Assert.NotEqual(BoardMachine.Phase.Menu, next.At);
    }

    [Fact]
    public void A_click_on_empty_board_replaces_the_selection_with_nothing_unless_it_is_additive()
    {
        var (pressed, _) = BoardMachine.OnPress(BoardMachine.Interaction.Selected, EmptyLeft);
        var (plain, plainEffect) = BoardMachine.OnRelease(pressed, travelPx: 0);

        Assert.Equal(BoardMachine.ReleaseEffect.ClearSelection, plainEffect);
        Assert.Equal(BoardMachine.Phase.Idle, plain.At);

        var shift = EmptyLeft with { Additive = true };
        var (additivePressed, _) = BoardMachine.OnPress(BoardMachine.Interaction.Selected, shift);
        var (_, additiveEffect) = BoardMachine.OnRelease(additivePressed, travelPx: 0);

        Assert.Equal(BoardMachine.ReleaseEffect.Nothing, additiveEffect);
    }

    [Fact]
    public void Pressing_a_note_grabs_it_unless_the_press_landed_on_an_edge()
    {
        var (move, moveEffect) = BoardMachine.OnPress(BoardMachine.Interaction.Selected, NoteLeft);
        Assert.Equal(BoardMachine.BeginEffect.StartMove, moveEffect);
        Assert.Equal(BoardMachine.PointerOp.Move, move.Op);

        var (resize, resizeEffect) = BoardMachine.OnPress(BoardMachine.Interaction.Selected, NoteEdgeLeft);
        Assert.Equal(BoardMachine.BeginEffect.StartResize, resizeEffect);
        Assert.Equal(BoardMachine.PointerOp.Resize, resize.Op);
        Assert.Equal("e", resize.Direction);
    }

    [Fact]
    public void A_right_press_on_a_note_still_pans_because_pan_must_be_reachable_from_anywhere()
    {
        var (next, effect) = BoardMachine.OnPress(BoardMachine.Interaction.Selected, NoteRight);

        Assert.Equal(BoardMachine.BeginEffect.StartPan, effect);
        Assert.Equal(BoardMachine.PointerOp.Pan, next.Op);
    }

    [Fact]
    public void A_gesture_that_loses_the_pointer_leaves_no_transaction_behind()
    {
        var (pressed, _) = BoardMachine.OnPress(BoardMachine.Interaction.Selected, NoteLeft);
        var dragging = BoardMachine.OnMove(pressed, travelPx: 30);

        var (next, effect) = BoardMachine.OnCancel(dragging);

        Assert.Equal(BoardMachine.ReleaseEffect.Discard, effect);
        Assert.Equal(BoardMachine.Phase.Selected, next.At);
    }

    [Fact]
    public void Only_one_surface_ever_holds_the_keyboard()
    {
        Assert.True(BoardMachine.Interaction.Selected.OwnsBoardKeys);
        Assert.True(BoardMachine.Interaction.Idle.OwnsBoardKeys);
        Assert.False(BoardMachine.OnEditBegan("n1").OwnsBoardKeys);
        Assert.False(new BoardMachine.Interaction(BoardMachine.Phase.Finding).OwnsBoardKeys);
        Assert.False(BoardMachine.OnMenuOpened(BoardMachine.Interaction.Selected).OwnsBoardKeys);
    }

    [Fact]
    public void While_a_note_is_being_typed_a_press_does_not_start_a_second_gesture()
    {
        var editing = BoardMachine.OnEditBegan("n1");

        var (next, effect) = BoardMachine.OnPress(editing, NoteLeft);

        Assert.Equal(BoardMachine.BeginEffect.KeepEditing, effect);
        Assert.Equal(BoardMachine.Phase.Editing, next.At);
        Assert.Equal("n1", next.NodeId);
    }

    [Fact]
    public void Escape_from_editing_ends_in_the_state_the_press_came_from()
    {
        Assert.Equal(BoardMachine.Phase.Selected, BoardMachine.OnEditEnded(hadSelection: true).At);
        Assert.Equal(BoardMachine.Phase.Idle, BoardMachine.OnEditEnded(hadSelection: false).At);
    }

    [Fact]
    public void A_zero_travel_marquee_is_not_a_transaction()
    {
        // 不变量 2：history 里不许出现"点了一下"的空动作。
        var (pressed, _) = BoardMachine.OnPress(BoardMachine.Interaction.Idle, EmptyLeft);
        var (_, effect) = BoardMachine.OnRelease(pressed, travelPx: 0);

        Assert.NotEqual(BoardMachine.ReleaseEffect.Commit, effect);
    }
}
