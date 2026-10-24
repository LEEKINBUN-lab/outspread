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
    public void Arrange_packs_each_key_into_a_matrix_instead_of_one_long_row()
    {
        var items = Enumerable.Range(0, 5)
            .Select(index => new Aligning.Placed($"n{index}", 0, 0, 100, 60))
            .ToList();

        var outcome = Aligning.Arrange(
            items,
            Enumerable.Repeat("red", 5).ToList(),
            Enumerable.Repeat(4, 5).ToList(),
            []);

        var moves = outcome.Moves.ToDictionary(move => move.Id, move => (move.X, move.Y));

        Assert.True(outcome.Ok);

        // ⌈√5⌉ = 3 列：前三个一行，后两个在第二行。
        Assert.Equal(40, moves["n0"].X);
        Assert.Equal(40 + 100 + Aligning.MinGapPx, moves["n1"].X);
        Assert.Equal((40 + (2 * (100 + Aligning.MinGapPx))), moves["n2"].X);
        Assert.Equal(40, moves["n3"].X);
        Assert.Equal(40 + 60 + Aligning.MinGapPx, moves["n3"].Y);
        Assert.Equal(moves["n3"].Y, moves["n4"].Y);
    }

    [Fact]
    public void Arrange_leaves_a_bigger_gap_between_keys_than_inside_one()
    {
        var items = new[]
        {
            new Aligning.Placed("r1", 0, 0, 100, 60),
            new Aligning.Placed("r2", 0, 0, 100, 60),
            new Aligning.Placed("g1", 0, 0, 100, 60),
        };

        var outcome = Aligning.Arrange(
            items,
            ["red", "red", "green"],
            [4, 4, 4],
            []);

        var moves = outcome.Moves.ToDictionary(move => move.Id, move => (move.X, move.Y));

        Assert.True(Aligning.BucketGapPx > Aligning.MinGapPx, "块间距必须比块内大，否则看不出分组");
        Assert.Equal(40 + 60 + Aligning.BucketGapPx, moves["g1"].Y);
    }

    [Fact]
    public void Arrange_puts_overdue_first_then_today_then_upcoming_then_undated()
    {
        var items = new[]
        {
            new Aligning.Placed("undated", 0, 0, 100, 60),
            new Aligning.Placed("late", 0, 0, 100, 60),
            new Aligning.Placed("future", 0, 0, 100, 60),
        };

        // 0=逾期、1=今天、2=将来、3=读不出、4=无日期。
        var outcome = Aligning.Arrange(items, ["red", "red", "red"], [4, 0, 2], []);
        var moves = outcome.Moves.ToDictionary(move => move.Id, move => (move.X, move.Y));

        Assert.Equal(40, moves["late"].X);                       // 第一格
        Assert.Equal(40 + 100 + Aligning.MinGapPx, moves["future"].X);
        Assert.Equal(40, moves["undated"].X);                    // 第二行第一格
        Assert.Equal(40 + 60 + Aligning.MinGapPx, moves["undated"].Y);
    }

    [Fact]
    public void Arrange_keeps_linked_notes_next_to_each_other_within_a_tier()
    {
        var items = new[]
        {
            new Aligning.Placed("a", 0, 0, 100, 60),
            new Aligning.Placed("b", 0, 0, 100, 60),
            new Aligning.Placed("c", 0, 0, 100, 60),
            new Aligning.Placed("d", 0, 0, 100, 60),
        };

        // 同一档里 b 与 d 有连线：排出来它们必须挨着（行优先相邻），而不是被 c 隔开。
        var outcome = Aligning.Arrange(items, ["red", "red", "red", "red"], [4, 4, 4, 4], [("b", "d")]);
        var moves = outcome.Moves.ToDictionary(move => move.Id, move => (move.X, move.Y));

        var ordered = outcome.Moves
            .OrderBy(move => move.Y)
            .ThenBy(move => move.X)
            .Select(move => move.Id)
            .ToList();

        var b = ordered.IndexOf("b");
        var d = ordered.IndexOf("d");

        Assert.Equal(1, Math.Abs(b - d));
        Assert.Equal(40, moves["a"].X);   // 第一个还是叠放顺序里的第一格
    }

    [Fact]
    public void Arrange_places_buckets_that_share_links_next_to_each_other()
    {
        var items = new[]
        {
            new Aligning.Placed("a", 0, 0, 100, 60),
            new Aligning.Placed("b", 0, 0, 100, 60),
            new Aligning.Placed("c", 0, 0, 100, 60),
        };

        // red 与 blue 有连线、green 没有：blue 的块要排在 green 前面（离 red 更近）。
        var outcome = Aligning.Arrange(items, ["red", "green", "blue"], [4, 4, 4], [("a", "c")]);
        var moves = outcome.Moves.ToDictionary(move => move.Id, move => (move.X, move.Y));

        Assert.True(moves["c"].Y < moves["b"].Y, "有连线的两块没有挨着，线会横穿整块板");
    }

    [Fact]
    public void Arrange_refuses_a_board_it_cannot_key()
    {
        var items = new[] { new Aligning.Placed("a", 0, 0, 10, 10) };

        Assert.False(Aligning.Arrange(items, ["x"], [4], []).Ok);
        Assert.False(Aligning.Arrange(items, ["x", "y"], [4], []).Ok);   // 键数与格子数不等 ⇒ 不猜
        Assert.False(Aligning.Arrange(items, ["x"], [4, 4], []).Ok);     // 优先级条数对不上也不猜
    }

    /// <summary>
    /// 几何那一档会吞人——这条钉的是**几何归属本身的行为**，不是界面上的规矩。
    /// </summary>
    /// <remarks>
    /// 他 10-24 报的两格（新建在框里当场入组、拖走一格把框拉长到吞掉路上别人）就是这套几何的代价。
    /// 界面上的归属已经换成名单（<see cref="GroupMap"/>、组节点的 <c>members</c>）：普通拖动不改归属，
    /// 只有组钮那一路入组／出组。这里留下的两件事是真的：没写名单的框（别的工具写的、那份改动之前的
    /// 板子）仍按几何收人；<see cref="Grouping.Enclose"/> 也确实会把框长到罩住最远的那一格。
    /// </remarks>
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

    [Fact]
    public void PlaceNear_keeps_the_block_at_the_anchor_when_the_way_is_clear()
    {
        var block = new[]
        {
            new Aligning.Placed("a", 40, 40, 100, 60),
            new Aligning.Placed("b", 164, 40, 100, 60),
        };

        var spot = Aligning.PlaceNear(block, [], anchorX: 300, anchorY: 200);

        Assert.Equal(300, spot.X);
        Assert.Equal(200, spot.Y);
    }

    [Fact]
    public void PlaceNear_moves_the_block_off_an_obstacle_but_stays_close()
    {
        var block = new[] { new Aligning.Placed("a", 0, 0, 100, 60) };
        var obstacles = new[] { new Aligning.Placed("other", 350, 200, 100, 60) };

        var spot = Aligning.PlaceNear(block, obstacles, anchorX: 300, anchorY: 200, margin: 12, step: 32);
        var moved = new Aligning.Placed("a", spot.X, spot.Y, 100, 60);

        // 不压、留缝：与障碍在两个轴上都不相交（含 12px 空隙）。
        Assert.True(
            moved.X + moved.Width + 12 <= 350
            || moved.X >= 450 + 12
            || moved.Y + moved.Height + 12 <= 200
            || moved.Y >= 260 + 12,
            $"放到 ({spot.X},{spot.Y})，还是压着/贴着障碍");

        // 就近：往左两圈（64px）就够让开。
        var away = Math.Max(Math.Abs(spot.X - 300), Math.Abs(spot.Y - 200));
        Assert.True(away <= 64, $"跑到 {away}px 之外了，不算“最近”");
    }

    [Fact]
    public void PlaceNear_uses_the_blocks_own_bounds_not_the_first_note()
    {
        // 块里最靠左上的一格不是第一枚：必须按整块的外接框来量，否则会压到左边。
        var block = new[]
        {
            new Aligning.Placed("a", 140, 40, 100, 60),
            new Aligning.Placed("b", 40, 120, 100, 60),
        };
        var obstacles = new[] { new Aligning.Placed("other", 20, 10, 200, 200) };

        var spot = Aligning.PlaceNear(block, obstacles, anchorX: 30, anchorY: 20, margin: 10, step: 20);
        var width = 240;   // 40..240
        var height = 140;  // 40..180

        Assert.True(
            spot.X + width + 10 <= 20
            || spot.X >= 220 + 10
            || spot.Y + height + 10 <= 10
            || spot.Y >= 210 + 10,
            $"放到 ({spot.X},{spot.Y})，整块还在障碍里");
    }

    [Fact]
    public void Arrange_can_put_one_bucket_last_so_untagged_notes_read_below_the_tagged_ones()
    {
        // 无标签那档即使最大、也连得最多，设了 lastKey 就必须垫底。
        var items = new[]
        {
            new Aligning.Placed("a", 0, 0, 100, 60),
            new Aligning.Placed("b", 0, 0, 100, 60),
            new Aligning.Placed("c", 0, 0, 100, 60),
            new Aligning.Placed("d", 0, 0, 100, 60),
            new Aligning.Placed("e", 0, 0, 100, 60),
        };

        var outcome = Aligning.Arrange(
            items,
            ["#无标签", "#无标签", "#无标签", "#周会", "#周会"],
            [4, 4, 4, 4, 4],
            [("a", "d")],
            lastKey: "#无标签");

        var moves = outcome.Moves.ToDictionary(move => move.Id, move => move.Y);
        var taggedBottom = Math.Max(moves["d"], moves["e"]);

        Assert.All(new[] { "a", "b", "c" }, id => Assert.True(moves[id] > taggedBottom, $"{id} 应排在带标签的两格下面"));
    }

    [Fact]
    public void Arrange_bucket_gap_is_visibly_larger_than_the_inside_gap()
    {
        Assert.True(Aligning.BucketGapPx >= 3 * Aligning.MinGapPx, "块间距至少是块内的三倍，才看得出分组");
    }

    [Fact]
    public void A_sequence_marker_reads_as_a_number_and_leaves_the_body_alone()
    {
        var marks = Tags.Read("@n2\n第二件");

        Assert.Equal(2, marks.Sequence);
        Assert.Equal("@n2", marks.SequenceSource);
        Assert.Equal("第二件", marks.Body);
        Assert.True(marks.HasMarks);
        Assert.Equal("2 号", NoteMarks.Readout(marks, new DateOnly(2026, 10, 11)));

        var chip = NoteMarks.Chips(marks, new DateOnly(2026, 10, 11)).Single(item => !item.IsDate);

        Assert.Equal("2 号", chip.Label);
        Assert.Equal("@n2", chip.Source);
    }

    [Fact]
    public void Non_numeric_n_tokens_are_ordinary_text_not_swallowed()
    {
        var marks = Tags.Read("@nfoo\n正文");

        Assert.Null(marks.Sequence);
        Assert.Contains("@nfoo", marks.Body);
    }

    [Fact]
    public void The_first_sequence_wins_and_a_second_one_is_not_a_second_number()
    {
        var marks = Tags.Read("@n3 @n9\n正文");

        Assert.Equal(3, marks.Sequence);
        Assert.Equal("@n3", marks.SequenceSource);
    }
}
