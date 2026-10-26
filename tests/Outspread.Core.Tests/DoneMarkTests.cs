namespace Outspread.Core.Tests;

/// <summary>
/// <c>@b0</c>／<c>@b1</c> 那枚完成标记（板上 @n2）。真身在正文里，牌子只是画法：
/// 点牌子＝在 0↔1 之间换，点 ×＝把这段拿掉，所以"换"与"删"两条路都得钉住。
/// </summary>
public class DoneMarkTests
{
    [Theory]
    [InlineData("@b1\n写完一页", true, "写完一页")]
    [InlineData("@b0\n还没动", false, "还没动")]
    [InlineData("@d2026-11-30 @b1\n同一行两个标记", true, "同一行两个标记")]
    public void A_b_line_reads_as_one_state(string text, bool done, string body)
    {
        var marks = Tags.Read(text);

        Assert.Equal(done, marks.Done);
        Assert.NotNull(marks.DoneSource);
        Assert.Equal(body, marks.Body);
    }

    [Theory]
    [InlineData("@b2\n这是正文")]
    [InlineData("@b\n这是正文")]
    [InlineData("@bdone\n这是正文")]
    public void Anything_that_is_not_zero_or_one_stays_the_writers_words(string text)
    {
        var marks = Tags.Read(text);

        Assert.Null(marks.Done);

        // 别把人家的话吃掉：@b2 不是标记，那一行连同后面那句都得留在正文里。
        Assert.Contains("@b", marks.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chip_says_the_word_and_deletes_the_span_it_came_from()
    {
        var chips = NoteMarks.Chips(Tags.Read("@b1\n正文"), new DateOnly(2026, 10, 25));
        var chip = Assert.Single(chips, mark => mark.Source == "@b1");

        Assert.Equal("已完成", chip.Label);
        Assert.False(chip.IsDate);

        Assert.Equal("已完成", NoteMarks.Readout(Tags.Read("@b1\n正文"), new DateOnly(2026, 10, 25)));
        Assert.Equal("未完成", NoteMarks.Readout(Tags.Read("@b0\n正文"), new DateOnly(2026, 10, 25)));

        var removed = Tags.Without("@b1\n正文", "@b1");

        Assert.Equal("正文", removed);
        Assert.Null(Tags.Read(removed).Done);
    }

    [Fact]
    public void Toggling_rewrites_the_same_span_and_keeps_everything_else()
    {
        var flipped = Tags.SetDone("@d2026-11-30 @b0\n第 1 行\n第 2 行", true);

        Assert.Equal("@d2026-11-30 @b1\n第 1 行\n第 2 行", flipped);
        Assert.True(Tags.Read(flipped).Done);
        Assert.Equal(new DateOnly(2026, 11, 30), Tags.Read(flipped).Due);

        // 换回去：一枚开关不该攒出第二枚 @b。
        var back = Tags.SetDone(flipped, false);

        Assert.Equal(1, back.Split('@').Count(part => part.StartsWith("b", StringComparison.Ordinal)));
        Assert.False(Tags.Read(back).Done);
    }

    [Fact]
    public void A_note_without_the_mark_gets_one_on_its_own_line_and_keeps_its_body()
    {
        var added = Tags.SetDone("先写一句", true);

        Assert.Equal("@b1\n先写一句", added);
        Assert.Equal("先写一句", Tags.Read(added).Body);

        // 已有别的标记时补进属性块，不新起一行正文——否则那一行会被当成正文。
        var withProps = Tags.SetDone("@t周会\n正文", false);

        Assert.Contains("@t周会", withProps, StringComparison.Ordinal);
        Assert.Contains("@b0", withProps, StringComparison.Ordinal);
        Assert.Equal("正文", Tags.Read(withProps).Body);
    }

    [Fact]
    public void The_first_b_wins_and_a_second_one_is_not_a_second_state()
    {
        var marks = Tags.Read("@b1 @b0\n正文");

        Assert.True(marks.Done);
        Assert.Equal("@b1", marks.DoneSource);
        Assert.Equal("正文", marks.Body);
    }

    [Fact]
    public void AddTag_appends_once_on_a_property_line_and_keeps_other_marks()
    {
        var added = Tags.AddTag("@n3\n正文", "周会");
        var marks = Tags.Read(added);

        Assert.Contains("#周会", marks.Tags);
        Assert.Equal(3, marks.Sequence);
        Assert.Equal("正文", marks.Body);
        Assert.Equal(added, Tags.AddTag(added, "#周会"));
        Assert.Equal(added, Tags.AddTag(added, "@t周会"));
    }
}
