namespace Outspread.Core.Tests;

/// <summary>
/// The gesture vocabulary, pinned as a decision table.
/// </summary>
/// <remarks>
/// This is the part of "the two products feel nothing alike" that has no excuse for drifting: it is
/// pure input mapping, so it can be asserted without a window. The contract is the prototype's
/// vocabulary minus one row: left drag selects, right drag pans, a still right click opens the menu
/// — and 空格 no longer rides along (he took that one out on 2026-10-24; see below).
/// </remarks>
public class BoardGesturesTests
{
    [Theory]
    [InlineData(false, true, SurfaceAction.Marquee)]      // 左键拖空白＝框选
    [InlineData(true, false, SurfaceAction.Pan)]          // 右键拖＝平移（静止的一发在松手时改成菜单）
    [InlineData(true, true, SurfaceAction.Ignore)]        // 两个键同时按着＝不猜
    [InlineData(false, false, SurfaceAction.Ignore)]      // 中键之类的一发＝交回宿主
    public void A_press_on_empty_space_maps_to_one_action(bool right, bool left, SurfaceAction expected)
    {
        Assert.Equal(expected, BoardGestures.OnEmptyPress(right, left));
    }

    /// <summary>
    /// "空格＋拖＝平移"这一档 10-24 他撤了（"不要平移，不要空格＋拖，正常拖动保留"）。这条钉的是
    /// 撤的**形状**：判定只剩两枚鼠标键，机器那一层的"一发按下"里也没有空格这一格。两处各自单独
    /// 咬过一次（分开做的变异）：把 <c>spaceDown</c> 加回方法 ⇒ 第一格红；把 <c>Space</c> 加回
    /// <c>PressFacts</c> ⇒ 第二格红。第二格读的是**属性**不是字段——record 的字段名带尖括号，
    /// <c>GetField("Space")</c> 永远返回 null，那样这条会假绿（本轮实测抓到过）。
    /// </summary>
    [Fact]
    public void 空格不参与手势判定()
    {
        Assert.Equal(2, typeof(BoardGestures).GetMethod("OnEmptyPress")!.GetParameters().Length);

        Assert.Null(typeof(BoardMachine.PressFacts).GetProperty("Space"));
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
