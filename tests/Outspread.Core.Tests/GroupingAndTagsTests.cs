using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>Groups (geometric membership + frame-press selection), tags/due parsing, and clustering.</summary>
public class GroupingAndTagsTests
{
    private static Grouping.Rect Note(string id, double x, double y, double w = 100, double h = 60)
        => new(id, x, y, w, h);

    [Fact]
    public void A_group_frame_pads_ten_world_pixels_out_on_every_side()
    {
        var frame = Grouping.Enclose("g", [Note("a", 0, 0), Note("b", 200, 100)]);

        Assert.Equal(-Grouping.Pad, frame.X);
        Assert.Equal(-Grouping.Pad, frame.Y);
        Assert.Equal(300 + (2 * Grouping.Pad), frame.Width);
        Assert.Equal(160 + (2 * Grouping.Pad), frame.Height);
    }

    [Fact]
    public void Membership_is_the_centre_not_the_outline()
    {
        var group = Note("g", 0, 0, 100, 100);

        // 中心在框内就算成员，哪怕它大部分身子在外；反过来，压过边框但中心在外就不算。
        Assert.True(group.Holds(Note("in", 90, 90, 20, 20)));
        Assert.False(group.Holds(Note("out", 101, 10)));
    }

    [Fact]
    public void Nested_frames_answer_with_the_innermost_one()
    {
        var outer = Note("outer", 0, 0, 400, 400);
        var inner = Note("inner", 100, 100, 100, 100);

        var hit = Grouping.GroupOf(Note("n", 120, 120), [outer, inner]);

        Assert.Equal("inner", hit?.Id);
    }

    [Fact]
    public void Pressing_the_frame_takes_every_note_it_holds_and_never_the_frame()
    {
        // 10-22 的语义：组是一个关系。点名牌/虚线边＝选中组里所有便签，框自己从不进选中——
        // 它进选中就是"整个组的范围变蓝"，那正是这一版要消灭的形状。
        var group = Note("g", -10, -10, 320, 180);
        var notes = new[] { Note("a", 0, 0), Note("b", 200, 100), Note("far", 900, 900) };

        var ids = Grouping.OnFramePressed(group, notes);

        Assert.Equal(new[] { "a", "b" }, ids);
        Assert.DoesNotContain("g", ids);
    }

    [Fact]
    public void A_note_pressed_on_its_own_needs_no_layer_answer_from_core()
    {
        // 单击便签＝那一枚，没有任何"先看有没有组"的分支：这条测试钉的是旧的两层契约
        // 已经不存在（OnNotePressed 被删除），别让它在哪个角落又长回来。
        Assert.Null(typeof(Grouping).GetMethod("OnNotePressed"));
    }

    [Fact]
    public void Stacking_a_note_onto_two_others_is_a_group_proposal()
    {
        var moved = Note("m", 0, 0, 200, 100);
        var neighbours = new[]
        {
            moved,
            Note("a", 60, 30, 200, 100),   // 叠掉小格的 49%——明显
            Note("b", 120, 60, 200, 100),  // 叠掉 16%——明显
        };

        var touched = Grouping.TouchedBy(moved, neighbours);

        Assert.Equal(new[] { "a", "b" }, touched.Select(rect => rect.Id).ToList());
    }

    [Fact]
    public void A_graze_is_not_a_stack()
    {
        var moved = Note("m", 0, 0, 200, 100);

        // 只压上 2px×100px＝1%——擦肩而过不该被读成"堆成一堆"（阈值 8%）。
        var graze = Note("g", 198, 0, 200, 100);

        Assert.Equal(0.01, Math.Round(Grouping.OverlapOf(moved, graze), 4));
        Assert.Empty(Grouping.TouchedBy(moved, [moved, graze]));

        // 边贴边连面积都没有：0。
        Assert.Equal(0, Grouping.OverlapOf(moved, Note("t", 200, 0, 100, 100)));
    }

    [Fact]
    public void One_corner_inside_the_frame_activates_the_group_drag()
    {
        var frame = Note("g", 0, 0, 300, 300);

        // 只有一角探进框（右下角压在框左上角一点）：中心还在框外，但一角碰上就该激活拖入。
        var corner = Note("c", -120, -120, 200, 200);

        Assert.False(frame.Holds(corner));
        Assert.True(frame.Intersects(corner));

        // 整格离开：不相交——那才是移出。
        Assert.False(frame.Intersects(Note("away", 400, 400, 100, 100)));
    }

    [Fact]
    public void A_hash_in_the_text_is_prose_now_and_stays_on_the_card()
    {
        // 条目9 的正面形状：# 让给 markdown（行首 # 是标题），所以正文里的 #周会、#due:…、#ff6b6b
        // 一律不再是标记，也一个字都不许被摘走。摘走任何一段＝卡片在撒谎。
        var text = "复盘 #周会 截止 #due:2026-10-20 颜色 #ff6b6b 再来一枚 #周会";
        var marks = Tags.Read(text);

        Assert.Empty(marks.Tags);
        Assert.Null(marks.Due);
        Assert.Equal(text, marks.Body);
    }

    [Fact]
    public void A_due_that_is_not_a_real_date_is_reported_not_dropped()
    {
        Assert.True(Tags.TryDue("@d2026-10-20", out var due));
        Assert.Equal(new DateOnly(2026, 10, 20), due);

        Assert.False(Tags.TryDue("@d2026-13-40", out _));
        Assert.Equal("@d2026-13-40", Tags.MalformedDue("@d2026-13-40"));
        Assert.Null(Tags.MalformedDue("没有到期"));
        Assert.Equal("@d2026-2-30", Tags.MalformedDue("@d2026-2-30"));   // 2 月 30 日不是日子：TryParseExact 会把它调成 3/2，所以必须回读校验
    }

    [Fact]
    public void A_property_line_is_read_and_taken_off_the_card()
    {
        var marks = Tags.Read("@d2026-10-09 @t周会\n\n交周报");

        Assert.Equal(new DateOnly(2026, 10, 9), marks.Due);
        Assert.Equal(new[] { "#周会" }, marks.Tags);
        Assert.Equal("交周报", marks.Body);
        Assert.True(marks.HasMarks);
    }

    [Fact]
    public void A_token_inside_prose_stays_exactly_where_it_was_written()
    {
        // 演示板上有格子在讲这套写法本身。若行内的 token 也被摘走，那格会把自己的例子吃掉。
        var text = "例：@d2026-10-09 这种属性要单独占一行";

        var marks = Tags.Read(text);

        Assert.Null(marks.Due);
        Assert.Empty(marks.Tags);
        Assert.Equal(text, marks.Body);
    }

    [Theory]
    [InlineData("@t周会", new string[] { "#周会" })]
    [InlineData("@T周会", new string[] { "#周会" })]                 // 键名不分大小写
    [InlineData("@t#周会", new string[] { "#周会" })]                // 已经是规范形状就不要再加一枚 #
    [InlineData("@t周会 @t急", new string[] { "#周会", "#急" })]      // 一行几枚，各自一枚
    [InlineData("@t周会、急", new string[] { "#周会、急" })]           // 短式到空格为止：拆开会变成"删一个带走两个"
    [InlineData("@t", new string[] { })]                            // 空标签是"没有"，不是错误
    public void Tag_tokens_normalise_to_one_canonical_name(string text, string[] expected)
    {
        Assert.Equal(expected, Tags.Read(text + "\n正文").Tags);
    }

    [Fact]
    public void One_line_one_date_and_the_first_believable_one_wins()
    {
        Assert.Equal(new DateOnly(2026, 10, 9), Tags.Read("@d2026-10-09 @d2026-11-11").Due);

        // 两种分隔符是同一个日子（他打字时混着用）。
        Assert.Equal(Tags.Read("@d2026-10-09").Due, Tags.Read("@d2026/10/9").Due);

        // 写在正文里的一律不算：属性行必须独占一行。
        Assert.Null(Tags.Read("备忘\n@d2026-10-09 顺手记一句").Due);
    }

    [Theory]
    [InlineData("@d2026-13-40")]
    [InlineData("@d2026-2-30")]
    [InlineData("@d明天")]
    [InlineData("@d")]
    public void An_unreadable_date_on_a_property_line_is_still_a_deadline_attempt(string text)
    {
        var marks = Tags.Read(text);

        Assert.Null(marks.Due);
        Assert.NotNull(marks.MalformedDue);
        Assert.True(marks.HasMarks);
        Assert.Equal(string.Empty, marks.Body);   // 这一行被当属性收走了，但 HasMarks 让它必须留下可见的痕迹
    }

    [Fact]
    public void The_retired_bracket_spelling_is_not_a_mark_and_blames_nobody()
    {
        // @due(…) 会被短式读成 @d + "ue(…)" ⇒ 若照读就是"你写了个读不出的日期"，
        // 而那个人写的明明是个合法日期。整行退回正文：卡面 = 文件。
        var marks = Tags.Read("@due(2026-10-09)");

        Assert.Null(marks.Due);
        Assert.Null(marks.MalformedDue);
        Assert.False(marks.HasMarks);
        Assert.Equal("@due(2026-10-09)", marks.Body);

        Assert.Equal(new[] { "#周会" }, Tags.Read("@t周会\n复盘 #周会").Tags);
    }

    [Fact]
    public void A_plain_note_costs_no_parsing_and_keeps_its_text()
    {
        var marks = Tags.Read("买菜，买牛奶");

        Assert.Equal("买菜，买牛奶", marks.Body);
        Assert.False(marks.HasMarks);
    }

    [Fact]
    public void Clustering_lays_one_row_per_key_in_first_appearance_order()
    {
        var items = new[]
        {
            new Aligning.Placed("r1", 0, 0, 100, 60),
            new Aligning.Placed("g1", 500, 0, 120, 80),
            new Aligning.Placed("r2", 900, 0, 100, 60),
        };

        var outcome = Aligning.Cluster(items, ["red", "green", "red"]);
        var moves = outcome.Moves.ToDictionary(move => move.Id, move => (move.X, move.Y));

        Assert.Equal(40, moves["r1"].X);
        Assert.Equal(40 + 100 + Aligning.MinGapPx, moves["r2"].X);   // 同簇接在同一行后面
        Assert.Equal(40, moves["g1"].X);
        Assert.Equal(40 + 60 + 48, moves["g1"].Y);                   // 第二行：第一行最高者 + rowGap
        Assert.True(outcome.Ok);
    }

    [Fact]
    public void Clustering_refuses_a_board_it_cannot_key()
    {
        var items = new[] { new Aligning.Placed("a", 0, 0, 10, 10) };

        Assert.False(Aligning.Cluster(items, ["x"]).Ok);
        Assert.False(Aligning.Cluster(items, ["x", "y"]).Ok);   // 键数与格子数不等 ⇒ 不猜
    }

    /// <summary>
    /// 把一格成员拖远，框会拉长去接它（<c>KeepDraggedMemberInFrame</c>，10-22 定的"普通拖动不许默默脱组"）。
    /// 这条钉的是**现在的行为**，也正是他 10-24 报那一格的机制：框一拉长，路上任何一格只要中心落进来
    /// 就越算成员；虚线边拉到屏幕外，看着就像"分组消失了"。改法（拖出＝脱组，或成员记成 id 列表
    /// 不再靠几何）等他定——定之前这条测试替我们看着它没被顺手改掉。
    /// </summary>
    [Fact]
    public void Dragging_one_member_far_away_stretches_the_frame_and_swallows_whatever_is_on_the_way()
    {
        var a = Note("a", 0, 0);
        var b = Note("b", 200, 0);
        var onTheWay = Note("c", 1_000, 0);

        var before = Grouping.Enclose("g", [a, b]);

        Assert.DoesNotContain(onTheWay.Id, Grouping.Members(before, [a, b, onTheWay]).Select(member => member.Id));

        var dragged = Note("b", 2_000, 0);
        var after = Grouping.Enclose("g", [a, dragged]);

        Assert.Equal(2_100 + (2 * Grouping.Pad), after.Width);
        Assert.Contains(onTheWay.Id, Grouping.Members(after, [a, dragged, onTheWay]).Select(member => member.Id));
    }
}
