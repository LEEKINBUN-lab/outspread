using System.Text.Json.Nodes;

namespace Outspread.Core.Tests;

public class AttachmentAndEntityTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(AttachmentStore.AutoCacheLimitBytes, false)]
    [InlineData(AttachmentStore.AutoCacheLimitBytes + 1, true)]
    public void The_ten_megabyte_line_asks_only_above_it(long bytes, bool prompt)
        => Assert.Equal(prompt, AttachmentStore.NeedsPrompt(bytes));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(2_621_440, "2.5 MB")]
    [InlineData(1_288_490_188, "1.2 GB")]
    public void Sizes_read_as_a_person_would_write_them(long bytes, string expected)
        => Assert.Equal(expected, AttachmentStore.FormatSize(bytes));

    [Fact]
    public void A_file_name_loses_only_what_windows_and_the_cap_forbid()
    {
        Assert.Equal("q4_plan_.pdf", AttachmentStore.SafeFileName("q4:plan?.pdf"));
        Assert.Equal("file", AttachmentStore.SafeFileName("..."));
        Assert.True(AttachmentStore.SafeFileName(new string('x', 120) + ".pdf")!.Length <= 64);
    }

    [Fact]
    public void A_cached_file_lands_under_attachments_and_the_second_write_is_reused()
    {
        using var scratch = new Scratch("attachment-write");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var first = AttachmentStore.WriteBytes(scratch.BoardPath, bytes, "Q4 plan.pdf", out var reusedFirst);
        var second = AttachmentStore.WriteBytes(scratch.BoardPath, bytes, "Q4 plan.pdf", out var reusedSecond);

        Assert.False(reusedFirst);
        Assert.True(reusedSecond);
        Assert.Equal(first, second);
        Assert.StartsWith("attachments/", first, StringComparison.Ordinal);
        Assert.EndsWith("_Q4 plan.pdf", first, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(scratch.Folder, first.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(AttachmentStore.IsOwnedPath(first));
        Assert.False(AttachmentStore.IsOwnedPath("../outside.pdf"));
        Assert.Equal("Q4 plan.pdf", AttachmentStore.OriginalNameFromRelative(first));
    }

    [Fact]
    public void A_file_already_inside_the_board_folder_is_left_where_it_is()
    {
        using var scratch = new Scratch("attachment-local");
        var source = Path.Combine(scratch.Folder, "already-here.pdf");
        File.WriteAllBytes(source, [1, 2, 3]);

        var relative = AttachmentStore.WriteFile(scratch.BoardPath, source, out var reused);

        Assert.True(reused);
        Assert.Equal("already-here.pdf", relative);
        Assert.False(AttachmentStore.IsOwnedPath(relative));
        Assert.False(Directory.Exists(AttachmentStore.AttachmentsDirectory(scratch.BoardPath)));
    }

    [Fact]
    public void Owned_paths_come_from_file_nodes_only_and_survive_a_sibling_board_scan()
    {
        using var scratch = new Scratch("attachment-refs");

        var one = CanvasDocument.CreateNew();
        one.Add(CanvasNodeKind.File, 0, 0, 100, 100, id: "ufileone001").FilePath = "attachments/abc123_a.pdf";
        one.Add(CanvasNodeKind.File, 0, 0, 100, 100, id: "ufiletwo001").FilePath = "assets/img_a.png";
        one.Add(CanvasNodeKind.Link, 0, 0, 100, 100, id: "ulinkone001").Url = "https://example.com";

        Assert.Equal(["attachments/abc123_a.pdf"], AttachmentStore.OwnedPaths(one));
        Assert.True(AttachmentStore.IsReferenced(one, "attachments/abc123_a.pdf"));
        Assert.False(AttachmentStore.IsReferenced(one, "attachments/other.pdf"));

        var sibling = Path.Combine(scratch.Folder, "sibling.canvas");
        CanvasStore.Write(sibling, one.ToJson());

        Assert.True(AttachmentStore.IsReferencedInSiblingBoards(scratch.BoardPath, "attachments/abc123_a.pdf", scratch.BoardPath));
        Assert.False(AttachmentStore.IsReferencedInSiblingBoards(scratch.BoardPath, "attachments/other.pdf", scratch.BoardPath));
    }

    [Fact]
    public void A_file_node_that_is_not_a_picture_is_a_file_card_but_a_picture_stays_a_picture()
    {
        var document = CanvasDocument.CreateNew();
        var pdf = document.Add(CanvasNodeKind.File, 0, 0, 100, 100, id: "updffile0001");
        pdf.FilePath = "attachments/abc.pdf";
        var image = document.Add(CanvasNodeKind.File, 0, 0, 100, 100, id: "uimagefile01");
        image.FilePath = "assets/a.png";

        Assert.Equal(EntityCardKind.File, EntityCards.KindOf(pdf));
        Assert.Equal(EntityCardKind.None, EntityCards.KindOf(image));
        Assert.True(EntityCards.CanEditBody(pdf));
        Assert.False(EntityCards.CanEditBody(image));
        Assert.Equal("PDF", EntityCards.TypeLabel(pdf));
    }

    [Fact]
    public void A_link_is_a_web_folder_or_file_card_from_its_hint_or_scheme()
    {
        var document = CanvasDocument.CreateNew();

        var web = document.Add(CanvasNodeKind.Link, 0, 0, 100, 100, id: "uweb0000001");
        web.Url = "https://example.com/a";
        EntityCards.SetHint(web, "web");

        var folder = document.Add(CanvasNodeKind.Link, 0, 0, 100, 100, id: "ufolder00001");
        folder.Url = "file:///C:/Pictures/Board";
        EntityCards.SetHint(folder, "folder");

        Assert.Equal(EntityCardKind.Web, EntityCards.KindOf(web));
        Assert.Equal(EntityCardKind.Folder, EntityCards.KindOf(folder));
        Assert.True(EntityCards.CanEditBody(web));
        Assert.Equal("WEB", EntityCards.TypeLabel(web));
        Assert.Equal("DIR", EntityCards.TypeLabel(folder));
    }

    [Fact]
    public void A_table_is_a_text_node_carrying_its_own_json()
    {
        var document = CanvasDocument.CreateNew();
        var table = document.Add(CanvasNodeKind.Text, 0, 0, 300, 100, id: "utable000001");
        table.Text = "| a | b |";
        table.Json["table"] = new JsonObject { ["version"] = 1, ["rows"] = new JsonArray() };

        Assert.True(EntityCards.IsTable(table));
        Assert.Equal(EntityCardKind.Table, EntityCards.KindOf(table));
        // 表格卡随时能改：双击起的是卡下面那几行备注，不是"改好再重新粘贴"。
        Assert.True(EntityCards.CanEditBody(table));
        Assert.Equal("XLS", EntityCards.TypeLabel(table));
    }
}
