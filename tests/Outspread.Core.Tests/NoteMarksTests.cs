using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>
/// The due/tag marks: the states a note's text can mean, and what each one owes the surface.
/// </summary>
public class NoteMarksTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);

    [Theory]
    [InlineData(null, NoteMarks.DueState.None)]
    [InlineData("没有标记", NoteMarks.DueState.None)]
    [InlineData("@d2026-10-11\n交周报", NoteMarks.DueState.Today)]
    [InlineData("@d2026-10-09\n交周报", NoteMarks.DueState.Late)]
    [InlineData("@d2026-10-20", NoteMarks.DueState.Upcoming)]
    [InlineData("@d2026/10/20", NoteMarks.DueState.Upcoming)]
    [InlineData("@d2026-2-30", NoteMarks.DueState.Malformed)]
    [InlineData("@d2026-13-1", NoteMarks.DueState.Malformed)]
    // 旧写法一律不再认，而且必须落在 None：# 现在是 markdown 的标题，长式是同一件事的第二种写法
    // ——他要的是一句话只有一种写法（"全都改成@d和@t"）。
    [InlineData("截止 #due:2026-10-11", NoteMarks.DueState.None)]
    [InlineData("写成 #due：2026-10-09 全角冒号也算", NoteMarks.DueState.None)]
    [InlineData("@due(2026-10-11)\n交周报", NoteMarks.DueState.None)]
    [InlineData("# 标题\n正文", NoteMarks.DueState.None)]
    public void Every_due_shape_lands_in_exactly_one_state(string? text, NoteMarks.DueState expected)
    {
        Assert.Equal(expected, NoteMarks.StateOf(text, Today));
    }

    [Fact]
    public void Late_and_today_are_the_only_states_that_paint_an_edge_and_malformed_still_shows()
    {
        Assert.NotNull(NoteMarks.BarFor(NoteMarks.DueState.Late, dark: false));
        Assert.NotNull(NoteMarks.BarFor(NoteMarks.DueState.Today, dark: false));
        Assert.Null(NoteMarks.BarFor(NoteMarks.DueState.Upcoming, dark: false));
        Assert.Null(NoteMarks.BarFor(NoteMarks.DueState.None, dark: false));

        // 说不清的日期必须可见：静默丢掉就等于告诉用户"排上了"。
        Assert.NotNull(NoteMarks.BarFor(NoteMarks.DueState.Malformed, dark: false));

        // 两档都得比板底重，否则色条在纸上看不见（浅档板底 249.9）。
        Assert.True(NoteMarks.BarFor(NoteMarks.DueState.Late, dark: false)!.Value.R < 240);
        Assert.NotEqual(NoteMarks.BarFor(NoteMarks.DueState.Late, dark: false), NoteMarks.BarFor(NoteMarks.DueState.Late, dark: true));
    }

    [Fact]
    public void Today_and_late_are_far_enough_apart_to_read_at_two_pixels()
    {
        // 10-24 他点的：琥珀11（171,100,0）与番茄11（209,52,21）在 2px 条上分不出，
        // 今天改成蓝11。锁的是"两档的 RGB 距离"，不是某一个色号——色号还能再调，糊成一档不许。
        static double Distance(Rgb a, Rgb b)
            => System.Math.Sqrt(System.Math.Pow(a.R - b.R, 2) + System.Math.Pow(a.G - b.G, 2) + System.Math.Pow(a.B - b.B, 2));

        foreach (var dark in new[] { false, true })
        {
            var late = NoteMarks.BarFor(NoteMarks.DueState.Late, dark)!.Value;
            var today = NoteMarks.BarFor(NoteMarks.DueState.Today, dark)!.Value;

            Assert.True(
                Distance(late, today) >= 120,
                $"{(dark ? "深" : "浅")}档今天与逾期只差 {Distance(late, today):0.#}——两条 2px 色条会被看成同一种");
        }
    }

    [Fact]
    public void The_edge_bar_is_two_screen_pixels_and_does_not_scale()
    {
        Assert.Equal(2, NoteMarks.BarWidthPx);
    }

    [Fact]
    public void Tags_are_tallied_most_used_first_then_by_name()
    {
        var tally = NoteMarks.Tally(["@t周会 @t急\n正文", "@t周会\n正文", "@t周会 @t急 @t周会\n正文"]);

        Assert.Equal("#周会", tally[0].Tag);
        Assert.Equal(3, tally[0].Count);
        Assert.Equal("#急", tally[1].Tag);
        Assert.Equal(2, tally[1].Count);
    }

    [Fact]
    public void A_due_marker_never_counts_as_a_tag_in_the_rail()
    {
        var tally = NoteMarks.Tally(["@d2026-10-09 @t周会\n复盘"]);

        Assert.Single(tally);
        Assert.Equal("#周会", tally[0].Tag);
    }

    [Fact]
    public void Filtering_asks_its_three_questions_and_a_lens_that_matches_nothing_shows_nothing()
    {
        var note = "@d2026-10-09 @t周会\n截止";
        var tags = Tags.Of(note);
        var late = NoteMarks.StateOf(note, Today);

        Assert.True(NoteMarks.Shows(late, tags, done: null, tag: null, dueFilter: null, doneFilter: null));
        Assert.True(NoteMarks.Shows(late, tags, done: null, tag: "#周会", dueFilter: NoteMarks.DueState.Late, doneFilter: null));
        Assert.False(NoteMarks.Shows(late, tags, done: null, tag: "#别的", dueFilter: null, doneFilter: null));
        Assert.False(NoteMarks.Shows(late, tags, done: null, tag: null, dueFilter: NoteMarks.DueState.Today, doneFilter: null));

        // 牌子上写的是 #周会，文件里存的是 @t周会：筛选认前者，两边必须对得上。
        Assert.True(NoteMarks.Shows(late, Tags.Read("@t周会\n正文").Tags, done: null, tag: "#周会", dueFilter: null, doneFilter: null));

        // 完成那一档：true 只放过 @b1；false 放过 @b0 **和压根没写的**——待办板上"没写"就是还没做完，
        // 把它单开一档会筛出一块空白。
        Assert.True(NoteMarks.Shows(late, tags, done: true, tag: null, dueFilter: null, doneFilter: true));
        Assert.False(NoteMarks.Shows(late, tags, done: false, tag: null, dueFilter: null, doneFilter: true));
        Assert.False(NoteMarks.Shows(late, tags, done: null, tag: null, dueFilter: null, doneFilter: true));
        Assert.True(NoteMarks.Shows(late, tags, done: false, tag: null, dueFilter: null, doneFilter: false));
        Assert.True(NoteMarks.Shows(late, tags, done: null, tag: null, dueFilter: null, doneFilter: false));
        Assert.False(NoteMarks.Shows(late, tags, done: true, tag: null, dueFilter: null, doneFilter: false));
    }

    [Theory]
    [InlineData("交周报", "")]
    [InlineData("@d2026-10-09\n交周报", "逾期 10-09")]
    [InlineData("@d2026-10-11\n交周报", "今天 10-11")]
    [InlineData("@d2026-10-20\n交周报", "10-20")]
    [InlineData("@d2027-01-05\n交周报", "2027-01-05")]
    [InlineData("@t周会\n交周报", "#周会")]
    [InlineData("@d2026-10-09 @t周会\n交周报", "逾期 10-09 · #周会")]
    [InlineData("@d2026-13-40", "日期读不出")]
    [InlineData("截止 #due:2026-10-09 #周会", "")]
    [InlineData("# 标题\n正文", "")]
    public void Every_mark_gets_exactly_one_line_of_its_own(string text, string expected)
    {
        Assert.Equal(expected, NoteMarks.Readout(Tags.Read(text), Today));
    }

    [Fact]
    public void An_upcoming_date_has_no_edge_bar_but_still_has_a_readout()
    {
        // 色条只给逾期/今天/读不出三档；未来的日子若这行也不写，卡片上就是"没排日程"的样子。
        var marks = Tags.Read("@d2026-10-20\n交周报");

        Assert.Null(NoteMarks.BarFor(NoteMarks.StateOf(marks, Today), dark: false));
        Assert.Equal("10-20", NoteMarks.Readout(marks, Today));
    }

    [Fact]
    public void The_chip_band_is_one_number_that_both_sides_read()
    {
        // 量自动高度的一方与画牌子的一方必须读同一个数，否则最后一行被切——这是老账。
        // 它不随便签字号变（牌子是界面上的东西），只随缩放长（不缩）。
        Assert.Equal(NoteType.ChipBandPx, NoteType.ChipBand(1));

        foreach (var size in new[] { 14d, 15d, 16d })
        {
            // 牌子不跟字号变（它是界面上的东西），但必须始终比正文小声。
            Assert.Equal(NoteType.ChipBandPx, NoteType.ChipBand(1));
            Assert.True(NoteType.ChipFontPx < NoteTypography.For(size).Size);
        }
    }

    [Theory]
    [InlineData("@d2026-10-09", 2026, 10, 9)]      // 横杠
    [InlineData("@d2026/10/9", 2026, 10, 9)]       // 斜杠、不补零：他怎么敲就怎么认
    [InlineData("@d2026-1-1", 2026, 1, 1)]
    public void The_short_date_token_reads_both_separators(string text, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), Tags.Read(text).Due);
        Assert.Equal(text, Tags.Read(text).DueSource);   // 删除时要拿回原文里那一段，不是拿回一个猜测
    }

    [Theory]
    [InlineData("@t周会", "#周会")]
    [InlineData("@t周会、急", "#周会、急")]     // 短 token 一枚就是一枚标签：拆开会变成"删一个带走两个"
    [InlineData("@T周会", "#周会")]                  // 键名不分大小写
    public void The_short_tag_token_names_one_tag_and_keeps_its_source(string text, string expected)
    {
        var marks = Tags.Read(text);

        Assert.Equal(new[] { expected }, marks.Tags);
        Assert.Equal(text, marks.TagSources[expected]);
    }

    [Fact]
    public void Deleting_a_chip_takes_the_token_out_and_leaves_the_body_alone()
    {
        var text = "@d2026-10-09 @t周会\n交周报";

        Assert.Equal("@t周会\n交周报", Tags.Without(text, "@d2026-10-09"));
        Assert.Equal("@d2026-10-09\n交周报", Tags.Without(text, "@t周会"));

        // 全删光 ⇒ 属性行整行消失，不留空行；正文一个字不动。
        var only = Tags.Without(Tags.Without(text, "@d2026-10-09"), "@t周会");
        Assert.Equal("交周报", only);

        // 找不到就不动：宁可什么都不做，也不删一个"看起来像"的东西。
        Assert.Equal(text, Tags.Without(text, "@t没有这枚"));
    }

    [Fact]
    public void A_short_token_inside_prose_is_neither_a_mark_nor_stripped()
    {
        var text = "例：@d2026-10-09 要单独占一行";
        var marks = Tags.Read(text);

        Assert.Null(marks.Due);
        Assert.Empty(marks.Tags);
        Assert.Equal(text, marks.Body);
    }
}
