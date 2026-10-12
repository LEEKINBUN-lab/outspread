using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>
/// The card's markdown subset: which notes are painted as blocks at all, what each block is, and
/// how tall the block is. The scale numbers are readings from <c>design/feel.html</c>, not choices —
/// see <see cref="HeightsMatchThePrototypeLineBoxes"/> which is the one that keeps the two honest.
/// </summary>
public class MarkdownTests
{
    private static readonly NoteType Fifteen = new(15, 21, 17);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("just words\nsecond line", false)]
    [InlineData("a * b — 单个星号是散文里的破折号不是斜体", false)]
    [InlineData("# 标题", true)]
    [InlineData("## 标题", true)]
    [InlineData("- 一项", true)]
    [InlineData("> 引用", true)]
    [InlineData("正文 **粗**", true)]
    [InlineData("跑 `npm i` 之前", true)]
    [InlineData("看 [文档](https://x)", true)]
    [InlineData("普通一行\n# 只有第二行带标记", true)]
    public void Only_notes_that_carry_markup_become_blocks(string? text, bool expected)
        => Assert.Equal(expected, Markdown.LooksLikeMarkdown(text));

    [Theory]
    [InlineData("# 一", BlockKind.Heading1)]
    [InlineData("## 二", BlockKind.Heading2)]
    [InlineData("### 三", BlockKind.Heading3)]
    [InlineData("#### 四级不认，原样留着", BlockKind.Plain)]
    [InlineData("- 项", BlockKind.Bullet)]
    [InlineData("1. 第一项", BlockKind.Numbered)]
    [InlineData("12) 第十二项", BlockKind.Numbered)]
    [InlineData("---", BlockKind.Divider)]
    [InlineData("***", BlockKind.Divider)]
    [InlineData("____", BlockKind.Divider)]
    [InlineData("- [ ] 待办", BlockKind.TodoOpen)]
    [InlineData("- [x] 完成", BlockKind.TodoDone)]
    [InlineData("> 引用", BlockKind.Quote)]
    [InlineData("普通", BlockKind.Plain)]
    [InlineData("", BlockKind.Plain)]
    public void Each_line_becomes_the_block_it_was_written_as(string text, BlockKind kind)
        => Assert.Equal(kind, Markdown.Lines(text)[0].Kind);

    [Fact]
    public void Heading_scales_are_the_ones_the_page_draws()
    {
        var lines = Markdown.Lines("# 一\n## 二\n### 三\n普通");

        // .md.h1 { font-size: 1.35em }, .md.h2 { font-size: 1.2em }, h3 is weight-only.
        Assert.Equal(1.35, lines[0].Scale, 3);
        Assert.Equal(1.2, lines[1].Scale, 3);
        Assert.Equal(1.0, lines[2].Scale, 3);
        Assert.Equal(1.0, lines[3].Scale, 3);

        // 600 in the page is SemiBold — a heading is bold, but not the 700 a <strong> is.
        Assert.True(lines[0].Bold);
        Assert.True(lines[2].Bold);
        Assert.False(lines[3].Bold);
    }

    [Fact]
    public void A_quote_bars_by_itself_so_the_card_draws_one_bar_not_two()
    {
        var line = Markdown.Lines("> 引用的话")[0];

        Assert.Equal(string.Empty, line.Prefix);
        Assert.Equal("引用的话", line.Text);
    }

    [Theory]
    [InlineData("- 项", "• ")]
    [InlineData("1. 第一项", "1. ")]
    [InlineData("12) 第十二项", "12. ")]
    [InlineData("---", "")]
    [InlineData("- [ ] 待办", "☐ ")]
    [InlineData("- [x] 完成", "☑ ")]
    [InlineData("# 一", "")]
    public void List_marks_travel_in_the_prefix_that_the_measures_see(string text, string prefix)
        => Assert.Equal(prefix, Markdown.Lines(text)[0].Prefix);

    [Fact]
    public void A_numbered_list_keeps_the_number_the_writer_gave_it()
    {
        // 重新编号＝卡片和它自己的文字对不上；作者写 3) 就是 3)。
        var lines = Markdown.Lines("1. 一\n3) 三\n99. 九十九");

        Assert.Equal(["1. ", "3. ", "99. "], lines.Select(line => line.Prefix).ToArray());

        // 四位数不是序号："2024. 回顾这一年" 是一句正文，不该被当成列表第 2024 项。
        Assert.Equal(BlockKind.Plain, Markdown.Lines("2024. 回顾这一年")[0].Kind);
    }

    [Fact]
    public void A_divider_is_one_line_box_of_rule_and_no_words()
    {
        var lines = Markdown.Lines("正文\n---\n正文");

        Assert.Equal(BlockKind.Divider, lines[1].Kind);
        Assert.Equal(string.Empty, lines[1].Text);

        // 它不改变高度算术：三行就是三个行距，分割线那一格和普通行一样高。
        Assert.Equal(Fifteen.Leading * 3, Markdown.BlockHeight(Fifteen, lines), 2);
    }

    [Fact]
    public void Inline_runs_keep_their_order_and_drop_their_markers()
    {
        var spans = Markdown.Inlines("正文 **粗** 与 `码` 和 *斜*");

        Assert.Equal(
            [("正文 ", SpanStyle.None), ("粗", SpanStyle.Bold), (" 与 ", SpanStyle.None),
             ("码", SpanStyle.Code), (" 和 ", SpanStyle.None), ("斜", SpanStyle.Italic)],
            spans.Select(s => (s.Text, s.Style)).ToArray());
    }

    [Fact]
    public void A_link_keeps_its_words_and_loses_its_target()
    {
        var spans = Markdown.Inlines("看 [文档](https://example.com/a)");

        Assert.Equal("看 ", spans[0].Text);
        Assert.Equal("文档", spans[1].Text);
        Assert.Equal(SpanStyle.Link, spans[1].Style);
    }

    [Fact]
    public void An_unmatched_marker_is_the_writers_text_not_a_parse_error()
    {
        var spans = Markdown.Inlines("**没有闭合");

        Assert.Single(spans);
        Assert.Equal("**没有闭合", spans[0].Text);
        Assert.Equal(SpanStyle.None, spans[0].Style);
    }

    [Theory]
    [InlineData(21.0, 21)]
    [InlineData(25.2, 25)]
    [InlineData(28.35, 28)]
    [InlineData(21.5, 22)]
    [InlineData(28.5, 29)]
    [InlineData(28.6, 29)]
    [InlineData(30.5, 31)]
    public void The_line_box_is_what_the_text_engine_was_measured_giving(double leading, int box)
    {
        /* Read out of the running card (the self-test prints the same table): the engine gives a
           whole number of pixels per line, half-up — which is not banker's rounding, and 28.5→29
           is the case that tells the two rules apart. */
        var line = new MarkdownLine(BlockKind.Plain, leading / Fifteen.Leading, string.Empty, []);

        Assert.Equal(leading, Fifteen.Leading * line.Scale, 2);
        Assert.Equal(box, Markdown.LineHeight(Fifteen, line), 0);
    }

    [Fact]
    public void HeightsMatchTheEnginesLineBoxes()
    {
        var lines = Markdown.Lines("# 一\n二\n三\n四");

        // .md.h1 is 1.35em in the page; the box it draws in is 28 here, 28.34px in a browser —
        // under a pixel per heading, and in the direction that keeps the last line inside.
        Assert.Equal(28.35, Fifteen.Leading * lines[0].Scale, 2);
        Assert.Equal(28, Markdown.LineHeight(Fifteen, lines[0]), 0);
        Assert.Equal(91, Markdown.BlockHeight(Fifteen, lines), 0);

        Assert.Equal(4, Markdown.Fits(Fifteen, lines, 91));
        Assert.Equal(3, Markdown.Fits(Fifteen, lines, 90));
    }

    [Fact]
    public void The_budget_and_the_block_are_the_same_sum()
    {
        // Two functions, one rule: whatever Fits admits, BlockHeight must have charged for it.
        var lines = Markdown.Lines("# 一\n- 二\n> 三\n`码`\n普通\n\n## 四");

        for (var room = 0; room <= 200; room++)
        {
            var shown = Markdown.Fits(Fifteen, lines, room);

            Assert.True(Markdown.BlockHeight(Fifteen, lines.Take(shown).ToList()) <= room,
                $"room {room}: {shown} lines but they need {Markdown.BlockHeight(Fifteen, lines.Take(shown).ToList())}");

            if (shown < lines.Count)
            {
                Assert.True(Markdown.BlockHeight(Fifteen, lines.Take(shown + 1).ToList()) > room,
                    $"room {room}: stopped at {shown} while {shown + 1} would have fitted");
            }
        }
    }

    [Fact]
    public void Blank_lines_stay_because_the_writer_put_there()
    {
        var lines = Markdown.Lines("一\n\n二");

        Assert.Equal(3, lines.Count);
        Assert.Empty(lines[1].Spans);
        Assert.Equal(Fifteen.Leading * 3, Markdown.BlockHeight(Fifteen, lines), 2);
    }

    [Fact]
    public void A_plain_note_is_one_line_per_source_line_and_nothing_else()
    {
        // The promise behind LooksLikeMarkdown: a note without markup must not change shape at all.
        var lines = Markdown.Lines("第一行\n第二行");

        Assert.All(lines, line => Assert.Equal(BlockKind.Plain, line.Kind));
        Assert.Equal("第一行", lines[0].Text);
        Assert.False(Markdown.LooksLikeMarkdown("第一行\n第二行"));
    }
}
