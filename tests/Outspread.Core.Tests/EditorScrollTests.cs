namespace Outspread.Core.Tests;

/// <summary>
/// 编辑中那一发滚轮的落点：把正在写的那一格滚进视野，滚不动了就明着滚不动。
/// </summary>
/// <remarks>
/// 这一发以前是缩放整块板子（他 10-24 点名要撤）。几何在这里，界面那侧只剩"搬到相机上"一句，
/// 所以"会不会把字滚出视野"、"到底以后还滚不滚"这两件事都能不打开窗口就钉住。
/// </remarks>
public class EditorScrollTests
{
    private const double Notch = 48;

    [Fact]
    public void Rolling_down_reveals_what_hangs_below_the_fold()
    {
        // 框从 300 站到 900、视口只有 768 ⇒ 底下探出去 132 像素，那一发走满一格 48。
        Assert.Equal(-Notch, EditorScroll.Reveal(300, 600, 768, delta: -120, Notch));
    }

    [Fact]
    public void Rolling_up_reveals_what_hangs_above_the_fold()
    {
        Assert.Equal(Notch, EditorScroll.Reveal(-200, 600, 768, delta: 120, Notch));
    }

    [Fact]
    public void A_notch_stops_at_the_edge_instead_of_overscrolling()
    {
        // 只探出去 12 像素，那一发就走 12，不是走满一格。
        Assert.Equal(-12, EditorScroll.Reveal(0, 780, 768, delta: -120, Notch));
    }

    [Fact]
    public void Fully_visible_means_nothing_to_scroll_and_says_so_with_a_zero()
    {
        // 0 不是"悄悄什么都不做"的许可，是界面要拿去说一句的读数。
        Assert.Equal(0, EditorScroll.Reveal(120, 300, 768, delta: -120, Notch));
        Assert.Equal(0, EditorScroll.Reveal(120, 300, 768, delta: 120, Notch));
    }

    [Fact]
    public void A_wheel_turn_toward_the_empty_side_does_not_guess_the_other_way()
    {
        // 探出的是上沿，他却往下滚：这一发没有"要看下面"的东西，不许反过来搬。
        Assert.Equal(0, EditorScroll.Reveal(-100, 400, 768, delta: -120, Notch));

        // 对称那一发：探出下沿，往上滚。
        Assert.Equal(0, EditorScroll.Reveal(500, 400, 768, delta: 120, Notch));
    }

    [Fact]
    public void A_note_taller_than_the_screen_walks_from_one_end_to_the_other_and_stops()
    {
        const double height = 1_400;
        var top = 0d;

        // 整格比视口高 ⇒ 两头的边各对齐一次视口，中间没有"已经全部看得见"那一档。
        while (EditorScroll.Reveal(top, height, 768, delta: -120, Notch) != 0)
        {
            top += EditorScroll.Reveal(top, height, 768, delta: -120, Notch);
        }

        Assert.Equal(768 - height, top, 6);          // 底边贴住视口底，再多一格也不动
        Assert.Equal(0, EditorScroll.Reveal(top, height, 768, delta: -120, Notch));

        while (EditorScroll.Reveal(top, height, 768, delta: 120, Notch) != 0)
        {
            top += EditorScroll.Reveal(top, height, 768, delta: 120, Notch);
        }

        Assert.Equal(0, top, 6);                     // 顶边贴回视口顶
        Assert.Equal(0, EditorScroll.Reveal(top, height, 768, delta: 120, Notch));
    }

    [Theory]
    [InlineData(100, 0, 768, -120)]      // 框没有高度
    [InlineData(100, 600, 0, -120)]      // 视口还没量出来（窗口刚建、被最小化）
    [InlineData(100, 600, 768, 0)]       // 这一发不是滚轮
    public void Degenerate_geometry_rolls_nowhere(double top, double height, double viewport, int delta)
    {
        Assert.Equal(0, EditorScroll.Reveal(top, height, viewport, delta, Notch));
    }
}
