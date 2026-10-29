namespace Outspread.Core.Tests;

/// <summary>
/// 视口内"被上面那几格完全盖住"的判定：摘掉的必须是屏幕上真的一颗像素都不剩的。
/// </summary>
/// <remarks>
/// 这一组用例的每一个都对着一种"摘错"的形状：只看纸盒会连别人底下的阴影带一起摘掉（用例 2）、
/// 只判单矩形包含会漏掉一摞里"两格各盖一半"那种（用例 3）、把摘掉的那格继续当遮挡源会一路摘到底
/// （用例 7）。遮挡剔除错一格的代价是屏幕上少一张卡，所以这里宁可判得保守。
/// </remarks>
public class OcclusionTests
{
    private static Occlusion.Cell Note(string id, double x, double y, double w = 100, double h = 60, bool canCover = true, bool canCull = true)
        => new(id, Rect(id, x, y, w, h), Rect(id, x, y, w, h + Reach), canCover, canCull);

    /// <summary>与产品同一个上界，不在测试里另刻一个数（另刻的那个会跟着阴影参数漂）。</summary>
    private static double Reach => NoteSurface.MaxShadowReachWorld;

    private static Grouping.Rect Rect(string id, double x, double y, double w, double h)
        => new(id, x, y, w, h);

    [Fact]
    public void A_cell_fully_under_one_later_cell_is_dropped()
    {
        var culled = Occlusion.CulledCells([Note("a", 0, 0), Note("b", 0, 0)]);

        Assert.Equal(["a"], culled);
    }

    [Fact]
    public void The_cell_on_top_is_never_dropped_by_the_one_under_it()
    {
        // 绘制顺序是输入的一部分：后交进来的压在前面那格上面，所以它自己不该被摘。
        var culled = Occlusion.CulledCells([Note("a", 0, 0), Note("b", 0, 0)]);

        Assert.DoesNotContain("b", culled);
    }

    [Fact]
    public void A_shadow_band_escaping_below_the_cover_leaves_the_cell_painted()
    {
        // 遮挡源只盖到纸盒底：下面那格的阴影带还伸到盒底之下 Reach 那么多，那一截没被盖住 ⇒ 不能摘。
        var cover = Rect("b", 0, 0, 100, 60);
        var lower = new Occlusion.Cell("a", Rect("a", 0, 0, 100, 60), Rect("a", 0, 0, 100, 60 + Reach), true, true);
        var upper = new Occlusion.Cell("b", cover, cover, true, true);

        Assert.False(Occlusion.Covers([cover], lower.Drawn));
        Assert.Empty(Occlusion.CulledCells([lower, upper]));
    }

    [Fact]
    public void Two_cells_that_each_cover_half_drop_the_one_they_both_sit_on()
    {
        // 一摞里压着下面那格的常常是上面好几格各占一条：单矩形包含在这里会漏判。
        var bottom = Note("a", 0, 0, 100, 60);
        var left = Note("l", 0, 0, 50, 60 + Reach);
        var right = Note("r", 50, 0, 50, 60 + Reach);

        Assert.Equal(["a"], Occlusion.CulledCells([bottom, left, right]));
    }

    [Fact]
    public void A_one_pixel_slit_keeps_the_cell()
    {
        var bottom = Note("a", 0, 0, 100, 60);
        var left = Note("l", 0, 0, 49.9, 60 + Reach);
        var right = Note("r", 50, 0, 50, 60 + Reach);

        Assert.Empty(Occlusion.CulledCells([bottom, left, right]));
    }

    [Fact]
    public void An_empty_group_frame_covers_nothing()
    {
        // 组框的纸面是透明的：它压在上面也不挡住任何东西。
        var note = Note("a", 0, 0);
        var frame = new Occlusion.Cell("g", Rect("g", 0, 0, 400, 400), Rect("g", 0, 0, 400, 400), CanCover: false, CanCull: false);

        Assert.Empty(Occlusion.CulledCells([note, frame]));
    }

    [Fact]
    public void A_dimmed_or_folded_cell_covers_nothing()
    {
        var note = Note("a", 0, 0);
        var dimmed = Note("d", 0, 0, canCover: false);

        Assert.Empty(Occlusion.CulledCells([note, dimmed]));
    }

    [Fact]
    public void A_dropped_cell_stops_being_a_cover_itself()
    {
        // a 被 b 盖住、b 又被 c 盖住：b 这一拍什么都不画，所以它不能替 c 去判 a 可见与否。
        // 这条是"摘到底"的那道闸——三层同位叠放时，只有最上面那一格该活着。
        var a = Note("a", 0, 0);
        var b = Note("b", 0, 0);
        var c = Note("c", 0, 0);

        Assert.Equal(["a", "b"], Occlusion.CulledCells([a, b, c]));
    }

    [Fact]
    public void A_cell_that_must_stay_selectable_is_never_offered_for_dropping()
    {
        var selected = Note("a", 0, 0, canCull: false);
        var onTop = Note("b", 0, 0);

        Assert.Empty(Occlusion.CulledCells([selected, onTop]));
    }

    [Fact]
    public void Side_by_side_cells_drop_nobody()
    {
        var a = Note("a", 0, 0);
        var b = Note("b", 120, 0);

        Assert.Empty(Occlusion.CulledCells([a, b]));
    }

    [Fact]
    public void Nothing_is_dropped_when_there_is_only_one_cell()
    {
        Assert.Empty(Occlusion.CulledCells([Note("a", 0, 0)]));
        Assert.Empty(Occlusion.CulledCells([]));
    }

    [Fact]
    public void The_reach_constant_is_an_upper_bound_for_every_note()
    {
        // 遮挡判定用的是这一个常数，所以它必须真的盖得住逐格算出来的那些数——差一档就会摘错。
        // 这一条同时是正对照：把 MaxShadowReachWorld 改小到任何一格的实测值之下，它会红。
        var worst = 0d;

        foreach (var id in new[] { "u1", "u2", "aaaaaaaaaaaa", "p0", "note-7", "x", "abcdefghijklmnop" })
        {
            worst = Math.Max(worst, NoteSurface.ShadowReachWorld(id));
        }

        Assert.True(worst > 0, "阴影得伸出去一点，否则这条用例没在量任何东西");
        Assert.True(NoteSurface.MaxShadowReachWorld >= worst, $"上界 {NoteSurface.MaxShadowReachWorld} 盖不住逐格实测的 {worst}");
    }
}
