namespace Outspread.Core.Tests;

/// <summary>
/// The gesture vocabulary, pinned as a decision table.
/// </summary>
/// <remarks>
/// This is the part of "the two products feel nothing alike" that has no excuse for drifting: it is
/// pure input mapping, so it can be asserted without a window. The prototype's behaviour (left drag
/// selects, right drag pans, space drag pans, a still right click opens the menu) is the contract.
/// </remarks>
public class BoardGesturesTests
{
    [Theory]
    [InlineData(false, true, false, SurfaceAction.Marquee)]     // 左键拖空白＝框选
    [InlineData(false, true, true, SurfaceAction.Pan)]          // 空格＋左键＝平移
    [InlineData(true, false, false, SurfaceAction.Pan)]         // 右键拖＝平移
    [InlineData(true, false, true, SurfaceAction.Pan)]          // 空格按下右键也还是平移
    [InlineData(true, true, false, SurfaceAction.Ignore)]       // 两个键同时按着＝不猜
    [InlineData(false, false, false, SurfaceAction.Ignore)]     // 中键之类的一发＝交回宿主
    public void A_press_on_empty_space_maps_to_one_action(
        bool right, bool left, bool space, SurfaceAction expected)
    {
        Assert.Equal(expected, BoardGestures.OnEmptyPress(right, left, space));
    }

    [Fact]
    public void Four_screen_pixels_is_the_line_between_a_click_and_a_drag()
    {
        // tldraw publishes 4px for mouse and 6px for coarse pointers; the menu-vs-pan decision and
        // the "did the marquee actually happen" decision both read this one number.
        Assert.Equal(4, BoardGestures.DragThresholdPx);
        Assert.False(BoardGestures.IsDrag(4));
        Assert.True(BoardGestures.IsDrag(4.01));
        Assert.False(BoardGestures.IsDrag(0));
    }
}
