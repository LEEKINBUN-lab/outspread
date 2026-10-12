using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>BoardSearch.Find: AND-of-terms, field priority, snippet windowing and the offset invariant.</summary>
public class BoardSearchTests
{
    /// <summary>A one-node board built around a single text body — the case almost every test needs.</summary>
    private static CanvasDocument Board(string id, string? text = null, string? label = null, string? url = null, string? file = null, CanvasNodeKind kind = CanvasNodeKind.Text)
    {
        var document = CanvasDocument.CreateNew();
        var node = document.Add(kind, 0, 0, 200, 60, id: id);

        node.Text = text;
        node.Label = label;
        node.Url = url;
        node.FilePath = file;

        return document;
    }

    private static void AssertOffsetsInBoard(CanvasDocument document, string query)
    {
        foreach (var hit in BoardSearch.Find(document, query))
        {
            Assert.InRange(hit.MatchStart, 0, hit.Snippet.Length);
            Assert.InRange(hit.MatchStart + hit.MatchLength, 0, hit.Snippet.Length);
            Assert.False(hit.Snippet.Contains('\n'));
            Assert.False(hit.Snippet.Contains('\r'));
        }
    }

    [Fact]
    public void An_empty_query_finds_nothing_because_it_means_not_typed_yet()
    {
        var document = Board("n1", text: "接口层的延迟");

        Assert.Empty(BoardSearch.Find(document, ""));
        Assert.Empty(BoardSearch.Find(document, "   "));
        Assert.Empty(BoardSearch.Find(document, "\t  \n"));
    }

    [Fact]
    public void A_single_chinese_term_highlights_where_it_sits_in_the_body()
    {
        // 中文没有词界，子串就是正确答案：延迟来自接口层，"接口"在第四个字。
        var document = Board("n1", text: "延迟来自接口层");

        var hits = BoardSearch.Find(document, "接口");

        var hit = Assert.Single(hits);
        Assert.Equal("n1", hit.NodeId);
        Assert.Equal(CanvasNodeKind.Text, hit.Kind);
        Assert.Equal("延迟来自接口层", hit.Snippet);
        Assert.Equal(4, hit.MatchStart);
        Assert.Equal(2, hit.MatchLength);
        Assert.Equal("接口", hit.Snippet.Substring(hit.MatchStart, hit.MatchLength));
    }

    [Theory]
    [InlineData("接口 延迟")]
    [InlineData("延迟 接口")]                 // 词的顺序不重要，重要的是都要在
    [InlineData("延迟 接口 层")]
    public void Every_term_must_be_present_and_the_order_does_not_matter(string query)
    {
        var document = Board("n1", text: "延迟来自接口层");

        Assert.Single(BoardSearch.Find(document, query));
    }

    [Fact]
    public void A_node_missing_one_term_is_not_a_match_however_close_the_others_are()
    {
        var document = Board("n1", text: "延迟来自接口层");

        Assert.Empty(BoardSearch.Find(document, "延迟 数据库"));
    }

    [Fact]
    public void A_term_that_only_lives_in_the_url_still_lands_on_the_link()
    {
        var document = Board("link", url: "https://example.com/quarterly", kind: CanvasNodeKind.Link);

        var hit = Assert.Single(BoardSearch.Find(document, "quarterly"));

        Assert.Equal(CanvasNodeKind.Link, hit.Kind);
        Assert.Equal("quarterly", hit.Snippet.Substring(hit.MatchStart, hit.MatchLength));
    }

    [Fact]
    public void A_term_that_only_lives_in_the_label_still_lands_on_the_group()
    {
        // 分组没有正文，只有名字；搜名字要能落在这块框上。
        var document = Board("group", label: "周会记录", kind: CanvasNodeKind.Group);

        var hit = Assert.Single(BoardSearch.Find(document, "周会"));

        Assert.Equal(CanvasNodeKind.Group, hit.Kind);
        Assert.Equal("周会记录", hit.Snippet);
        Assert.Equal("周会", hit.Snippet.Substring(hit.MatchStart, hit.MatchLength));
    }

    [Fact]
    public void When_both_the_body_and_the_label_carry_the_word_the_body_is_what_shows()
    {
        var document = Board("n1", text: "正文里的接口说明", label: "接口", kind: CanvasNodeKind.Group);

        var hit = Assert.Single(BoardSearch.Find(document, "接口"));

        Assert.Equal("正文里的接口说明", hit.Snippet);   // Text 优先，片段从正文来
    }

    [Fact]
    public void Latin_terms_match_without_regard_to_case()
    {
        var document = Board("n1", text: "Report Draft");

        var hit = Assert.Single(BoardSearch.Find(document, "report"));

        Assert.Equal(0, hit.MatchStart);
        Assert.Equal("Report", hit.Snippet.Substring(hit.MatchStart, hit.MatchLength));
    }

    [Fact]
    public void A_board_with_nothing_to_find_returns_empty()
    {
        var document = Board("n1", text: "买菜，买牛奶");

        Assert.Empty(BoardSearch.Find(document, "接口"));
    }

    [Fact]
    public void The_limit_caps_the_list_and_keeps_the_front_of_the_board()
    {
        var document = CanvasDocument.CreateNew();

        foreach (var id in new[] { "a", "b", "c" })
        {
            document.Add(CanvasNodeKind.Text, 0, 0, 100, 40, id: id).Text = "苹果";
        }

        var hits = BoardSearch.Find(document, "苹果", limit: 2);

        Assert.Equal(2, hits.Count);
        Assert.Equal(["a", "b"], hits.Select(hit => hit.NodeId));   // 三个词位置相同，按板上顺序取前二
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_limit_finds_nothing(int limit)
    {
        var document = Board("n1", text: "接口");

        Assert.Empty(BoardSearch.Find(document, "接口", limit));
    }

    [Fact]
    public void Two_identical_nodes_come_back_in_board_order_not_in_id_order()
    {
        var document = CanvasDocument.CreateNew();
        document.Add(CanvasNodeKind.Text, 0, 0, 100, 40, id: "zeta").Text = "报告";
        document.Add(CanvasNodeKind.Text, 0, 0, 100, 40, id: "alpha").Text = "报告";

        var hits = BoardSearch.Find(document, "报告");

        Assert.Equal(["zeta", "alpha"], hits.Select(hit => hit.NodeId));   // 文档顺序是 z 序，谁先摆谁在前
    }

    [Fact]
    public void A_note_whose_word_sits_earlier_outranks_one_where_it_sits_later()
    {
        var document = CanvasDocument.CreateNew();
        document.Add(CanvasNodeKind.Text, 0, 0, 100, 40, id: "late").Text = "开头一大段废话然后接口";
        document.Add(CanvasNodeKind.Text, 0, 0, 100, 40, id: "early").Text = "接口在开头";

        var hits = BoardSearch.Find(document, "接口");

        Assert.Equal(["early", "late"], hits.Select(hit => hit.NodeId));   // 命中位置更早的在前，哪怕它摆在后面
    }

    [Fact]
    public void A_blank_line_folds_to_one_space_and_the_offsets_move_with_the_fold()
    {
        // 空行 "\n\n" 折成一个空格，所以"关键词"的原始下标是 7，折完只剩 6 —— 偏移必须落在折后的串上。
        var document = Board("n1", text: "标题\n\n正文 关键词");

        var hit = Assert.Single(BoardSearch.Find(document, "关键词"));

        Assert.Equal("标题 正文 关键词", hit.Snippet);
        Assert.Equal(6, hit.MatchStart);
        Assert.Equal(3, hit.MatchLength);
        Assert.Equal("关键词", hit.Snippet.Substring(hit.MatchStart, hit.MatchLength));
        Assert.False(hit.Snippet.Contains('\n'));
    }

    [Fact]
    public void A_body_longer_than_the_window_is_cut_on_both_ends_but_the_word_survives_whole()
    {
        var text = new string('字', 100) + "命中" + new string('字', 100);
        var document = Board("n1", text: text);

        var hit = Assert.Single(BoardSearch.Find(document, "命中"));

        Assert.Equal("命中", hit.Snippet.Substring(hit.MatchStart, hit.MatchLength));
        Assert.StartsWith("…", hit.Snippet, StringComparison.Ordinal);   // 前后各切掉了一段
        Assert.EndsWith("…", hit.Snippet, StringComparison.Ordinal);
        Assert.Equal(82, hit.Snippet.Length);                            // 80 的字 + 两枚省略号
        Assert.Equal(40, hit.MatchStart);
        Assert.Equal(2, hit.MatchLength);
    }

    [Fact]
    public void A_word_at_the_head_of_a_long_body_keeps_no_leading_ellipsis()
    {
        var text = "开头命中" + new string('字', 100);
        var document = Board("n1", text: text);

        var hit = Assert.Single(BoardSearch.Find(document, "命中"));

        Assert.False(hit.Snippet.StartsWith('…'));
        Assert.Equal(2, hit.MatchStart);
        Assert.Equal("命中", hit.Snippet.Substring(hit.MatchStart, hit.MatchLength));
    }

    [Theory]
    [InlineData("接口层延迟来的", "接口")]
    [InlineData("Report Draft", "report")]
    [InlineData("a\nb\nc", "c")]
    [InlineData("a\r\nb\r\nc", "b")]
    [InlineData("标题\n\n正文 关键词", "关键词")]
    [InlineData("字字字字 命中 字字字字", "命中")]
    [InlineData("第一段\n第二段\n第三段 结尾词", "第三段")]
    public void Every_snippet_that_comes_back_has_emphasiseable_offsets(string text, string query)
    {
        // 这是 UI 唯一不能崩的地方：加粗的区间必须完整落在片段里。逐行扫一张生成表，而不是丢进随机数。
        var document = Board("n1", text: text);

        var hits = BoardSearch.Find(document, query);

        Assert.NotEmpty(hits);

        foreach (var hit in hits)
        {
            Assert.InRange(hit.MatchStart, 0, hit.Snippet.Length);
            Assert.InRange(hit.MatchStart + hit.MatchLength, 0, hit.Snippet.Length);
            Assert.False(hit.Snippet.Contains('\n'));
            Assert.False(hit.Snippet.Contains('\r'));

            var emphasised = hit.Snippet.Substring(hit.MatchStart, hit.MatchLength);
            Assert.NotEmpty(emphasised);
            Assert.Contains(query, new[] { emphasised }, StringComparer.OrdinalIgnoreCase);   // 高亮正好是那个词
        }

        AssertOffsetsInBoard(document, query);
    }
}
