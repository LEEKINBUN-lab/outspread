using System.Text.Json.Nodes;

namespace Outspread.Core.Tests;

public class WebAddressTests
{
    [Theory]
    [InlineData("https://example.com/a/b?x=1")]
    [InlineData("  http://www.example.com  ")]
    [InlineData("www.example.com/posts/hello-world")]
    public void A_single_address_is_recognised_as_a_web_card(string text)
        => Assert.True(WebAddress.TrySingleUrl(text, out _));

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("file:///C:/notes.txt")]
    [InlineData("example.com")]
    public void A_sentence_or_a_non_web_scheme_is_not_a_web_card(string text)
        => Assert.False(WebAddress.TrySingleUrl(text, out _));

    [Fact]
    public void A_host_loses_the_www_prefix_but_not_the_rest()
    {
        Assert.True(WebAddress.TrySingleUrl("https://www.learn.microsoft.com/a", out var uri));
        Assert.Equal("learn.microsoft.com", WebAddress.Host(uri));
    }

    [Fact]
    public void A_title_is_decoded_collapsed_and_capped()
    {
        var title = WebAddress.TitleFromHtml("<html><head><title>\n  Hello &amp;   world \n</title></head></html>");

        Assert.Equal("Hello & world", title);
        Assert.Null(WebAddress.TitleFromHtml("<html><body>no title</body></html>"));
        Assert.Equal(WebAddress.MaxTitleLength, WebAddress.TitleFromHtml("<title>" + new string('x', 300) + "</title>")!.Length);
    }

    [Fact]
    public void A_fallback_title_uses_the_last_path_part_not_the_whole_url()
    {
        Assert.True(WebAddress.TrySingleUrl("https://example.com/posts/hello-world?from=home", out var uri));
        Assert.Equal("hello world", WebAddress.FallbackTitle(uri));
        Assert.True(WebAddress.TrySingleUrl("https://example.com/", out var root));
        Assert.Equal("example.com", WebAddress.FallbackTitle(root));
    }
}

public class TablePasteTests
{
    private const string ExcelHtml = """
        <html><head><style>td{mso-number-format:"General"}</style></head><body>
        <!--StartFragment--><table border=0 cellpadding=0 cellspacing=0 width=300 style='border-collapse:collapse'>
        <caption> Q4 排期 </caption>
        <col width="100"><col width="200">
        <tr height=20>
          <td rowspan=2 style='background:#FFF2CC;font-weight:700'>阶段</td>
          <td colspan=2 style='background-color:rgb(217, 226, 243);font-weight:bold'>Q4</td>
          <td width="80">负责人</td>
        </tr>
        <tr>
          <td>10 月</td><td>11 月</td><td>沈</td>
        </tr>
        <tr>
          <td>A &amp; B<br>two</td><td style='background:#E2EFDA'>定稿</td><td>走查</td><td>陈</td>
        </tr>
        </table><!--EndFragment--></body></html>
        """;

    [Fact]
    public void Excel_html_keeps_merges_fills_widths_and_the_caption()
    {
        var table = TablePaste.ReadHtml(ExcelHtml);

        Assert.NotNull(table);
        Assert.Equal("Q4 排期", table!.Title);
        Assert.Equal(4, table.ColumnCount);
        Assert.Equal(3, table.RowCount);
        Assert.Equal(10, table.CellCount);

        var placements = table.Placements().ToDictionary(item => (item.Row, item.Column), item => item.Cell);

        Assert.False(placements[(0, 0)].Header);
        Assert.Equal(2, placements[(0, 0)].RowSpan);
        Assert.Equal("#fff2cc", placements[(0, 0)].Fill);
        Assert.True(placements[(0, 0)].Bold);

        Assert.Equal(2, placements[(0, 1)].ColSpan);
        Assert.Equal("#d9e2f3", placements[(0, 1)].Fill);

        Assert.Equal("10 月", placements[(1, 1)].Text);
        Assert.Equal("11 月", placements[(1, 2)].Text);
        Assert.Equal("沈", placements[(1, 3)].Text);

        Assert.Equal("A & B\ntwo", placements[(2, 0)].Text);
        Assert.Equal("#e2efda", placements[(2, 1)].Fill);

        Assert.Equal(100, table.ColumnWeights[0]);
        Assert.Equal(200, table.ColumnWeights[1]);
    }

    [Fact]
    public void The_markdown_fallback_keeps_the_grid_and_escapes_pipes()
    {
        var table = TablePaste.ReadHtml(ExcelHtml)!;
        var markdown = table.ToMarkdown();

        Assert.Contains("| 阶段 | Q4 |  | 负责人 |", markdown);
        Assert.Contains("| 10 月 | 11 月 | 沈 |", markdown);
        Assert.Contains("A & B<br>two", markdown);
        Assert.Contains("| --- | --- | --- | --- |", markdown);
    }

    [Fact]
    public void The_table_survives_a_json_round_trip()
    {
        var table = TablePaste.ReadHtml(ExcelHtml)!;
        var restored = TableGrid.FromJson(table.ToJson());

        Assert.NotNull(restored);
        Assert.Equal(table.ColumnCount, restored!.ColumnCount);
        Assert.Equal(table.RowCount, restored.RowCount);
        Assert.Equal(table.CellCount, restored.CellCount);

        var before = table.Placements().ToDictionary(item => (item.Row, item.Column), item => item.Cell);
        var after = restored.Placements().ToDictionary(item => (item.Row, item.Column), item => item.Cell);

        Assert.Equal(before[(0, 0)].RowSpan, after[(0, 0)].RowSpan);
        Assert.Equal(before[(0, 1)].ColSpan, after[(0, 1)].ColSpan);
        Assert.Equal(before[(2, 1)].Fill, after[(2, 1)].Fill);
        Assert.Equal("A & B\ntwo", after[(2, 0)].Text);
    }

    [Fact]
    public void Tab_separated_text_is_a_table_but_one_line_with_a_tab_is_not()
    {
        var table = TablePaste.ReadTsv("a\tb\nc\td");

        Assert.NotNull(table);
        Assert.Equal(2, table!.ColumnCount);
        Assert.Equal(2, table.RowCount);
        Assert.Equal("c", table.Placements().First(item => item.Row == 1 && item.Column == 0).Cell.Text);

        Assert.Null(TablePaste.ReadTsv("a\tb"));
        Assert.Null(TablePaste.ReadTsv("just words"));
    }

    [Fact]
    public void A_single_cell_html_fragment_is_text_not_a_table()
    {
        Assert.Null(TablePaste.ReadHtml("<table><tr><td>only one</td></tr></table>"));
        Assert.Null(TablePaste.ReadHtml("<p>no table</p>"));
    }

    [Fact]
    public void A_pasted_table_is_marked_as_such_on_its_node()
    {
        var document = CanvasDocument.CreateNew();
        var node = document.Add(CanvasNodeKind.Text, 0, 0, 300, 120, id: "utable000002");
        node.Json["table"] = TablePaste.ReadHtml(ExcelHtml)!.ToJson();

        Assert.True(TableGrid.LooksLikeTable(node.Json["table"]));
        Assert.True(EntityCards.IsTable(node));
    }
    [Fact]
    public void A_cell_edit_rewrites_only_that_cell_and_keeps_the_spans()
    {
        var grid = TablePaste.ReadHtml(
            "<table><tr><td rowspan=\"2\"> tall </td><td>one</td><td>two</td></tr><tr><td colspan=\"2\">wide</td></tr></table>")!;

        // 合并格回答的是它的锚点：双击被并掉的那一块，改的是那唯一的一格。
        Assert.True(grid.TrySetCellText(1, 0, "改成别的"));
        Assert.Equal("改成别的", grid.CellAt(1, 0)!.Text);
        Assert.Equal("改成别的", grid.CellAt(0, 0)!.Text);
        Assert.Equal("one", grid.CellAt(0, 1)!.Text);
        Assert.False(grid.TrySetCellText(9, 9, "没有这一格"));

        // 一格在板上只有一行：打字打出的换行读作空格。
        Assert.True(grid.TrySetCellText(0, 1, "上\n下  行"));
        Assert.Equal("上 下 行", grid.CellAt(0, 1)!.Text);

        // 正文那份 markdown 是这张表的备份，改完要能跟着变。
        Assert.Contains("改成别的", grid.ToMarkdown(), StringComparison.Ordinal);
        Assert.Equal("改成别的", TableGrid.FromJson(grid.ToJson())!.CellAt(0, 0)!.Text);
    }

    [Fact]
    public void A_table_card_editors_the_words_under_it_not_the_grid()
    {
        var document = CanvasDocument.CreateNew();
        var node = document.Add(CanvasNodeKind.Text, 0, 0, 300, 120, id: "utable000003");
        node.Json["table"] = TablePaste.ReadTsv("a\tb\nc\td")!.ToJson();
        node.Text = TableGrid.FromJson(node.Json["table"])!.ToMarkdown();

        // 表格卡随时能改：双击起的是"卡下面那几行字"，不是那张表。
        Assert.True(EntityCards.CanEditBody(node));
        Assert.Equal(string.Empty, EntityCards.NoteOf(node));

        EntityCards.SetNote(node, "备注写在表下面");

        Assert.Equal("备注写在表下面", EntityCards.NoteOf(node));
        Assert.NotEqual("备注写在表下面", node.Text);

        // 备注要原样活过一次存盘补丁。
        var again = CanvasDocument.Parse(document.ToJson());
        var back = again.Nodes.First(item => item.Id == node.Id);

        Assert.Equal("备注写在表下面", EntityCards.NoteOf(back));
        Assert.True(EntityCards.IsTable(back));
    }

    [Fact]
    public void A_folder_previews_only_media_names()
    {
        Assert.True(EntityCards.LooksLikeVideo("片断.MP4"));
        Assert.True(EntityCards.LooksLikeImage("封面.jpeg"));
        Assert.False(EntityCards.LooksLikeVideo("说明.txt"));
        Assert.False(EntityCards.LooksLikeImage("封面.psd"));
        Assert.False(EntityCards.LooksLikeImage(null));
    }
}
