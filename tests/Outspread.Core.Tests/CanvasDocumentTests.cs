using System.Security.Cryptography;
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

    private static (int Length, string Sha, int CR) Emitted(CanvasDocument document)
    {
        var bytes = Encoding.UTF8.GetBytes(document.ToJson());

        return (bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Count(b => b == (byte)'\r'));
    }

    private static CanvasDocument Words()
    {
        var document = CanvasDocument.CreateNew();

        document.Add(CanvasNodeKind.Text, 0, 0, 260, 90, color: "4", id: "uzhong1").Text =
            "接口 <b> & '引号' \"双引号\" " + (char)0x2028 + " 斜杠\\ 尾部";

        return document;
    }

    private static CanvasDocument Grouped()
    {
        var document = CanvasDocument.CreateNew();

        document.Add(CanvasNodeKind.Group, 0, 0, 400, 300, id: "ugroup1").Text = "一季度";

        var child = document.Add(CanvasNodeKind.Text, 20, 20, 200, 60, id: "ukid001");

        child.Json["group"] = "ugroup1";
        child.Text = "子里头那格";

        return document;
    }

    private const string FractionJson = """
        {"nodes":[{"id":"uone","type":"text","text":"x","x":0,"y":0,"width":260,"height":40.0}],"edges":[]}
        """;

    private static CanvasDocument Linked()
    {
        var document = CanvasDocument.CreateNew();

        document.Add(CanvasNodeKind.Text, 0, 0, 200, 60, id: "uaaaa1").Text = "上";
        document.Add(CanvasNodeKind.Text, 0, 200, 200, 60, id: "ubbbb1").Text = "下";

        // An explicit edge id: AddEdge mints a random one, and a random id in a frozen-byte
        // test is a test that fails on the second run for the wrong reason.
        document.AppendEdge(new JsonObject
        {
            ["id"] = "uedge001",
            ["fromNode"] = "uaaaa1",
            ["toNode"] = "ubbbb1",
            ["fromSide"] = "bottom",
            ["toSide"] = "top",
        });

        return document;
    }

    /// <summary>
    /// The other end reproduces this emitter byte for byte in its own CI. Nothing else on this
    /// side guards it: change the indent, the encoder, the trailing newline, or how a number is
    /// written, and no test here would have gone red — it would have surfaced over there as
    /// "the phone can't keep up", and the attribution would have landed on them.
    /// </summary>
    /// <remarks>
    /// This is a single-end regression pin, not the cross-end referee: the phone's own corpus
    /// stays the judge of agreement between the two. The two must not be confused for each other.
    /// </remarks>
    [Fact]
    public void The_bytes_this_emitter_writes_are_frozen()
    {
        Assert.Equal((256, "6b4af732068d957a8abb5a99e01a9bcef32c83af99151a332c7bb99ae88fa6b3", 0), Emitted(Words()));
        Assert.Equal((375, "10f83fc6d53748dcc0ec1f6816630f3aa8a9962e2e726b676943e47b59fe5207", 0), Emitted(Grouped()));
        Assert.Equal((470, "d01ff16290827e9bbff7657d0f4c7a1ef97aa78f5ff645c9d3138d84dee59e33", 0), Emitted(Linked()));
        Assert.Equal((178, "0767894b4e4704f9ef27fa3e1d67dbe05ac29a09f4fb8f2d1610d9176dbc0371", 0), Emitted(CanvasDocument.Parse(FractionJson)));
    }

    [Fact]
    public void The_emitter_writes_lf_and_ends_with_a_newline()
    {
        var text = Words().ToJson();

        // CR would make one document answer as two digests depending on the machine that wrote
        // it, which is the same trap the sync JSONs normalise away. And a board without its
        // trailing newline differs from the one on disk by a byte nobody chose.
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_that_arrived_with_a_fraction_comes_back_the_same_way_twice()
    {
        var once = CanvasDocument.Parse(FractionJson).ToJson();

        // `40.0` is kept as written rather than re-minted as `40`. An end that normalises it
        // would never converge with one that does not, and the board would look dirty after
        // every round trip even though nothing was edited.
        Assert.Contains("40.0", once, StringComparison.Ordinal);
        Assert.Equal(once, CanvasDocument.Parse(once).ToJson());
    }

    [Fact]
    public void Find_and_NodesWhere_answer_the_same_and_read_the_live_json()
    {
        /* 10-27 把 Find 从"先物化整张 Nodes 再扫"改成"直接扫 JSON、只造命中那一枚"，并给了
           NodesWhere 一枚同样的形状：拖一格每一帧要调它好几枚，而 `Nodes` 那个属性每读一次就为
           **全部**节点各 new 一枚包装——600 格的板上那就是每帧白造两千多枚对象
           （"便签一多拖动就卡"里实测到的那 2.8ms）。
           钉两件事：命中与不命中的答案一个都没变；盘上改过之后两条路读到的都是新值（不许退化成快照）。 */
        var document = CanvasDocument.Parse(SampleJson);

        Assert.NotNull(document.Find("u111111111111"));
        Assert.Null(document.Find("nope"));
        Assert.Equal(10, document.Find("u111111111111")!.X);

        Assert.Equal(
            ["u111111111111"],
            document.NodesWhere(id => id.StartsWith("u", StringComparison.Ordinal)).Select(node => node.Id).ToArray());

        document.Find("u111111111111")!.X = 77;

        Assert.Equal(77, document.NodesWhere(id => id == "u111111111111").Single().X);
        Assert.Equal(77, document.Nodes[0].X);
        Assert.Equal(77, document.Find("u111111111111")!.X);
    }

    [Fact]
    public void The_node_list_is_handed_back_until_the_shape_actually_changes()
    {
        /* 10-28：`Nodes` 从"每读一次就为全部节点各 new 一枚包装"改成惰性缓存＋逐槽身份校验。
           一边是 ArrangeAll、SaveNow、Rebuild、EmptyGroupIds（每次 arrange 两遍）这些热读点，
           另一边是拖一格每帧几发——省的是每帧成百上千枚短命对象的分配与随之而来的 GC。 */
        var document = CanvasDocument.Parse(SampleJson);
        var first = document.Nodes;

        Assert.Same(first, document.Nodes);          // 缓存成立：交回同一份

        document.Find("u111111111111")!.X = 55;      // 只改值、不换槽 → 缓存不该作废
        Assert.Same(first, document.Nodes);
        Assert.Equal(55, document.Nodes[0].X);       // 包装是活的，缓存不是快照
    }

    [Fact]
    public void A_replacement_that_keeps_the_count_is_still_noticed()
    {
        /* 这一发专钉"校验不能只比数量"。删一格再加一格：数量一模一样，槽里的 JSON 对象换了。
           只比 Count 的校验会把旧包装交回去，界面上那格就永远停在旧内容上——
           而这种错在 105 个读取点上会以"某块界面不刷新"的形状冒出来，极难归因。 */
        var document = CanvasDocument.Parse(SampleJson);
        var before = document.Nodes.ToList();

        document.Remove("u111111111111");
        document.Add(CanvasNodeKind.Text, 1, 2, 30, 40, id: "n999999999999");

        Assert.Equal(
            ["a222222222222", "n999999999999"],
            document.Nodes.Select(node => node.Id).ToArray());

        Assert.NotSame(before[0], document.Nodes[0]);
    }

    [Fact]
    public void A_reorder_is_visible_through_the_list_at_once()
    {
        var document = CanvasDocument.Parse(SampleJson);

        Assert.Equal("u111111111111", document.Nodes[0].Id);

        document.BringToFront("u111111111111");

        Assert.Equal(
            ["a222222222222", "u111111111111"],
            document.Nodes.Select(node => node.Id).ToArray());
    }
}
