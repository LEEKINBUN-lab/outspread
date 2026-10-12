using System.Text;
using System.Text.Json.Nodes;

namespace Outspread.Core.Tests;

public class CanvasDocumentTests
{
    private const string SampleJson = """
        {
          "nodes": [
            {
              "id": "u111111111111",
              "type": "text",
              "x": 10,
              "y": 20,
              "width": 100,
              "height": 50,
              "text": "hello",
              "color": "4",
              "outspread:shadow": true
            },
            {
              "id": "a222222222222",
              "type": "group",
              "x": 0,
              "y": 0,
              "width": 300,
              "height": 300,
              "label": "G",
              "background": "assets/img_x.png",
              "backgroundStyle": "cover"
            }
          ],
          "edges": [
            {
              "id": "e1",
              "fromNode": "u111111111111",
              "fromSide": "bottom",
              "fromEnd": "none",
              "toNode": "a222222222222",
              "toSide": "top",
              "toEnd": "arrow"
            }
          ],
          "someFutureKey": { "kept": 1 }
        }
        """;

    private static string Order(CanvasDocument document)
        => string.Join(',', document.Nodes.Select(node => node.Id));

    [Fact]
    public void Mutating_one_node_keeps_fields_and_keys_this_program_does_not_model()
    {
        var document = CanvasDocument.Parse(SampleJson);

        var moved = document.Find("u111111111111")
            ?? throw new InvalidOperationException("node missing after parse");

        moved.X = 999;

        var after = CanvasDocument.Parse(document.ToJson());

        Assert.Equal(999, after.Find("u111111111111")!.X);

        // The two things that must survive: a property this program has never seen,
        // and the whole edge collection it does not draw.
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{ "kept": 1 }"""),
            after.Root["someFutureKey"]));

        var group = after.Find("a222222222222")!;
        Assert.Equal("cover", group.BackgroundStyle);
        Assert.Equal("assets/img_x.png", group.Background);

        var shadow = after.Find("u111111111111")!.Json["outspread:shadow"];
        Assert.NotNull(shadow);
        Assert.True(shadow!.GetValue<bool>());

        var edges = after.Root["edges"]!.AsArray();
        Assert.Single(edges);
        Assert.Equal("arrow", edges[0]!.AsObject()["toEnd"]!.GetValue<string>());
    }

    [Fact]
    public void Edges_survive_a_round_trip_untouched()
    {
        var document = CanvasDocument.Parse(SampleJson);
        document.Find("u111111111111")!.Y = 42;

        var after = CanvasDocument.Parse(document.ToJson());

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(SampleJson)!.AsObject()["edges"],
            after.Root["edges"]));
    }

    [Fact]
    public void Geometry_is_written_whole_and_reads_back_whole()
    {
        var document = CanvasDocument.CreateNew();
        var node = document.Add(CanvasNodeKind.Text, 412.7, -3.2, 200.5, 80.0);

        var json = document.ToJson();

        // 412.7 must land as 413, and nothing may be written as "80.0" or with a
        // fractional tail: the spec describes pixel coordinates, and diffs stay legible.
        Assert.Contains("\"x\": 413", json, StringComparison.Ordinal);
        Assert.Contains("\"y\": -3", json, StringComparison.Ordinal);
        Assert.Contains("\"width\": 201", json, StringComparison.Ordinal);
        Assert.DoesNotContain(".0", json, StringComparison.Ordinal);

        // The trap this test exists for: a number this program wrote is backed by
        // Int64, and reading it as double throws, which a careless getter turns into
        // a silent zero.
        var reread = CanvasDocument.Parse(json).Find(node.Id)!;
        Assert.Equal(413, reread.X);
        Assert.Equal(-3, reread.Y);
        Assert.Equal(201, reread.Width);
        Assert.Equal(80, reread.Height);
    }

    [Fact]
    public void Repeated_ids_are_refused_naming_both_positions()
    {
        var json = """
            { "nodes": [
                { "id": "u1", "type": "text", "x": 0, "y": 0, "width": 10, "height": 10, "text": "a" },
                { "id": "u1", "type": "text", "x": 5, "y": 0, "width": 10, "height": 10, "text": "b" }
            ] }
            """;

        var exception = Assert.Throws<CanvasDocumentException>(() => CanvasDocument.Parse(json, "board.canvas"));

        Assert.Contains("u1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("node #0 and node #1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("board.canvas", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_without_an_id_is_refused_instead_of_becoming_unaddressable()
    {
        var json = """{ "nodes": [ { "type": "text", "x": 0, "y": 0, "width": 10, "height": 10 } ] }""";

        var exception = Assert.Throws<CanvasDocumentException>(() => CanvasDocument.Parse(json));
        Assert.Contains("no \"id\"", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Written_text_is_lf_only_with_two_space_indent_and_no_bom()
    {
        using var scratch = new Scratch("format");

        var document = CanvasDocument.Parse(SampleJson);
        document.Find("u111111111111")!.X = 12;

        CanvasStore.Write(scratch.BoardPath, document.ToJson());

        var bytes = File.ReadAllBytes(scratch.BoardPath);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Contains("\n  \"nodes\"", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_ascii_text_is_written_as_itself_not_as_escapes()
    {
        var document = CanvasDocument.CreateNew();
        document.Add(CanvasNodeKind.Text, 0, 0, 200, 60).Text = "假设 A：延迟来自接口层";

        var json = document.ToJson();

        Assert.Contains("假设 A", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u5047", json, StringComparison.Ordinal);
        Assert.Equal("假设 A：延迟来自接口层", CanvasDocument.Parse(json).Nodes[0].Text);
    }

    [Fact]
    public void New_nodes_land_at_the_end_which_is_on_top_and_order_survives()
    {
        var document = CanvasDocument.CreateNew();

        var first = document.Add(CanvasNodeKind.Text, 0, 0, 10, 10);
        var second = document.Add(CanvasNodeKind.Text, 0, 0, 10, 10);
        var third = document.Add(CanvasNodeKind.Text, 0, 0, 10, 10);

        Assert.Equal($"{first.Id},{second.Id},{third.Id}", Order(document));

        document.BringToFront(first.Id);
        Assert.Equal($"{second.Id},{third.Id},{first.Id}", Order(document));

        document.SendToBack(third.Id);
        Assert.Equal($"{third.Id},{second.Id},{first.Id}", Order(document));

        Assert.Equal($"{third.Id},{second.Id},{first.Id}", Order(CanvasDocument.Parse(document.ToJson())));
    }

    [Fact]
    public void An_empty_document_still_reads_as_a_board()
    {
        var document = CanvasDocument.Parse("{}");

        Assert.Empty(document.Nodes);
        Assert.NotNull(document.Root["nodes"]);

        var node = document.Add(CanvasNodeKind.Text, 1, 2, 3, 4);
        Assert.StartsWith("u", node.Id, StringComparison.Ordinal);
        Assert.Equal(NodeIds.Length, node.Id.Length);
    }
}
